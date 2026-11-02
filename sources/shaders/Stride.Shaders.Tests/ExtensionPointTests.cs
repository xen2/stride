// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Rendering;
using Stride.Shaders.Compilers.SDSL;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Extension points in the compat form: <c>replace</c>, <c>pipeline</c> or <c>around</c> on the first definition of a module
/// method, <c>[Phase]</c> on an override, and <c>base</c> in the sorted chain. What the running order gives is checked by
/// the render test <c>ExtensionPointPhases</c>.
/// </summary>
public partial class ExtensionPointTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"extension-point-src-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string Owner = """
        module Owner
        {
            stream float Steps;

            [Phases(Early, Default, A, B)]
            pipeline void Run() { streams.Steps = 1; }

            [Phases(Outer, Inner)]
            around float Wrap() { return 1; }

            replace float Pick() { return 1; }

            float Plain() { return 1; }
        }
        """;

    // A mix needs a pixel shader to compile
    private const string Entry = """
        module Entry
        {
            stream float4 ColorTarget : SV_Target0;

            void PSMain() { streams.ColorTarget = 0; }
        }
        """;

    private void Write(string name, string code) => File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), $"namespace Test;\n{code}\n");

    private void WriteOwner() => Write("Owner", Owner);

    private void WriteModule(string name, string body) => Write(name, $"module {name} {{ import Owner; {body} }}");

    private (bool Ok, string Log, string Explanation) Mix(bool isAssetBuild, params string[] classes)
    {
        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource("Test.Entry") } };
        foreach (var name in classes)
            source.Mixins.Add(new ShaderClassSource(name));
        return Mix(isAssetBuild, source);
    }

    private (bool Ok, string Log, string Explanation) Mix(bool isAssetBuild, ShaderMixinSource source)
    {
        Write("Entry", Entry);
        var log = new LoggerResult();
        var mixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL") { WarningsAsErrors = false }) { CollectExplanation = true, IsAssetBuild = isAssetBuild };
        bool ok;
        try
        {
            ok = mixer.MergeSDSL(source, new ShaderMixer.Options(true), log, out _, out _, out _, out _);
        }
        catch (InvalidOperationException e)
        {
            ok = false;
            log.Error(e.Message);
        }
        return (ok, string.Join(Environment.NewLine, log.Messages.Select(x => $"{x.Type}: {x.Text}")), mixer.Explanation ?? "");
    }

    private (bool Ok, string Log, string Explanation) Mix(params string[] classes) => Mix(false, classes);

    private static string[] Block(string explanation, string method)
    {
        var lines = explanation.Split('\n').Select(x => x.TrimEnd()).ToList();
        var start = lines.FindIndex(x => x.Contains($"method {method}("));
        return lines.Skip(start).TakeWhile((x, i) => i == 0 || x.StartsWith("    ", StringComparison.Ordinal)).Select(x => x.Trim()).ToArray();
    }

    [Fact]
    public void PhasesSortTheChain()
    {
        WriteOwner();
        WriteModule("StepB", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("StepA", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("StepEarly", "[Phase(Early)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix("Test.StepEarly", "Test.StepB", "Test.StepA");

        Assert.True(ok, log);
        Assert.Equal(
            ["module method Run() -> void = Owner > StepA > StepB > StepEarly", "pipeline", "Early: StepEarly", "Default: Owner", "A: StepA", "B: StepB"],
            Block(explanation, "Run"));
    }

    // Without a phase: outside every declared phase, in the order of the mix
    [Fact]
    public void AnOverrideWithoutPhaseIsOutermost()
    {
        WriteOwner();
        WriteModule("Patch2", "override void Run() { base.Run(); }");
        WriteModule("StepB", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("Patch1", "override void Run() { base.Run(); }");
        WriteModule("StepA", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix("Test.Patch2", "Test.StepB", "Test.Patch1", "Test.StepA");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > StepA > StepB > Patch2 > Patch1", block[0]);
        Assert.Contains("External: Patch2 > Patch1", block);
    }

    // The price of the rule: mixed first or last, a replacement without a phase is outermost, and it says what it drops
    [Fact]
    public void AReplacementWithoutPhaseDropsThePhasesMixedAfterIt()
    {
        WriteOwner();
        WriteModule("Replacement", "override void Run() { streams.Steps = 2; }");
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix("Test.Replacement", "Test.Step");

        Assert.True(ok, log);
        Assert.Equal("module method Run() -> void = Owner > Step > Replacement", Block(explanation, "Run")[0]);
        Assert.Contains("SDSL0216: Replacement.Run doesn't call base, so Step never run", log);
    }

    [Fact]
    public void WithoutPhasesTheChainIsTheMixOrder()
    {
        WriteOwner();
        WriteModule("First", "override void Run() { base.Run(); }");
        WriteModule("Second", "override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix("Test.Second", "Test.First");

        Assert.True(ok, log);
        Assert.Equal("module method Run() -> void = Owner > Second > First", Block(explanation, "Run")[0]);
    }

    [Fact]
    public void TheFirstPhaseOfAnAroundPointIsTheOutermost()
    {
        WriteOwner();
        WriteModule("WrapOuter", "[Phase(Outer)] override float Wrap() { return base.Wrap() * 2; }");
        WriteModule("WrapInner", "[Phase(Inner)] override float Wrap() { return base.Wrap() + 1; }");

        var (ok, log, explanation) = Mix("Test.WrapOuter", "Test.WrapInner");

        Assert.True(ok, log);
        Assert.Equal(
            ["module method Wrap() -> float = Owner > WrapInner > WrapOuter", "around", "Outer: WrapOuter", "Inner: WrapInner", "default: Owner"],
            Block(explanation, "Wrap"));
    }

    [Fact]
    public void AnOverrideOfAnOverrideFindsThePoint()
    {
        WriteOwner();
        WriteModule("Middle", "override void Run() { base.Run(); }");
        Write("Last", "module Last { import Middle; [Phase(A)] override void Run() { base.Run(); } }");

        var (ok, log, explanation) = Mix("Test.Last");

        Assert.True(ok, log);
        Assert.Contains("A: Last", Block(explanation, "Run"));
    }

    // VL's MaterialExtension: a helper reaches inward from the place of its class
    [Fact]
    public void BaseFromAHelperKeepsWorking()
    {
        WriteOwner();
        WriteModule("Patch", "override float Wrap() { return Helper(); } float Helper() { return base.Wrap(); }");

        var (ok, log, _) = Mix("Test.Patch");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("collect void F() { }", "SDSL0139")]
    [InlineData("[Phases(A)] replace void F() { }", "SDSL0135")]
    [InlineData("[Phases(A, A)] pipeline void F() { }", "SDSL0135")]
    [InlineData("[Phases(A + B)] pipeline void F() { }", "SDSL0135")]
    [InlineData("[Phases(A)] [Phases(B)] around void F() { }", "SDSL0135")]
    [InlineData("[Phase(A, B)] override void Run() { base.Run(); }", "SDSL0135")]
    [InlineData("[Phase(A)] void F() { }", "SDSL0141")]
    [InlineData("[Phase(Missing)] override void Run() { base.Run(); }", "SDSL0141")]
    [InlineData("[Phase(A)] override float Pick() { return 2; }", "SDSL0141")]
    [InlineData("[Phase(A)] override float Plain() { return 2; }", "SDSL0246")]
    [InlineData("[Phases(A)] override pipeline void Run() { base.Run(); }", "SDSL0142")]
    [InlineData("[Phase(Early)] override void Run() { base.Run(); streams.Steps = 2; }", "SDSL0140")]
    [InlineData("[Phase(A)] override void Run() { streams.Steps = 2; base.Run(); }", "SDSL0140")]
    [InlineData("[Phase(A)] override void Run() { if (streams.Steps > 0) base.Run(); }", "SDSL0140")]
    public void AWrongAttributeIsAnError(string member, string code)
    {
        WriteOwner();
        WriteModule("Wrong", member);

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains(code, log);
    }

    [Fact]
    public void AStringInReplacesOnAClassIsAnError()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");
        Write("Wrong", "[Replaces(\"Test.Step\")] module Wrong : Test.Step { }");

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains("SDSL0255: [Replaces] on Wrong: \"Test.Step\" is a string.", log);
    }

    [Fact]
    public void AKindGoesOnAModuleMethod()
    {
        Write("Wrong", "shader Wrong { [Phases(A)] pipeline void F() { } }");

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains("SDSL0250: pipeline on Wrong.F: a kind goes on a module method.", log);
    }

    [Theory]
    [InlineData("[Phase(Early)] override void Run() { base.Run(); }")]
    [InlineData("[Phase(Early)] override void Run() { streams.Steps = 2; base.Run(); }")]
    [InlineData("[Phase(Default)] override void Run() { streams.Steps = 2; base.Run(); }")]
    [InlineData("[Phase(Default)] override void Run() { base.Run(); streams.Steps = 2; }")]
    [InlineData("[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }")]
    [InlineData("[Phase(A)] override void Run() { streams.Steps = 2; }")]
    public void TheBasePositionFitsThePhase(string member)
    {
        WriteOwner();
        WriteModule("Fits", member);

        var (ok, log, _) = Mix("Test.Fits");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("[Phases(A)] pipeline", false)]
    [InlineData("replace", false)]
    [InlineData("around", false)]
    [InlineData("", true)]
    public void OverridingAnUndeclaredModuleMethodIsAnError(string kind, bool isError)
    {
        Write("Base", $"module Base {{ {kind} void F() {{ }} }}");
        Write("Patch", "module Patch { import Base; override void F() { base.F(); } }");

        var (ok, log, _) = Mix("Test.Patch");

        Assert.Equal(isError, !ok);
        Assert.Equal(isError, log.Contains("SDSL0246"));
        if (isError)
            Assert.Contains("If Base is in a library or in the engine, its author has to write it", log);
    }

    [Fact]
    public void ARootShaderOverridingAnUndeclaredModuleMethodIsAnError()
    {
        Write("Base", "module Base { void F() { } }");
        Write("Patch", "[Root] shader Patch { import Base; override void F() { base.F(); } }");

        var (ok, log, _) = Mix("Test.Patch");

        Assert.False(ok, log);
        Assert.Contains("SDSL0246", log);
    }

    [Fact]
    public void ImplementingAnAbstractModuleMethodDoesNotWarn()
    {
        Write("Base", "module Base { abstract void F(); }");
        Write("Implementation", "module Implementation { import Base; override void F() { } }");

        var (ok, log, _) = Mix("Test.Implementation");

        Assert.True(ok, log);
        Assert.DoesNotContain("SDSL0246", log);
    }

    [Fact]
    public void AnInstanceOverrideDoesNotWarn()
    {
        Write("Base", "shader Base { void F() { } }");
        Write("Implementation", "shader Implementation : Base { override void F() { } }");

        var (ok, log, _) = Mix("Test.Implementation");

        Assert.True(ok, log);
        Assert.DoesNotContain("SDSL0246", log);
    }

    [Theory]
    [InlineData(false, "Info")]
    [InlineData(true, "Warning")]
    public void ADroppedContributorIsReported(bool isAssetBuild, string type)
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Replacement", "override void Run() { streams.Steps = 2; }");

        var (ok, log, explanation) = Mix(isAssetBuild, "Test.Step", "Test.Replacement");

        Assert.True(ok, log);
        Assert.Contains($"{type}: SDSL0216: Replacement.Run doesn't call base, so Step never run", log);
        Assert.Contains("dropped by Replacement: Step", Block(explanation, "Run"));
    }

    [Fact]
    public void ReplacingTheDefaultBodyIsNotADrop()
    {
        WriteOwner();
        WriteModule("Replacement", "override void Run() { streams.Steps = 2; }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.Replacement");

        Assert.True(ok, log);
        Assert.DoesNotContain("SDSL0216", log);
    }

    // Single inheritance: the class knows what its parent contributed
    [Fact]
    public void ReplacingYourOwnParentIsNotADrop()
    {
        WriteOwner();
        Write("Parent", "shader Parent { import Owner; module override void Run() { base.Run(); } }");
        Write("Child", "shader Child : Parent { module override void Run() { streams.Steps = 2; } }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.Child");

        Assert.True(ok, log);
        Assert.DoesNotContain("SDSL0216", log);
    }

    // An import only gives access: replacing what the imported module contributes is a choice to state
    [Fact]
    public void ReplacingAnImportedModuleIsADrop()
    {
        WriteOwner();
        WriteModule("Imported", "override void Run() { base.Run(); }");
        Write("Replacement", "module Replacement { import Imported; override void Run() { streams.Steps = 2; } }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.Replacement");

        Assert.True(ok, log);
        Assert.Contains("SDSL0216: Replacement.Run doesn't call base, so Imported never run", log);
    }

    [Fact]
    public void AReplacePointNamesTheWinner()
    {
        WriteOwner();
        WriteModule("Winner", "override float Pick() { return 2; }");

        var (ok, log, explanation) = Mix("Test.Winner");

        Assert.True(ok, log);
        Assert.Equal(["module method Pick() -> float = Owner > Winner", "replace: Winner"], Block(explanation, "Pick"));
    }

    // A misspelled kind on a method is an unknown attribute, which is an error
    [Fact]
    public void AMisspelledKindIsAnError()
    {
        WriteOwner();
        WriteModule("Wrong", "[Pipline(A)] void F() { }");

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains("SDSL0171", log);
    }

    // They go on a method, and a class that writes them expects them to do something
    [Theory]
    [InlineData("[Phases(A, B)] shader Wrong { }")]
    [InlineData("[Phase(A)] shader Wrong { }")]
    [InlineData("shader Wrong { [Phases(A)] cbuffer PerDraw { float A; } }")]
    public void AKindOrAPhaseOnAClassIsAnError(string code)
    {
        Write("Wrong", code);

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains("SDSL0135", log);
        Assert.Contains("it goes on a method", log);
    }

    // Libraries read attributes of their own
    [Fact]
    public void AnUnknownAttributeOnAClassStaysAllowed()
    {
        Write("Other", "[SomethingElse] shader Other { }");

        var (ok, log, _) = Mix("Test.Other");

        Assert.True(ok, log);
    }

    // No capture mixes these two: VL.ImGui and StrideTerrainEditor do
    [Theory]
    [InlineData("Stride.Rendering.PositionStream2", "Input: PositionStream2")]
    [InlineData("Stride.Rendering.PositionVertexTransform", "Attributes: PositionVertexTransform")]
    public void TheEngineShadersNoCaptureMixesHaveTheirPhase(string shader, string line)
    {
        Write("Root", "module Root { import Stride.Rendering.ShaderBase; }");

        var (ok, log, explanation) = Mix("Test.Root", shader);

        Assert.True(ok, log);
        Assert.Contains(line, Block(explanation, "VSMain"));
    }
}
