// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using CommunityToolkit.HighPerformance.Buffers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Stride.Shaders.Compilers.SDSL;
using Spv = Stride.Shaders.Spirv.Tools.Spv;

namespace Stride.Shaders.Parsers.Tests;

public partial class RenderingTests
{
    /// <summary>
    /// On Vulkan, a texture array is indexed with a value that is the same for the whole draw, here from a cbuffer. Direct3D 11
    /// needs a constant index: the RenderTest <c>TextureArray</c> renders literal indices on both.
    /// </summary>
    [Theory]
    [InlineData(0, "#01000000")]
    [InlineData(2, "#00020000")]
    [InlineData(3, "#00000300")]
    public void TextureArrayIndexedWithAValue(int index, string expected)
    {
        SkipUnlessBackendAvailable(RendererBackend.Vulkan);

        var sourceDir = Directory.CreateTempSubdirectory("stride-texture-array-render");
        try
        {
            File.WriteAllText(Path.Combine(sourceDir.FullName, "TextureArrayIndex.sdsl"), """
                shader TextureArrayIndex
                {
                    stream float4 ColorTarget : SV_Target0;
                    stream float2 TexCoord : TEXCOORD;

                    rgroup PerMaterial
                    {
                        module Texture2D<float4> Textures[4];
                        module SamplerState Sampler1;
                    }
                    cbuffer PerDraw { uint Index; };

                    void PSMain()
                    {
                        streams.ColorTarget = Textures[Index].Sample(Sampler1, streams.TexCoord);
                    }
                }
                """);

            var shaderMixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName));
            shaderMixer.ShaderLoader.LoadExternalBuffer("TextureArrayIndex", [], out _, out _, out _);
            var log = new Stride.Core.Diagnostics.LoggerResult();
            shaderMixer.MergeSDSL(new ShaderClassSource("TextureArrayIndex"), GetMixerOptions(RendererBackend.Vulkan), log, out var bytecode, out var effectReflection, out _, out _);
            Assert.False(log.HasErrors, string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)));

            var validation = Spv.ValidateBinary(bytecode, targetVulkan: true);
            Assert.True(validation.IsValid, validation.Output);

            var renderer = new VulkanFrameRenderer((uint)width, (uint)height, bytecode.ToArray(), quadVsSpirv.Value) { EffectReflection = effectReflection };
            SetupTestParameters(renderer, new()
            {
                ["texture.Textures"] = "#01000000 #00010000 #00020000 #00000300",
                ["cbuffer.PerDraw"] = $"(Index={index})",
            });

            using var frameBuffer = MemoryOwner<byte>.Allocate(width * height * 4);
            renderer.SetupTest();
            renderer.RenderFrame(frameBuffer.Span);
            renderer.PresentAndFinish();
            var pixel = Image.LoadPixelData<Rgba32>(frameBuffer.Span, width, height)[0, 0].PackedValue;
            pixel = ((pixel & 0x000000FF) << 24) | (pixel & 0x0000FF00) << 8 | ((pixel & 0x00FF0000) >> 8) | (pixel & 0xFF000000) >> 24;

            Assert.Equal(StringToRgba(expected).ToString("X8"), pixel.ToString("X8"));
        }
        finally
        {
            sourceDir.Delete(recursive: true);
        }
    }
}
