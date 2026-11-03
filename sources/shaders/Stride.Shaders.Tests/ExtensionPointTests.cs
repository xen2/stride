// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Shaders.Compilers.SDSL;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Extension points in the compat form: <c>[Replace]</c>, <c>[Pipeline]</c> or <c>[Around]</c> on the first definition of a module
/// method, <c>[Phase]</c> on an override, and <c>base</c> in the sorted chain. What the running order gives is checked by
/// the render test <c>ExtensionPointPhases</c>.
/// </summary>
public class ExtensionPointTests : IDisposable
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

            [Pipeline(Early, Default, A, B)]
            void Run() { streams.Steps = 1; }

            [Around(Outer, Inner)]
            float Wrap() { return 1; }

            [Replace]
            float Pick() { return 1; }

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

    private (bool Ok, string Log, string Explanation) Mix(bool wired, params string[] classes) => Mix(wired, _ => { }, classes);

    private (bool Ok, string Log, string Explanation) Mix(bool wired, Action<ShaderMixinSource> wiring, params string[] classes)
    {
        Write("Entry", Entry);
        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource("Test.Entry") } };
        foreach (var name in classes)
            source.Mixins.Add(new ShaderClassSource(name));
        wiring(source);

        var log = new LoggerResult();
        var mixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL") { WarningsAsErrors = false }) { CollectExplanation = true, IsWiredEffect = wired };
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
    [InlineData("[Collect] void F() { }", "SDSL0139")]
    [InlineData("[Replace(A)] void F() { }", "SDSL0135")]
    [InlineData("[Pipeline(A, A)] void F() { }", "SDSL0135")]
    [InlineData("[Pipeline(A + B)] void F() { }", "SDSL0135")]
    [InlineData("[Pipeline(A)] [Around(B)] void F() { }", "SDSL0135")]
    [InlineData("[Phase(A, B)] override void Run() { base.Run(); }", "SDSL0135")]
    [InlineData("[Phase(A)] void F() { }", "SDSL0141")]
    [InlineData("[Phase(Missing)] override void Run() { base.Run(); }", "SDSL0141")]
    [InlineData("[Phase(A)] override float Pick() { return 2; }", "SDSL0141")]
    [InlineData("[Phase(A)] override float Plain() { return 2; }", "SDSL0246")]
    [InlineData("[Pipeline(A)] override void Run() { base.Run(); }", "SDSL0142")]
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

    [Theory]
    [InlineData("[After(StepB)] void F() { }", "SDSL0135")]
    [InlineData("[After(StepB)] override float Plain() { return 2; }", "SDSL0246")]
    [InlineData("[After(StepB)] override float Pick() { return 2; }", "SDSL0135")]
    [InlineData("[After] override void Run() { base.Run(); }", "SDSL0135")]
    [InlineData("[After(StepB + 1)] override void Run() { base.Run(); }", "SDSL0135")]
    [InlineData("[After(NoSuchClass)] override void Run() { base.Run(); }", "SDSL0110")]
    public void AWrongAfterIsAnError(string member, string code)
    {
        WriteOwner();
        WriteModule("StepB", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("Wrong", member);

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains(code, log);
    }

    // After a phase that runs later: B runs after A whatever the mix says, so A can't run after B
    [Fact]
    public void AfterAClassOfALaterPhaseIsAnError()
    {
        WriteOwner();
        WriteModule("StepB", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("StepA", "[Phase(A)] [After(StepB)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.StepB", "Test.StepA");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138", log);
    }

    [Fact]
    public void AfterAnOverrideWithoutPhaseIsAnError()
    {
        WriteOwner();
        WriteModule("Patch", "override void Run() { base.Run(); }");
        WriteModule("StepA", "[Phase(A)] [After(Patch)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.Patch", "Test.StepA");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138", log);
    }

    [Fact]
    public void AfterGoingInACircleIsAnError()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] [After(Second)] override void Run() { base.Run(); }");
        WriteModule("Second", "[Phase(A)] [After(First)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138", log);
    }

    [Fact]
    public void AfterOrdersAPhaseThatCallsBaseFirst()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] [After(Second)] override void Run() { base.Run(); }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix("Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Contains("A: Second > First   order: after", Block(explanation, "Run"));
    }

    // The chain is run from its end here: the step that runs later is reached from the one that runs first, so it is lower in the chain
    [Fact]
    public void AfterOrdersAPhaseThatCallsBaseLast()
    {
        WriteOwner();
        WriteModule("First", "[Phase(Early)] override void Run() { streams.Steps = 2; base.Run(); }");
        WriteModule("Second", "[Phase(Early)] [After(First)] override void Run() { streams.Steps = 3; base.Run(); }");

        var (ok, log, explanation) = Mix(wired: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Equal("module method Run() -> void = Owner > Second > First", Block(explanation, "Run")[0]);
    }

    [Fact]
    public void AfterOrdersAnAroundPhaseFromTheOutside()
    {
        WriteOwner();
        WriteModule("Inner", "[Phase(Outer)] [After(Outermost)] override float Wrap() { return base.Wrap(); }");
        WriteModule("Outermost", "[Phase(Outer)] override float Wrap() { return base.Wrap(); }");

        var (ok, log, explanation) = Mix("Test.Outermost", "Test.Inner");

        Assert.True(ok, log);
        Assert.Equal("module method Wrap() -> float = Owner > Inner > Outermost", Block(explanation, "Wrap")[0]);
    }

    [Fact]
    public void AfterAClassThatIsNotInTheEffectIsIgnored()
    {
        WriteOwner();
        WriteModule("Absent", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Step", "[Phase(A)] [After(Absent)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.Step");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("streams.Steps = 2;", "streams.Steps = 3;", "streams.Steps")]
    [InlineData("streams.Other = streams.Steps;", "streams.Steps = 3;", "streams.Steps")]
    [InlineData("Helper();", "streams.Steps = 3;", "streams.Steps")]
    [InlineData("discard;", "discard;", "side effect")]
    [InlineData("Values[0] = 1;", "discard;", "side effect")]
    public void AConflictInAPhaseIsAnErrorInAWiredEffect(string first, string second, string message)
    {
        WriteOwner();
        Write("Helpers", "module Helpers { import Owner; stream float Other; RWStructuredBuffer<float> Values; void Helper() { streams.Steps = 2; } }");
        Write("First", $"module First {{ import Helpers; [Phase(A)] override void Run() {{ base.Run(); {first} }} }}");
        Write("Second", $"module Second {{ import Helpers; [Phase(A)] override void Run() {{ base.Run(); {second} }} }}");

        var (ok, log, _) = Mix(wired: true, "Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0137", log);
        Assert.Contains(message, log);

        // In a mix built at run time, the order of the mix decides
        var (runtimeOk, runtimeLog, _) = Mix("Test.First", "Test.Second");

        Assert.True(runtimeOk, runtimeLog);
    }

    // Streams with one semantic become one field, so a contributor reads what the other wrote under another name
    [Theory]
    [InlineData("streams.One = 2;", "streams.Two = 3;")]
    [InlineData("streams.Other = streams.One;", "streams.Two = 3;")]
    public void StreamsWithOneSemanticAreOneField(string first, string second)
    {
        WriteOwner();
        Write("Helpers", "module Helpers { import Owner; stream float Other; stream float One : SAME; stream float Two : SAME; }");
        Write("First", $"module First {{ import Helpers; [Phase(A)] override void Run() {{ base.Run(); {first} }} }}");
        Write("Second", $"module Second {{ import Helpers; [Phase(A)] override void Run() {{ base.Run(); {second} }} }}");

        var (ok, log, _) = Mix(wired: true, "Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0137", log);
        Assert.Contains("streams.One and streams.Two (semantic SAME)", log);
    }

    [Fact]
    public void StreamsWithTwoSemanticsAreTwoFields()
    {
        WriteOwner();
        Write("Helpers", "module Helpers { import Owner; stream float One : ONE; stream float Two : TWO; }");
        Write("First", "module First { import Helpers; [Phase(A)] override void Run() { base.Run(); streams.One = 2; } }");
        Write("Second", "module Second { import Helpers; [Phase(A)] override void Run() { base.Run(); streams.Two = 3; } }");

        var (ok, log, _) = Mix(wired: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
    }

    // A store to the whole of streams writes every field the other contributor uses
    [Theory]
    [InlineData("var saved = streams; streams = saved;", "streams.Steps = 3;", "streams.Steps")]
    [InlineData("var saved = streams; streams = saved;", "streams.Other = streams.Steps;", "streams.Other, streams.Steps")]
    [InlineData("Restore();", "streams.Steps = 3;", "streams.Steps")]
    [InlineData("var saved = streams; streams = saved;", "var saved = streams; streams = saved;", "the whole of streams")]
    public void AWriteOfTheWholeValueIsAWriteOfEveryField(string first, string second, string message)
    {
        WriteOwner();
        Write("Helpers", "module Helpers { import Owner; stream float Other; void Restore() { var saved = streams; streams = saved; } }");
        Write("First", $"module First {{ import Helpers; [Phase(A)] override void Run() {{ base.Run(); {first} }} }}");
        Write("Second", $"module Second {{ import Helpers; [Phase(A)] override void Run() {{ base.Run(); {second} }} }}");

        var (ok, log, _) = Mix(wired: true, "Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0137", log);
        Assert.Contains(message, log);
    }

    // A copy is a local variable: writing to it changes no stream. And a contributor that uses no stream shares nothing
    [Theory]
    [InlineData("var saved = streams;", "streams.Steps = 3;")]
    [InlineData("var saved = streams; streams = saved;", "discard;")]
    public void AWriteOfACopyOrOfNothingSharedIsNoConflict(string first, string second)
    {
        WriteOwner();
        WriteModule("First", $"[Phase(A)] override void Run() {{ base.Run(); {first} }}");
        WriteModule("Second", $"[Phase(A)] override void Run() {{ base.Run(); {second} }}");

        var (ok, log, _) = Mix(wired: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("streams.Other = 2;", "streams.Steps = 3;")]
    [InlineData("streams.Other = streams.Steps;", "streams.Third = streams.Steps;")]
    [InlineData("discard;", "streams.Steps = 3;")]
    public void NoConflictNeedsNoOrder(string first, string second)
    {
        WriteOwner();
        Write("Helpers", "module Helpers { import Owner; stream float Other; stream float Third; }");
        Write("First", $"module First {{ import Helpers; [Phase(A)] override void Run() {{ base.Run(); {first} }} }}");
        Write("Second", $"module Second {{ import Helpers; [Phase(A)] override void Run() {{ base.Run(); {second} }} }}");

        var (ok, log, _) = Mix(wired: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
    }

    // What the steps of other phases do isn't part of the step, although base reaches them
    [Fact]
    public void BaseIsNotPartOfWhatAContributorDoes()
    {
        WriteOwner();
        WriteModule("Writes", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Later", "[Phase(B)] override void Run() { base.Run(); streams.Steps = 3; }");

        var (ok, log, _) = Mix(wired: true, "Test.Writes", "Test.Later");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("[After(First)]")]
    [InlineData("[After(Middle)]")]
    public void AfterSettlesAConflict(string attribute)
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Middle", "[Phase(A)] [After(First)] override void Run() { base.Run(); }");
        Write("Last", $"module Last {{ import First; import Middle; [Phase(A)] {attribute} override void Run() {{ base.Run(); streams.Steps = 3; }} }}");

        var (ok, log, _) = Mix(wired: true, "Test.First", "Test.Middle", "Test.Last");

        Assert.True(ok, log);
    }

    // A class comes after its parents, so inheritance is an order
    [Fact]
    public void InheritanceSettlesAConflict()
    {
        WriteOwner();
        Write("Parent", "shader Parent { import Owner; [Phase(A)] module override void Run() { base.Run(); streams.Steps = 2; } }");
        Write("Child", "shader Child : Parent { [Phase(A)] module override void Run() { base.Run(); streams.Steps = 3; } }");

        var (ok, log, _) = Mix(wired: true, "Test.Child");

        Assert.True(ok, log);
    }

    // A contributor that a replacement drops never runs
    [Fact]
    public void ADroppedContributorConflictsWithNothing()
    {
        WriteOwner();
        WriteModule("Dropped", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Replacement", "[Phase(A)] override void Run() { streams.Steps = 3; }");

        // In a wired effect, the drop is an error before any conflict is looked at
        var (ok, log, _) = Mix(wired: false, "Test.Dropped", "Test.Replacement");

        Assert.True(ok, log);
        Assert.Contains("SDSL0216", log);
        Assert.DoesNotContain("because of the order of the mix", log);
    }

    // The order of the mix: code without a phase never needs an order
    [Fact]
    public void ExternalHasNoConflicts()
    {
        WriteOwner();
        WriteModule("First", "override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "override void Run() { base.Run(); streams.Steps = 3; }");

        var (ok, log, _) = Mix(wired: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("[Replaces(Step)] void F() { }", "SDSL0135")]
    [InlineData("[Replaces(Step)] override float Plain() { return 2; }", "SDSL0246")]
    [InlineData("[Replaces] override void Run() { base.Run(); }", "SDSL0135")]
    [InlineData("[Replaces(\"Test.Step\")] override void Run() { base.Run(); }", "on an override, [Replaces] takes names without quotes")]
    [InlineData("[Replaces(NoSuchClass)] override void Run() { base.Run(); }", "SDSL0110")]
    public void AWrongReplacesIsAnError(string member, string code)
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Wrong", member);

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains(code, log);
    }

    [Fact]
    public void ABareNameOnAClassIsAnError()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");
        Write("Wrong", "[Replaces(Step)] module Wrong { import Step; }");

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains("on a class, [Replaces] takes a full name as a string", log);
    }

    [Fact]
    public void ReplacesTakesAClassOutOfThisChainOnly()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); } override float Wrap() { return base.Wrap(); }");
        WriteModule("Other", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Replacement", "[Phase(B)] [Replaces(Step)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(wired: true, "Test.Step", "Test.Other", "Test.Replacement");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > Other > Replacement", block[0]);
        Assert.Contains("replaced by Replacement: Step", block);
        Assert.Equal("module method Wrap() -> float = Owner > Step", Block(explanation, "Wrap")[0]);
    }

    [Fact]
    public void ReplacesTakesAPhaseOutOfTheChain()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Later", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("Replacement", "[Phase(A)] [Replaces(A)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(wired: true, "Test.First", "Test.Second", "Test.Later", "Test.Replacement");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > Replacement > Later", block[0]);
        Assert.Contains("replaced by Replacement: First, Second", block);
    }

    // What the design calls a replacement without a report: it is stated, so nothing is dropped
    [Fact]
    public void AStatedReplacementIsNotADrop()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Replacement", "[Phase(A)] [Replaces(Step)] override void Run() { streams.Steps = 2; }");

        var (ok, log, _) = Mix(wired: true, "Test.Step", "Test.Replacement");

        Assert.True(ok, log);
        Assert.DoesNotContain("SDSL0216", log);
    }

    // Step isn't reached: the base call that would reach it is in the class that was taken out
    [Fact]
    public void AReplacedContributorReachesNothing()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Middle", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Replacement", "[Phase(A)] [Replaces(Middle)] override void Run() { streams.Steps = 2; }");

        var (ok, log, _) = Mix(wired: true, "Test.Step", "Test.Middle", "Test.Replacement");

        Assert.False(ok, log);
        Assert.Contains("SDSL0216: Replacement.Run doesn't call base, so Step never run", log);
    }

    [Fact]
    public void ReplacingAClassThatIsNotInTheEffectIsIgnored()
    {
        WriteOwner();
        WriteModule("Absent", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Replacement", "[Phase(A)] [Replaces(Absent)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix("Test.Replacement");

        Assert.True(ok, log);
        Assert.Equal("module method Run() -> void = Owner > Replacement", Block(explanation, "Run")[0]);
    }

    [Fact]
    public void ReplacingEachOtherIsAnError()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] [Replaces(Second)] override void Run() { base.Run(); }");
        WriteModule("Second", "[Phase(A)] [Replaces(First)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138", log);
    }

    // The one a [Replace] point keeps: the stated replacement, wherever the mix puts it
    [Fact]
    public void ReplacesPicksTheWinnerOfAReplacePoint()
    {
        WriteOwner();
        WriteModule("Winner", "[Replaces(Other)] override float Pick() { return 2; }");
        WriteModule("Other", "override float Pick() { return 3; }");

        var (ok, log, explanation) = Mix(wired: true, "Test.Winner", "Test.Other");

        Assert.True(ok, log);
        var block = Block(explanation, "Pick");
        Assert.Equal("module method Pick() -> float = Owner > Winner", block[0]);
        Assert.Contains("replace: Winner", block);
        Assert.Contains("replaced by Winner: Other", block);
    }

    // The node is the effect of an instance method: classes mixed into one composition
    private (bool Ok, string Log, string Explanation) MixInComposition(params string[] classes)
    {
        Write("Entry", Entry);
        Write("Host", "module Host { import Entry; compose Part Slot; }");
        var node = new ShaderMixinSource();
        foreach (var name in classes)
            node.Mixins.Add(new ShaderClassSource(name));
        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource("Test.Host") } };
        source.AddComposition("Slot", node);

        var log = new LoggerResult();
        var mixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL") { WarningsAsErrors = false }) { CollectExplanation = true };
        var ok = mixer.MergeSDSL(source, new ShaderMixer.Options(true), log, out _, out _, out _, out _);
        return (ok, string.Join(Environment.NewLine, log.Messages.Select(x => $"{x.Type}: {x.Text}")), mixer.Explanation ?? "");
    }

    [Fact]
    public void AnInstanceMethodIsAPointOfItsNode()
    {
        Write("Part", "shader Part { [Pipeline(A, B)] void Go() { } }");
        Write("PartB", "shader PartB : Part { [Phase(B)] override void Go() { base.Go(); } }");
        Write("PartA", "shader PartA : Part { [Phase(A)] override void Go() { base.Go(); } }");

        var (ok, log, explanation) = MixInComposition("Test.PartB", "Test.PartA");

        Assert.True(ok, log);
        var block = Block(explanation, "Go");
        Assert.Equal("method Go() -> void = Part > PartA > PartB", block[0]);
        Assert.Contains("pipeline", block);
    }

    [Fact]
    public void AnUndeclaredInstanceMethodKeepsTheOrderOfTheMix()
    {
        Write("Part", "shader Part { void Go() { } }");
        Write("PartB", "shader PartB : Part { override void Go() { base.Go(); } }");
        Write("PartA", "shader PartA : Part { override void Go() { base.Go(); } }");

        var (ok, log, explanation) = MixInComposition("Test.PartB", "Test.PartA");

        Assert.True(ok, log);
        Assert.Equal("method Go() -> void = Part > PartB > PartA", Block(explanation, "Go")[0]);
        Assert.DoesNotContain("SDSL0246", log);
    }

    [Fact]
    public void TheOrderOfTheMixDecidesAConflictInAMixBuiltAtRunTime()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 3; }");

        var (ok, log, explanation) = Mix("Test.Second", "Test.First");

        Assert.True(ok, log);
        Assert.Contains("Info: Owner.Run, phase A: Second runs before First because of the order of the mix", log);
        Assert.Contains("A: Second > First   order: builder", Block(explanation, "Run"));
    }

    [Fact]
    public void ExplainNamesWhatOrdersAPhase()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "[Phase(A)] [After(First)] override void Run() { base.Run(); streams.Steps = 3; }");
        WriteModule("Free1", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("Free2", "[Phase(B)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix("Test.Second", "Test.First", "Test.Free1", "Test.Free2");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Contains("A: First > Second   order: after", block);
        Assert.Contains("B: Free1 > Free2   order: tie-break", block);
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
    [InlineData("[Pipeline(A)]", false)]
    [InlineData("[Replace]", false)]
    [InlineData("[Around]", false)]
    [InlineData("", true)]
    public void OverridingAnUndeclaredModuleMethodIsAnError(string kind, bool isError)
    {
        Write("Base", $"module Base {{ {kind} void F() {{ }} }}");
        Write("Patch", "module Patch { import Base; override void F() { base.F(); } }");

        var (ok, log, _) = Mix("Test.Patch");

        Assert.Equal(isError, !ok);
        Assert.Equal(isError, log.Contains("SDSL0246"));
        if (isError)
            Assert.Contains("If you own Base, add [Replace], [Pipeline] or [Around] to the first definition of F. If you don't, write patch Base.F instead of override.", log);
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
    [InlineData(true, "Error")]
    public void ADroppedContributorIsReported(bool wired, string type)
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Replacement", "override void Run() { streams.Steps = 2; }");

        var (ok, log, explanation) = Mix(wired, "Test.Step", "Test.Replacement");

        Assert.Equal(!wired, ok);
        Assert.Contains($"{type}: SDSL0216: Replacement.Run doesn't call base, so Step never run in this effect. Write [Replaces(Step)] on it, or call base.", log);
        Assert.Contains("dropped by Replacement: Step", Block(explanation, "Run"));
    }

    // The message gives a line to write, and that line is enough
    [Fact]
    public void WritingWhatTheMessageOfADropSaysCompiles()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Other", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Replacement", "override void Run() { streams.Steps = 2; }");

        var (ok, log, _) = Mix(wired: true, "Test.Step", "Test.Other", "Test.Replacement");

        Assert.False(ok, log);
        Assert.Contains("Write [Replaces(Step, Other)] on it", log);

        WriteModule("Replacement", "[Replaces(Step, Other)] override void Run() { streams.Steps = 2; }");
        (ok, log, _) = Mix(wired: true, "Test.Step", "Test.Other", "Test.Replacement");

        Assert.True(ok, log);
        Assert.DoesNotContain("SDSL0216", log);
    }

    [Fact]
    public void ReplacingTheDefaultBodyIsNotADrop()
    {
        WriteOwner();
        WriteModule("Replacement", "override void Run() { streams.Steps = 2; }");

        var (ok, log, _) = Mix(wired: true, "Test.Replacement");

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

        var (ok, log, _) = Mix(wired: true, "Test.Child");

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

        var (ok, log, _) = Mix(wired: true, "Test.Replacement");

        Assert.False(ok, log);
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

    [Fact]
    public void AnOrderLineOrdersAPhaseAndWinsOverAfter()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] [After(Second)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 3; }");

        var (ok, log, explanation) = Mix(true, x => x.AddWiring(ShaderWiringKind.Order, "Test.Owner", "Run", "A", "Test.First", "Test.Second"), "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Contains("A: First > Second   order: order line", Block(explanation, "Run"));
    }

    [Fact]
    public void AnOrderLineSettlesAConflict()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 3; }");

        var (noLine, noLineLog, _) = Mix(wired: true, "Test.First", "Test.Second");
        Assert.False(noLine, noLineLog);
        Assert.Contains("this line to the effect: order Owner.Run.A: First, Second;", noLineLog);

        // The last layer wins, and a name as written counts
        var (ok, log, explanation) = Mix(true, x =>
        {
            x.AddWiring(ShaderWiringKind.Order, "Test.Owner", "Run", "A", "Test.First", "Test.Second");
            x.AddWiring(ShaderWiringKind.Order, "Owner", "Run", "A", "Second", "First");
        }, "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Contains("A: Second > First   order: order line", Block(explanation, "Run"));
    }

    [Fact]
    public void AnOrderLineOfAPhaseReachedFromTheOutsideIsTheRunningOrder()
    {
        WriteOwner();
        WriteModule("First", "[Phase(Early)] override void Run() { streams.Steps = 2; base.Run(); }");
        WriteModule("Second", "[Phase(Early)] override void Run() { streams.Steps = 3; base.Run(); }");

        var (ok, log, explanation) = Mix(true, x => x.AddWiring(ShaderWiringKind.Order, "Test.Owner", "Run", "Early", "Test.First", "Test.Second"), "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Equal("module method Run() -> void = Owner > Second > First", Block(explanation, "Run")[0]);
    }

    [Fact]
    public void BindGivesAPhaseToAClassThatHasNone()
    {
        WriteOwner();
        WriteModule("Later", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("Patch", "override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(true, x => x.AddWiring(ShaderWiringKind.Bind, "Test.Owner", "Run", "A", "Test.Patch"), "Test.Later", "Test.Patch");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > Patch > Later", block[0]);
        Assert.Contains("A: Patch", block);
    }

    [Fact]
    public void UnbindTakesAPhaseOrAClassOutOfTheChainWithoutADrop()
    {
        WriteOwner();
        WriteModule("InPhase", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Patch", "override void Run() { base.Run(); }");
        WriteModule("Replacement", "override void Run() { streams.Steps = 2; }");

        var (ok, log, explanation) = Mix(true, x =>
        {
            x.AddWiring(ShaderWiringKind.Unbind, "Test.Owner", "Run", "A");
            x.AddWiring(ShaderWiringKind.Unbind, "Test.Owner", "Run", null, "Test.Patch");
        }, "Test.InPhase", "Test.Patch", "Test.Replacement");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > Replacement", block[0]);
        Assert.Contains("not bound by the effect: InPhase, Patch", block);
        Assert.DoesNotContain("SDSL0216", log);
    }

    [Theory]
    [InlineData(ShaderWiringKind.Order)]
    [InlineData(ShaderWiringKind.Bind)]
    [InlineData(ShaderWiringKind.Unbind)]
    public void ALineWithAPhaseThatDoesNotExistIsAnError(ShaderWiringKind kind)
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, _) = MixCatching(x => x.AddWiring(kind, "Test.Owner", "Run", "Missing", "Test.Step"), "Test.Step");

        Assert.False(ok, log);
        Assert.Contains("SDSL0141", log);
    }

    private (bool Ok, string Log, string Explanation) MixCatching(Action<ShaderMixinSource> wiring, params string[] classes) => Mix(false, wiring, classes);

    [Fact]
    public void ALineAboutAPointThatIsNotInTheEffectIsIgnored()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix(true, x => x.AddWiring(ShaderWiringKind.Unbind, "Test.Other", "Run", "A"), "Test.Step");

        Assert.True(ok, log);
    }

    [Fact]
    public void APhaseLineAddsAPhase()
    {
        WriteOwner();
        WriteModule("InB", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("InA", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Patch", "override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(true, x =>
        {
            x.AddWiring(ShaderWiringKind.Phase, "Test.Owner", "Run", "Wind", "A", "B");
            x.AddWiring(ShaderWiringKind.Bind, "Test.Owner", "Run", "Wind", "Test.Patch");
        }, "Test.InB", "Test.InA", "Test.Patch");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > InA > Patch > InB", block[0]);
        Assert.Contains("Wind: Patch", block);
    }

    [Fact]
    public void AnOverrideAddsAPhase()
    {
        WriteOwner();
        WriteModule("InB", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("InA", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Wind", "[Phase(Wind, After = A, Before = B)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(wired: true, "Test.InB", "Test.Wind", "Test.InA");

        Assert.True(ok, log);
        Assert.Equal("module method Run() -> void = Owner > InA > Wind > InB", Block(explanation, "Run")[0]);
    }

    [Fact]
    public void APhaseAddedNextToAPhaseThatDoesNotExistIsAnError()
    {
        WriteOwner();
        WriteModule("InB", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("Wind", "[Phase(Wind, After = Missing)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix(wired: false, "Test.Wind", "Test.InB");

        Assert.False(ok, log);
        Assert.Contains("SDSL0218: phase Wind added by Wind to Owner.Run has no place: Owner.Run has no phase Missing", log);
    }

    [Theory]
    [InlineData("[Phase(Wind, Other = A)] override void Run() { base.Run(); }", "SDSL0135")]
    [InlineData("[Phase(Wind, After = A)] void F() { }", "SDSL0141")]
    public void AWrongAddedPhaseIsAnError(string member, string code)
    {
        WriteOwner();
        WriteModule("Wrong", member);

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains(code, log);
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
    [InlineData("[Replace] shader Wrong { }")]
    [InlineData("[Pipeline(A, B)] shader Wrong { }")]
    [InlineData("[Around] module Wrong { }")]
    [InlineData("[Phase(A)] shader Wrong { }")]
    [InlineData("[After(Other)] shader Wrong { }")]
    [InlineData("[Collect] shader Wrong { }")]
    [InlineData("shader Wrong { [Pipeline] cbuffer PerDraw { float A; } }")]
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

    // SDSL0148: the order of two patches would be the order of the mix, which nothing states
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoPatchesWithNoOrderAreAnError(bool wired)
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain { return next() + 1; }");
        WriteModule("PatchB", "patch Owner.Plain { return next() * 2; }");

        var (ok, log, _) = Mix(wired, "Test.PatchA", "Test.PatchB");

        Assert.False(ok, log);
        Assert.Contains("SDSL0148: PatchA and PatchB both patch Owner.Plain, and nothing orders them. Add 'after PatchA' to the patch in PatchB, or this line to the effect: order Owner.Plain.External: PatchA, PatchB;", log);
    }

    [Theory]
    [InlineData("Test.PatchA", "Test.PatchB")]
    [InlineData("Test.PatchB", "Test.PatchA")]
    public void AfterOrdersTwoPatches(string first, string second)
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain { return next() + 1; }");
        WriteModule("PatchB", "patch Owner.Plain after PatchA { return next() * 2; }");

        var (ok, log, explanation) = Mix(first, second);

        Assert.True(ok, log);
        Assert.Equal("module method Plain() -> float = Owner > PatchA > PatchB", Block(explanation, "Plain")[0]);
    }

    [Fact]
    public void AfterOrdersPatchesThroughAThird()
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain { return next() + 1; }");
        WriteModule("PatchB", "patch Owner.Plain after PatchA { return next() + 2; }");
        WriteModule("PatchC", "patch Owner.Plain after PatchB { return next() + 3; }");

        var (ok, log, explanation) = Mix("Test.PatchC", "Test.PatchA", "Test.PatchB");

        Assert.True(ok, log);
        Assert.Equal("module method Plain() -> float = Owner > PatchA > PatchB > PatchC", Block(explanation, "Plain")[0]);
    }

    // Each pair needs an order
    [Fact]
    public void AThirdPatchWithNoOrderIsAnError()
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain { return next() + 1; }");
        WriteModule("PatchB", "patch Owner.Plain after PatchA { return next() + 2; }");
        WriteModule("PatchC", "patch Owner.Plain { return next() + 3; }");

        var (ok, log, _) = Mix("Test.PatchA", "Test.PatchB", "Test.PatchC");

        Assert.False(ok, log);
        Assert.Contains("SDSL0148: PatchA and PatchC both patch", log);
    }

    [Fact]
    public void PatchesAfterEachOtherAreAnError()
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain after PatchB { return next() + 1; }");
        WriteModule("PatchB", "patch Owner.Plain after PatchA { return next() + 2; }");

        var (ok, log, _) = Mix("Test.PatchA", "Test.PatchB");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138", log);
    }

    // The class named by 'after' isn't in the effect: nothing orders the two that are
    [Fact]
    public void AfterAClassOutsideTheEffectIsNoOrder()
    {
        WriteOwner();
        WriteModule("PatchOther", "patch Owner.Plain { return next() + 1; }");
        WriteModule("PatchA", "patch Owner.Plain after PatchOther { return next() + 2; }");
        WriteModule("PatchB", "patch Owner.Plain { return next() + 3; }");

        var (ok, log, _) = Mix("Test.PatchA", "Test.PatchB");

        Assert.False(ok, log);
        Assert.Contains("SDSL0148: PatchA and PatchB", log);
    }

    // Two patches of libraries that the author of the effect can't change
    [Fact]
    public void AnOrderLineOrdersTwoPatches()
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain { return next() + 1; }");
        WriteModule("PatchB", "patch Owner.Plain { return next() * 2; }");

        var (ok, log, explanation) = Mix(true, x => x.AddWiring(ShaderWiringKind.Order, "Test.Owner", "Plain", "External", "Test.PatchB", "Test.PatchA"), "Test.PatchA", "Test.PatchB");

        Assert.True(ok, log);
        Assert.Equal("module method Plain() -> float = Owner > PatchB > PatchA", Block(explanation, "Plain")[0]);
    }

    [Fact]
    public void OnePatchNeedsNoOrder()
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain { return next() + 1; }");

        var (ok, log, _) = Mix("Test.PatchA");

        Assert.True(ok, log);
    }

    // A class comes after the classes it inherits
    [Fact]
    public void APatchOfAChildComesAfterThePatchOfItsParent()
    {
        WriteOwner();
        Write("PatchParent", "shader PatchParent { import Owner; patch Owner.Plain { return next() + 1; } }");
        Write("PatchChild", "shader PatchChild : PatchParent { patch Owner.Plain { return next() * 2; } }");

        var (ok, log, explanation) = Mix("Test.PatchChild");

        Assert.True(ok, log);
        Assert.Equal("module method Plain() -> float = Owner > PatchParent > PatchChild", Block(explanation, "Plain")[0]);
    }

    // The rule is on patches: two overrides of an instance method follow the order of the mix, as they always did
    [Fact]
    public void OverridesOfAnInstanceMethodKeepTheOrderOfTheMix()
    {
        WriteOwner();
        Write("InstanceBase", "shader InstanceBase { float Value() { return 1; } }");
        Write("InstanceA", "shader InstanceA : InstanceBase { override float Value() { return base.Value() + 1; } }");
        Write("InstanceB", "shader InstanceB : InstanceBase { override float Value() { return base.Value() * 2; } }");

        var (ok, log, _) = Mix("Test.InstanceA", "Test.InstanceB");

        Assert.True(ok, log);
        Assert.DoesNotContain("SDSL0148", log);
    }

    // One patch and an override of an instance method: one patch needs no order
    [Fact]
    public void APatchAndAnOverrideOfAnInstanceMethodNeedNoOrder()
    {
        WriteOwner();
        Write("InstanceBase", "shader InstanceBase { float Value() { return 1; } }");
        Write("InstanceA", "shader InstanceA : InstanceBase { override float Value() { return base.Value() + 1; } }");
        Write("InstanceB", "shader InstanceB : InstanceBase { patch InstanceBase.Value() { return next() * 2; } }");

        var (ok, log, _) = Mix("Test.InstanceA", "Test.InstanceB");

        Assert.True(ok, log);
    }

    [Fact]
    public void TwoPatchesOfAnInstanceMethodNeedAnOrder()
    {
        WriteOwner();
        Write("InstanceBase", "shader InstanceBase { float Value() { return 1; } }");
        Write("InstanceA", "shader InstanceA : InstanceBase { patch InstanceBase.Value() { return next() + 1; } }");
        Write("InstanceB", "shader InstanceB : InstanceBase { patch InstanceBase.Value() { return next() * 2; } }");

        var (ok, log, _) = Mix("Test.InstanceA", "Test.InstanceB");

        Assert.False(ok, log);
        Assert.Contains("SDSL0148: InstanceA and InstanceB both patch InstanceBase.Value", log);
    }

    // A patch has no phases, and 'in' used to be read and dropped
    [Fact]
    public void APatchInAPhaseIsAnError()
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain in A { return next() + 1; }");

        var (ok, log, _) = Mix("Test.PatchA");

        Assert.False(ok, log);
        Assert.Contains("SDSL0143: patch of Owner.Plain: it is not an extension point, so it has no phases.", log);
    }

    [Fact]
    public void AnOverrideOfAMethodWithNoKindNamesPatch()
    {
        WriteOwner();
        WriteModule("Plainer", "override float Plain() { return 2; }");

        var (ok, log, _) = Mix("Test.Plainer");

        Assert.False(ok, log);
        Assert.Contains("SDSL0246: Plainer overrides Plain of Owner, which is not an extension point. If you own Owner, add [Replace], [Pipeline] or [Around] to the first definition of Plain. If you don't, write patch Owner.Plain instead of override.", log);
    }
}
