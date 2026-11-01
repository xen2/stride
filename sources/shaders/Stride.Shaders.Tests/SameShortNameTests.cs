// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.IO;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders.Compiler;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Two classes with one short name in one effect: the names in the generated code come from the short name, and they must still compile.
/// </summary>
public class SameShortNameTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"short-src-{Guid.NewGuid():N}"));
    private readonly FileSystemProvider sourceProvider = new($"/shorttest-{Guid.NewGuid():N}", AppContext.BaseDirectory);
    private readonly EffectCompiler compiler;

    public SameShortNameTests()
    {
        compiler = new EffectCompiler(sourceProvider) { ImplicitUsings = ShaderImplicitUsings.List };
        compiler.SourceDirectories.Add(sourceDir.Name);
        compiler.SourceDirectories.Add("assets/Stride/SDSL");
    }

    public void Dispose()
    {
        compiler.Dispose();
        sourceProvider.Dispose();
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string name, string code) => File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), code);

    private EffectBytecode Compile(string root, GraphicsPlatform platform)
    {
        var parameters = new CompilerParameters { EffectParameters = EffectCompilerParameters.Default with { Platform = platform } };
        var results = compiler.Compile(new ShaderClassSource(TestShader.FullName(root)), parameters);
        Assert.False(results.HasErrors, string.Join(Environment.NewLine, results.Messages.Select(m => m.Text)));
        var completed = results.Bytecode.WaitForResult();
        Assert.False(completed.CompilationLog.HasErrors, string.Join(Environment.NewLine, completed.CompilationLog.Messages.Select(m => m.Text)));
        return completed.Bytecode;
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
    public void TwoClassesOfOneShortNameInCompositions(GraphicsPlatform platform)
    {
        Write("Foo", """
            namespace A
            {
                shader Foo
                {
                    Texture2D Tex;
                    SamplerState Sampler;
                    cbuffer PerDraw { float4 Color; };
                    struct Data { float4 value; };
                    float4 Get(float2 uv) { Data data; data.value = Tex.Sample(Sampler, uv) * Color; return data.value; }
                };
            }
            namespace B
            {
                shader Foo
                {
                    Texture2D Tex;
                    SamplerState Sampler;
                    cbuffer PerDraw { float4 Color; };
                    struct Data { float4 value; float4 other; };
                    float4 Get(float2 uv) { Data data; data.value = Tex.Sample(Sampler, uv) + Color; data.other = data.value; return data.other; }
                };
            }
            """);
        Write("Root", """
            shader Root : SpriteBase
            {
                compose A.Foo first;
                compose B.Foo second;
                module override float4 Shading() { return first.Get(streams.TexCoord) + second.Get(streams.TexCoord); }
            };
            """);

        var bytecode = Compile("Root", platform);
        // The two textures are two resources: each one reaches its own binding
        var textures = bytecode.Reflection.ResourceBindings.Where(x => x.Class == EffectParameterClass.ShaderResourceView).Select(x => x.KeyInfo.KeyName).Distinct().Order();
        Assert.Equal(["A.Foo.Tex.first", "B.Foo.Tex.second"], textures);
    }

    // A module member has one value per effect: no composition path in its key
    [Theory]
    [MemberData(nameof(Platforms))]
    public void TwoClassesOfOneShortNameWithAModuleMemberInCompositions(GraphicsPlatform platform)
    {
        Write("Foo", """
            namespace A { shader Foo { module Texture2D Tex; SamplerState Sampler; float4 Get(float2 uv) { return Tex.Sample(Sampler, uv); } }; }
            namespace B { shader Foo { module Texture2D Tex; SamplerState Sampler; float4 Get(float2 uv) { return Tex.Sample(Sampler, uv) * 2; } }; }
            """);
        Write("Root", """
            shader Root : SpriteBase
            {
                compose A.Foo first;
                compose B.Foo second;
                module override float4 Shading() { return first.Get(streams.TexCoord) + second.Get(streams.TexCoord); }
            };
            """);

        var bytecode = Compile("Root", platform);
        var textures = bytecode.Reflection.ResourceBindings.Where(x => x.Class == EffectParameterClass.ShaderResourceView).Select(x => x.KeyInfo.KeyName).Distinct().Order();
        Assert.Equal(["A.Foo.Tex", "B.Foo.Tex"], textures);
    }

    // [Link] is how two classes share a key
    [Theory]
    [MemberData(nameof(Platforms))]
    public void TwoClassesOfOneShortNameCanShareAKeyWithLink(GraphicsPlatform platform)
    {
        Write("Foo", """
            namespace A { shader Foo { [Link("Shared.Tex")] Texture2D TexA; SamplerState SamplerA; float4 GetA(float2 uv) { return TexA.Sample(SamplerA, uv); } }; }
            namespace B { shader Foo { [Link("Shared.Tex")] Texture2D TexB; SamplerState SamplerB; float4 GetB(float2 uv) { return TexB.Sample(SamplerB, uv); } }; }
            """);
        Write("Root", """
            shader Root : SpriteBase, A.Foo, B.Foo
            {
                module override float4 Shading() { return GetA(streams.TexCoord) + GetB(streams.TexCoord); }
            };
            """);

        var bytecode = Compile("Root", platform);
        var textures = bytecode.Reflection.ResourceBindings.Where(x => x.Class == EffectParameterClass.ShaderResourceView).ToList();
        Assert.Equal(["Shared.Tex"], textures.Select(x => x.KeyInfo.KeyName).Distinct());
        Assert.Equal(2, textures.Select(x => x.RawName).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void TwoClassesOfOneShortNameAtTheRoot(GraphicsPlatform platform)
    {
        Write("Foo", """
            namespace A { shader Foo { Texture2D TexA; SamplerState SamplerA; float4 GetA(float2 uv) { return TexA.Sample(SamplerA, uv) * Helper(); } float4 Helper() { return 1; } }; }
            namespace B { shader Foo { Texture2D TexB; SamplerState SamplerB; float4 GetB(float2 uv) { return TexB.Sample(SamplerB, uv) + Helper(); } float4 Helper() { return 2; } }; }
            """);
        Write("Root", """
            shader Root : SpriteBase, A.Foo, B.Foo
            {
                module override float4 Shading() { return GetA(streams.TexCoord) + GetB(streams.TexCoord); }
            };
            """);

        Compile("Root", platform);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void TwoClassesOfOneShortNameAtTheRootWithOneMemberName(GraphicsPlatform platform)
    {
        Write("Foo", """
            namespace A { shader Foo { Texture2D Tex; SamplerState Sampler; float4 Value; float4 GetA(float2 uv) { return Tex.Sample(Sampler, uv) * Value; } }; }
            namespace B { shader Foo { Texture2D Tex; SamplerState Sampler; float4 Value; float4 GetB(float2 uv) { return Tex.Sample(Sampler, uv) + Value; } }; }
            """);
        Write("Root", """
            shader Root : SpriteBase, A.Foo, B.Foo
            {
                module override float4 Shading() { return GetA(streams.TexCoord) + GetB(streams.TexCoord); }
            };
            """);

        var bytecode = Compile("Root", platform);
        var textures = bytecode.Reflection.ResourceBindings.Where(x => x.Class == EffectParameterClass.ShaderResourceView).ToList();
        Assert.Equal(["A.Foo.Tex", "B.Foo.Tex"], textures.Select(x => x.KeyInfo.KeyName).Distinct().Order());
        // Direct3D 11 lists a resource once more per stage
        Assert.True(textures.Select(x => x.RawName).Distinct().Count() == 2 && textures.Select(x => x.SlotStart).Distinct().Count() == 2,
            string.Join(", ", textures.Select(x => $"{x.KeyInfo.KeyName} {x.RawName} {x.SlotStart} {x.Stage}")));
        var values = bytecode.Reflection.ConstantBuffers.SelectMany(x => x.Members).Select(x => x.KeyInfo.KeyName).Where(x => x.EndsWith(".Value", StringComparison.Ordinal)).Order();
        Assert.Equal(["A.Foo.Value", "B.Foo.Value"], values);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void TwoModulesOfOneShortName(GraphicsPlatform platform)
    {
        Write("Util", """
            namespace A { [Pure] module Util { float4 Scale(float4 x) { return x * 2; } }; }
            namespace B { [Pure] module Util { float4 Scale(float4 x) { return x * 3; } }; }
            """);
        Write("Root", """
            shader Root : SpriteBase
            {
                module override float4 Shading() { return A.Util.Scale(1) + B.Util.Scale(1); }
            };
            """);

        Compile("Root", platform);
    }
}
