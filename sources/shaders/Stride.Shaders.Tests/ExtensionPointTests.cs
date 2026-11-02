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

    private (bool Ok, string Log, string Explanation) Mix(bool isAssetBuild, params string[] classes) => Mix(isAssetBuild, _ => { }, classes);

    private (bool Ok, string Log, string Explanation) Mix(bool isAssetBuild, Action<ShaderMixinSource> linkage, params string[] classes)
    {
        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource("Test.Entry") } };
        foreach (var name in classes)
            source.Mixins.Add(new ShaderClassSource(name));
        linkage(source);
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
        Assert.Contains("A: Second > First   order: mixin order", Block(explanation, "Run"));
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

        var (runtimeOk, runtimeLog, explanation) = Mix("Test.First", "Test.Second");

        Assert.True(runtimeOk, runtimeLog);
        Assert.Contains($"share {place}", runtimeLog);
        Assert.Contains("A: Places > First > Second   order: builder", Block(explanation, "Run"));
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

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Contains("A: Places > First > Second   order: tie-break", Block(explanation, "Run"));
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

        var (runtimeOk, runtimeLog, explanation) = Mix("Test.First", "Test.Second");

        Assert.True(runtimeOk, runtimeLog);
        Assert.Contains("wrap each other", runtimeLog);
        Assert.Contains("order: builder", string.Join('\n', Block(explanation, "Wrap")));
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
    // [MixinOrder] and a block take the name of a class, without its generic values
    [Fact]
    public void TheMessageOfAConflictNamesAClassAsItIsWritten()
    {
        WriteOwner();
        Write("Gen", "module Gen<int N> { import Owner; [Phase(A)] override void Run() { base.Run(); streams.Steps = N; } }");
        WriteModule("UsesGen", "import Gen<1>;");
        WriteModule("Other", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");

        var (ok, log, _) = Mix(true, "Test.UsesGen", "Test.Other");

        Assert.False(ok, log);
        Assert.Contains("SDSL0137", log);
        Assert.Contains("[MixinOrder(After = Gen)]", log);
    }

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

    [Theory]
    [InlineData("[Drops(Mixins = { Step })] void F() { }", "SDSL0135")]
    [InlineData("[Drops(Mixins = { Step })] override float Plain() { return 2; }", "SDSL0246")]
    [InlineData("[Drops] override void Run() { base.Run(); }", "SDSL0254")]
    [InlineData("[Drops(Step)] override void Run() { base.Run(); }", "SDSL0254: [Drops] on Wrong.Run: [Drops] takes Mixins = { ... } for classes and Phases = { ... } for phases.")]
    [InlineData("[Drops(Mixins = { A })] override void Run() { base.Run(); }", "SDSL0254: [Drops] on Wrong.Run: A is a phase of Owner.Run: write Phases = { A }.")]
    [InlineData("[Drops(Phases = { Step })] override void Run() { base.Run(); }", "SDSL0254: [Drops] on Wrong.Run: Step is a class: write Mixins = { Step }.")]
    [InlineData("[Drops(Phases = { Missing })] override void Run() { base.Run(); }", "SDSL0254: [Drops] on Wrong.Run: Owner.Run has no phase Missing.")]
    [InlineData("[Drops(Mixins = { \"Test.Step\" })] override void Run() { base.Run(); }", "SDSL0255: [Drops] on Wrong.Run: \"Test.Step\" is a string.")]
    [InlineData("[Drops(Mixins = { NoSuchClass })] override void Run() { base.Run(); }", "SDSL0110")]
    public void AWrongDropsIsAnError(string member, string code)
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");
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
    public void ReplacesTakesAClassOutOfThisChainOnly()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); } override float Wrap() { return base.Wrap(); }");
        WriteModule("Other", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Replacement", "[Phase(B)] [Drops(Mixins = { Step })] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.Step", "Test.Other", "Test.Replacement");

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
        WriteModule("Replacement", "[Phase(A)] [Drops(Phases = { A })] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.First", "Test.Second", "Test.Later", "Test.Replacement");

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
        WriteModule("Replacement", "[Phase(A)] [Drops(Mixins = { Step })] override void Run() { streams.Steps = 2; }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.Step", "Test.Replacement");

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
        WriteModule("Replacement", "[Phase(A)] [Drops(Mixins = { Middle })] override void Run() { streams.Steps = 2; }");

        var (ok, log, _) = Mix(isAssetBuild: true, "Test.Step", "Test.Middle", "Test.Replacement");

        Assert.True(ok, log);
        Assert.Contains("SDSL0216: Replacement.Run doesn't call base, so Step never run", log);
    }

    [Fact]
    public void ReplacingAClassThatIsNotInTheEffectIsIgnored()
    {
        WriteOwner();
        WriteModule("Absent", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Replacement", "[Phase(A)] [Drops(Mixins = { Absent })] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix("Test.Replacement");

        Assert.True(ok, log);
        Assert.Equal("module method Run() -> void = Owner > Replacement", Block(explanation, "Run")[0]);
    }

    [Fact]
    public void ReplacingEachOtherIsAnError()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] [Drops(Mixins = { Second })] override void Run() { base.Run(); }");
        WriteModule("Second", "[Phase(A)] [Drops(Mixins = { First })] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138", log);
    }

    // None of the three would run
    [Fact]
    public void ReplacingInACircleIsAnError()
    {
        WriteOwner();
        WriteModule("First", "[Drops(Mixins = { Third })] override float Pick() { return 2; }");
        WriteModule("Second", "[Drops(Mixins = { First })] override float Pick() { return 3; }");
        WriteModule("Third", "[Drops(Mixins = { Second })] override float Pick() { return 4; }");

        var (ok, log, _) = Mix("Test.First", "Test.Second", "Test.Third");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138", log);
        Assert.Contains("drop each other", log);
    }

    // The one a replace point keeps: the stated replacement, wherever the mix puts it
    [Fact]
    public void ReplacesPicksTheWinnerOfAReplacePoint()
    {
        WriteOwner();
        WriteModule("Winner", "[Drops(Mixins = { Other })] override float Pick() { return 2; }");
        WriteModule("Other", "override float Pick() { return 3; }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.Winner", "Test.Other");

        Assert.True(ok, log);
        var block = Block(explanation, "Pick");
        Assert.Equal("module method Pick() -> float = Owner > Winner", block[0]);
        Assert.Contains("replace: Winner", block);
        Assert.Contains("replaced by Winner: Other", block);
    }

    // The node is the effect of an instance method: classes mixed into one composition
    private (bool Ok, string Log, string Explanation) MixInComposition(params string[] classes) => MixInComposition(false, classes);

    private (bool Ok, string Log, string Explanation) MixInComposition(bool isAssetBuild, params string[] classes)
    {
        Write("Entry", Entry);
        Write("Host", "module Host { import Entry; compose Part Slot; }");
        var node = new ShaderMixinSource();
        foreach (var name in classes)
            node.Mixins.Add(new ShaderClassSource(name));
        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource("Test.Host") } };
        source.AddComposition("Slot", node);

        var log = new LoggerResult();
        var mixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL") { WarningsAsErrors = false }) { CollectExplanation = true, IsAssetBuild = isAssetBuild };
        var ok = mixer.MergeSDSL(source, new ShaderMixer.Options(true), log, out _, out _, out _, out _);
        return (ok, string.Join(Environment.NewLine, log.Messages.Select(x => $"{x.Type}: {x.Text}")), mixer.Explanation ?? "");
    }

    // A rule of the effect is about its module methods: SDSL0137 asks for what works in a node
    [Fact]
    public void AConflictInANodeDoesNotAskForAnOrderRule()
    {
        Write("Part", "shader Part { stream float Value; [Phases(A)] pipeline void Go() { } }");
        Write("PartOne", "shader PartOne : Part { [Phase(A)] override void Go() { base.Go(); streams.Value = 1; } }");
        Write("PartTwo", "shader PartTwo : Part { [Phase(A)] override void Go() { base.Go(); streams.Value = 2; } }");

        var (ok, log, _) = MixInComposition(true, "Test.PartOne", "Test.PartTwo");

        Assert.False(ok, log);
        Assert.Contains("SDSL0137", log);
        Assert.Contains("[MixinOrder(After = PartOne)] to PartTwo", log);
        Assert.Contains("[Phase(Vendor.Later, After = A)]", log);
        Assert.DoesNotContain("order Part.Go", log);
    }

    [Fact]
    public void AnInstanceMethodIsAPointOfItsNode()
    {
        Write("Part", "shader Part { [Phases(A, B)] pipeline void Go() { } }");
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
        WriteModule("Second", "[Phase(A)] [MixinOrder(After = First)] override void Run() { base.Run(); streams.Steps = 3; }");
        WriteModule("Free1", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("Free2", "[Phase(B)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix("Test.Second", "Test.First", "Test.Free1", "Test.Free2");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Contains("A: First > Second   order: mixin order", block);
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
            Assert.Contains("If you own Base, write replace, pipeline or around on the first definition of F. If you don't, write patch Base.F instead of override.", log);
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

    [Fact]
    public void ABlockListWinsOverTheOrderAClassStates()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] [MixinOrder(After = Second)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 3; }");

        var (ok, log, explanation) = Mix(true, x => x.MixinOrder("Test.Owner", "Run", "Test.First", "Test.Second"), "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Contains("A: First > Second   order: block", Block(explanation, "Run"));
    }

    [Fact]
    public void ABlockSettlesAConflict()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 3; }");

        var (noRule, noRuleLog, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");
        Assert.False(noRule, noRuleLog);
        Assert.Contains("this block to the effect: extend shader Owner { [MixinOrder(First, Second)] Run; }", noRuleLog);

        // A name as written counts
        var (ok, log, explanation) = Mix(true, x => x.MixinOrder("Owner", "Run", "Second", "First"), "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Contains("A: Second > First   order: block", Block(explanation, "Run"));
    }

    // Neither is further out, so neither replaces the other
    [Fact]
    public void TwoOppositeOrdersOfOneLevelAreAnError()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix(true, x =>
        {
            x.ExtendShader("Test.Owner", "One.sdfx, line 4").Method("Run").MixinOrder("Test.First", "Test.Second");
            x.ExtendShader("Owner", "Two.sdfx, line 9").Method("Run").MixinOrder("Second", "First");
        }, "Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138: the order of Owner.Run can't be followed: First runs before Second (block of One.sdfx, line 4) and after it (block of Two.sdfx, line 9), and neither is written further out to replace the other.", log);
    }

    private sealed class OrdersThree : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSource mixin, ShaderMixinContext context)
        {
            foreach (var name in new[] { "Test.Entry", "Test.First", "Test.Second", "Test.Third" })
                mixin.Mixins.Add(new ShaderClassSource(name));
            context.ExtendShader("Test.Owner", "Effect.sdfx, line 3").Method("Run").MixinOrder("Test.First", "Test.Second", "Test.Third");
        }
    }

    private sealed class PutsThirdFirst : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSource mixin, ShaderMixinContext context)
            => context.ExtendShader("Test.Owner", "Layer.sdfx, line 5").Method("Run").MixinOrder("Test.Third", "Test.Second");
    }

    // The layer replaces Second before Third, and First before Second still holds
    [Fact]
    public void ALayerReplacesOnlyTheOppositePair()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 1; }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Third", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 3; }");
        var name = $"Ordered{Guid.NewGuid():N}";
        ShaderMixinManager.Register(name, new OrdersThree());
        ShaderMixinManager.RegisterLayer(name, new PutsThirdFirst());

        var (ok, log, explanation) = Mix(false, ShaderMixinManager.Generate(name, new ParameterCollection()));

        Assert.True(ok, log);
        Assert.Contains("A: First > Third > Second   order: block, builder", Block(explanation, "Run"));
        Assert.Contains("Info: Owner.Run, phase A: First runs before Third because of the order of the mix", log);
    }

    [Fact]
    public void ACycleAcrossAClassAndABlockNamesEverySource()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] [MixinOrder(After = Third)] override void Run() { base.Run(); }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Third", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix(true, x => x.ExtendShader("Test.Owner", "Effect.sdfx, line 7").Method("Run").MixinOrder("Test.First", "Test.Second", "Test.Third"), "Test.First", "Test.Second", "Test.Third");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138: the order of Owner.Run can't be followed: these orders go in a circle:", log);
        Assert.Contains("Third before First ([MixinOrder] of First.sdsl, line 2)", log);
        Assert.Contains("First before Second (block of Effect.sdfx, line 7)", log);
        Assert.Contains("Second before Third (block of Effect.sdfx, line 7)", log);
    }

    [Fact]
    public void ABlockListOfAPhaseReachedFromTheOutsideIsTheRunningOrder()
    {
        WriteOwner();
        WriteModule("First", "[Phase(Early)] override void Run() { streams.Steps = 2; base.Run(); }");
        WriteModule("Second", "[Phase(Early)] override void Run() { streams.Steps = 3; base.Run(); }");

        var (ok, log, explanation) = Mix(true, x => x.MixinOrder("Test.Owner", "Run", "Test.First", "Test.Second"), "Test.First", "Test.Second");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > Second > First", block[0]);
        Assert.Contains("Early: First > Second   order: block", block);
    }

    [Fact]
    public void ABlockGivesAClassItsPhase()
    {
        WriteOwner();
        WriteModule("Later", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("Patch", "override void Run() { base.Run(); }");
        WriteModule("Moved", "[Phase(B)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(true, x =>
        {
            x.ExtendShader("Test.Patch").Method("Test.Owner.Run").Phase("A");
            x.ExtendShader("Test.Moved").Method("Test.Owner.Run").Phase("A");
        }, "Test.Later", "Test.Patch", "Test.Moved");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > Patch > Moved > Later", block[0]);
        Assert.Contains("A: Patch > Moved   order: tie-break", block);
    }

    [Fact]
    public void UnbindTakesAPhaseOrAClassOutOfTheChainWithoutADrop()
    {
        WriteOwner();
        WriteModule("InPhase", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Patch", "override void Run() { base.Run(); }");
        WriteModule("Replacement", "override void Run() { streams.Steps = 2; }");

        var (ok, log, explanation) = Mix(true, x => { x.UnbindPhases("Test.Owner", "Run", "A"); x.UnbindMixins("Test.Owner", "Run", "Test.Patch"); }, "Test.InPhase", "Test.Patch", "Test.Replacement");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > Replacement", block[0]);
        Assert.Contains("not bound by the effect: InPhase, Patch", block);
        Assert.DoesNotContain("SDSL0216", log);
    }

    [Theory]
    [InlineData("Unbind", "SDSL0254: [Unbind] on Owner.Run (block of C#): Owner.Run has no phase Missing.")]
    [InlineData("Phase", "SDSL0141: [Phase(Missing)] on Step.Run (block of Here.sdfx, line 2): Owner.Run has the phases Early, Default, A, B, and no class of the effect adds Missing.")]
    [InlineData("UnbindAClass", "SDSL0254: [Unbind] on Owner.Run (C#): A is a phase: write Phases = { A }.")]
    public void ABlockAboutAPhaseThatDoesNotExistIsAnError(string attribute, string message)
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix(false, x =>
        {
            if (attribute == "Unbind")
                x.UnbindPhases("Test.Owner", "Run", "Missing");
            else if (attribute == "Phase")
                x.ExtendShader("Test.Step", "Here.sdfx, line 2").Method("Test.Owner.Run").Phase("Missing");
            else
                x.UnbindMixins("Test.Owner", "Run", "A");
        }, "Test.Step");

        Assert.False(ok, log);
        Assert.Contains(message, log);
    }

    private (bool Ok, string Log, string Explanation) MixCatching(Action<ShaderMixinSource> linkage, params string[] classes) => Mix(false, linkage, classes);

    [Fact]
    public void ABlockForAClassThatIsNotInTheEffectDoesNothing()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Other", "");

        var (ok, log, _) = Mix(true, x =>
        {
            x.UnbindPhases("Test.Other", "Run", "A");
            x.ExtendShader("Test.Other").Method("Test.Owner.Runn").Phase("B");
        }, "Test.Step");

        Assert.True(ok, log);
    }

    // A name the project couldn't resolve stays as written, and the class can be in a namespace
    [Fact]
    public void ABlockCanNameAClassByTheEndOfItsFullName()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(true, x => x.ExtendShader("Step").Method("Owner.Run").Phase("B"), "Test.Step");

        Assert.True(ok, log);
        Assert.Contains("B: Step", Block(explanation, "Run"));
    }

    // A block is about every generic version of a class
    [Fact]
    public void ABlockCoversEveryGenericVersionOfAClass()
    {
        WriteOwner();
        Write("Gen", "module Gen<int N> { import Owner; [Phase(A)] override void Run() { base.Run(); streams.Steps = N; } }");

        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource("Test.Entry"), new ShaderClassSource("Test.Gen", 2) } };
        source.ExtendShader("Test.Gen").Method("Test.Owner.Run").Phase("B");

        var (ok, log, explanation) = Mix(true, source);

        Assert.True(ok, log);
        Assert.Contains("B: Gen<2>", Block(explanation, "Run"));
    }

    [Theory]
    [InlineData("Test.Owner.Ownr", "Test.Owner.Ownr")]
    [InlineData("Test.Stepp", "Test.Stepp")]
    [InlineData("Order", "Test.Stepp")]
    [InlineData("Mixins", "Test.Stepp")]
    [InlineData("After", "Test.Stepp")]
    public void AClassThatExistsNowhereInABlockIsAnError(string where, string misspelled)
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix(true, x =>
        {
            switch (where)
            {
                case "Test.Owner.Ownr": x.UnbindPhases("Test.Owner.Ownr", "Run", "A"); break;
                case "Test.Stepp": x.ExtendShader("Test.Stepp").Method("Test.Owner.Run").Phase("A"); break;
                case "Order": x.MixinOrder("Test.Owner", "Run", "Test.Step", "Test.Stepp"); break;
                case "Mixins": x.UnbindMixins("Test.Owner", "Run", "Test.Stepp"); break;
                default: x.ExtendShader("Test.Step").Method("Test.Owner.Run").MixinOrderAfter("Test.Stepp"); break;
            }
        }, "Test.Step");

        Assert.False(ok, log);
        Assert.Contains($"SDSL0158: the block of C# names the class {misspelled}, which doesn't exist.", log);
    }

    // Before the order is looked at: what would be missing from it is a misspelled name, not an order
    [Fact]
    public void AMisspelledClassIsReportedBeforeTheConflictItLeaves()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 1; }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");

        var (ok, log, _) = Mix(true, x => x.MixinOrder("Test.Owner", "Run", "Test.First", "Test.Secnd"), "Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0158", log);
        Assert.DoesNotContain("SDSL0137", log);
    }

    [Fact]
    public void AClassInAnotherNamespaceIsSuggested()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix(true, x => x.UnbindPhases("Wrong.Owner", "Run", "A"), "Test.Step");

        Assert.False(ok, log);
        Assert.Contains("SDSL0158", log);
        Assert.Contains("Did you mean `using Test;`?", log);
    }

    [Fact]
    public void ABlockAddsAPhaseForItsClass()
    {
        WriteOwner();
        WriteModule("InB", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("InA", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Patch", "override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(true, x => x.ExtendShader("Test.Patch").Method("Test.Owner.Run").Phase("Lib.Wind", after: "A", before: "B"), "Test.InB", "Test.InA", "Test.Patch");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > InA > Patch > InB", block[0]);
        Assert.Contains("Lib.Wind: Patch", block);
    }

    // The class of a block built in C# adds the phase
    [Theory]
    [InlineData("Wind", "SDSL0252: phase Wind, added by Patch.Run (block of C#), has a plain name.")]
    [InlineData("Stride.Wind", "SDSL0253: phase Stride.Wind, added by Patch.Run (block of C#): a name that starts with Stride. is kept for the engine.")]
    public void APhaseThatABlockAddsHasAPrefix(string phase, string message)
    {
        WriteOwner();
        WriteModule("Patch", "override void Run() { base.Run(); }");

        var (ok, log, _) = Mix(false, x => x.ExtendShader("Test.Patch").Method("Test.Owner.Run").Phase(phase, after: "A"), "Test.Patch");

        Assert.False(ok, log);
        Assert.Contains(message, log);
    }

    [Fact]
    public void AnOverrideAddsAPhase()
    {
        WriteOwner();
        WriteModule("InB", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("InA", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("Wind", "[Phase(Lib.Wind, After = A, Before = B)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.InB", "Test.Wind", "Test.InA");

        Assert.True(ok, log);
        Assert.Equal("module method Run() -> void = Owner > InA > Wind > InB", Block(explanation, "Run")[0]);
    }

    // SDSL0137 says to write this block
    [Fact]
    public void ABlockCanOrderAPhaseAnOverrideAdds()
    {
        WriteOwner();
        WriteModule("First", "[Phase(Lib.Wind, After = A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "[Phase(Lib.Wind, After = A)] override void Run() { base.Run(); streams.Steps = 3; }");

        var (ok, log, explanation) = Mix(true, x => x.MixinOrder("Test.Owner", "Run", "Test.Second", "Test.First"), "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Contains("Lib.Wind: Second > First", explanation);
    }

    [Fact]
    public void AddedPhasesCanNameEachOtherInAnyOrder()
    {
        WriteOwner();
        WriteModule("Last", "[Phase(Lib.Y, After = Lib.X)] override void Run() { base.Run(); }");
        WriteModule("Next", "[Phase(Lib.X, After = A)] override void Run() { base.Run(); }");
        WriteModule("Other", "[Phase(A)] override void Run() { base.Run(); }");

        Assert.True(Mix(false, "Test.Last", "Test.Next").Ok);
        Assert.True(Mix(false, x => x.ExtendShader("Test.Other").Method("Test.Owner.Run").Phase("Lib.X", after: "A"), "Test.Last", "Test.Other").Ok);
    }

    [Theory]
    [InlineData("[Phase(Lib.Wind, After = Early)] override void Run() { base.Run(); streams.Steps = 2; }")]
    [InlineData("[Phase(Lib.Wind, Before = Default)] override void Run() { base.Run(); streams.Steps = 2; }")]
    [InlineData("[Phase(Lib.Wind, After = A)] override void Run() { streams.Steps = 2; base.Run(); }")]
    [InlineData("[Phase(Lib.Wind, Before = B)] override void Run() { streams.Steps = 2; base.Run(); }")]
    public void TheBaseCallOfAnAddedPhaseFitsItsPlace(string member)
    {
        WriteOwner();
        WriteModule("Wrong", member);

        var (ok, log, _) = Mix(false, "Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains("SDSL0140", log);
    }

    // Its place depends on a phase that another class adds
    [Fact]
    public void TheBaseCallOfAnAddedPhaseIsCheckedAtLink()
    {
        WriteOwner();
        WriteModule("Wrong", "[Phase(Lib.Y, After = Lib.X)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Other", "[Phase(Lib.X, After = Early)] override void Run() { streams.Steps = 3; base.Run(); }");

        var (ok, log, _) = Mix(false, "Test.Wrong", "Test.Other");

        Assert.False(ok, log);
        Assert.Contains("SDSL0140: Wrong.Run is in phase Lib.Y", log);
    }

    [Theory]
    [InlineData("List", "SDSL0154: the block extend shader Owner (C#) names Runn, but Owner is in the effect and has no method Runn that is an extension point or has patches.")]
    [InlineData("Unbind", "SDSL0154: the block extend shader Owner (C#) names Runn, but Owner is in the effect and has no method Runn that is an extension point.")]
    [InlineData("UnbindPlain", "SDSL0154: the block extend shader Owner (C#) names Plain, but Owner is in the effect and Plain is not an extension point, so there is nothing to unbind.")]
    [InlineData("Phase", "SDSL0154: the block extend shader Step (C#) names Test.Owner.Runn, but Step is in the effect and Owner has no method Runn that is an extension point or has patches.")]
    [InlineData("NotContributing", "SDSL0154: the block extend shader Step (C#) names Test.Owner.Wrap, but Step is in the effect and doesn't contribute to Test.Owner.Wrap.")]
    public void ABlockAboutAMethodItsClassDoesNotHaveIsAnError(string attribute, string message)
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix(true, x =>
        {
            switch (attribute)
            {
                case "List": x.MixinOrder("Test.Owner", "Runn", "Test.Step", "Test.Entry"); break;
                case "Unbind": x.UnbindPhases("Test.Owner", "Runn", "A"); break;
                case "UnbindPlain": x.UnbindMixins("Test.Owner", "Plain", "Test.Step"); break;
                case "Phase": x.ExtendShader("Test.Step").Method("Test.Owner.Runn").Phase("A"); break;
                default: x.ExtendShader("Test.Step").Method("Test.Owner.Wrap").Phase("Outer"); break;
            }
        }, "Test.Step");

        Assert.False(ok, log);
        Assert.Contains(message, log);
    }

    private const string OverloadedOwner = """
        module OverloadedOwner
        {
            stream float Steps;

            [Phases(A)]
            pipeline void Go(float value) { }

            [Phases(A)]
            pipeline void Go(int value) { }
        }
        """;

    [Fact]
    public void ABlockAboutAnOverloadWritesItsParameterTypes()
    {
        Write("OverloadedOwner", OverloadedOwner);
        Write("First", "module First { import OverloadedOwner; [Phase(A)] override void Go(float value) { base.Go(value); streams.Steps = 1; } }");
        Write("Second", "module Second { import OverloadedOwner; [Phase(A)] override void Go(float value) { base.Go(value); streams.Steps = 2; } }");

        var (ok, log, _) = Mix(true, x => x.MixinOrder("Test.OverloadedOwner", "Go", "Test.Second", "Test.First"), "Test.First", "Test.Second");
        Assert.False(ok, log);
        Assert.Contains("SDSL0154: the block extend shader OverloadedOwner (C#) names Go, but OverloadedOwner is in the effect and Go has several overloads: write the parameter types, as in Go(", log);

        (ok, log, var explanation) = Mix(true, x => x.MixinOrder("Test.OverloadedOwner", "Go(float)", "Test.Second", "Test.First"), "Test.First", "Test.Second");
        Assert.True(ok, log);
        Assert.Contains("A: OverloadedOwner > Second > First   order: block", explanation.Split('\n').Select(x => x.Trim()));
    }

    [Fact]
    public void APhaseAddedNextToAPhaseThatDoesNotExistIsAnError()
    {
        WriteOwner();
        WriteModule("InB", "[Phase(B)] override void Run() { base.Run(); }");
        WriteModule("Wind", "[Phase(Lib.Wind, After = Missing)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix(isAssetBuild: false, "Test.Wind", "Test.InB");

        Assert.False(ok, log);
        Assert.Contains("SDSL0218: phase Lib.Wind added by Wind to Owner.Run has no place: Owner.Run has no phase Missing", log);
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
    [InlineData("[Phases(A, B)] shader Wrong { }")]
    [InlineData("[Phase(A)] shader Wrong { }")]
    [InlineData("[MixinOrder(After = Other)] shader Wrong { }")]
    [InlineData("[Drops(Mixins = { Other })] shader Wrong { }")]
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

    // SDSL0148: the order of two patches would be the order of the mix, which nothing states
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoPatchesWithNoOrderAreAnError(bool isAssetBuild)
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain() { return next() + 1; }");
        WriteModule("PatchB", "patch Owner.Plain() { return next() * 2; }");

        var (ok, log, _) = Mix(isAssetBuild, "Test.PatchA", "Test.PatchB");

        Assert.False(ok, log);
        Assert.Contains("SDSL0148: PatchA and PatchB both patch Owner.Plain, and nothing orders them. Add [MixinOrder(After = PatchA)] to the patch in PatchB, or this block to the effect: extend shader Owner { [MixinOrder(PatchA, PatchB)] Plain; }", log);
    }

    [Theory]
    [InlineData("Test.PatchA", "Test.PatchB")]
    [InlineData("Test.PatchB", "Test.PatchA")]
    public void MixinOrderAfterOrdersTwoPatches(string first, string second)
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain() { return next() + 1; }");
        WriteModule("PatchB", "[MixinOrder(After = PatchA)] patch Owner.Plain() { return next() * 2; }");

        var (ok, log, explanation) = Mix(first, second);

        Assert.True(ok, log);
        Assert.Equal("module method Plain() -> float = Owner > PatchA > PatchB", Block(explanation, "Plain")[0]);
    }

    [Fact]
    public void MixinOrderAfterOrdersPatchesThroughAThird()
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain() { return next() + 1; }");
        WriteModule("PatchB", "[MixinOrder(After = PatchA)] patch Owner.Plain() { return next() + 2; }");
        WriteModule("PatchC", "[MixinOrder(After = PatchB)] patch Owner.Plain() { return next() + 3; }");

        var (ok, log, explanation) = Mix("Test.PatchC", "Test.PatchA", "Test.PatchB");

        Assert.True(ok, log);
        Assert.Equal("module method Plain() -> float = Owner > PatchA > PatchB > PatchC", Block(explanation, "Plain")[0]);
    }

    // Each pair needs an order
    [Fact]
    public void AThirdPatchWithNoOrderIsAnError()
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain() { return next() + 1; }");
        WriteModule("PatchB", "[MixinOrder(After = PatchA)] patch Owner.Plain() { return next() + 2; }");
        WriteModule("PatchC", "patch Owner.Plain() { return next() + 3; }");

        var (ok, log, _) = Mix("Test.PatchA", "Test.PatchB", "Test.PatchC");

        Assert.False(ok, log);
        Assert.Contains("SDSL0148: PatchA and PatchC both patch", log);
    }

    [Fact]
    public void PatchesAfterEachOtherAreAnError()
    {
        WriteOwner();
        WriteModule("PatchA", "[MixinOrder(After = PatchB)] patch Owner.Plain() { return next() + 1; }");
        WriteModule("PatchB", "[MixinOrder(After = PatchA)] patch Owner.Plain() { return next() + 2; }");

        var (ok, log, _) = Mix("Test.PatchA", "Test.PatchB");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138", log);
    }

    // The class named by 'after' isn't in the effect: nothing orders the two that are
    [Fact]
    public void MixinOrderAfterAClassOutsideTheEffectIsNoOrder()
    {
        WriteOwner();
        WriteModule("PatchOther", "patch Owner.Plain() { return next() + 1; }");
        WriteModule("PatchA", "[MixinOrder(After = PatchOther)] patch Owner.Plain() { return next() + 2; }");
        WriteModule("PatchB", "patch Owner.Plain() { return next() + 3; }");

        var (ok, log, _) = Mix("Test.PatchA", "Test.PatchB");

        Assert.False(ok, log);
        Assert.Contains("SDSL0148: PatchA and PatchB", log);
    }

    // Two patches of libraries that the author of the effect can't change
    [Fact]
    public void AnOrderRuleOrdersTwoPatches()
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain() { return next() + 1; }");
        WriteModule("PatchB", "patch Owner.Plain() { return next() * 2; }");

        var (ok, log, explanation) = Mix(true, x => x.MixinOrder("Test.Owner", "Plain", "Test.PatchB", "Test.PatchA"), "Test.PatchA", "Test.PatchB");

        Assert.True(ok, log);
        Assert.Equal("module method Plain() -> float = Owner > PatchB > PatchA", Block(explanation, "Plain")[0]);
    }

    [Fact]
    public void TheMessageOfTwoPatchesNamesAClassAsItIsWritten()
    {
        WriteOwner();
        Write("Gen", "module Gen<int N> { import Owner; patch Owner.Plain() { return N; } }");
        WriteModule("UsesGen", "import Gen<1>;");
        WriteModule("Other", "patch Owner.Plain() { return 2; }");

        var (ok, log, _) = Mix(false, "Test.UsesGen", "Test.Other");

        Assert.False(ok, log);
        Assert.Contains("Add [MixinOrder(After = Gen)] to the patch in Other", log);
    }

    [Fact]
    public void OnePatchNeedsNoOrder()
    {
        WriteOwner();
        WriteModule("PatchA", "patch Owner.Plain() { return next() + 1; }");

        var (ok, log, _) = Mix("Test.PatchA");

        Assert.True(ok, log);
    }

    // A class comes after the classes it inherits
    [Fact]
    public void APatchOfAChildComesAfterThePatchOfItsParent()
    {
        WriteOwner();
        Write("PatchParent", "shader PatchParent { import Owner; patch Owner.Plain() { return next() + 1; } }");
        Write("PatchChild", "shader PatchChild : PatchParent { patch Owner.Plain() { return next() * 2; } }");

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
        WriteModule("PatchA", "[Phase(A)] patch Owner.Plain() { return next() + 1; }");

        var (ok, log, _) = Mix("Test.PatchA");

        Assert.False(ok, log);
        Assert.Contains("SDSL0141: [Phase(A)] on PatchA.Plain: Plain is not an extension point, so it has no phases.", log);
    }

    [Fact]
    public void AnOverrideOfAMethodWithNoKindNamesPatch()
    {
        WriteOwner();
        WriteModule("Plainer", "override float Plain() { return 2; }");

        var (ok, log, _) = Mix("Test.Plainer");

        Assert.False(ok, log);
        Assert.Contains("SDSL0246: Plainer overrides Plain of Owner, which is not an extension point. If you own Owner, write replace, pipeline or around on the first definition of Plain. If you don't, write patch Owner.Plain instead of override.", log);
    }
}
