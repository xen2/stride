// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Shaders.Compilers.SDSL;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// A keyword says how a method takes part: <c>pipeline</c>, <c>replace</c> or <c>around</c>. What adds information about it is an attribute: <c>[Phases]</c>.
/// </summary>
public class KindKeywordTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"kind-src-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string name, string code) => File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), $"namespace Test;\n{code}\n");

    private (bool Ok, string Log) Mix(string name)
    {
        Write("Entry", "module Entry { stream float4 ColorTarget : SV_Target0; void PSMain() { streams.ColorTarget = 0; } }");
        var log = new LoggerResult();
        var mixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL") { WarningsAsErrors = false });
        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource("Test.Entry"), new ShaderClassSource($"Test.{name}") } };
        var ok = mixer.MergeSDSL(source, new ShaderMixer.Options(true), log, out _, out _, out _, out _);
        return (ok, string.Join("\n", log.Messages.Select(x => x.ToString())));
    }

    // Valid code that no upgrade would fix if these were reserved
    [Theory]
    [InlineData("pipeline")]
    [InlineData("replace")]
    [InlineData("around")]
    [InlineData("collect")]
    public void TheWordsAreNames(string word)
    {
        Write("Names", $$"""
            module Names
            {
                struct {{word}} { float Value; };

                {{word}} Stored;
                float {{word}}Count;
                {{word}} {{word}}Value;
                float {{word}}2;

                {{word}} Make() { {{word}} made = ({{word}})0; return made; }
                float Use(float {{word}}Parameter) { return {{word}}Parameter; }
                float Local() { float {{word}}Local = 1; float value = {{word}}Local; return value; }
                float Read() { return Make().Value + Stored.Value + {{word}}Count; }
            }
            """);

        var (ok, log) = Mix("Names");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("pipeline")]
    [InlineData("replace")]
    [InlineData("around")]
    [InlineData("collect")]
    public void TheWordsAreMethodAndMemberNames(string word)
    {
        Write("Names", $$"""
            module Names
            {
                float {{word}}() { return 1; }
                float Other() { return {{word}}(); }
            }
            """);
        Write("Members", $$"""
            shader Members
            {
                float {{word}};
                float Get() { float {{word}} = 2; return {{word}} + this.{{word}}; }
            }
            """);

        var names = Mix("Names");
        var members = Mix("Members");

        Assert.True(names.Ok, names.Log);
        Assert.True(members.Ok, members.Log);
    }

    // A kind on a method whose return type has that name
    [Theory]
    [InlineData("pipeline", "void")]
    [InlineData("replace", "float")]
    [InlineData("around", "float")]
    public void AKindOnAMethodThatReturnsATypeOfTheName(string word, string kind)
    {
        var body = kind == "void" ? "" : "return 1;";
        Write("Names", $$"""
            module Names
            {
                struct {{word}} { float Value; };
                {{word}} {{word}} Kinded() { {{word}} made = ({{word}})0; return made; }
                {{word}} {{word}}Named() { return Kinded(); }
                {{word}} {{kind}} Plain() { {{body}} }
            }
            """);

        var (ok, log) = Mix("Names");

        Assert.True(ok, log);
    }

    // A type whose name starts with a modifier is a type
    [Theory]
    [InlineData("module")]
    [InlineData("override")]
    [InlineData("clone")]
    [InlineData("abstract")]
    [InlineData("static")]
    public void ATypeNameThatStartsWithAModifierIsAType(string modifier)
    {
        Write("Names", $$"""
            module Names
            {
                struct {{modifier}}Data { float Value; };
                {{modifier}}Data Make() { {{modifier}}Data made = ({{modifier}}Data)0; return made; }
            }
            """);

        var (ok, log) = Mix("Names");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("module pipeline void F() { }")]
    [InlineData("pipeline module void F() { }")]
    [InlineData("[Phases(A, B)] module pipeline void F() { }")]
    [InlineData("[Phases(A, B)] pipeline void F() { }")]
    [InlineData("[Phases(A, B)] around float F() { return 1; }")]
    [InlineData("around float F() { return 1; }")]
    [InlineData("pipeline void F() { }")]
    [InlineData("replace float F() { return 1; }")]
    [InlineData("replace\n    float F() { return 1; }")]
    public void AKindCanBeWritten(string member)
    {
        Write("Good", $"shader Good {{ {member} }}");

        var (ok, log) = Mix("Good");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("abstract pipeline void F();", "SDSL0250: pipeline on Wrong.F: an abstract method has no body to extend, and its overrides implement it.")]
    [InlineData("replace abstract float F();", "SDSL0250: replace on Wrong.F: an abstract method has no body to extend, and its overrides implement it.")]
    [InlineData("pipeline around void F() { }", "SDSL0250: around on Wrong.F: a method has one kind.")]
    [InlineData("pipeline pipeline void F() { }", "SDSL0250: pipeline on Wrong.F: a method has one kind.")]
    [InlineData("collect void F() { }", "SDSL0139: collect on Wrong.F: collect is reserved for a later version. Use pipeline or around.")]
    public void AWrongCombinationIsAnError(string member, string expected)
    {
        Write("Wrong", $"shader Wrong {{ {member} }}");

        var (ok, log) = Mix("Wrong");

        Assert.False(ok, log);
        Assert.Contains(expected, log);
    }

    [Theory]
    [InlineData("override pipeline void F() { base.F(); }")]
    [InlineData("pipeline override void F() { base.F(); }")]
    public void AKindOnAnOverrideIsAnError(string member)
    {
        Write("Owner", "shader Owner { pipeline void F() { } }");
        Write("Wrong", $"shader Wrong : Owner {{ {member} }}");

        var (ok, log) = Mix("Wrong");

        Assert.False(ok, log);
        Assert.Contains("SDSL0142: pipeline on Wrong.F: a kind goes on the first definition of F, not on an override.", log);
    }

    [Theory]
    [InlineData("[Phases(A)] float F() { return 1; }", "SDSL0135: [Phases] on Wrong.F: the phases belong to a pipeline or an around method.")]
    [InlineData("[Phases(A)] replace float F() { return 1; }", "SDSL0135: [Phases] on Wrong.F: a replace method has no phases.")]
    [InlineData("[Phases] pipeline void F() { }", "SDSL0135: [Phases] on Wrong.F: [Phases] takes the names of the phases.")]
    [InlineData("[Phases(A)] [Phases(B)] pipeline void F() { }", "SDSL0135: [Phases] on Wrong.F: a method has one list of phases.")]
    [InlineData("[Phases(A, A)] pipeline void F() { }", "SDSL0135: [Phases] on Wrong.F: phase A is written twice.")]
    [InlineData("[Phases(A + B)] pipeline void F() { }", "is not a phase name.")]
    [InlineData("[Phases(A)] abstract float F();", "SDSL0135: [Phases] on Wrong.F: the phases belong to a pipeline or an around method.")]
    public void PhasesWithoutAKindThatUsesThemIsAnError(string member, string expected)
    {
        Write("Wrong", $"shader Wrong {{ {member} }}");

        var (ok, log) = Mix("Wrong");

        Assert.False(ok, log);
        Assert.Contains(expected, log);
    }

    [Fact]
    public void PhasesOnAnOverrideIsAnError()
    {
        Write("Owner", "shader Owner { [Phases(A)] pipeline void F() { } }");
        Write("Wrong", "shader Wrong : Owner { [Phases(A, B)] override void F() { base.F(); } }");

        var (ok, log) = Mix("Wrong");

        Assert.False(ok, log);
        Assert.Contains("SDSL0135: [Phases] on Wrong.F: the phases are on the first definition.", log);
    }

    [Fact]
    public void PhasesOnAClassIsAnError()
    {
        Write("Wrong", "[Phases(A)] shader Wrong { }");

        var (ok, log) = Mix("Wrong");

        Assert.False(ok, log);
        Assert.Contains("SDSL0135: [Phases] on Wrong: it goes on a method.", log);
    }
}
