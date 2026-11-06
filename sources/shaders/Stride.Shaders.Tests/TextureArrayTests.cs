// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Texture arrays: a fixed-size array of read-only textures in an rgroup. Every other resource array is an error with its code
/// (SDSL0259 to SDSL0266).
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
    [InlineData("rgroup PerMaterial { Texture2D Textures[4]; }", "SDSL0259: Textures is a texture array, and texture arrays are not supported yet.")]
    public void TextureArrayInRGroupIsNotSupportedYet(string declaration, string expected)
        => AssertDeclarationError(declaration, expected);

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
        File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), code);
        return Assert.Throws<InvalidOperationException>(() => new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL").LoadExternalBuffer(TestShader.FullName(name), [], out _, out _, out _)).Message;
    }
}
