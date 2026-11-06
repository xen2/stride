// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using System.Linq;

using Xunit;

using Stride.Shaders;

namespace Stride.MaterialTestBed;

/// <summary>
///   What the compiler says about the stress cases, under the rules of the asset build.
/// </summary>
public class TestBedLanguageTests
{
    private const string Point = "Stride.MaterialTestBed.TestBedSurfaceModifiers";

    private static TestBedMixer.Result MixSource(string classes, string file)
    {
        var name = file.Split(' ', '<', ':')[1];
        var sources = new Dictionary<string, string> { [name] = $"namespace Stride.MaterialTestBed;\n{file}\n" };
        return TestBedMixer.Mix(TestBedMixer.Effect(classes.Split(',').Append(name).ToArray()), isAssetBuild: true, sources);
    }

    // Case 1: two plugins on one point

    [Theory]
    [InlineData("TestBedWetness", "TestBedSnow", "share s.albedo, s.roughness")]
    [InlineData("TestBedCurrentWet", "TestBedCurrentSnow", "share Current.roughness")]
    [InlineData("TestBedSharedWet", "TestBedSharedSnow", "share SharedValue")]
    public void TwoWritersOfOneFieldThatAreNotStreamsConflict(string first, string second, string message)
    {
        var result = TestBedMixer.Mix("TestBedConflictProbe", first, second);

        Assert.False(result.Ok, result.Log);
        Assert.Contains("SDSL0137", result.Log);
        Assert.Contains(message, result.Log);
    }

    // With the order of the effect, each order is what it states
    [Theory]
    [InlineData("TestBedInoutWetSnow", "ModifySurface", "Scene: TestBedSurfaceModifiers > TestBedWetness > TestBedSnow   order: block")]
    [InlineData("TestBedInoutSnowWet", "ModifySurface", "Scene: TestBedSurfaceModifiers > TestBedSnow > TestBedWetness   order: block")]
    [InlineData("TestBedCurrentWetSnow", "ModifyCurrent", "Scene: TestBedConflictPoints > TestBedCurrentWet > TestBedCurrentSnow   order: block")]
    [InlineData("TestBedCurrentSnowWet", "ModifyCurrent", "Scene: TestBedConflictPoints > TestBedCurrentSnow > TestBedCurrentWet   order: block")]
    [InlineData("TestBedSharedWetSnow", "ModifyShared", "Scene: TestBedConflictPoints > TestBedSharedWet > TestBedSharedSnow   order: block")]
    [InlineData("TestBedSharedSnowWet", "ModifyShared", "Scene: TestBedConflictPoints > TestBedSharedSnow > TestBedSharedWet   order: block")]
    public void ABlockSettlesTheseWriters(string effect, string method, string line)
    {
        var result = TestBedMixer.MixEffect(effect);

        Assert.True(result.Ok, result.Log);
        Assert.Contains(line, result.Block(method));
    }

    // A write of the whole struct loses the other's change, and a reader gets what the other wrote
    [Theory]
    [InlineData("TestBedWetness", "TestBedSurfaceReset")]
    [InlineData("TestBedWetness", "TestBedSurfaceReader")]
    [InlineData("TestBedWetness", "TestBedSurfaceCopy")]
    public void AWholeStructWriteAndAReadConflict(string first, string second)
    {
        var result = TestBedMixer.Mix("TestBedConflictProbe", first, second);

        Assert.False(result.Ok, result.Log);
        Assert.Contains("SDSL0137", result.Log);
    }

    [Fact]
    public void TheSamePairOnStreamsIsAnError()
    {
        var result = TestBedMixer.Mix("TestBedConflictProbe", "TestBedStreamWet", "TestBedStreamSnow");

        Assert.False(result.Ok, result.Log);
        Assert.Contains("SDSL0137: TestBedStreamWet and TestBedStreamSnow are both in phase Scene of TestBedConflictPoints.ModifyStream and share streams.StreamValue", result.Log);
        Assert.Contains("extend shader TestBedConflictPoints { [MixinOrder(TestBedStreamWet, TestBedStreamSnow)] ModifyStream; }", result.Log);

        var runtime = TestBedMixer.Mix(TestBedMixer.Effect("TestBedConflictProbe", "TestBedStreamWet", "TestBedStreamSnow"), isAssetBuild: false);

        Assert.True(runtime.Ok, runtime.Log);
        Assert.Contains("because of the order of the mix", runtime.Log);
        Assert.Contains("Scene: TestBedConflictPoints > TestBedStreamWet > TestBedStreamSnow   order: builder", runtime.Block("ModifyStream"));
    }

    [Fact]
    public void TheEffectOrdersThePairWithoutEitherPluginKnowingTheOther()
    {
        var result = TestBedMixer.MixEffect("TestBedInoutOrdered");

        Assert.True(result.Ok, result.Log);
        Assert.Contains("Scene: TestBedSurfaceModifiers > TestBedWetness > TestBedSnow   order: block", result.Block("ModifySurface"));
    }

    [Fact]
    public void AfterOrdersThePairWhenOnePluginKnowsTheOther()
    {
        var result = TestBedMixer.MixEffect("TestBedInoutAfter");

        Assert.True(result.Ok, result.Log);
        Assert.Contains("Scene: TestBedSurfaceModifiers > TestBedWetness > TestBedSnowAfterWetness   order: mixin order", result.Block("ModifySurface"));
    }

    [Fact]
    public void TheEffectOrdersThePairOnStreams()
    {
        var result = TestBedMixer.MixEffect("TestBedStreamOrdered");

        Assert.True(result.Ok, result.Log);
        Assert.Contains("Scene: TestBedConflictPoints > TestBedStreamWet > TestBedStreamSnow   order: block", result.Block("ModifyStream"));
    }

    // Case 2: a second shading model

    [Fact]
    public void AModelIsAReplacePointWithADefaultBody()
    {
        var standard = TestBedMixer.MixEffect("TestBedModelDefault");
        Assert.True(standard.Ok, standard.Log);
        Assert.Contains("replace: TestBedShadingModel", standard.Block("ModelDirectLight"));

        var cel = TestBedMixer.MixEffect("TestBedModelCel");
        Assert.True(cel.Ok, cel.Log);
        Assert.Contains("replace: TestBedCelModel", cel.Block("ModelDirectLight"));
        Assert.Contains("replace: TestBedShadingModel", cel.Block("ModelEmissive"));
    }

    [Fact]
    public void ReplacesPicksTheModelWhereverTheMixPutsIt()
    {
        var result = TestBedMixer.MixEffect("TestBedModelRim");

        Assert.True(result.Ok, result.Log);
        Assert.Contains("replace: TestBedRimCelModel", result.Block("ModelDirectLight"));
        Assert.Contains("replaced by TestBedRimCelModel: TestBedCelModel", result.Block("ModelDirectLight"));
    }

    [Fact]
    public void TwoModelsWithNoReplacesAreAnError()
    {
        var result = MixSource("TestBedModelProbe,TestBedCelModel", "module OtherModel { import TestBedShadingModel; extend TestBedShadingModel.ModelDirectLight(Surface s, LightSample light, float3 viewWS) { return s.albedo; } };");

        Assert.False(result.Ok, result.Log);
        Assert.Contains("SDSL0136: OtherModel.ModelDirectLight doesn't call next, so TestBedCelModel never run in this effect.", result.Log);
    }

    // What the language lets a model be today: section 6 of the report
    [Theory]
    [InlineData("SpriteBase", "module NoShaderParent : TestBedNode { };", "SDSL0199")]
    [InlineData("SpriteBase", "module ByType<TestBedStandardModel M> { };", "SDSL0190")]
    [InlineData("SpriteBase", "[Pure] shader PureShader { };", "SDSL0211")]
    public void WhatAModelCanNotBe(string classes, string file, string code)
    {
        var result = MixSource(classes, file);

        Assert.False(result.Ok, result.Log);
        Assert.Contains(code, result.Log);
    }

    [Fact]
    public void SdslHasNoInterface()
    {
        var sources = new Dictionary<string, string>
        {
            ["IModel"] = "namespace Stride.MaterialTestBed;\ninterface IModel { float3 DirectLight(float3 c); };",
            ["UseInterface"] = "namespace Stride.MaterialTestBed;\nmodule UseInterface : IModel { };",
        };

        var result = TestBedMixer.Mix(TestBedMixer.Effect("SpriteBase", "UseInterface"), isAssetBuild: true, sources);

        Assert.False(result.Ok, result.Log);
        Assert.Contains("SDSL0001", result.Log);
    }

    [Fact]
    public void ASlotTakesNoModule()
    {
        var sources = new Dictionary<string, string>
        {
            ["ModelSlotType"] = "namespace Stride.MaterialTestBed;\nshader ModelSlotType { import TestBedSurface; abstract float3 DirectLight(Surface s, LightSample light, float3 viewWS); };",
            ["ModelSlotProbe"] = "namespace Stride.MaterialTestBed;\nmodule ModelSlotProbe { import SpriteBase; import TestBedSurface; compose ModelSlotType Model; [Phase(Surface)] extend SpriteBase.Shading() { Surface s = (Surface)0; LightSample l = (LightSample)0; return float4(Model.DirectLight(s, l, float3(0, 1, 0)), 1); } };",
            ["NoLight"] = "namespace Stride.MaterialTestBed;\nshader NoLight { float Other() { return 1; } };",
        };

        EffectSource Effect(string model)
        {
            var effect = TestBedMixer.Effect("ModelSlotProbe");
            effect.Root.AddComposition("Model", new ShaderClassSource(TestBedMixer.FullName(model)));
            return effect;
        }

        var module = TestBedMixer.Mix(Effect("TestBedStandardModel"), isAssetBuild: true, sources);
        Assert.False(module.Ok, module.Log);
        Assert.Contains("SDSL0247", module.Log);

        // A shader isn't checked against the type of the slot yet: only the call fails

        var missing = TestBedMixer.Mix(Effect("NoLight"), isAssetBuild: true, sources);
        Assert.False(missing.Ok, missing.Log);
        Assert.Contains("Can't find function DirectLight in current mixin", missing.Log);
    }

    [Fact]
    public void AnAbstractMethodIsARequiredPartWithAnErrorAtLink()
    {
        var sources = new Dictionary<string, string>
        {
            ["RequiredPart"] = "namespace Stride.MaterialTestBed;\n[Pure] module RequiredPart { abstract float3 Required(float3 c); };",
            ["RequiredUse"] = "namespace Stride.MaterialTestBed;\nmodule RequiredUse { import SpriteBase; import RequiredPart; [Phase(Surface)] extend SpriteBase.Shading() { return float4(Required(float3(1, 1, 1)), 1); } };",
            ["RequiredOverride"] = "namespace Stride.MaterialTestBed;\n[Pure] module RequiredOverride { import RequiredPart; override float3 Required(float3 c) { return c; } };",
            ["RequiredExtend"] = "namespace Stride.MaterialTestBed;\n[Pure] module RequiredExtend { import RequiredPart; extend RequiredPart.Required(float3 c) { return c; } };",
        };

        var missing = TestBedMixer.Mix(TestBedMixer.Effect("RequiredUse"), isAssetBuild: true, sources);
        Assert.False(missing.Ok, missing.Log);
        Assert.Contains("SDSL0166: RequiredUse.Shading calls abstract method RequiredPart.Required", missing.Log);

        var written = TestBedMixer.Mix(TestBedMixer.Effect("RequiredUse", "RequiredOverride"), isAssetBuild: true, sources);
        Assert.True(written.Ok, written.Log);

        var extended = TestBedMixer.Mix(TestBedMixer.Effect("RequiredUse", "RequiredExtend"), isAssetBuild: true, sources);
        Assert.False(extended.Ok, extended.Log);
        Assert.Contains("SDSL0143: extend of RequiredPart.Required: it is not an extension point: use patch.", extended.Log);
    }

    // Case 3: Around from outside

    [Theory]
    [InlineData("TestBedSpriteDebugTint", "Shading", "Post: TestBedHalfTint > TestBedDebugView   order: block")]
    [InlineData("TestBedSpriteTintDebug", "Shading", "Post: TestBedDebugView > TestBedHalfTint   order: block")]
    [InlineData("TestBedFilterDoubleOffset", "FilterColor", "Inner: TestBedFilterOffset > TestBedFilterDouble   order: block")]
    [InlineData("TestBedFilterOffsetDouble", "FilterColor", "Inner: TestBedFilterDouble > TestBedFilterOffset   order: block")]
    public void TwoWrappersInAPhaseAreNotSeen(string effect, string method, string line)
    {
        var result = TestBedMixer.MixEffect(effect);

        Assert.True(result.Ok, result.Log);
        Assert.Contains(line, result.Block(method));
    }

    [Fact]
    public void AWrapperThatNeverCallsNextIsAnError()
    {
        var result = TestBedMixer.Mix("SpriteBase", "TestBedSpriteColor", "TestBedHalfTint", "TestBedFixedPost");

        Assert.False(result.Ok, result.Log);
        Assert.Contains("SDSL0136: TestBedFixedPost.Shading doesn't call next, so TestBedSpriteColor, TestBedHalfTint never run in this effect. Write [Drops(Mixins = { TestBedSpriteColor, TestBedHalfTint })] on it, or call next.", result.Log);
    }

    [Fact]
    public void AWrapperThatNeverCallsNextAndDropsNoContributorIsFine()
    {
        var result = TestBedMixer.Mix("SpriteBase", "TestBedFixedPost");

        Assert.True(result.Ok, result.Log);
    }

    [Fact]
    public void AConditionalNextIsNotADrop()
    {
        var result = TestBedMixer.MixEffect("TestBedSpriteDebugView");

        Assert.True(result.Ok, result.Log);
        Assert.DoesNotContain("SDSL0136", result.Log);
    }

    // Case 5: composition with extension

    private const string NodeStream = "namespace Stride.MaterialTestBed;\nmodule NodeStream { stream float NodeValue; };";

    private static readonly Dictionary<string, string> NodeSources = new()
    {
        ["NodeStream"] = NodeStream,
        ["NodeStepA"] = "namespace Stride.MaterialTestBed;\nshader NodeStepA : TestBedNode { import NodeStream; [Phase(Detail)] extend TestBedNode.ModifyColor(inout float3 color) { streams.NodeValue = 1.0; } };",
        ["NodeStepB"] = "namespace Stride.MaterialTestBed;\nshader NodeStepB : TestBedNode { import NodeStream; [Phase(Detail)] extend TestBedNode.ModifyColor(inout float3 color) { streams.NodeValue = 2.0; } };",
        ["NodeStepC"] = "namespace Stride.MaterialTestBed;\nshader NodeStepC : TestBedNode { import NodeStream; [Phase(Detail)] [MixinOrder(After = NodeStepA)] extend TestBedNode.ModifyColor(inout float3 color) { streams.NodeValue = 2.0; } };",
        ["NodeStepD"] = "namespace Stride.MaterialTestBed;\nshader NodeStepD : TestBedNode { import NodeStream; [Phase(MaterialTestBed.Later, After = Detail)] override void ModifyColor(inout float3 color) { base.ModifyColor(color); streams.NodeValue = 3.0; } };",
        ["NodeFromRoot"] = "namespace Stride.MaterialTestBed;\nshader NodeFromRoot : TestBedNode { [Phase(Detail)] extend TestBedNode.ModifyColor(inout float3 color) { color += 0.5; } };",
    };

    private static EffectSource NodeEffect(string[] rootClasses, params string[] nodeClasses)
    {
        var effect = TestBedMixer.Effect(["TestBedNodeProbe", .. rootClasses]);
        var node = new ShaderMixinSource();
        foreach (var name in nodeClasses)
            node.Mixins.Add(new ShaderClassSource(TestBedMixer.FullName(name)));
        effect.Root.AddComposition("Node", node);
        return effect;
    }

    [Fact]
    public void APointOfANodeOrdersByPhaseInEachNode()
    {
        var result = TestBedMixer.MixEffect("TestBedNodeDoubleOffset");

        Assert.True(result.Ok, result.Log);
        Assert.Contains("Base: TestBedNode > TestBedNodeDouble > TestBedNodeOffsetLast   order: mixin order", result.Block("ModifyColor"));
    }

    // The error asks for what works. A block at the root does nothing here, and in a composition it is SDSL0151
    [Fact]
    public void AConflictInANodeAsksForMixinOrderOrAPhase()
    {
        var plain = TestBedMixer.Mix(NodeEffect([], "NodeStepA", "NodeStepB"), isAssetBuild: true, NodeSources);
        Assert.False(plain.Ok, plain.Log);
        Assert.Contains("A block of the effect doesn't reach a composition: add [MixinOrder(After = NodeStepA)] to NodeStepB", plain.Log);
        Assert.DoesNotContain("extend shader TestBedNode", plain.Log);

        var withRule = NodeEffect([], "NodeStepA", "NodeStepB");
        withRule.MixinOrder("TestBedNode", "ModifyColor", "NodeStepA", "NodeStepB");
        var ruled = TestBedMixer.Mix(withRule, isAssetBuild: true, NodeSources);
        Assert.False(ruled.Ok, ruled.Log);
        Assert.Contains("SDSL0137", ruled.Log);

        var after = TestBedMixer.Mix(NodeEffect([], "NodeStepC", "NodeStepA"), isAssetBuild: true, NodeSources);
        Assert.True(after.Ok, after.Log);
        Assert.Contains("Detail: NodeStepA > NodeStepC   order: mixin order", after.Block("ModifyColor"));
    }

    // Without [MixinOrder]: a phase that one of the two adds
    [Fact]
    public void AnAddedPhaseAlsoSettlesAConflictInANode()
    {
        var result = TestBedMixer.Mix(NodeEffect([], "NodeStepA", "NodeStepD"), isAssetBuild: true, NodeSources);

        Assert.True(result.Ok, result.Log);
        Assert.Contains("MaterialTestBed.Later: NodeStepD", result.Block("ModifyColor"));
    }

    // It has a chain of its own at the root, which nothing calls: the node doesn't get the step, and nothing says so
    [Fact]
    public void AClassAtTheRootDoesNotReachThePointOfANode()
    {
        var result = TestBedMixer.Mix(NodeEffect(["NodeFromRoot"], "TestBedNodeDouble"), isAssetBuild: true, NodeSources);

        Assert.True(result.Ok, result.Log);
        Assert.Contains("method ModifyColor(inout float3) -> void = TestBedNode > NodeFromRoot", result.Explanation);
        Assert.Contains("method ModifyColor(inout float3) -> void = TestBedNode > TestBedNodeDouble", result.Explanation);
    }

    // For a slot that isn't a list, a second statement replaces the first: a layer can replace a node, not add a class to it
    [Fact]
    public void AnEffectCanOnlyReplaceANode()
    {
        var result = TestBedMixer.MixEffect("TestBedNodeWrittenTwice");

        Assert.True(result.Ok, result.Log);
        Assert.Contains("method ModifyColor(inout float3) -> void = TestBedNode > TestBedNodeOffset", result.Explanation);
        Assert.DoesNotContain("TestBedNode > TestBedNodeDouble > TestBedNodeOffset", result.Explanation);
    }

    // Case 6: a pure module in compute

    [Fact]
    public void APureModelCompilesInCompute()
    {
        var result = TestBedMixer.MixEffect("TestBedModelComputeCel");

        Assert.True(result.Ok, result.Log);
    }

    [Fact]
    public void APureModuleTakesAnExplicitLevelOfDetail()
    {
        var result = TestBedMixer.Mix("TestBedPureSampleCompute");

        Assert.True(result.Ok, result.Log);
    }

    [Fact]
    public void APureModuleRejectsAnAutomaticLevelOfDetail()
    {
        var result = MixSource("SpriteBase", "[Pure] module PureAutomatic { float4 Read(Texture2D source, SamplerState samplerState, float2 uv) { return source.Sample(samplerState, uv); } };");

        Assert.False(result.Ok, result.Log);
        Assert.Contains("SDSL0213: [Pure] module PureAutomatic: Sample() in Read() works only in a pixel shader", result.Log);
    }

    // Outside a pure module the mixer takes it, and the error comes from SPIR-V validation: see the value test
    [Fact]
    public void AnAutomaticLevelOfDetailInComputeMixes()
    {
        var result = TestBedMixer.MixEffect("TestBedComputeAutomaticSample");

        Assert.True(result.Ok, result.Log);
    }
}
