// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Xunit;

using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Stride.Shaders.Compiler;

using Buffer = Stride.Graphics.Buffer;

namespace Stride.MaterialTestBed;

/// <summary>
///   Runs effects of <c>TestBedStressEffects.sdfx</c> on the GPU and checks what they return, with no reference image.
/// </summary>
public class TestBedValueTests : GameTestBase
{
    private const float Tolerance = 0.0005f;

    private readonly string[] pixelEffects;
    private readonly string[] computeEffects;
    private readonly Action<EffectInstance> setParameters;
    private readonly Dictionary<string, Vector4> values = [];

    public TestBedValueTests() : this([], [])
    {
    }

    private TestBedValueTests(string[] pixelEffects, string[] computeEffects, Action<EffectInstance> setParameters = null)
    {
        this.pixelEffects = pixelEffects;
        this.computeEffects = computeEffects;
        this.setParameters = setParameters;
        GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
        GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
    }

    protected override void RegisterTests()
    {
        base.RegisterTests();
        FrameGameSystem.Draw(1, ReadValues);
    }

    private Effect LoadEffect(string name)
        => EffectSystem.LoadEffect($"Stride.MaterialTestBed.{name}", new CompilerParameters()).WaitForResult();

    private void ReadValues()
    {
        var commandList = GraphicsContext.CommandList;

        using var target = Texture.New2D(GraphicsDevice, 1, 1, PixelFormat.R32G32B32A32_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        foreach (var name in pixelEffects)
        {
            using var effect = new EffectInstance(LoadEffect(name));
            effect.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
            setParameters?.Invoke(effect);

            commandList.ResourceBarrierTransition(target, BarrierLayout.RenderTarget);
            commandList.SetRenderTargetAndViewport(null, target);
            GraphicsContext.DrawQuad(effect);
            commandList.SetRenderTargetAndViewport(null, GraphicsDevice.Presenter.BackBuffer);
            values[name] = target.GetData<Vector4>(commandList)[0];
        }

        using var result = Buffer.Structured.New<Vector4>(GraphicsDevice, 1, unorderedAccess: true);
        foreach (var name in computeEffects)
        {
            using var effect = new EffectInstance(LoadEffect(name));
            effect.Parameters.Set(TestBedModelComputeKeys.ModelResult, result);
            effect.Parameters.Set(TestBedPureSampleComputeKeys.ModelResult, result);
            effect.Parameters.Set(TexturingKeys.Texture0, GraphicsDevice.GetSharedWhiteTexture());
            effect.UpdateEffect(GraphicsDevice);

            var pipeline = new MutablePipelineState(GraphicsDevice);
            pipeline.State.SetDefaults();
            pipeline.State.RootSignature = effect.RootSignature;
            pipeline.State.EffectBytecode = effect.Effect.Bytecode;
            pipeline.Update();

            commandList.SetPipelineState(pipeline.CurrentState);
            commandList.ResourceBarrierTransition(result, BarrierLayout.UnorderedAccess);
            effect.Apply(GraphicsContext);
            commandList.Dispatch(1, 1, 1);
            values[name] = result.GetData<Vector4>(commandList)[0];
        }
    }

    private static Dictionary<string, Vector4> Run(string[] pixelEffects, string[] computeEffects = null, Action<EffectInstance> setParameters = null)
    {
        var game = new TestBedValueTests(pixelEffects, computeEffects ?? [], setParameters);
        RunGameTest(game);
        return game.values;
    }

    private static void AssertNear(Vector4 expected, Vector4 actual, string name)
    {
        for (var i = 0; i < 4; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= Tolerance, $"{name}: expected {expected}, got {actual}");
    }

    // From 0.5: wet is lerp(r, 0.1, 0.5), snow is lerp(r, 0.9, 0.5). So 0.7 then 0.4, or 0.3 then 0.6
    private const float WetThenSnow = 0.6f;
    private const float SnowThenWet = 0.4f;

    // The compiler sees nothing here, and the order of the mix gives another result
    [SkippableFact]
    public void TheOrderOfTheMixChangesTheResultOfTheBlindPairs()
    {
        var values = Run(["TestBedInoutWetSnow", "TestBedInoutSnowWet", "TestBedCurrentWetSnow", "TestBedCurrentSnowWet", "TestBedSharedWetSnow", "TestBedSharedSnowWet"]);

        Assert.Equal(WetThenSnow, values["TestBedInoutWetSnow"].X, Tolerance);
        Assert.Equal(SnowThenWet, values["TestBedInoutSnowWet"].X, Tolerance);
        Assert.Equal(WetThenSnow, values["TestBedCurrentWetSnow"].Y, Tolerance);
        Assert.Equal(SnowThenWet, values["TestBedCurrentSnowWet"].Y, Tolerance);
        Assert.Equal(WetThenSnow, values["TestBedSharedWetSnow"].Z, Tolerance);
        Assert.Equal(SnowThenWet, values["TestBedSharedSnowWet"].Z, Tolerance);
    }

    [SkippableFact]
    public void TheOrderLineAndAfterGiveTheResultTheyState()
    {
        var values = Run(["TestBedInoutOrdered", "TestBedInoutAfter", "TestBedStreamOrdered"]);

        Assert.Equal(WetThenSnow, values["TestBedInoutOrdered"].X, Tolerance);
        Assert.Equal(WetThenSnow, values["TestBedInoutAfter"].X, Tolerance);
        Assert.Equal(WetThenSnow, values["TestBedStreamOrdered"].W, Tolerance);
    }

    // The same evaluation in a pixel shader and in a compute shader, for two models
    [SkippableFact]
    public void AModelGivesTheSameValuesInComputeAndInAPixelShader()
    {
        var values = Run(["TestBedModelDefault", "TestBedModelCel", "TestBedModelRim", "TestBedModelGlow"], ["TestBedModelComputeDefault", "TestBedModelComputeCel"]);

        AssertNear(values["TestBedModelDefault"], values["TestBedModelComputeDefault"], "Standard");
        AssertNear(values["TestBedModelCel"], values["TestBedModelComputeCel"], "Cel");

        // Cel: 0.8 * 0.75 diffuse, nDotL of 0.87 gives the band 1, and the color and attenuation
        Assert.Equal(0.8f * 0.75f * 1.0f * 0.75f, values["TestBedModelCel"].X, Tolerance);
        Assert.NotEqual(values["TestBedModelDefault"].X, values["TestBedModelCel"].X);

        // [Replaces] gives the winner its value, and the part it doesn't write keeps the default
        Assert.Equal(0.25f, values["TestBedModelRim"].X, Tolerance);
        Assert.Equal(values["TestBedModelCel"].Z, values["TestBedModelRim"].Z, Tolerance);

        // The optional part: the default of the contract, and a model that writes only this part
        Assert.Equal(0.5f, values["TestBedModelCel"].W, Tolerance);
        Assert.Equal(1.0f, values["TestBedModelGlow"].W, Tolerance);
        Assert.Equal(values["TestBedModelDefault"].X, values["TestBedModelGlow"].X, Tolerance);
    }

    [SkippableFact]
    public void APureModuleSamplesAtAnExplicitLevelOfDetailInCompute()
    {
        var values = Run([], ["TestBedPureSampleInCompute"]);

        AssertNear(new Vector4(1.0f), values["TestBedPureSampleInCompute"], "White texture");
    }

    [SkippableFact]
    public void ASampleWithAnAutomaticLevelOfDetailBreaksInTheEffectCompiler()
    {
        var error = Assert.ThrowsAny<Exception>(() => Run([], ["TestBedComputeAutomaticSample"]));

        Assert.Contains("ImplicitLod instructions require", error.ToString());
    }

    [SkippableFact]
    public void AWrapperChangesTheResultOrSkipsIt()
    {
        var off = Run(["TestBedSpriteOnly", "TestBedSpriteDebugView", "TestBedSpriteDebugTint", "TestBedSpriteTintDebug"], setParameters: x => x.Parameters.Set(TestBedDebugViewKeys.DebugViewEnabled, 0.0f));

        var color = new Vector4(0.25f, 0.5f, 0.75f, 1.0f);
        var tinted = new Vector4(0.125f, 0.25f, 0.375f, 1.0f);
        AssertNear(color, off["TestBedSpriteOnly"], "No wrapper");
        AssertNear(color, off["TestBedSpriteDebugView"], "Debug view off");
        AssertNear(tinted, off["TestBedSpriteDebugTint"], "Both, off");
        AssertNear(tinted, off["TestBedSpriteTintDebug"], "Both, off, swapped");

        // Two wrappers meet: the outermost decides, so the order of the mix changes the result
        var on = Run(["TestBedSpriteDebugView", "TestBedSpriteDebugTint", "TestBedSpriteTintDebug"], setParameters: x => x.Parameters.Set(TestBedDebugViewKeys.DebugViewEnabled, 1.0f));

        var magenta = new Vector4(1.0f, 0.0f, 1.0f, 1.0f);
        AssertNear(magenta, on["TestBedSpriteDebugView"], "Debug view on");
        AssertNear(new Vector4(0.5f, 0.0f, 0.5f, 1.0f), on["TestBedSpriteDebugTint"], "Tint outside the debug view");
        AssertNear(magenta, on["TestBedSpriteTintDebug"], "Debug view outside the tint");
    }

    [SkippableFact]
    public void TheOrderOfTwoWrappersOnAPointOfTheTestBedChangesTheResult()
    {
        var values = Run(["TestBedFilterDoubleOffset", "TestBedFilterOffsetDouble"]);

        // The last of the mix is the outermost. Offset outside: 0.25 * 2 + 0.125. Double outside: (0.25 + 0.125) * 2
        Assert.Equal(0.625f, values["TestBedFilterDoubleOffset"].X, Tolerance);
        Assert.Equal(0.75f, values["TestBedFilterOffsetDouble"].X, Tolerance);
    }

    [SkippableFact]
    public void APointOfANodeAndAListOfNodesGiveTheSameResult()
    {
        var values = Run(["TestBedNodeDoubleOffset", "TestBedNodeOffsetDouble"]);

        // Double then offset: 0.25 * 2 + 0.125. Offset then double: (0.25 + 0.125) * 2
        Assert.Equal(0.625f, values["TestBedNodeDoubleOffset"].X, Tolerance);
        Assert.Equal(0.625f, values["TestBedNodeDoubleOffset"].Y, Tolerance);
        Assert.Equal(0.75f, values["TestBedNodeOffsetDouble"].X, Tolerance);
        Assert.Equal(0.75f, values["TestBedNodeOffsetDouble"].Y, Tolerance);
    }
}
