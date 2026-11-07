// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Stride.Shaders.Parsing.SDFX;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// The keys generator (<see cref="EffectGenerator"/>) on a shader, then the C# compiler on what it generates: the keys build.
/// </summary>
public class KeysGeneratorTests
{
    // A key of a struct type needs a C# struct of the same name (written by hand, like DirectionalLightData); static and
    // groupshared members are not parameters
    [Fact]
    public void GeneratedKeysBuild()
    {
        var shader = """
            namespace Stride.Shaders.Tests.Keys;

            shader KeysGeneratorCheck
            {
                struct KeysWithCSharpType
                {
                    float4 A;
                };

                struct KeysWithoutCSharpType
                {
                    float4 B;
                };

                static KeysWithoutCSharpType StaticStruct;
                groupshared float4 Shared[4];
                KeysWithoutCSharpType GlobalStruct;

                cbuffer PerDraw
                {
                    KeysWithCSharpType WithType;
                    KeysWithoutCSharpType WithoutType;
                    float4 Plain;
                };
            };
            """;
        var csharp = """
            namespace Stride.Shaders.Tests.Keys
            {
                public struct KeysWithCSharpType
                {
                    public Stride.Core.Mathematics.Vector4 A;
                }
            }
            """;

        var (generated, errors) = Build(shader, csharp);

        Assert.Empty(errors);
        Assert.Contains("ValueParameterKey<KeysWithCSharpType> WithType", generated);
        Assert.Contains("ValueParameterKey<Vector4> Plain", generated);
        Assert.Contains("// No key for WithoutType: no C# type KeysWithoutCSharpType for its struct type", generated);
        Assert.Contains("// No key for GlobalStruct: no C# type KeysWithoutCSharpType for its struct type", generated);
        Assert.DoesNotContain("StaticStruct", generated);
        Assert.DoesNotContain("Shared", generated);
    }

    // An initializer list as a default value: the C# constructor takes the same values in the same order
    [Fact]
    public void InitializerListDefaultsBuild()
    {
        var shader = """
            namespace Stride.Shaders.Tests.Keys;

            shader KeysGeneratorCheck
            {
                cbuffer PerDraw
                {
                    float3 Direction = { 0, -1.5, 1 };
                    int2 Cell = { 1, -2 };
                    float4x4 Transform = { {1, 0, 0, 0}, {0, 1, 0, 0}, {0, 0, 1, 0}, {0, 0, 0, 1} };
                    float2 NotLiteral = { Cell.x, 0 };
                };
            };
            """;

        var (generated, errors) = Build(shader, "");

        Assert.Empty(errors);
        Assert.Contains("ParameterKeys.NewValue<Vector3>(new Vector3(0f, -1.5f, 1f))", generated);
        Assert.Contains("ParameterKeys.NewValue<Int2>(new Int2(1, -2))", generated);
        Assert.Contains("ParameterKeys.NewValue<Matrix>(new Matrix(1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f))", generated);
        // A default the key can't hold: none
        Assert.Contains("ParameterKeys.NewValue<Vector2>()", generated);
    }

    [Fact]
    public void PreprocessorErrorNamesTheFileAndLine()
    {
        var (generated, errors) = Build("shader KeysGeneratorCheck { };\n#error bad thing\n", "");

        Assert.Equal("#error KeysGeneratorCheck.sdsl(2,2): #error bad thing", generated);
        Assert.Contains("KeysGeneratorCheck.sdsl(2,2): #error bad thing", Assert.Single(errors));
    }

    // The key of a member is named after the class: its full name, and the name the mixer gives the member
    [Theory]
    [InlineData("namespace A.B;", "A.B.KeysGeneratorCheck.Plain")]
    [InlineData("", "KeysGeneratorCheck.Plain")]
    public void KeyNamesAreFullNames(string @namespace, string keyName)
    {
        var shader = $$"""
            {{@namespace}}

            shader KeysGeneratorCheck
            {
                cbuffer PerDraw
                {
                    float4 Plain;
                };
            };
            """;

        var (generated, errors) = Build(shader, "");

        Assert.Empty(errors);
        Assert.Contains($"\"{keyName}\"", generated);
    }

    [Theory]
    [InlineData("namespace A.B;", "A.B.KeysGeneratorCheck")]
    [InlineData("", "KeysGeneratorCheck")]
    public void AShaderWithNoKeyHasItsFullName(string @namespace, string fullName)
    {
        var (generated, errors) = Build($$"""
            {{@namespace}}

            shader KeysGeneratorCheck { float4 Get() { return 1; } };
            """, "");

        Assert.Empty(errors);
        Assert.Contains($"public const string ShaderName = \"{fullName}\";", generated);
        Assert.DoesNotContain("RunClassConstructor", generated);
    }

    [Fact]
    public void AShaderWithKeysHasItsFullNameAndRunsItsConstructor()
    {
        var (generated, errors) = Build("""
            namespace A.B;

            shader KeysGeneratorCheck { cbuffer PerDraw { float4 Plain; }; };
            """, "");

        Assert.Empty(errors);
        Assert.Contains("public const string ShaderName = \"A.B.KeysGeneratorCheck\";", generated);
        Assert.Contains("RunClassConstructor", generated);
    }

    [Fact]
    public void AKeysClassWrittenByHandWithoutPartialKeepsItsName()
    {
        var (generated, errors) = Build("""
            namespace A.B;

            shader KeysGeneratorCheck { float4 Get() { return 1; } };
            """, "namespace A.B { public static class KeysGeneratorCheckKeys { } }");

        Assert.Empty(errors);
        Assert.DoesNotContain("ShaderName", generated);
    }

    [Fact]
    public void AMemberNamedShaderNameWarnsAndGivesNoHandle()
    {
        var (generated, errors) = Build("""
            namespace A.B;

            shader KeysGeneratorCheck { cbuffer PerDraw { float4 ShaderName; }; };
            """, "");

        Assert.Empty(errors);
        Assert.Contains("#warning KeysGeneratorCheck has a member named ShaderName", generated);
        Assert.DoesNotContain("public const string ShaderName", generated);
    }

    // The name in a mixin resolves as in a shader: its namespace, its parents, global, usings
    [Theory]
    [InlineData("namespace A.B;", "Sibling", "A.B", "A.B.Sibling")]
    [InlineData("namespace A.B;", "Sibling", "A", "A.Sibling")]
    [InlineData("namespace A.B;", "Sibling", "", "Sibling")]
    [InlineData("using C.D;\nnamespace A.B;", "Sibling", "C.D", "C.D.Sibling")]
    [InlineData("namespace A.B;", "Sibling", "C.D", "Sibling")]
    public void MixinNamesResolveWithTheScope(string header, string mixin, string siblingNamespace, string expected)
    {
        var sibling = siblingNamespace.Length > 0 ? $"namespace {siblingNamespace};\n" : "";
        var effect = $$"""
            {{header}}

            effect KeysGeneratorEffect
            {
                mixin {{mixin}};
            };
            """;

        var (generated, errors) = Build([("KeysGeneratorEffect.sdfx", effect), ("Sibling.sdsl", $"{sibling}shader Sibling {{ }};")], "");

        Assert.Empty(errors);
        Assert.Contains($"context.Mixin(mixin, \"{expected}\")", generated);
    }

    // A key class alone doesn't make a shader: effects have hand-written ones
    [Fact]
    public void AMixinNameThatOnlyHasAKeysClassStaysAsItIsWritten()
    {
        var (generated, errors) = Build([("KeysGeneratorEffect.sdfx", "namespace A;\neffect KeysGeneratorEffect { mixin Sibling; };")], "namespace A { public static class SiblingKeys { } }");

        Assert.Empty(errors);
        Assert.Contains("context.Mixin(mixin, \"Sibling\")", generated);
    }

    // The key class of a shader with no namespace, from another assembly, is in the default namespace of the generated code
    [Fact]
    public void AMixinNameOfAGlobalShaderOfAReferenceStaysAsItIsWritten()
    {
        var (generated, errors) = Build([("KeysGeneratorEffect.sdfx", "namespace A;\neffect KeysGeneratorEffect { mixin Sibling; };")], """
            namespace Stride.Rendering
            {
                public static class ShaderImplicitUsings { public const string Namespaces = "Stride.Rendering"; }
                public partial class SiblingKeys { public const string ShaderName = "Sibling"; }
            }
            """);

        Assert.Empty(errors);
        Assert.Contains("context.Mixin(mixin, \"Sibling\")", generated);
    }

    [Fact]
    public void AMixinNameFoundInTwoUsingsStaysAsItIsWritten()
    {
        var (generated, errors) = Build([
            ("KeysGeneratorEffect.sdfx", "using C;\nusing D;\nnamespace A;\neffect KeysGeneratorEffect { mixin Sibling; };"),
            ("Sibling1.sdsl", "namespace C;\nshader Sibling { };"),
            ("Sibling2.sdsl", "namespace D;\nshader Sibling { };"),
        ], "");

        Assert.Empty(errors);
        Assert.Contains("context.Mixin(mixin, \"Sibling\")", generated);
    }

    [Theory]
    [InlineData("namespace A.B;", "A.B.KeysGeneratorEffect")]
    [InlineData("", "KeysGeneratorEffect")]
    public void AnEffectHasItsFullName(string @namespace, string fullName)
    {
        var (generated, errors) = Build([("KeysGeneratorEffect.sdfx", $"{@namespace}\neffect KeysGeneratorEffect {{ mixin Sibling; }};")], "");

        Assert.Empty(errors);
        Assert.Contains($"public const string EffectName = \"{fullName}\";", generated);
        Assert.Contains($"ShaderMixinManager.Register(\"{fullName}\"", generated);
    }

    // A mixin of an effect is resolved; the name of a child stays one segment
    [Fact]
    public void MixinChildResolvesItsTargetOnly()
    {
        var (generated, errors) = Build([
            ("KeysGeneratorEffect.sdfx", "namespace A.B;\neffect KeysGeneratorEffect { mixin child Other; };"),
            ("Other.sdfx", "namespace A;\neffect Other { mixin Sibling; };"),
        ], "");

        Assert.Empty(errors);
        Assert.Contains("context.ChildEffectName == \"Other\"", generated);
        Assert.Contains("context.Mixin(mixin, \"A.Other\")", generated);
    }

    // A shader namespace doesn't have to exist in C#
    [Fact]
    public void UsingOfANamespaceCSharpDoesNotKnowBuilds()
    {
        var shader = """
            using Only.Shaders.Here;
            using Stride.Core.Mathematics;

            namespace Stride.Shaders.Tests.Keys;

            shader KeysGeneratorCheck
            {
                cbuffer PerDraw
                {
                    float4 Plain;
                };
            };
            """;

        var (generated, errors) = Build(shader, "");

        Assert.Empty(errors);
        Assert.DoesNotContain("using Only.Shaders.Here;", generated);
        Assert.Contains("using Stride.Core.Mathematics;", generated);
    }

    // An enum of the shaders has no C# type: its member has no key, and a C# type of the same name isn't taken for it
    [Fact]
    public void EnumMemberHasNoKey()
    {
        var shader = """
            namespace Stride.Shaders.Tests.Keys;

            shader KeysGeneratorCheck
            {
                enum class Blend : uint { Off, On };
                struct Pick { Blend blend; float weight; };
                cbuffer PerDraw
                {
                    Blend Mode;
                    float Weight;
                };
            };
            """;

        var (generated, errors) = Build(shader, "");

        Assert.Empty(errors);
        Assert.Contains("// No key for Mode: the enum Blend has no C# type", generated);
        Assert.Contains("ValueParameterKey<float> Weight", generated);
    }

    // A texture array has one key, set with an array of textures
    [Fact]
    public void TextureArrayKeyBuilds()
    {
        var shader = """
            namespace Stride.Shaders.Tests.Keys;

            shader KeysGeneratorCheck
            {
                static const int Count = 4;
                rgroup PerMaterial
                {
                    Texture2D Textures[Count];
                }
            };
            """;

        var (generated, errors) = Build(shader, "");

        Assert.Empty(errors);
        Assert.Contains("ObjectParameterKey<Texture> Textures", generated);
    }

    // Runs the generator on the shader and compiles its output with the C# source
    private static (string Generated, List<string> Errors) Build(string shader, string csharp) => Build([("KeysGeneratorCheck.sdsl", shader)], csharp);

    private static (string Generated, List<string> Errors) Build((string Path, string Text)[] files, string csharp)
    {
        // The assemblies the generated keys use
        var references = new[] { typeof(object), typeof(Stride.Rendering.ParameterKeys), typeof(Stride.Core.Mathematics.Vector4), typeof(Stride.Graphics.Texture), typeof(Stride.Core.DataContractAttribute), typeof(Stride.Shaders.ShaderMixinSource), typeof(Stride.Shaders.ShaderMixinContext) }
            .Select(x => x.Assembly.Location)
            .Concat(Directory.EnumerateFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll").Where(x => Path.GetFileName(x) is "System.Runtime.dll" or "netstandard.dll" or "System.Collections.dll"))
            .Distinct()
            .Select(x => MetadataReference.CreateFromFile(x))
            .ToList();
        var compilation = CSharpCompilation.Create("KeysGeneratorCheck", [CSharpSyntaxTree.ParseText(csharp)], references, new(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new EffectGenerator().AsSourceGenerator()], additionalTexts: [.. files.Select(x => new ShaderText(x.Path, x.Text))]);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var generated = string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(x => x.ToString()));
        var errors = output.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Select(x => x.ToString()).ToList();
        return (generated, errors);
    }

    private sealed class ShaderText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text, Encoding.UTF8);
    }
}
