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

    [Theory]
    [InlineData("[MixinOrder(After = StepB)] void F() { }", "SDSL0135")]
    [InlineData("[MixinOrder(After = StepB)] override float Plain() { return 2; }", "SDSL0246")]
    [InlineData("[MixinOrder(After = StepB)] override float Pick() { return 2; }", "SDSL0135")]
    [InlineData("[MixinOrder] override void Run() { base.Run(); }", "SDSL0135")]
    [InlineData("[MixinOrder(After = StepB + 1)] override void Run() { base.Run(); }", "SDSL0135")]
    [InlineData("[MixinOrder(StepB)] override void Run() { base.Run(); }", "SDSL0135: [MixinOrder] on Wrong.Run: on an override, [MixinOrder] takes After = and Before =")]
    [InlineData("[MixinOrder(After = \"Test.StepB\")] override void Run() { base.Run(); }", "SDSL0255: [MixinOrder] on Wrong.Run: \"Test.StepB\" is a string.")]
    [InlineData("[MixinOrder(After = NoSuchClass)] override void Run() { base.Run(); }", "SDSL0110")]
    public void AWrongMixinOrderIsAnError(string member, string code)
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
    public void MixinOrderAfterAClassOfALaterPhaseIsAnError()
    {
        WriteOwner();
        WriteModule("StepB", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("StepA", "[Phase(A)] [MixinOrder(After = StepB)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.StepB", "Test.StepA");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138", log);
    }

    [Fact]
    public void MixinOrderAfterAnOverrideWithoutPhaseIsAnError()
    {
        WriteOwner();
        WriteModule("Patch", "override void Run() { base.Run(); }");
        WriteModule("StepA", "[Phase(A)] [MixinOrder(After = Patch)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.Patch", "Test.StepA");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138", log);
    }

    [Fact]
    public void MixinOrderAfterGoingInACircleIsAnError()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] [MixinOrder(After = Second)] override void Run() { base.Run(); }");
        WriteModule("Second", "[Phase(A)] [MixinOrder(After = First)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138", log);
    }

    [Fact]
    public void MixinOrderAfterOrdersAPhaseThatCallsBaseFirst()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] [MixinOrder(After = Second)] override void Run() { base.Run(); }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix("Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Contains("A: Second > First", Block(explanation, "Run"));
    }

    // The chain is run from its end here: the step that runs later is reached from the one that runs first, so it is lower in the chain
    [Fact]
    public void MixinOrderAfterOrdersAPhaseThatCallsBaseLast()
    {
        WriteOwner();
        WriteModule("First", "[Phase(Early)] override void Run() { streams.Steps = 2; base.Run(); }");
        WriteModule("Second", "[Phase(Early)] [MixinOrder(After = First)] override void Run() { streams.Steps = 3; base.Run(); }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Equal("module method Run() -> void = Owner > Second > First", Block(explanation, "Run")[0]);
    }

    [Fact]
    public void MixinOrderAfterOrdersAnAroundPhaseFromTheOutside()
    {
        WriteOwner();
        WriteModule("Inner", "[Phase(Outer)] [MixinOrder(After = Outermost)] override float Wrap() { return base.Wrap(); }");
        WriteModule("Outermost", "[Phase(Outer)] override float Wrap() { return base.Wrap(); }");

        var (ok, log, explanation) = Mix("Test.Outermost", "Test.Inner");

        Assert.True(ok, log);
        Assert.Equal("module method Wrap() -> float = Owner > Inner > Outermost", Block(explanation, "Wrap")[0]);
    }

    [Fact]
    public void MixinOrderAfterAClassThatIsNotInTheEffectIsIgnored()
    {
        WriteOwner();
        WriteModule("Absent", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Step", "[Phase(A)] [MixinOrder(After = Absent)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.Step");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("streams.Steps = 2;", "streams.Steps = 3;", "streams.Steps")]
    [InlineData("streams.Other = streams.Steps;", "streams.Steps = 3;", "streams.Steps")]
    [InlineData("Helper();", "streams.Steps = 3;", "streams.Steps")]
    [InlineData("discard;", "discard;", "side effect")]
    [InlineData("Values[0] = 1;", "discard;", "side effect")]
    public void AConflictInAPhaseIsAnErrorInAnAssetBuild(string first, string second, string message)
    {
        WriteOwner();
        Write("Helpers", "module Helpers { import Owner; stream float Other; RWStructuredBuffer<float> Values; void Helper() { streams.Steps = 2; } }");
        Write("First", $"module First {{ import Helpers; [Phase(A)] override void Run() {{ base.Run(); {first} }} }}");
        Write("Second", $"module Second {{ import Helpers; [Phase(A)] override void Run() {{ base.Run(); {second} }} }}");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0137", log);
        Assert.Contains(message, log);

        // In a mix built at run time, the order of the mix decides
        var (runtimeOk, runtimeLog, _) = Mix("Test.First", "Test.Second");

        Assert.True(runtimeOk, runtimeLog);
    }

    private void WritePlaces(string first, string second)
    {
        Write("Places", """
            module Places
            {
                struct Data { float a; float b; float c[2]; };
                static Data Current;
                static float Shared;

                [Phases(A)]
                pipeline void Run(inout Data s) { }

                [Phases(A)]
                pipeline void Fill(out Data s) { s = (Data)0; }

                [Phases(A)]
                pipeline void Look(Data s) { }

                void SetA(inout Data d) { d.a = 1; }
                void SetALocally(Data d) { d.a = 1; }
                float GetA(Data d) { return d.a; }
                void SetCurrent() { Current.a = 1; }
            }
            """);
        Write("First", $"module First {{ import Places; [Phase(A)] override void Run(inout Data s) {{ base.Run(s); {first} }} }}");
        Write("Second", $"module Second {{ import Places; [Phase(A)] override void Run(inout Data s) {{ base.Run(s); {second} }} }}");
    }

    // A field of an inout parameter or of a module static is a place, as a stream is
    [Theory]
    [InlineData("s.a = 1;", "s.a = 2;", "s.a")]
    [InlineData("s.b = s.a;", "s.a = 2;", "s.a")]
    [InlineData("s = (Data)0;", "s.b = 2;", "s")]
    [InlineData("Data copy = s; s.b = copy.b;", "s.a = 2;", "s.a")]
    [InlineData("SetA(s);", "s.a = 2;", "s.a")]
    [InlineData("s.b = GetA(s);", "s.a = 2;", "s.a")]
    [InlineData("s.c[0] = 1;", "s.c[0] = 2;", "s.c[0]")]
    [InlineData("int i = 1; s.c[i] = 1;", "s.c[1] = 2;", "s.c")]
    [InlineData("Current.a = 1;", "Current.a = 2;", "Current.a")]
    [InlineData("SetCurrent();", "s.b = Current.a;", "Current.a")]
    [InlineData("Shared = 1;", "s.b = Shared;", "Shared")]
    public void TwoContributorsOfOnePlaceConflict(string first, string second, string place)
    {
        WritePlaces(first, second);

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0137", log);
        Assert.Contains($"share {place}", log);
    }

    // The copy of the struct around the call to the next one, the copy a helper takes by value, and other fields are nothing to order
    [Theory]
    [InlineData("s.a = 1;", "s.b = 2;")]
    [InlineData("s.b = s.a;", "s.c[0] = s.a;")]
    [InlineData("s.c[0] = 1;", "s.c[1] = 2;")]
    [InlineData("float x = s.a;", "float y = s.a;")]
    [InlineData("SetALocally(s);", "s.a = 2;")]
    [InlineData("s.c[0] = GetA(s);", "s.b = 2;")]
    [InlineData("Current.a = 1;", "Current.b = 2;")]
    [InlineData("Data local = (Data)0; local.a = 1;", "s.a = 2;")]
    public void OtherPlacesDoNotConflict(string first, string second)
    {
        WritePlaces(first, second);

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
    }

    [Fact]
    public void AnOutParameterIsAPlace()
    {
        WritePlaces("", "");
        Write("First", "module First { import Places; [Phase(A)] override void Fill(out Data s) { base.Fill(s); s.a = 1; } }");
        Write("Second", "module Second { import Places; [Phase(A)] override void Fill(out Data s) { base.Fill(s); s.a = 2; } }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("share s.a", log);
    }

    // Each one gets its own copy
    [Fact]
    public void AParameterByValueIsNotAPlace()
    {
        WritePlaces("", "");
        Write("First", "module First { import Places; [Phase(A)] override void Look(Data s) { base.Look(s); s.a = 1; } }");
        Write("Second", "module Second { import Places; [Phase(A)] override void Look(Data s) { base.Look(s); s.a = 2; } }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
    }

    [Fact]
    public void TwoWrappersOfOnePhaseConflict()
    {
        WriteOwner();
        WriteModule("First", "[Phase(Inner)] override float Wrap() { return base.Wrap() + 1; }");
        WriteModule("Second", "[Phase(Inner)] override float Wrap() { return base.Wrap() * 2; }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0137", log);
        Assert.Contains("wrap each other", log);
    }

    [Theory]
    [InlineData("[Phase(Inner)] [MixinOrder(After = First)]", "[Phase(Inner)]")]
    [InlineData("[Phase(Outer)]", "[Phase(Inner)]")]
    public void WrappersWithAnOrderDoNotConflict(string secondAttributes, string firstAttributes)
    {
        WriteOwner();
        WriteModule("First", $"{firstAttributes} override float Wrap() {{ return base.Wrap() + 1; }}");
        WriteModule("Second", $"{secondAttributes} override float Wrap() {{ return base.Wrap() * 2; }}");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
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

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

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

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

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

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

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

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

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

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
    }

    // What the steps of other phases do isn't part of the step, although base reaches them
    [Fact]
    public void BaseIsNotPartOfWhatAContributorDoes()
    {
        WriteOwner();
        WriteModule("Writes", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Later", "[Phase(B)] override void Run() { base.Run(); streams.Steps = 3; }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.Writes", "Test.Later");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("[MixinOrder(After = First)]")]
    [InlineData("[MixinOrder(After = Middle)]")]
    public void MixinOrderAfterSettlesAConflict(string attribute)
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Middle", "[Phase(A)] [MixinOrder(After = First)] override void Run() { base.Run(); }");
        Write("Last", $"module Last {{ import First; import Middle; [Phase(A)] {attribute} override void Run() {{ base.Run(); streams.Steps = 3; }} }}");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Middle", "Test.Last");

        Assert.True(ok, log);
    }

    // A class comes after its parents, so inheritance is an order
    [Fact]
    public void InheritanceSettlesAConflict()
    {
        WriteOwner();
        Write("Parent", "shader Parent { import Owner; [Phase(A)] module override void Run() { base.Run(); streams.Steps = 2; } }");
        Write("Child", "shader Child : Parent { [Phase(A)] module override void Run() { base.Run(); streams.Steps = 3; } }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.Child");

        Assert.True(ok, log);
    }

    // A contributor that a replacement drops never runs
    [Fact]
    public void ADroppedContributorConflictsWithNothing()
    {
        WriteOwner();
        WriteModule("Dropped", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Replacement", "[Phase(A)] override void Run() { streams.Steps = 3; }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.Dropped", "Test.Replacement");

        Assert.True(ok, log);
    }

    // The order of the mix: code without a phase never needs an order
    [Fact]
    public void ExternalHasNoConflicts()
    {
        WriteOwner();
        WriteModule("First", "override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "override void Run() { base.Run(); streams.Steps = 3; }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
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
    [InlineData("[MixinOrder(After = Other)] shader Wrong { }")]
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
