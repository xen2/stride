// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Runtime.InteropServices;
using Stride.Core.IO;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders.Compiler;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Texture arrays through the effect compiler. On Direct3D 11, FXC indexes a texture array with a constant only: a literal, or a
/// generic once the methods are inlined; any other index is error SDSL0268. On Vulkan, texture arrays are in descriptor set 1.
/// </summary>
public class TextureArrayEffectTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"array-src-{Guid.NewGuid():N}"));
    private readonly FileSystemProvider sourceProvider = new($"/arraytest-{Guid.NewGuid():N}", AppContext.BaseDirectory);
    private readonly EffectCompiler compiler;

    public TextureArrayEffectTests()
    {
        compiler = new EffectCompiler(sourceProvider) { ImplicitUsings = ShaderImplicitUsings.List };
        compiler.SourceDirectories.Add(sourceDir.Name);
        compiler.SourceDirectories.Add("assets/Stride/SDSL");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "ArrayShape.sdsl"), """
            shader ArrayShape<int Slot> : SpriteBase
            {
                rgroup PerMaterial { Texture2D Textures[4]; }
                SamplerState Point;
                cbuffer PerDraw { uint Index; };

                float4 Read(int slot) { return Textures[slot].Sample(Point, streams.TexCoord); }

                module override float4 Shading()
                {
                    return Read(Slot) + Textures[3].Sample(Point, streams.TexCoord);
                }
            };
            """);
        File.WriteAllText(Path.Combine(sourceDir.FullName, "ArrayRuntimeIndex.sdsl"), """
            shader ArrayRuntimeIndex : SpriteBase
            {
                rgroup PerMaterial { Texture2D Textures[4]; }
                SamplerState Point;
                cbuffer PerDraw { uint Index; };

                module override float4 Shading()
                {
                    return Textures[Index].Sample(Point, streams.TexCoord);
                }
            };
            """);
    }

    public void Dispose()
    {
        compiler.Dispose();
        sourceProvider.Dispose();
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    public static TheoryData<GraphicsPlatform> Platforms()
    {
        var platforms = new TheoryData<GraphicsPlatform> { GraphicsPlatform.Vulkan };
        if (OperatingSystem.IsWindows())
            platforms.Add(GraphicsPlatform.Direct3D11);
        return platforms;
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void ConstantIndicesCompile(GraphicsPlatform platform)
    {
        var (bytecode, log) = Compile(new ShaderClassSource(TestShader.FullName("ArrayShape"), 1), platform);
        Assert.True(bytecode != null, log);

        // On Direct3D 11, FXC reports elements 1 and 3, each with the base slot of the array
        var entry = bytecode.Reflection.ResourceGroups.Single(x => x.Name == "PerMaterial").Entries.Single(x => x.RawName.EndsWith("Textures"));
        Assert.Equal(4, entry.SlotCount);
        if (platform == GraphicsPlatform.Direct3D11)
        {
            Assert.True(entry.Stages.HasFlag(ShaderStageFlags.Pixel), entry.ToString());
            var pixelSlots = bytecode.Reflection.ResourceBindings.Where(x => x.KeyInfo.KeyName == entry.KeyInfo.KeyName && x.Stage == ShaderStage.Pixel).Select(x => x.SlotStart - entry.SlotStart).Order();
            Assert.Equal([1, 3], pixelSlots);
        }

        // On Vulkan, the array is in descriptor set 1, and the sampler and the cbuffer in set 0
        if (platform == GraphicsPlatform.Vulkan)
        {
            var words = MemoryMarshal.Cast<byte, uint>(bytecode.Stages[0].Data);
            var sets = new List<uint>();
            for (var w = 5; w < words.Length; w += (int)(words[w] >> 16))
            {
                if ((Op)(words[w] & 0xFFFF) == Op.OpDecorate && words[w + 2] == (uint)Decoration.DescriptorSet)
                    sets.Add(words[w + 3]);
            }
            Assert.Equal([0u, 0u, 1u], sets.Order());
        }
    }

    [Fact]
    public void RuntimeIndexIsAnErrorOnDirect3D11()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Direct3D 11 compiles on Windows only");

        var (bytecode, log) = Compile(new ShaderClassSource(TestShader.FullName("ArrayRuntimeIndex")), GraphicsPlatform.Direct3D11);
        Assert.Null(bytecode);
        Assert.Contains("ArrayRuntimeIndex.sdsl(9,", log);
        Assert.Contains("SDSL0268: On Direct3D 11, a texture array index must be a constant once the methods are inlined", log);

        var (vulkanBytecode, vulkanLog) = Compile(new ShaderClassSource(TestShader.FullName("ArrayRuntimeIndex")), GraphicsPlatform.Vulkan);
        Assert.True(vulkanBytecode != null, vulkanLog);
    }

    private (EffectBytecode? Bytecode, string Log) Compile(ShaderClassSource root, GraphicsPlatform platform)
    {
        var parameters = new CompilerParameters { EffectParameters = EffectCompilerParameters.Default with { Platform = platform } };
        var results = compiler.Compile(new EffectSource(root), parameters);
        if (results.HasErrors)
            return (null, string.Join(Environment.NewLine, results.Messages.Select(m => m.Text)));
        var completed = results.Bytecode.WaitForResult();
        return (completed.CompilationLog.HasErrors ? null : completed.Bytecode, string.Join(Environment.NewLine, completed.CompilationLog.Messages.Select(m => m.Text)));
    }
}
