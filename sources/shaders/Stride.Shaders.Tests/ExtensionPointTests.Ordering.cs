// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Rendering;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// The ordering model: <c>[MixinOrder]</c> as pairs, and what <c>after</c> means on an <c>around</c> point.
/// </summary>
public partial class ExtensionPointTests
{
    [Fact]
    public void MixinOrderBeforeOrdersAPhase()
    {
        WriteOwner();
        WriteModule("First", "[Phase(A)] override void Run() { base.Run(); streams.Steps = 2; }");
        WriteModule("Second", "[Phase(A)] [MixinOrder(Before = First)] override void Run() { base.Run(); streams.Steps = 3; }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Contains("A: Second > First", Block(explanation, "Run"));
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
        Assert.Contains("A: Third > First > Second", Block(explanation, "Run"));
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
            pipeline void Go() { }
        }
        """;

    // On the owner's own first definition
    [Fact]
    public void TheFirstDefinitionCanList()
    {
        Write("ListingOwner", ListingOwner);
        Write("First", "module First { import ListingOwner; [Phase(A)] override void Go() { base.Go(); streams.Steps = 2; } }");
        Write("Second", "module Second { import ListingOwner; [Phase(A)] override void Go() { base.Go(); streams.Steps = 3; } }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.True(ok, log);
        Assert.Contains("A: ListingOwner > Second > First", Block(explanation, "Go"));
    }

    [Theory]
    [InlineData("[MixinOrder(After = First)] [Phases(A)] pipeline void Go() { }", "SDSL0135: [MixinOrder] on Wrong.Go: on the method being extended, [MixinOrder] lists the classes in their running order")]
    [InlineData("[MixinOrder(First)] replace void Go() { }", "SDSL0135: [MixinOrder] on Wrong.Go: one override of a replace point runs, so it has no order.")]
    public void AWrongListOnTheFirstDefinitionIsAnError(string member, string message)
    {
        Write("First", "module First { }");
        Write("Wrong", $"module Wrong {{ {member} }}");

        var (ok, log, _) = Mix("Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains(message, log);
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
        Assert.Contains("Early: First > Second", block);
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
        Assert.Contains("Post: SpriteSuperSampler > ImageScalerShader", block);
    }

}
