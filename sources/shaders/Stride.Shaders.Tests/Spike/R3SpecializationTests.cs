// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using Stride.Core.Diagnostics;
using Stride.Graphics;
using Stride.Shaders.Compiler;
using Stride.Shaders.Compilers;
using Stride.Shaders.Compilers.Direct3D;
using Stride.Shaders.Compilers.SDSL;
using Stride.Shaders.Parsers.Tests.Corpus;
using Stride.Shaders.Spirv.Processing.Interfaces;
using Stride.Shaders.Spirv.Tools;
using ShaderStage = Stride.Shaders.ShaderStage;

namespace Stride.Shaders.Parsers.Tests.Spike;

/// <summary>
/// Spike R3 (throwaway): is "mix one übershader with value flags as specialization constants, then specialize per
/// combination" fast enough and good enough? Explicit tests; results go to <c>R3/</c> next to the test assembly.
/// </summary>
/// <remarks>
/// The flags are <c>uint</c> generics of <c>MaterialSurfaceShadingStandardLobes</c>. "Separate" variants mix with 0 / 1
/// (today's path: one mix per variant). The "uber" module mixes once with a unique sentinel value per flag; after the mix,
/// <see cref="MakeSpecConstants"/> turns each sentinel <c>OpConstant</c> into an <c>OpSpecConstant</c> with a <c>SpecId</c>
/// (bool generics can't be used: <c>true</c> / <c>false</c> constants are shared).
/// </remarks>
public partial class R3SpecializationTests
{
    const string UberClass = "MaterialSurfaceShadingStandardLobes";
    const int GenericFlagCount = 7;
    const uint Sentinel = 0x5EC0DE00;

    static readonly string[] LobeNames = ["coat", "anisotropy", "sheen", "transmission", "thin film", "subsurface", "metal tint"];

    static string SpikeDirectory => Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders", "Stride.Shaders.Tests", "Spike", "R3");

    static string UberFile => Path.Combine(SpikeDirectory, UberClass + ".sdsl");

    static string OutputDirectory => Path.Combine(AppContext.BaseDirectory, "R3");

    static readonly Lazy<ShaderCorpusEntry> BaseEntry = new(() => ShaderCorpus.ReadEntries()
        .Where(e => e.EffectName == "StrideForwardShadingEffect")
        .MaxBy(e => ShaderCorpusEntry.ToJson(e).Length)!);

    // ---------------------------------------------------------------------------------------------------------------
    // Trees and mixing

    static string[] SeparateGenerics(uint mask) => [.. Enumerable.Range(0, GenericFlagCount).Select(k => ((mask >> k) & 1).ToString(CultureInfo.InvariantCulture)), "0"];

    static string[] UberGenerics(int flagCount) => [.. Enumerable.Range(0, GenericFlagCount).Select(k => k < flagCount ? (Sentinel + (uint)k).ToString(CultureInfo.InvariantCulture) : "0"), "0"];

    static string[] RuntimeGenerics() => [.. Enumerable.Repeat("0", GenericFlagCount), "1"];

    static ShaderMixinSource ForwardTree(GraphicsPlatform platform, string[] generics)
    {
        var entry = BaseEntry.Value;
        var mixin = new ShaderMixinSource();
        mixin.DeepCloneFrom(entry.Mixin);
        mixin.Name = entry.EffectName;
        var surfaces = FindSurfaces(mixin) ?? throw new InvalidOperationException("No MaterialSurfaceLightingAndShading surfaces in the base entry");
        surfaces.Add(new ShaderClassSource(UberClass, generics));
        EffectCompiler.AddPlatformMacros(mixin, new EffectCompilerParameters { Platform = platform, Profile = entry.Profile });
        return mixin;
    }

    static ShaderArraySource? FindSurfaces(ShaderSource source) => source switch
    {
        ShaderMixinSource m when m.Mixins.Any(x => x.ClassName == "MaterialSurfaceLightingAndShading")
            && m.Compositions.TryGetValue("surfaces", out var s) && s is ShaderArraySource a => a,
        ShaderMixinSource m => m.Compositions.Values.Select(FindSurfaces).FirstOrDefault(x => x != null),
        ShaderArraySource a => a.Values.Select(FindSurfaces).FirstOrDefault(x => x != null),
        _ => null,
    };

    sealed record Mixed(uint[] Words, EffectReflection Reflection, List<InterfaceProcessor.EntryPointInfo> EntryPoints);

    static Mixed Mix(ShaderMixinSource tree, GraphicsPlatform platform, ShaderLoaderBase loader)
    {
        var log = new LoggerResult();
        var mixer = new ShaderMixer(loader);
        if (!mixer.MergeSDSL(tree, EffectCompiler.GetMixerOptions(platform), log, out var bytecode, out var reflection, out _, out var entryPoints) || log.HasErrors)
            throw new InvalidOperationException("Mix failed: " + string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)));
        return new(MemoryMarshal.Cast<byte, uint>(bytecode).ToArray(), reflection!, entryPoints!);
    }

    static ShaderLoaderBase NewLoader() => ShaderCorpus.CreateLoader(UberFile);

    // ---------------------------------------------------------------------------------------------------------------
    // SPIR-V rewrites

    const ushort OpConstant = 43, OpSpecConstant = 50, OpDecorate = 71, OpMemberDecorate = 72, OpDecorateString = 5632;
    const uint DecorationSpecId = 1;

    /// <summary>
    /// Sentinel <c>OpConstant</c>s become <c>OpSpecConstant</c> (default 0) decorated with <c>SpecId</c> = flag index.
    /// </summary>
    static int SentinelCopies;

    static uint[] MakeSpecConstants(uint[] words, int flagCount)
    {
        var specIds = new Dictionary<uint, uint>();
        for (int i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            if ((words[i] & 0xFFFF) == OpConstant && (words[i] >> 16) == 4 && words[i + 3] - Sentinel < (uint)flagCount)
                specIds.Add(words[i + 2], words[i + 3] - Sentinel);
        }
        // The mixer doesn't merge equal constants across the module: one flag can have several copies, all get the same SpecId
        if (specIds.Values.Distinct().Count() != flagCount)
            throw new InvalidOperationException($"Found sentinel constants for {specIds.Values.Distinct().Count()} flags, expected {flagCount}");
        SentinelCopies = specIds.Count;

        var result = new List<uint>(words.Length + 4 * flagCount);
        result.AddRange(words.AsSpan(0, 5));
        bool decorationsAdded = false;
        for (int i = 5; i < words.Length;)
        {
            var op = (ushort)(words[i] & 0xFFFF);
            var count = (int)(words[i] >> 16);
            if (!decorationsAdded && op is OpDecorate or OpMemberDecorate or OpDecorateString)
            {
                foreach (var (id, specId) in specIds.OrderBy(x => x.Value))
                    result.AddRange([(4u << 16) | OpDecorate, id, DecorationSpecId, specId]);
                decorationsAdded = true;
            }
            if (op == OpConstant && count == 4 && specIds.ContainsKey(words[i + 2]))
                result.AddRange([(4u << 16) | OpSpecConstant, words[i + 1], words[i + 2], 0]);
            else
                result.AddRange(words.AsSpan(i, count));
            i += count;
        }
        if (!decorationsAdded)
            throw new InvalidOperationException("No decoration section found");
        return result.ToArray();
    }

    /// <summary>
    /// The C# "freeze": spec constants with a <c>SpecId</c> become <c>OpConstant</c> with the flag value, their decorations are removed.
    /// </summary>
    static uint[] Freeze(uint[] words, uint mask)
    {
        var specIds = new Dictionary<uint, uint>();
        for (int i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            if ((words[i] & 0xFFFF) == OpDecorate && (words[i] >> 16) == 4 && words[i + 2] == DecorationSpecId)
                specIds.Add(words[i + 1], words[i + 3]);
        }
        var result = new uint[words.Length - 4 * specIds.Count];
        int o = 0;
        for (int i = 0; i < 5; i++)
            result[o++] = words[i];
        for (int i = 5; i < words.Length;)
        {
            var op = (ushort)(words[i] & 0xFFFF);
            var count = (int)(words[i] >> 16);
            if (op == OpDecorate && count == 4 && words[i + 2] == DecorationSpecId)
            {
                // dropped
            }
            else if (op == OpSpecConstant && count == 4 && specIds.TryGetValue(words[i + 2], out var specId))
            {
                result[o++] = (4u << 16) | OpConstant;
                result[o++] = words[i + 1];
                result[o++] = words[i + 2];
                result[o++] = (mask >> (int)specId) & 1;
            }
            else
            {
                words.AsSpan(i, count).CopyTo(result.AsSpan(o));
                o += count;
            }
            i += count;
        }
        return result;
    }

    static uint[] FreezeWithSpirvOpt(uint[] words, uint mask, int flagCount)
    {
        var values = string.Join(' ', Enumerable.Range(0, flagCount).Select(k => $"{k}:{(mask >> k) & 1}"));
        return SpirvTools.Optimize(words, [$"--set-spec-const-default-value={values}", "--freeze-spec-const"]);
    }

    /// <summary>
    /// What runs: legalized, <c>spirv-opt -O</c>, canonical ids, normalized without names.
    /// </summary>
    static string OptimizedText(uint[] words)
    {
        var w = SpirvTools.LegalizeForHlsl(words);
        w = SpirvTools.OptimizeForPerformance(w);
        w = SpirvTools.Optimize(w, ["--unify-const", "--canonicalize-ids"]);
        return SpirvNormalizer.Normalize(MemoryMarshal.Cast<uint, int>(w.AsSpan()), keepNames: false);
    }

    static int InstructionCount(string normalizedText)
        => normalizedText.Split('\n').Count(l => l.Length > 0 && !l.StartsWith(';'));

    // ---------------------------------------------------------------------------------------------------------------
    // Back ends

    static EffectCompilerParameters FxcParameters(GraphicsProfile profile) => new()
    {
        Platform = GraphicsPlatform.Direct3D11,
        Profile = profile,
        // CompilationMode.Release (what game asset builds use)
        Debug = true,
        OptimizationLevel = 1,
    };

    sealed record FxcStage(ShaderStage Stage, int Instructions, int Temps, int Bytes);

    static ShaderStage ToStage(ExecutionModel model) => model switch
    {
        ExecutionModel.Vertex => ShaderStage.Vertex,
        ExecutionModel.TessellationControl => ShaderStage.Hull,
        ExecutionModel.TessellationEvaluation => ShaderStage.Domain,
        ExecutionModel.Geometry => ShaderStage.Geometry,
        ExecutionModel.Fragment => ShaderStage.Pixel,
        ExecutionModel.GLCompute => ShaderStage.Compute,
        _ => throw new NotSupportedException(model.ToString()),
    };

    /// <summary>
    /// SPIRV-Cross for every entry point (legalized input).
    /// </summary>
    static List<(ShaderStage Stage, string Code)> CrossCompile(uint[] legalized)
    {
        var translator = new SpirvTranslator(legalized.AsMemory());
        return translator.GetEntryPoints().Select(e => (ToStage(e.ExecutionModel), translator.Translate(Backend.Hlsl, e))).ToList();
    }

    static List<FxcStage> Fxc(List<(ShaderStage Stage, string Code)> stages, string prefix, GraphicsProfile profile, EffectReflection reflection)
    {
        var compiler = new Stride.Shaders.Compiler.Direct3D.ShaderCompiler();
        var result = new List<FxcStage>();
        // FXC updates the reflection in place: each compile gets its own copy (runs in parallel)
        using (var stream = new MemoryStream())
        {
            new EffectBytecode { Reflection = reflection, Stages = [], HashSources = new() }.WriteTo(stream);
            reflection = EffectBytecode.FromBytes(stream.ToArray()).Reflection;
        }
        foreach (var (stage, code) in stages)
        {
            var r = compiler.Compile(prefix + code, "main", stage, FxcParameters(profile), reflection);
            if (r.HasErrors || r.Bytecode == null)
                throw new InvalidOperationException($"FXC failed for {stage}: {string.Join(Environment.NewLine, r.Messages.Select(m => m.Text))}");
            result.Add(new FxcStage(stage, FxcInstructionSlots(r.DisassembleText), FxcTemps(r.DisassembleText), r.Bytecode.Data.Length));
        }
        return result;
    }

    static int FxcInstructionSlots(string? disassembly)
        => disassembly != null && Regex.Match(disassembly, @"Approximately (\d+) instruction slots used") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;

    static int FxcTemps(string? disassembly)
        => disassembly != null && Regex.Match(disassembly, @"dcl_temps (\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

    static string SpecDefines(uint mask, int flagCount)
    {
        var builder = new StringBuilder();
        for (int k = 0; k < flagCount; k++)
            builder.Append(CultureInfo.InvariantCulture, $"#define SPIRV_CROSS_CONSTANT_ID_{k} {(mask >> k) & 1}u\n");
        return builder.ToString();
    }

    static List<ShaderBytecode> DxilPipeline(uint[] words, List<InterfaceProcessor.EntryPointInfo> entryPoints)
    {
        var result = new List<ShaderBytecode>();
        EffectCompiler.CompileDxilPipeline(MemoryMarshal.AsBytes(words.AsSpan()), entryPoints, result);
        return result;
    }

    [DllImport("spirv_to_dxil.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void spirv_to_dxil_free(ref DXILSpirvObject dxil);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static unsafe void IgnoreLog(void* priv, byte* message) { }

    /// <summary>
    /// D1: one stage at a time (not linked: the I/O signatures of the stages may not match), spec values given to <c>spirv_to_dxil</c>.
    /// </summary>
    static unsafe List<int> DxilSingleStage(uint[] words, List<InterfaceProcessor.EntryPointInfo> entryPoints, uint mask, int flagCount)
    {
        var conf = new RuntimeConf
        {
            runtime_data_cbv = { base_shader_register = 0, register_space = 31 },
            push_constant_cbv = { base_shader_register = 1, register_space = 31 },
            yzflip_mode = FlipMode.YZFlipNone,
            shader_model_max = dxil_shader_model.SHADER_MODEL_6_2,
        };
        var debug = new DebugOptions();
        var logger = new DXILSpirvLogger { log = &IgnoreLog };
        var specializations = stackalloc Specialization[flagCount];
        for (int k = 0; k < flagCount; k++)
            specializations[k] = new Specialization { id = (uint)k, value = new SpecializationValue { u32 = (mask >> k) & 1 } };
        var sizes = new List<int>();
        fixed (uint* w = words)
        {
            foreach (var entryPoint in entryPoints)
            {
                var stage = entryPoint.Stage switch
                {
                    ShaderStage.Vertex => Compilers.Direct3D.ShaderStage.DXIL_SPIRV_SHADER_VERTEX,
                    ShaderStage.Pixel => Compilers.Direct3D.ShaderStage.DXIL_SPIRV_SHADER_FRAGMENT,
                    _ => throw new NotSupportedException(entryPoint.Stage.ToString()),
                };
                if (!Spv2DXIL.spirv_to_dxil(w, words.Length, specializations, (uint)flagCount, stage, entryPoint.Name, ValidatorVersion.DXIL_VALIDATOR_1_4, ref debug, ref conf, ref logger, out var dxil))
                    throw new InvalidOperationException($"spirv_to_dxil failed for {entryPoint.Name}");
                sizes.Add((int)dxil.size);
                spirv_to_dxil_free(ref dxil);
            }
        }
        return sizes;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Timing

    sealed record Timing(double Median, double P90, int Runs)
    {
        public override string ToString() => $"{Median:F2}";
    }

    static Timing Time(int warmup, int runs, Action action)
    {
        for (int i = 0; i < warmup; i++)
            action();
        var samples = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return new Timing(samples[runs / 2], samples[Math.Min(runs - 1, (int)Math.Ceiling(runs * 0.9) - 1)], runs);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Tests

    [Fact]
    public void SpecializationStructMatchesMesa()
    {
        Assert.Equal(24, Marshal.SizeOf<Specialization>());
        Assert.Equal(8, (int)Marshal.OffsetOf<Specialization>(nameof(Specialization.value)));
        Assert.Equal(16, (int)Marshal.OffsetOf<Specialization>(nameof(Specialization.defined_on_module)));
    }

    /// <summary>
    /// Quick check of the whole chain on two variants; prints sizes.
    /// </summary>
    [Fact(Explicit = true)]
    public void Smoke()
    {
        const int flagCount = 5;
        var loader = NewLoader();
        var platform = GraphicsPlatform.Vulkan;
        Console.WriteLine($"Base entry: {BaseEntry.Value.Id} ({BaseEntry.Value.Profile})");

        var uber = Mix(ForwardTree(platform, UberGenerics(flagCount)), platform, loader);
        var spec = MakeSpecConstants(uber.Words, flagCount);
        Console.WriteLine($"uber: {uber.Words.Length} words, {SentinelCopies} sentinel copies, entry points {string.Join(", ", uber.EntryPoints.Select(e => $"{e.Name}:{e.Stage}"))}");
        Assert.Null(SpirvTools.Validate(spec, SpirvTools.TargetEnv.Vulkan_1_3));

        foreach (var mask in new uint[] { 0, 0b11111, 0b00101 })
        {
            var separate = Mix(ForwardTree(platform, SeparateGenerics(mask)), platform, loader);
            var frozen = Freeze(spec, mask);
            var frozenOpt = FreezeWithSpirvOpt(spec, mask, flagCount);
            var a = OptimizedText(separate.Words);
            var b = OptimizedText(frozen);
            var c = OptimizedText(frozenOpt);
            Console.WriteLine($"mask {mask:b5}: separate {separate.Words.Length} words -> {InstructionCount(a)} optimized; frozen -> {InstructionCount(b)}; spirv-opt frozen -> {InstructionCount(c)}; same: {a == b} / {b == c}");
            if (a != b)
            {
                Directory.CreateDirectory(OutputDirectory);
                File.WriteAllText(Path.Combine(OutputDirectory, $"smoke-{mask}-separate.spvasm"), a);
                File.WriteAllText(Path.Combine(OutputDirectory, $"smoke-{mask}-frozen.spvasm"), b);
            }
        }
    }
}
