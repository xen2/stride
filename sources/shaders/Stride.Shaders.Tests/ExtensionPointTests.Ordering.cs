// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Rendering;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// The ordering model: phases a class adds or joins, <c>[MixinOrder]</c> as pairs, and what <c>after</c> means on an <c>around</c> point.
/// </summary>
public partial class ExtensionPointTests
{
    [Theory]
    [InlineData("[Phase(Wind, After = A)] override void Run() { base.Run(); }", "SDSL0252: phase Wind, added by Wrong.Run, has a plain name. Only the owner of Owner.Run declares plain names, in [Phases]: give the phase a prefix of your own, such as MyLibrary.Wind.")]
    [InlineData("[Phase(Stride.Wind, After = A)] override void Run() { base.Run(); }", "SDSL0253: phase Stride.Wind, added by Wrong.Run: a name that starts with Stride. is kept for the engine.")]
    [InlineData("[Phase(Lib.Wind, After = \"A\")] override void Run() { base.Run(); }", "SDSL0255: [Phase] on Wrong.Run: \"A\" is a string.")]
    [InlineData("[Phase(Wind)] override void Run() { base.Run(); }", "SDSL0141: [Phase(Wind)] on Wrong.Run: Owner.Run declares Early, Default, A, B.")]
    public void AnAddedPhaseIsNamedWithAPrefix(string member, string message)
    {
        WriteOwner();
        WriteModule("Wrong", member);

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains(message, log);
    }

    // A name with a dot can be added by another class: it is checked when mixing
    [Fact]
    public void AClassJoinsAPhaseThatAnotherClassAdds()
    {
        WriteOwner();
        WriteModule("Adder", "[Phase(Lib.Wind, After = A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Joiner", "[Phase(Lib.Wind)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.Adder", "Test.Joiner");
        Assert.True(ok, log);
        Assert.Contains("Lib.Wind: Adder > Joiner   order: tie-break", Block(explanation, "Run"));

        var (alone, aloneLog, _) = Mix(isAssetBuild: true, "Test.Joiner");
        Assert.False(alone, aloneLog);
        Assert.Contains("SDSL0141: [Phase(Lib.Wind)] on Joiner.Run: Owner.Run has the phases Early, Default, A, B, and no class of the effect adds Lib.Wind.", aloneLog);
    }

    // Each declaration counts, and the phase is one
    [Fact]
    public void TwoClassesThatAddOnePhaseShareIt()
    {
        WriteOwner();
        WriteModule("First", "[Phase(Lib.Wind, After = A)] override void Run() { base.Run(); }");
        WriteModule("Second", "[Phase(Lib.Wind, Before = B)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(isAssetBuild: false, "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Equal(["Early: (none)", "Default: Owner", "A: (none)", "Lib.Wind: First > Second   order: tie-break", "B: (none)"], Block(explanation, "Run").Skip(2).ToArray());
    }

    [Fact]
    public void TwoDeclarationsOfOnePhaseThatContradictAreAnError()
    {
        WriteOwner();
        WriteModule("First", "[Phase(Lib.Wind, After = A)] override void Run() { base.Run(); }");
        WriteModule("Second", "[Phase(Lib.Wind, Before = Early)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix(isAssetBuild: false, "Test.First", "Test.Second");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138: the order of Owner.Run can't be followed: phase Lib.Wind goes after A (First.sdsl, line 2), so it can't also go before Early (Second.sdsl, line 2).", log);
    }

    // Nothing orders them: they keep the order they were collected in, the order of the mix
    [Fact]
    public void AddedPhasesAtOnePlaceKeepTheOrderOfTheMix()
    {
        WriteOwner();
        WriteModule("One", "[Phase(Lib.One, After = A)] override void Run() { base.Run(); }");
        WriteModule("Two", "[Phase(Lib.Two, After = A)] override void Run() { base.Run(); }");

        var (ok, log, explanation) = Mix(isAssetBuild: false, "Test.One", "Test.Two");
        Assert.True(ok, log);
        Assert.Equal(["A: (none)", "Lib.One: One", "Lib.Two: Two", "B: (none)"], Block(explanation, "Run").Skip(4).ToArray());

        (ok, log, explanation) = Mix(isAssetBuild: false, "Test.Two", "Test.One");
        Assert.True(ok, log);
        Assert.Equal(["A: (none)", "Lib.Two: Two", "Lib.One: One", "B: (none)"], Block(explanation, "Run").Skip(4).ToArray());
    }

    [Fact]
    public void MixinOrderBeforeOrdersAPhase()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "[Phase(A)] [MixinOrder(Before = First)] override void Run() { base.Run(); streams.Steps = 3; }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Contains("A: Second > First   order: mixin order", Block(explanation, "Run"));
    }

    // Each name is one pair, in braces or in another attribute
    [Fact]
    public void MixinOrderTakesBracesAndCanBeRepeated()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 3; }");
        WriteModule("Third", "[Phase(A)] [MixinOrder(Before = { Second })] [MixinOrder(Before = First)] override void Run() { base.Run(); streams.Steps = 4; }");

        var (ok, log, explanation) = Mix(isAssetBuild: false, "Test.First", "Test.Second", "Test.Third");

        Assert.True(ok, log);
        Assert.Contains("Info: Owner.Run, phase A: First runs before Second because of the order of the mix", log);
        Assert.Contains("A: Third > First > Second   order: mixin order, builder", Block(explanation, "Run"));
    }

    // The phases already order classes of different phases, so naming one is a mistake
    [Fact]
    public void MixinOrderBetweenPhasesIsAnErrorBothWays()
    {
        WriteOwner();
        WriteModule("StepA", "[Phase(A)] override void Run() { base.Run(); }");
        WriteModule("StepB", "[Phase(B)] [MixinOrder(After = StepA)] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.StepA", "Test.StepB");

        Assert.False(ok, log);
        Assert.Contains("SDSL0138: the order of Owner.Run can't be followed: [MixinOrder] of StepB.sdsl, line 2 orders StepA, in phase A, and StepB, in phase B. [MixinOrder] orders classes of one phase", log);
    }

    private const string ListingOwner = """
        module ListingOwner
        {
            stream float Steps;

            [Phases(A, B)]
            [MixinOrder(Second, First)]
            [Unbind(Phases = { B }, Mixins = { Gone })]
            pipeline void Go() { }
        }
        """;

    // On the owner's own first definition, as a block of an effect would write them
    [Fact]
    public void TheFirstDefinitionCanListAndUnbind()
    {
        Write("ListingOwner", ListingOwner);
        Write("First", "module First { import ListingOwner; [Phase(A)] override void Go() { base.Go(); streams.Steps = 2; } }");
        Write("Second", "module Second { import ListingOwner; [Phase(A)] override void Go() { base.Go(); streams.Steps = 3; } }");
        Write("Gone", "module Gone { import ListingOwner; [Phase(A)] override void Go() { base.Go(); } }");
        Write("InB", "module InB { import ListingOwner; [Phase(B)] override void Go() { base.Go(); } }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.First", "Test.Gone", "Test.InB", "Test.Second");

        Assert.True(ok, log);
        var block = Block(explanation, "Go");
        Assert.Contains("A: ListingOwner > Second > First   order: mixin order", block);
        Assert.Contains("B: (none)", block);
        Assert.Contains("not bound by the effect: Gone, InB", block);
    }

    [Theory]
    [InlineData("[MixinOrder(After = First)] [Phases(A)] pipeline void Go() { }", "SDSL0135: [MixinOrder] on Wrong.Go: on the method being extended, [MixinOrder] lists the classes in their running order")]
    [InlineData("[Unbind(B)] [Phases(A, B)] pipeline void Go() { }", "SDSL0254: [Unbind] on Wrong.Go: [Unbind] takes Mixins = { ... } for classes and Phases = { ... } for phases.")]
    [InlineData("[Unbind(Phases = { C })] [Phases(A, B)] pipeline void Go() { }", "SDSL0254: [Unbind] on Wrong.Go: Wrong.Go has no phase C.")]
    [InlineData("[Unbind(Mixins = { B })] [Phases(A, B)] pipeline void Go() { }", "SDSL0254: [Unbind] on Wrong.Go: B is a phase of Wrong.Go: write Phases = { B }.")]
    [InlineData("[Unbind(Mixins = { First })] void Go() { }", "SDSL0135: [Unbind] on Wrong.Go: Go is not an extension point.")]
    [InlineData("[MixinOrder(First)] replace void Go() { }", "SDSL0135: [MixinOrder] on Wrong.Go: one override of a replace point runs, so it has no order.")]
    public void AWrongListOnTheFirstDefinitionIsAnError(string member, string message)
    {
        Write("First", "module First { }");
        Write("Wrong", $"module Wrong {{ {member} }}");

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains(message, log);
    }

    [Fact]
    public void AnOverrideCanNotUnbind()
    {
        WriteOwner();
        WriteModule("Wrong", "[Unbind(Phases = { A })] override void Run() { base.Run(); }");

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains("SDSL0135: [Unbind] on Wrong.Run: [Unbind] goes on the method being extended, or in a block of the effect. An override drops what it replaces with [Drops].", log);
    }

    // Before the default body each step calls base last, so the outermost one runs first: Explain lists the running order
    [Fact]
    public void ExplainListsAPhaseBeforeTheDefaultBodyInRunningOrder()
    {
        WriteOwner();
        WriteModule("First", "[Phase(Early)] override void Run() { streams.Steps = 2; base.Run(); }");
        WriteModule("Second", "[Phase(Early)] [MixinOrder(After = First)] override void Run() { streams.Steps = 3; base.Run(); }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.Second", "Test.First");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > Second > First", block[0]);
        Assert.Contains("Early: First > Second   order: mixin order", block);
    }

    // On an around point, phases are nesting levels and each wrapper runs its code around the next one: after a class means inside it.
    // SpriteSuperSampler calls next() once per sample, and ImageScalerShader scales each of them.
    [Fact]
    public void MixinOrderAfterOnAnAroundPointRunsInside()
    {
        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource("Stride.Rendering.SpriteSuperSampler"), new ShaderClassSource("Stride.Rendering.Images.ImageScalerShader") } };

        var (ok, log, explanation) = Mix(false, source);

        Assert.True(ok, log);
        var block = Block(explanation, "Shading");
        Assert.Equal("module method Shading() -> float4 = SpriteBase > ImageScalerShader > SpriteSuperSampler", block[0]);
        Assert.Contains("Post: SpriteSuperSampler > ImageScalerShader   order: mixin order", block);
    }

    // Its class can't place the phase when it compiles: it compiles both forms, and the mixer keeps the one that fits. The render test
    // ExtensionPointJoinedPhase checks the image.
    [Theory]
    [InlineData("After = Early", "Owner > Joiner > Adder", "Lib.Wind: Adder > Joiner")]
    [InlineData("After = A", "Owner > Adder > Joiner", "Lib.Wind: Adder > Joiner")]
    public void AnExtendJoiningAPhaseAddedElsewhereCallsBaseWhereItRuns(string place, string chain, string phase)
    {
        WriteOwner();
        WriteModule("Adder", $"[Phase(Lib.Wind, {place})] extend Owner.Run() {{ streams.Steps = 2; }}");
        WriteModule("Joiner", "[Phase(Lib.Wind)] [MixinOrder(After = Adder)] extend Owner.Run() { streams.Steps = 3; }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.Adder", "Test.Joiner");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal($"module method Run() -> void = {chain}", block[0]);
        Assert.Contains($"{phase}   order: mixin order", block);
    }

    // The second form isn't a method of the class: a class that inherits it overrides the first one
    [Fact]
    public void TheSecondFormIsNotAMethodOfTheClass()
    {
        WriteOwner();
        WriteModule("Adder", "[Phase(Lib.Wind, After = Early)] extend Owner.Run() { streams.Steps = 2; }");
        Write("Joiner", "shader Joiner { import Owner; [Phase(Lib.Wind)] extend Owner.Run() { streams.Steps = 3; } }");
        Write("Child", "shader Child : Joiner { [Phase(Lib.Wind)] override module void Run() { streams.Steps = 4; base.Run(); } }");

        var (ok, log, explanation) = Mix(isAssetBuild: false, "Test.Adder", "Test.Child");

        Assert.True(ok, log);
        Assert.Equal("module method Run() -> void = Owner > Adder > Joiner > Child", Block(explanation, "Run")[0]);
    }

    // A block that moves an extend to the other side of the default body: its class placed its call to base for the phase it wrote
    [Fact]
    public void ABlockThatMovesAnExtendAcrossTheDefaultBodyIsAnError()
    {
        WriteOwner();
        WriteModule("Step", "[Phase(A)] extend Owner.Run() { streams.Steps = 2; }");

        var (ok, log, _) = Mix(false, x => x.ExtendShader("Test.Step").Method("Test.Owner.Run").Phase("Early"), "Test.Step");

        Assert.False(ok, log);
        Assert.Contains("SDSL0140: Step.Run is in phase Early of Owner.Run and calls base first, so it runs after the default body.", log);
    }

    // An added phase is a nesting level between its neighbours: inside Outer, around Inner
    [Fact]
    public void APhaseAddedToAnAroundPointIsANestingLevel()
    {
        WriteOwner();
        WriteModule("InOuter", "[Phase(Outer)] override float Wrap() { return base.Wrap() * 2; }");
        WriteModule("InMiddle", "[Phase(Lib.Middle, After = Outer)] override float Wrap() { return base.Wrap() + 1; }");
        WriteModule("InInner", "[Phase(Inner)] override float Wrap() { return base.Wrap() - 1; }");

        var (ok, log, explanation) = Mix(false, "Test.InOuter", "Test.InMiddle", "Test.InInner");

        Assert.True(ok, log);
        var block = Block(explanation, "Wrap");
        Assert.Equal("module method Wrap() -> float = Owner > InInner > InMiddle > InOuter", block[0]);
        Assert.Equal(["around", "Outer: InOuter", "Lib.Middle: InMiddle", "Inner: InInner", "default: Owner"], block.Skip(1).ToArray());
    }
}
