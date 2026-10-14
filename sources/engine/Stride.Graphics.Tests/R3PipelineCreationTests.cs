// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#if STRIDE_GRAPHICS_API_VULKAN || STRIDE_GRAPHICS_API_DIRECT3D12
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Stride.Core.Storage;
using Stride.Shaders;
using Xunit;

namespace Stride.Graphics.Tests;

/// <summary>
/// Spike R3 (throwaway): pipeline creation time of the R3 variants, through Stride's <see cref="PipelineState"/>.
/// Inputs come from <c>R3SpecializationTests.Measure</c> (Stride.Shaders.Tests). Driven by environment variables:
/// R3_DIR (the <c>flags*</c> folder), R3_GPU (part of the adapter name), R3_SALT (same salt in a new process = warm
/// driver cache; new salt = cold), R3_OUT (results file, appended), R3_STATS=1 (Vulkan driver statistics pass).
/// </summary>
public class R3PipelineCreationTests
{
    static readonly string Api =
#if STRIDE_GRAPHICS_API_VULKAN
        "vulkan";
#else
        "d3d12";
#endif

    [Fact]
    public void CreatePipelines()
    {
        // Runs only when driven by the spike scripts
        if (Environment.GetEnvironmentVariable("R3_DIR") is not { Length: > 0 } directory)
            return;
        var gpu = Environment.GetEnvironmentVariable("R3_GPU") ?? "NVIDIA";
        var salt = Environment.GetEnvironmentVariable("R3_SALT") ?? Guid.NewGuid().ToString("N");
        var outputFile = Environment.GetEnvironmentVariable("R3_OUT") ?? Path.Combine(directory, $"pipelines-{Api}.txt");
        var statistics = Environment.GetEnvironmentVariable("R3_STATS") == "1";

        var adapters = GraphicsAdapterFactory.Adapters.ToArray();
        var adapter = adapters.FirstOrDefault(a => a.Description.Contains(gpu, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No adapter matches {gpu}: {string.Join(", ", adapters.Select(a => a.Description))}");
        using var device = GraphicsDevice.New(adapter, DeviceCreationFlags.None, GraphicsProfile.Level_11_0);
        var flagCount = int.Parse(Path.GetFileName(directory).Replace("flags", ""), CultureInfo.InvariantCulture);
        var count = 1 << flagCount;
        // Stable across processes (string.GetHashCode is not)
        var saltValue = BitConverter.ToUInt32(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(salt)));

        var report = new StringBuilder();
        report.AppendLine($"## {Api} on {adapter.Description}, {flagCount} flags, salt {salt}, stats {statistics}, {DateTime.Now:s}");
        report.AppendLine("kind\tmask\tstride_ms\tdriver_ms\tstats");

        void Create(string kind, uint mask, EffectBytecode bytecode, uint[]? specValues)
        {
            R3PipelineSpike.SpecializationValues = specValues;
            R3PipelineSpike.CaptureStatistics = statistics;
            var description = Description(device, bytecode);
            var stopwatch = Stopwatch.StartNew();
            var pipeline = PipelineState.New(device, description);
            var total = stopwatch.Elapsed.TotalMilliseconds;
            var stats = statistics ? string.Join(" | ", R3PipelineSpike.LastStatistics) : "";
            report.AppendLine(CultureInfo.InvariantCulture, $"{kind}\t{mask}\t{total:F3}\t{R3PipelineSpike.LastDriverMilliseconds:F3}\t{stats}");
            pipeline.Dispose();
            R3PipelineSpike.SpecializationValues = null;
        }

        EffectBytecode Load(string name) => Salt(EffectBytecode.FromBytes(File.ReadAllBytes(Path.Combine(directory, Api, name + ".sdfxbytecode"))), saltValue);

        // Warm-up: Stride + driver one-time costs, on a pipeline not measured
        Create("warmup", 0, Salt(EffectBytecode.FromBytes(File.ReadAllBytes(Path.Combine(directory, Api, "runtime.sdfxbytecode"))), saltValue ^ 0xFFFFFFFF), null);

        Create("runtime", 0, Load("runtime"), null);
        if (Api == "vulkan")
        {
            var spec = Load("spec");
            for (uint mask = 0; mask < count; mask++)
                Create("separate", mask, Load($"separate-{mask}"), null);
            for (uint mask = 0; mask < count; mask++)
                Create("V1-spec-info", mask, spec, Enumerable.Range(0, flagCount).Select(k => (mask >> k) & 1).ToArray());
            for (uint mask = 0; mask < count; mask++)
                Create("V2-optimized", mask, Load($"optimized-{mask}"), null);
        }
        else
        {
            for (uint mask = 0; mask < count; mask++)
                Create("separate", mask, Load($"separate-{mask}"), null);
            for (uint mask = 0; mask < count; mask++)
                Create("D2-frozen", mask, Load($"frozen-{mask}"), null);
        }

        File.AppendAllText(outputFile, report.ToString() + Environment.NewLine);
        Console.WriteLine(report.ToString());
    }

    static PipelineStateDescription Description(GraphicsDevice device, EffectBytecode bytecode)
    {
        // Resolves the parameter keys of the reflection
        bytecode = new Effect(device, bytecode).Bytecode;
        var slots = bytecode.Reflection.ResourceGroups.Select(g => g.Name ?? "Globals").Distinct().ToList();
        if (!slots.Contains("Globals"))
            slots.Add("Globals");
        var descriptorReflection = EffectDescriptorSetReflection.New(device, bytecode, slots, "Globals");
        var description = new PipelineStateDescription();
        description.SetDefaults();
        description.EffectBytecode = bytecode;
        description.RootSignature = RootSignature.New(device, descriptorReflection);
        description.InputElements = (bytecode.Reflection.InputAttributes ?? [])
            .Select((a, i) => new InputElementDescription { SemanticName = a.SemanticName, SemanticIndex = a.SemanticIndex, Format = PixelFormat.R32G32B32A32_Float, InputSlot = 0, AlignedByteOffset = i * 16 })
            .ToArray();
        description.PrimitiveType = PrimitiveType.TriangleList;
        description.Output = new RenderOutputDescription(PixelFormat.R16G16B16A16_Float, PixelFormat.D32_Float);
        return description;
    }

    /// <summary>
    /// Makes the shader bytes unique for this salt, so driver shader caches miss on a new salt (cold) and can hit on
    /// the same salt in a later process (warm). SPIR-V: an unused 32-bit constant. DXIL: a PRIV container part.
    /// </summary>
    static EffectBytecode Salt(EffectBytecode bytecode, uint salt)
    {
        var stages = bytecode.Stages.Select(s => new ShaderBytecode
        {
            Stage = s.Stage,
            EntryPoint = s.EntryPoint,
            Data = Api == "vulkan" ? SaltSpirv(s.Data, salt) : SaltDxil(s.Data, salt),
        }).ToArray();
        foreach (var stage in stages)
            stage.Id = ObjectId.FromBytes(stage.Data);
        return new EffectBytecode { Reflection = bytecode.Reflection, Stages = stages, HashSources = bytecode.HashSources };
    }

    static byte[] SaltSpirv(byte[] data, uint salt)
    {
        var words = new List<uint>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(data).ToArray());
        var newId = words[3]++;
        for (int i = 5; i < words.Count; i += (int)(words[i] >> 16))
        {
            // OpTypeInt %id 32 0
            if ((words[i] & 0xFFFF) == 21 && words[i + 2] == 32 && words[i + 3] == 0)
            {
                words.InsertRange(i + 4, [(4u << 16) | 43, words[i + 1], newId, salt]);
                return System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.ToArray().AsSpan()).ToArray();
            }
        }
        throw new InvalidOperationException("No uint type to salt the SPIR-V with");
    }

    static unsafe byte[] SaltDxil(byte[] data, uint salt)
    {
        var partCount = BitConverter.ToInt32(data, 28);
        var headerSize = 32 + 4 * partCount;
        var result = new byte[data.Length + 4 + 12];
        // Header + offsets (each +4 for the new offset entry), then the new offset
        Array.Copy(data, result, 32);
        BitConverter.TryWriteBytes(result.AsSpan(24), result.Length);
        BitConverter.TryWriteBytes(result.AsSpan(28), partCount + 1);
        for (int p = 0; p < partCount; p++)
            BitConverter.TryWriteBytes(result.AsSpan(32 + 4 * p), BitConverter.ToInt32(data, 32 + 4 * p) + 4);
        BitConverter.TryWriteBytes(result.AsSpan(32 + 4 * partCount), data.Length + 4);
        Array.Copy(data, headerSize, result, headerSize + 4, data.Length - headerSize);
        // PRIV part: fourcc, size, salt
        var part = data.Length + 4;
        Encoding.ASCII.GetBytes("PRIV").CopyTo(result, part);
        BitConverter.TryWriteBytes(result.AsSpan(part + 4), 4);
        BitConverter.TryWriteBytes(result.AsSpan(part + 8), salt);
        fixed (byte* p = result)
            Stride.Shaders.Compiler.Direct3D.DxilHash.ComputeHashRetail(&p[20], (uint)(result.Length - 20), &p[4]);
        return result;
    }
}
#endif
