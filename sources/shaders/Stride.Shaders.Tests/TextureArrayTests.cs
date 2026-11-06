// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Shaders.Compilers.SDSL;
using Spv = Stride.Shaders.Spirv.Tools.Spv;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Texture arrays: a fixed-size array of read-only textures in an rgroup, one slot per element. Every other resource array is an
/// error with its code (SDSL0260 to SDSL0266).
/// </summary>
public class TextureArrayTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateTempSubdirectory("stride-texture-arrays");

    public void Dispose()
    {
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(RendererBackend.Direct3D11, "Texture2D", "Textures[Index].Sample(Point, streams.TexCoord)")]
    [InlineData(RendererBackend.Vulkan, "Texture2D", "Textures[Index].Sample(Point, streams.TexCoord)")]
    [InlineData(RendererBackend.Vulkan, "Texture2D<float4>", "Textures[2].Sample(Point, streams.TexCoord)")]
    [InlineData(RendererBackend.Vulkan, "Texture2D", "Pick(Textures[Index])")]
    [InlineData(RendererBackend.Vulkan, "TextureCube", "Textures[Index].Sample(Point, float3(streams.TexCoord, 1))")]
    [InlineData(RendererBackend.Vulkan, "Texture2DArray", "Textures[Index].Sample(Point, float3(streams.TexCoord, 1))")]
    public void TextureArrayTakesOneSlotPerElement(RendererBackend backend, string type, string expression)
    {
        Write("ArrayMix", $$"""
            shader ArrayMix
            {
                stream float4 ColorTarget : SV_Target0;
                stream float2 TexCoord : TEXCOORD0;

                static const int Count = 4;
                rgroup PerMaterial
                {
                    {{type}} Textures[Count];
                    Texture2D After;
                }
                SamplerState Point;
                cbuffer PerDraw { uint Index; };

                float4 Pick(Texture2D t) { return t.Sample(Point, streams.TexCoord); }

                void PSMain()
                {
                    streams.ColorTarget = {{expression}} + After.Sample(Point, streams.TexCoord);
                }
            };
            """);

        var (ok, log, bytecode, reflection) = Mix("ArrayMix", backend);
        Assert.True(ok, log);

        var textures = reflection.ResourceBindings.Single(x => x.RawName.EndsWith("Textures"));
        var after = reflection.ResourceBindings.Single(x => x.RawName.EndsWith("After"));
        Assert.Equal(4, textures.SlotCount);
        Assert.Equal(textures.SlotStart + 4, after.SlotStart);
        var entry = reflection.ResourceGroups.Single(x => x.Name == "PerMaterial").Entries.Single(x => x.RawName.EndsWith("Textures"));
        Assert.Equal(4, entry.SlotCount);

        var validation = Spv.ValidateBinary(bytecode, targetVulkan: backend == RendererBackend.Vulkan);
        Assert.True(validation.IsValid, validation.Output);
    }

    [Theory]
    [InlineData("Texture2D Textures[4];", "SDSL0260: Textures is a texture array outside an rgroup. A texture array is bound as one block: declare it in an rgroup, for example rgroup Textures { Texture2D Textures[4]; }.")]
    [InlineData("module Texture2D<float4> Textures[4];", "SDSL0260: Textures is a texture array outside an rgroup.")]
    [InlineData("static Texture2D Textures[4];", "SDSL0260: Textures is a texture array outside an rgroup.")]
    [InlineData("cbuffer PerDraw { Texture2D Textures[4]; }", "SDSL0260: Textures is a texture array outside an rgroup.")]
    [InlineData("float4 Pick() { Texture2D local[2]; return 0; }", "SDSL0260: local is a texture array outside an rgroup.")]
    public void TextureArrayOutsideAnRGroup(string declaration, string expected)
        => AssertDeclarationError(declaration, expected);

    [Theory]
    [InlineData("rgroup PerMaterial { RWTexture2D<float4> Textures[4]; }", "SDSL0261: Textures is an array of RWTexture2D. Only read-only textures can be in an array")]
    [InlineData("rgroup PerMaterial { Texture2DMS<float4> Textures[4]; }", "SDSL0261: Textures is an array of Texture2DMS.")]
    [InlineData("rgroup PerMaterial { Buffer<float4> Values[4]; }", "SDSL0261: Values is an array of Buffer.")]
    [InlineData("rgroup PerMaterial { StructuredBuffer<float4> Values[4]; }", "SDSL0261: Values is an array of StructuredBuffer.")]
    [InlineData("rgroup PerMaterial { ByteAddressBuffer Values[4]; }", "SDSL0261: Values is an array of ByteAddressBuffer.")]
    [InlineData("rgroup PerMaterial { SamplerState Samplers[2]; }", "SDSL0261: Samplers is an array of SamplerState. Only read-only textures can be in an array (Texture1D, Texture2D, Texture3D, TextureCube and their Array forms). Declare one SamplerState per element.")]
    [InlineData("SamplerState Samplers[2];", "SDSL0261: Samplers is an array of SamplerState.")]
    [InlineData("module SamplerComparisonState Samplers[2];", "SDSL0261: Samplers is an array of SamplerState.")]
    [InlineData("Buffer<float4> Values[4];", "SDSL0261: Values is an array of Buffer.")]
    public void ArrayOfOtherResources(string declaration, string expected)
        => AssertDeclarationError(declaration, expected);

    [Fact]
    public void ArrayOfArraysOfTextures()
        => AssertDeclarationError("rgroup PerMaterial { Texture2D Textures[2][3]; }", "SDSL0262: Textures is an array of arrays of textures. A texture array has one dimension: write Texture2D Textures[6].");

    [Fact]
    public void UnsizedTextureArrayIsReserved()
        => AssertDeclarationError("rgroup PerMaterial { Texture2D Textures[]; }", "SDSL0263: Textures[] has no size. An unsized texture array is kept for bindless textures");

    [Fact]
    public void TextureArraySizeFromAGeneric()
    {
        var message = CompileError("ArraySizeGeneric", """
            shader ArraySizeGeneric<int Count>
            {
                rgroup PerMaterial { Texture2D Textures[Count]; }
            };
            """);
        Assert.Contains("ArraySizeGeneric.sdsl(3,", message);
        Assert.Contains("SDSL0264: The size of the texture array Textures is not a constant.", message);
    }

    [Fact]
    public void TextureArrayParameter()
        => AssertDeclarationError("float4 PickAll(Texture2D textures[4], uint i) { return 0; }", "SDSL0266: Parameter textures of PickAll is a whole texture array, which is not supported yet. Pass one element instead, such as textures[i], with a Texture2D parameter.");

    [Fact]
    public void TextureArrayInAPureModule()
    {
        var message = CompileError("PureTextures", """
            [Pure]
            module PureTextures
            {
                rgroup PerMaterial { Texture2D Textures[4]; }
            };
            """);
        Assert.Contains("SDSL0209: [Pure] module PureTextures declares PerMaterial", message);
    }

    [Theory]
    [InlineData("Textures[7]", "SDSL0265: Index 7 is outside Textures, which has 4 elements (0 to 3).")]
    [InlineData("Textures[Count]", "SDSL0265: Index Count is outside Textures, which has 4 elements (0 to 3).")]
    [InlineData("Textures[-1]", "SDSL0265: Index -1 is outside Textures")]
    [InlineData("Textures[NonUniformResourceIndex(Index)]", "SDSL0267: NonUniformResourceIndex is not supported yet")]
    public void TextureArrayIndexError(string element, string expected)
    {
        var message = CompileError("ArrayIndex", $$"""
            shader ArrayIndex
            {
                static const int Count = 4;
                rgroup PerMaterial { Texture2D Textures[Count]; }
                SamplerState Point;
                cbuffer PerDraw { uint Index; };
                float4 Read(float2 uv)
                {
                    return {{element}}.Sample(Point, uv);
                }
            };
            """);
        Assert.Contains("ArrayIndex.sdsl(9,", message);
        Assert.Contains(expected, message);
    }

    // The declaration is on line 3 of the shader
    private void AssertDeclarationError(string declaration, string expected)
    {
        var message = CompileError("ArrayDeclaration", $$"""
            shader ArrayDeclaration
            {
                {{declaration}}
            };
            """);
        Assert.Contains("ArrayDeclaration.sdsl(3,", message);
        Assert.Contains(expected, message);
    }

    private string CompileError(string name, string code)
    {
        Write(name, code);
        return Assert.Throws<InvalidOperationException>(() => new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL").LoadExternalBuffer(TestShader.FullName(name), [], out _, out _, out _)).Message;
    }

    private void Write(string name, string code) => File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), code);

    private (bool Ok, string Log, byte[] Bytecode, EffectReflection Reflection) Mix(string shaderName, RendererBackend backend)
    {
        var mixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName));
        mixer.ShaderLoader.LoadExternalBuffer(TestShader.FullName(shaderName), [], out _, out _, out _);
        var log = new LoggerResult();
        var options = backend == RendererBackend.Direct3D11
            ? new ShaderMixer.Options(ResourcesRegisterSeparate: true)
            : new ShaderMixer.Options(ResourcesRegisterSeparate: false, StripGoogleUserType: true, Direct3DStructuredBufferLayout: true);
        var ok = mixer.MergeSDSL(new ShaderClassSource(TestShader.FullName(shaderName)), options, log, out var bytecode, out var reflection, out _, out _);
        return (ok && !log.HasErrors, string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)), bytecode.ToArray(), reflection);
    }
}
