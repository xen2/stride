// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Linq;

using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.MaterialTestBed;

/// <summary>
///   Gives each draw the index of its material through the push constants. <see cref="MeshRenderFeature"/> has no per-draw hook,
///   so this draws one node at a time.
/// </summary>
public sealed class TestBedMeshRenderFeature : MeshRenderFeature
{
    public MaterialTable MaterialTable { get; set; }

    public override void Draw(RenderDrawContext context, RenderView renderView, RenderViewStage renderViewStage, int startIndex, int endIndex)
    {
        for (int index = startIndex; index < endIndex; index++)
        {
            var renderNode = GetRenderNode(renderViewStage.SortedRenderNodes[index].RenderNode);
            var renderMesh = (RenderMesh)renderNode.RenderObject;
            if (MaterialTable != null && MaterialTable.TryGetIndex(renderMesh.MaterialPass, out var materialIndex))
                context.CommandList.SetPushConstants(materialIndex);

            base.Draw(context, renderView, renderViewStage, index, index + 1);
        }
    }

    /// <summary>
    ///   Replaces the mesh feature of a compositor with this one, keeping its sub features, selectors and processors.
    /// </summary>
    public static TestBedMeshRenderFeature Install(GraphicsCompositor compositor)
    {
        var source = compositor.RenderFeatures.OfType<MeshRenderFeature>().Single();
        var feature = new TestBedMeshRenderFeature();
        foreach (var subFeature in source.RenderFeatures.ToArray())
        {
            source.RenderFeatures.Remove(subFeature);
            feature.RenderFeatures.Add(subFeature);
        }
        foreach (var selector in source.RenderStageSelectors)
            feature.RenderStageSelectors.Add(selector);
        foreach (var processor in source.PipelineProcessors)
            feature.PipelineProcessors.Add(processor);

        compositor.RenderFeatures[compositor.RenderFeatures.IndexOf(source)] = feature;
        return feature;
    }
}

/// <summary>
///   Selects the optional parts of the effect layer in <c>TestBedEffects.sdfx</c> for each mesh, and uploads their values. The
///   same job as <c>BendFogRenderFeature</c> in the SpaceEscape sample.
/// </summary>
public sealed class TestBedEffectRenderFeature : SubRenderFeature
{
    private StaticObjectPropertyKey<RenderEffect> renderEffectKey;
    private ConstantBufferOffsetReference textureRegion;

    public bool EnableUvTransform { get; set; }

    public bool EnableWetness { get; set; }

    public Vector4 TextureRegion { get; set; } = new(0, 0, 1, 1);

    protected override void InitializeCore()
    {
        base.InitializeCore();

        var rootFeature = (RootEffectRenderFeature)RootRenderFeature;
        renderEffectKey = rootFeature.RenderEffectKey;
        textureRegion = rootFeature.CreateDrawCBufferOffsetSlot(TestBedUvTransformKeys.TextureRegion.Name);
    }

    public override void PrepareEffectPermutations(RenderDrawContext context)
    {
        var renderEffects = RootRenderFeature.RenderData.GetData(renderEffectKey);
        var effectSlotCount = ((RootEffectRenderFeature)RootRenderFeature).EffectPermutationSlotCount;

        foreach (var renderObject in RootRenderFeature.RenderObjects)
        {
            var staticObjectNode = renderObject.StaticObjectNode;
            for (int i = 0; i < effectSlotCount; i++)
            {
                var renderEffect = renderEffects[staticObjectNode * effectSlotCount + i];
                if (renderEffect == null || !renderEffect.IsUsedDuringThisFrame(RenderSystem))
                    continue;

                renderEffect.EffectValidator.ValidateParameter(TestBedEffectKeys.EnableUvTransform, EnableUvTransform);
                renderEffect.EffectValidator.ValidateParameter(TestBedEffectKeys.EnableWetness, EnableWetness);
            }
        }
    }

    public override unsafe void Prepare(RenderDrawContext context)
    {
        foreach (var renderNode in ((RootEffectRenderFeature)RootRenderFeature).RenderNodes)
        {
            var perDrawLayout = renderNode.RenderEffect.Reflection?.PerDrawLayout;
            if (perDrawLayout == null)
                continue;

            var offset = perDrawLayout.GetConstantBufferOffset(textureRegion);
            if (offset != -1)
                *(Vector4*)((byte*)renderNode.Resources.ConstantBuffer.Data + offset) = TextureRegion;
        }
    }
}
