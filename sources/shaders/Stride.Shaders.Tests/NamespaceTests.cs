// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Linq;
using Stride.Shaders.Compilers.SDSL;
using Stride.Shaders.Core;
using Stride.Shaders.Parsing;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Parsing.SDSL.AST;
using Stride.Shaders.Spirv;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;
using Stride.Shaders.Spirv.Core.Buffers;
using Stride.Shaders.Spirv.Tools;
using Spv = Stride.Shaders.Spirv.Tools.Spv;
using Xunit;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Namespaces and <c>using</c> in SDSL, as in C#: the identity of a class is its full name, and a short name is looked up from
/// where it is written.
/// </summary>
public class NamespaceTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"namespace-src-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string name, string code) => File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), code);

    private ShaderLoader Loader(params string[] implicitUsings) => new(sourceDir.FullName, "./assets/Stride/SDSL", "./assets/SDSL/CompilerTests") { ImplicitUsings = implicitUsings };

    // What the class compiled from code named by a short name resolved it to
    private static List<string> Imported(ShaderBuffers buffer)
    {
        var imported = new List<string>();
        foreach (var i in buffer.Context)
        {
            if (i.Op == Specification.Op.OpImportShaderSDSL)
                imported.Add(((OpImportShaderSDSL)i).ShaderName);
        }
        return imported;
    }

    private static string ClassName(ShaderBuffers buffer)
    {
        foreach (var i in buffer.Buffer)
        {
            if (i.Op == Specification.Op.OpShaderSDSL)
                return ((OpShaderSDSL)i).ShaderName;
        }
        return "";
    }

    // Every Target of a test is in this file, so that it has one file name
    private void WriteTargets(params string[] namespaces)
    {
        Write("Target", string.Concat(namespaces.Select(x => x == "" ? "shader Target { };\n" : $"namespace {x} {{ shader Target {{ }}; }}\n")));
    }

    [Fact]
    public void TheIdentityOfAClassIsItsFullName()
    {
        Write("Inside", "namespace A.B; shader Inside { };");
        Write("Outside", "shader Outside { };");

        var loader = Loader();
        Assert.True(loader.LoadExternalBuffer("A.B.Inside", [], out var inside, out _, out _));
        Assert.Equal("A.B.Inside", ClassName(inside));
        Assert.True(loader.LoadExternalBuffer("Outside", [], out var outside, out _, out _));
        Assert.Equal("Outside", ClassName(outside));
    }

    public static IEnumerable<object[]> LookupOrder()
    {
        // Each case: the class's scope, the Targets that exist, the implicit using, the one it finds
        yield return ["namespace A.B { using Z; using F; shader Root : Target { }; }", new[] { "A.B", "Z", "A", "" }, "A.B.Target"];
        yield return ["namespace A.B { using Z; shader Root : Target { }; }", new[] { "Z", "A", "" }, "Z.Target"];
        yield return ["namespace A.B { shader Root : Target { }; }", new[] { "A", "" }, "A.Target"];
        yield return ["namespace A.B { shader Root : Target { }; }", new[] { "", "A.B.C" }, "Target"];
        yield return ["using F; shader Root : Target { };", new[] { "", "F" }, "Target"];
        yield return ["using F; namespace A { shader Root : Target { }; }", new[] { "", "F" }, "Target"];
        yield return ["using F; namespace A { shader Root : Target { }; }", new[] { "F" }, "F.Target"];
        yield return ["namespace A { shader Root : Target { }; }", new[] { "Implicit" }, "Implicit.Target"];
        yield return ["using F; shader Root : Target { };", new[] { "Implicit", "" }, "Target"];
        // A using inside a namespace comes before the parents; one at the root comes after the global namespace
        yield return ["using F; namespace A.B { using Z; shader Root : Target { }; }", new[] { "F", "Z" }, "Z.Target"];
    }

    [Theory]
    [MemberData(nameof(LookupOrder))]
    public void AShortNameIsLookedUpClosestFirst(string root, string[] targets, string expected)
    {
        WriteTargets(targets);
        Write("Root", root);

        var loader = Loader("Implicit");
        Assert.True(loader.LoadExternalBuffer(root.Contains("namespace A.B") ? "A.B.Root" : root.Contains("namespace A") ? "A.Root" : "Root", [], out var buffer, out _, out _));
        Assert.Contains(expected, Imported(buffer));
    }

    [Theory]
    [InlineData("using F; using G; shader Root : Target { };", "F", "G")]
    [InlineData("using F; shader Root : Target { };", "F", "Implicit")]
    [InlineData("namespace A { using F; using G; shader Root : Target { }; }", "F", "G")]
    public void TwoMatchesAtOneLevelAreAnError(string root, string first, string second)
    {
        WriteTargets(first, second);
        Write("Root", root);

        var error = Assert.Throws<InvalidOperationException>(() => Loader("Implicit").LoadExternalBuffer(root.Contains("namespace A") ? "A.Root" : "Root", [], out _, out _, out _));
        Assert.Contains("SDSL0300: Target is ambiguous", error.Message);
        Assert.Contains($"{first}.Target", error.Message);
        Assert.Contains($"{second}.Target", error.Message);
    }

    [Fact]
    public void AFullNameEndsAnAmbiguity()
    {
        WriteTargets("F", "G");
        Write("Root", "using F; using G; shader Root : G.Target { };");

        Assert.True(Loader().LoadExternalBuffer("Root", [], out var buffer, out _, out _));
        Assert.Contains("G.Target", Imported(buffer));
    }

    [Fact]
    public void ADottedNameIsLookedUpFromTheNamespaceAndItsParents()
    {
        WriteTargets("A.B");
        Write("Root", "namespace A.C { shader Root : B.Target { }; }");

        Assert.True(Loader().LoadExternalBuffer("A.C.Root", [], out var buffer, out _, out _));
        Assert.Contains("A.B.Target", Imported(buffer));
    }

    [Fact]
    public void AUsingBringsNoNestedNamespace()
    {
        // The search in every namespace of the transition still finds it: this is about the scope only
        var scope = new ShaderNameScope(null, [], ["A"]);
        Assert.Null(scope.Resolve("B.Target", [], x => x == "A.B.Target", out _));
        Assert.Equal("A.Target", scope.Resolve("Target", [], x => x == "A.Target", out _));
    }

    [Fact]
    public void AnImportAndACompositionAreLookedUpToo()
    {
        Write("Util", "namespace A.B; [Pure] module Util { static const float Value = 2; float Twice(float x) { return x * 2; } };");
        Write("Color", "namespace A.B; shader Color { float Get() { return 1; } };");
        Write("Root", """
            using A.B;
            namespace Other;
            shader Root
            {
                import Util;
                compose Color color;
                compose A.B.Color colors[];
                float Get() { return Twice(color.Get()) + Util.Value + A.B.Util.Value + A.B.Util.Twice(1); }
            };
            """);

        Assert.True(Loader().LoadExternalBuffer("Other.Root", [], out var buffer, out _, out _));
        Assert.Contains("A.B.Util", Imported(buffer));
        Assert.Contains("A.B.Color", Imported(buffer));
    }

    // A name out of scope in shader code says the using it needs
    [Theory]
    [InlineData("shader Root : Target { };")]
    [InlineData("shader Root { import Target; };")]
    [InlineData("shader Root { compose Target target; };")]
    [InlineData("shader Root { Target target; };")]
    [InlineData("shader Root { float Get() { return Target.Value; } };")]
    public void ANameOutOfScopeSaysItsUsing(string root)
    {
        WriteTargets("F");
        Write("Root", $"namespace A; {root}");

        var error = Assert.Throws<InvalidOperationException>(() => Loader().LoadExternalBuffer("A.Root", [], out _, out _, out _));
        Assert.Contains("Did you mean `using F;`?", error.Message);
        Assert.DoesNotContain("SDSL0307", error.Message);
    }

    // A name out of scope in a string says the full name
    [Fact]
    public void AShortNameInAStringIsAnErrorThatSaysTheFullName()
    {
        WriteTargets("F");

        var error = Assert.Throws<InvalidOperationException>(() => Loader().LoadExternalBuffer("Target", [], out _, out _, out _));
        Assert.Contains("SDSL0307: no shader or effect is named Target. Did you mean F.Target?", error.Message);
    }

    // A member comes before a namespace of that name
    [Fact]
    public void AMemberComesBeforeANamespace()
    {
        WriteTargets("Light");
        Write("Root", """
            shader Root
            {
                float4 Light[2];
                float Get() { return Light[1].x; }
                float GetTarget() { float Target = 1; return Target; }
            };
            """);

        Assert.True(Loader().LoadExternalBuffer("Root", [], out _, out _, out _));
    }

    // A class with MemberName parameters compiles again from its own code, with its scope
    [Fact]
    public void AClassCompiledAgainKeepsItsScope()
    {
        Write("Sibling", "using Stride.Shaders.Tests; namespace A; shader Sibling : ComposeSharedBase { float Value; };");
        Write("Member", """
            using F;
            namespace A
            {
                using G;
                shader Member<MemberName Field> : Sibling, Helper
                {
                    override void Compute() { Out[0] = Field + Helped(); }
                    RWBuffer<float> Out;
                };
            }
            """);
        Write("Helper", "namespace G; shader Helper { float Helped() { return 1; } };");

        var mixer = new ShaderMixer(Loader());
        var log = new Stride.Core.Diagnostics.LoggerResult();
        var ok = mixer.MergeSDSL(new ShaderClassSource("A.Member", "Value"), new ShaderMixer.Options(true), log, out _, out _, out _, out _);
        Assert.True(ok, string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)));
    }

    // Two owners with one short name own different structs: what one adds isn't in the other
    [Fact]
    public void PartialStructsOfTwoOwnersWithOneShortNameDontMerge()
    {
        Write("Owner", """
            namespace A { shader Owner { partial struct S { float a; }; S First() { S s; s.a = 1; return s; } }; }
            namespace B { shader Owner { partial struct S { float b; }; S Second() { S s; s.b = 2; return s; } }; }
            """);

        var loader = Loader();
        Assert.True(loader.LoadExternalBuffer("A.Owner", [], out var a, out _, out _));
        Assert.True(loader.LoadExternalBuffer("B.Owner", [], out var b, out _, out _));
        Assert.Equal("A.Owner", ClassName(a));
        Assert.Equal("B.Owner", ClassName(b));
        Assert.Equal("A.Owner", a.Context.ReverseTypes.Values.OfType<StructType>().Single(x => x.Name == "S").PartialOwner);
        Assert.Equal("B.Owner", b.Context.ReverseTypes.Values.OfType<StructType>().Single(x => x.Name == "S").PartialOwner);
    }

    private static (bool ok, string log, string disassembly) Compile(string root, params string[] files)
    {
        var loader = new ShaderLoader("./assets/SDSL/CompilerTests");
        var mixer = new ShaderMixer(loader);
        foreach (var name in files)
            mixer.ShaderLoader.LoadExternalBuffer(TestShader.FullName(name), [], out _, out _, out _);

        var log = new Stride.Core.Diagnostics.LoggerResult();
        var ok = mixer.MergeSDSL(new ShaderClassSource(TestShader.FullName(root)), new ShaderMixer.Options(true), log, out var bytecode, out _, out _, out _);
        var text = string.Join(Environment.NewLine, log.Messages.Select(m => m.Text));
        var disassembly = ok ? Spv.Dis(SpirvBytecode.CreateFromSpan(bytecode), DisassemblerFlags.Name | DisassemblerFlags.Id | DisassemblerFlags.InstructionIndex, true) : "";
        return (ok, text, disassembly);
    }

    // A `using` of the namespace a base class lives in compiles, and the base is found.
    [Fact]
    public void UsingDirectiveIsAcceptedAndTheClassIsFoundByName()
    {
        var (ok, log, disassembly) = Compile("NsUsingRoot", "ComposeSharedBase", "NsUtils", "NsUsingRoot");
        Assert.True(ok, log);
        Assert.Contains("NsUtils.One", disassembly);
    }

    // A `using` of a namespace nothing declares is not an error: it can be a C# namespace of the generated code
    [Fact]
    public void UsingOfAnUnknownNamespaceIsIgnored()
    {
        var (ok, log, _) = Compile("NsUnknownUsingRoot", "ComposeSharedBase", "NsUtils", "NsUnknownUsingRoot");
        Assert.True(ok, log);
    }

    private static ShaderFile Parse(string code)
    {
        var result = SDSLParser.Parse(code);
        Assert.True(result.Errors.Count == 0, string.Join("\n", result.Errors));
        return Assert.IsType<ShaderFile>(result.AST);
    }

    [Fact]
    public void ClassNamesCanBeDotted()
    {
        var shader = Assert.IsType<ShaderClass>(Parse("""
            shader Foo : A.B.Parent, A.B.Generic<3, X.Y>, Plain, PlainGeneric<1>
            {
                import A.B.Module;
                import A.B.GenericModule<3>;
                compose A.B.Compute color;
                compose A.B.Compute colors[];
                A.B.Bar member;
                A.B.Bar Get(A.B.Bar parameter)
                {
                    A.B.Bar local = parameter;
                    return local;
                }
            };
            """).RootDeclarations[0]);

        Assert.Equal(["A.B.Parent", "A.B.Generic", "Plain", "PlainGeneric"], shader.Mixins.Select(x => x is GenericIdentifier g ? g.Name.Name : x.Name));
        Assert.Equal([false, true, false, true], shader.Mixins.Select(x => x is GenericIdentifier));
        Assert.Equal(["A.B.Module", "A.B.GenericModule"], shader.Elements.OfType<ShaderImport>().Select(x => x.Module is GenericIdentifier g ? g.Name.Name : x.Module.Name));
        Assert.Equal(["A.B.Compute", "A.B.Compute", "A.B.Bar"], shader.Elements.OfType<ShaderMember>().Select(x => x.TypeName.Name));
        var method = shader.Elements.OfType<ShaderMethod>().Single();
        Assert.Equal("A.B.Bar", method.ReturnTypeName.Name);
    }

    // What looks like a dotted name in code stays an expression
    [Fact]
    public void AccessChainsAreNotDottedTypes()
    {
        var shader = Assert.IsType<ShaderClass>(Parse("""
            shader Foo
            {
                float Get()
                {
                    streams.Value.x = 1;
                    Lights[0].Color.r = 2;
                    float a = (streams.Value.x) + 1;
                    float b = (streams.Value.x) - 1;
                    streams.Value.x++;
                    Math.Sum(a, b);
                    return a < streams.Value.x ? a : b;
                }
            };
            """).RootDeclarations[0]);

        var statements = shader.Elements.OfType<ShaderMethod>().Single().Body!.Statements;
        Assert.Equal(7, statements.Count);
        Assert.IsNotType<Declare>(statements[0]);
        Assert.IsNotType<Declare>(statements[1]);
        Assert.IsType<Declare>(statements[2]);
        Assert.IsNotType<Declare>(statements[4]);
        Assert.IsNotType<Declare>(statements[5]);
    }

    [Theory]
    [InlineData("namespace A.B { using X; using Y.Z; shader Foo { }; }")]
    [InlineData("namespace A.B; using X; using Y.Z; shader Foo { };")]
    public void UsingInsideANamespaceComesBeforeItsDeclarations(string code)
    {
        var declarations = Parse(code).Namespaces.Single().Declarations;
        Assert.Equal(3, declarations.Count);
        Assert.Equal(["X"], Assert.IsType<UsingShaderNamespace>(declarations[0]).NamespacePath.Select(x => x.Name));
        Assert.Equal(["Y", "Z"], Assert.IsType<UsingShaderNamespace>(declarations[1]).NamespacePath.Select(x => x.Name));
        Assert.IsType<ShaderClass>(declarations[2]);
    }

    [Theory]
    [InlineData("namespace A.B { shader Foo { }; using X; }")]
    [InlineData("namespace A.B; shader Foo { }; using X;")]
    public void UsingAfterADeclarationOfTheNamespaceIsAnError(string code)
    {
        Assert.NotEmpty(SDSLParser.Parse(code).Errors);
    }

    // Two classes of one name in two namespaces of one file: the `using` picks the first
    [Fact]
    public void SameNameInTwoNamespacesIsDisambiguatedByUsing()
    {
        var (ok, log, disassembly) = Compile("NsClashRoot", "ComposeSharedBase", "NsClashed", "NsClashRoot");
        Assert.True(ok, log);
        // One NsClashed.Which is mixed in, not two.
        Assert.Equal(1, disassembly.Split("OpName").Count(s => s.Contains("NsClashed.Which")));
        // And it is the first one: Which() returns 1.
        var constants = System.Text.RegularExpressions.Regex.Matches(disassembly, @"OpConstant %\S+ (\d+)").Select(m => m.Groups[1].Value).ToList();
        Assert.Contains("1", constants);
        Assert.DoesNotContain("2", constants);
    }
}
