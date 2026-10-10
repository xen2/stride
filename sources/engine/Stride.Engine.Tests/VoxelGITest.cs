// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Xunit;

using Stride.Core.Mathematics;
using Stride.Engine.Processors;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Stride.Rendering.Colors;
using Stride.Rendering.Compositing;
using Stride.Rendering.Lights;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.ProceduralModels;
using Stride.Rendering.Voxels;
using Stride.Rendering.Voxels.VoxelGI;
using Stride.Shaders.Compiler;

namespace Stride.Engine.Tests;

/// <summary>
/// A Cornell box lit by a point light and voxel global illumination, voxelized with multisampled render targets, once per
/// voxelization method: the red and green walls bleed their color onto the white floor, ceiling and boxes.
/// </summary>
public class VoxelGITest : RenderFeatureTestBase
{
    // The clipmaps of the volume fill over the first frames
    private const int ScreenshotFrame = 16;

    // Inner size of the box, from the floor at 0
    private const float BoxSize = 4;

    public IVoxelizationMethod VoxelizationMethod { get; init; } = new VoxelizationMethodDominantAxis();

    protected override async Task LoadContent()
    {
        await base.LoadContent();

        // The volume fills over the first frames once its effects are compiled: compiling them before their first draw
        // keeps the screenshot independent of how fast the machine compiles
        ((EffectCompilerCache)EffectSystem.Compiler).CompileEffectAsynchronously = false;

        // The voxel parts of DefaultGraphicsCompositorVoxels (Stride.Voxels), added to the test compositor
        var compositor = SceneSystem.GraphicsCompositor;
        var voxelStages = new[] { new RenderStage("VoxelizationPassFirst", "Voxelizer"), new RenderStage("VoxelizationPassSecond", "Voxelizer2") };
        var meshRenderFeature = compositor.RenderFeatures.OfType<MeshRenderFeature>().First();
        var voxelPipelineProcessor = new VoxelPipelineProcessor();
        foreach (var stage in voxelStages)
        {
            compositor.RenderStages.Add(stage);
            meshRenderFeature.RenderStageSelectors.Add(new MeshTransparentRenderStageSelector
            {
                OpaqueRenderStage = stage,
                TransparentRenderStage = stage,
                EffectName = "StrideForwardShadingEffectVXGI.VoxelizeToFragmentsEffect",
            });
            voxelPipelineProcessor.VoxelRenderStage.Add(stage);
        }
        meshRenderFeature.PipelineProcessors.Add(voxelPipelineProcessor);
        meshRenderFeature.RenderFeatures.Add(new VoxelRenderFeature());
        meshRenderFeature.RenderFeatures.OfType<ForwardLightingRenderFeature>().First().LightRenderers.Add(new LightVoxelRenderer());

        var forwardRenderer = (ForwardRenderer)compositor.SingleView;
        var voxelRenderer = new VoxelRenderer();
        voxelRenderer.VoxelStages.AddRange(voxelStages);
        var forwardRendererVoxels = new ForwardRendererVoxels
        {
            Clear = forwardRenderer.Clear,
            OpaqueRenderStage = forwardRenderer.OpaqueRenderStage,
            TransparentRenderStage = forwardRenderer.TransparentRenderStage,
            VoxelRenderer = voxelRenderer,
        };
        forwardRendererVoxels.ShadowMapRenderStages.AddRange(forwardRenderer.ShadowMapRenderStages);
        var children = ((SceneRendererCollection)((SceneCameraRenderer)compositor.Game).Child).Children;
        children[children.IndexOf(forwardRenderer)] = forwardRendererVoxels;
        compositor.SingleView = forwardRendererVoxels;

        CreateCornellBox();

        var volume = new VoxelVolumeComponent
        {
            VoxelizationMethod = VoxelizationMethod,
            VoxelVolumeSize = BoxSize + 1,
            AproximateVoxelSize = 0.1f,
            Attributes = { new VoxelAttributeEmissionOpacity() },
        };
        var volumeEntity = new Entity { volume, new LightComponent { Type = new LightVoxel { Volume = volume }, Intensity = 2 } };
        volumeEntity.Transform.Position = new Vector3(0, BoxSize / 2, 0);
        Scene.Entities.Add(volumeEntity);
    }

    private void CreateCornellBox()
    {
        // The bounces light the box: almost no ambient light
        AmbientLight.Intensity = 0.05f;

        var white = CreateDiffuseMaterial(new Color4(0.75f, 0.75f, 0.75f, 1));
        var red = CreateDiffuseMaterial(new Color4(0.75f, 0.1f, 0.1f, 1));
        var green = CreateDiffuseMaterial(new Color4(0.1f, 0.75f, 0.1f, 1));

        const float half = BoxSize / 2;
        const float thickness = 0.1f;
        AddBox(new Vector3(BoxSize, thickness, BoxSize), new Vector3(0, -thickness / 2, 0), 0, white);              // Floor
        AddBox(new Vector3(BoxSize, thickness, BoxSize), new Vector3(0, BoxSize + thickness / 2, 0), 0, white);     // Ceiling
        AddBox(new Vector3(BoxSize, BoxSize, thickness), new Vector3(0, half, -half - thickness / 2), 0, white);    // Back
        AddBox(new Vector3(thickness, BoxSize, BoxSize), new Vector3(-half - thickness / 2, half, 0), 0, red);      // Left
        AddBox(new Vector3(thickness, BoxSize, BoxSize), new Vector3(half + thickness / 2, half, 0), 0, green);     // Right
        AddBox(new Vector3(1.2f, 2.4f, 1.2f), new Vector3(-0.7f, 1.2f, -0.6f), 0.3f, white);                        // Tall box
        AddBox(new Vector3(1.2f, 1.2f, 1.2f), new Vector3(0.8f, 0.6f, 0.7f), -0.3f, white);                         // Short box

        var light = new Entity { new LightComponent { Type = new LightPoint { Radius = 8, Color = new ColorRgbProvider(Color.White) }, Intensity = 4 } };
        light.Transform.Position = new Vector3(0, BoxSize - 0.5f, 0);
        Scene.Entities.Add(light);

        Camera.Transform.Position = new Vector3(0, half, 7.5f);
        Camera.Transform.Rotation = Quaternion.Identity;
    }

    private Material CreateDiffuseMaterial(Color4 color)
    {
        return Material.New(GraphicsDevice, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(color)),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
            },
        });
    }

    private void AddBox(Vector3 size, Vector3 position, float rotationY, Material material)
    {
        var model = new ProceduralModelDescriptor(new CubeProceduralModel { Size = size, MaterialInstance = { Material = material } }).GenerateModel(Services);
        var entity = new Entity { new ModelComponent(model) };
        entity.Transform.Position = position;
        entity.Transform.Rotation = Quaternion.RotationY(rotationY);
        Scene.Entities.Add(entity);
    }

    // False when the voxelization method needs geometry shaders and the device has none: the voxel renderer skips the
    // volume, so there is nothing to check
    private bool supported = true;

    protected override void RegisterTests()
    {
        base.RegisterTests();
        supported = !VoxelizationMethod.RequireGeometryShader() || GraphicsDevice.Features.HasGeometryShaders;
        if (supported)
            FrameGameSystem.TakeScreenshot(ScreenshotFrame);
        else
            FrameGameSystem.Draw(1, () => { });
    }

    private static void Run(VoxelGITest game, [CallerMemberName] string callerName = null)
    {
        RunGameTest(game, callerName);
        Skip.IfNot(game.supported, $"{game.VoxelizationMethod.GetType().Name} needs geometry shaders, which this device does not support.");
    }

    // Needs geometry shaders: skipped where there are none (e.g. MoltenVK)
    [SkippableFact]
    public void DominantAxis() => Run(new VoxelGITest { VoxelizationMethod = new VoxelizationMethodDominantAxis(), TestName = nameof(DominantAxis) });

    // Three single axis passes, without geometry shaders
    [SkippableFact]
    public void TriAxis() => Run(new VoxelGITest { VoxelizationMethod = new VoxelizationMethodTriAxis(), TestName = nameof(TriAxis) });
}
