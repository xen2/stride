// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Stride.Rendering.Colors;
using Stride.Rendering.Compositing;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;

namespace Stride.MaterialTestBed;

/// <summary>
///   One scene, the same materials: drawn by today's generated materials, and by the material table of the test-bed.
/// </summary>
public class TestBedSceneTests : GameTestBase
{
    private const string LookupTableUrl = "/Stride.Engine/StrideEnvironmentLightingDFGLUT16";

    private readonly bool useMaterialTable;
    private readonly bool enableUvTransform;
    private readonly bool enableWetness;
    private readonly StressOptions stress;
    private Color[] capturedPixels;

    private string ImageName => !useMaterialTable ? "GeneratedMaterials" : enableUvTransform ? "UvTransform" : enableWetness ? "Wetness" : "MaterialTable";

    private TestBedTextures textures;
    private MaterialTable materialTable;

    /// <summary>What a stress run of the scene turns on. Such a run compares its own pixels, and has no reference image.</summary>
    private sealed record StressOptions(bool Snow = false, bool Wetness = false, bool ModelShading = false, bool Cel = false, bool DebugViewClass = false, bool ShowDebugView = false);

    public TestBedSceneTests() : this(false)
    {
    }

    private TestBedSceneTests(bool useMaterialTable, bool enableUvTransform = false, bool enableWetness = false, StressOptions stress = null)
    {
        this.useMaterialTable = useMaterialTable;
        this.enableUvTransform = enableUvTransform;
        this.enableWetness = enableWetness;
        this.stress = stress;

        GraphicsDeviceManager.PreferredBackBufferWidth = 960;
        GraphicsDeviceManager.PreferredBackBufferHeight = 540;
        GraphicsDeviceManager.PreferredDepthStencilFormat = PixelFormat.D24_UNorm_S8_UInt;
        GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
        GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;

        IsFixedTimeStep = true;
        ForceOneUpdatePerDraw = true;
        IsDrawDesynchronized = false;
    }

    protected override void RegisterTests()
    {
        base.RegisterTests();
        if (stress != null)
            FrameGameSystem.Draw(2, () => { });
        else
            FrameGameSystem.TakeScreenshot(2, ImageName);
    }

    protected override void Draw(GameTime gameTime)
    {
        base.Draw(gameTime);

        if (stress != null && capturedPixels == null && gameTime.FrameCount >= 2)
        {
            using var image = GraphicsDevice.Presenter.BackBuffer.GetDataAsImage(GraphicsContext.CommandList);
            capturedPixels = image.PixelBuffer[0].GetPixels<Color>();
        }
    }

    protected override async Task LoadContent()
    {
        await base.LoadContent();

        textures = new TestBedTextures(GraphicsDevice);
        var descriptions = CreateMaterials(textures);

        var materials = new List<Material>();
        if (useMaterialTable)
        {
            materialTable = new MaterialTable(GraphicsDevice, Content.Load<Texture>(LookupTableUrl), stress is { ModelShading: true } ? TestBedModelShadingKeys.ShaderName : null);
            foreach (var description in descriptions)
                materials.Add(materialTable.Add(description));
            materialTable.Upload(GraphicsDevice);
        }
        else
        {
            foreach (var description in descriptions)
                materials.Add(GeneratedMaterials.Create(GraphicsDevice, Content, description));
        }

        var scene = new Scene();

        // Ground with the first material, then one rock per material
        scene.Entities.Add(CreateModel(TestBedMeshes.CreateGround(GraphicsDevice, 20, 8), materials[0], Vector3.Zero));
        var rockMesh = TestBedMeshes.CreateRock(GraphicsDevice, 64, 7);
        for (int i = 0; i < materials.Count; i++)
            scene.Entities.Add(CreateModel(rockMesh, materials[i], new Vector3((i - (materials.Count - 1) * 0.5f) * 2.4f, 1.0f, 0)));

        // A shadowed directional light, a point light, an ambient light
        var sun = new Entity
        {
            new LightComponent
            {
                Type = new LightDirectional { Color = new ColorRgbProvider(new Color3(1.0f, 0.95f, 0.9f)), Shadow = { Enabled = true, Size = LightShadowMapSize.Large, CascadeCount = LightShadowMapCascadeCount.OneCascade } },
                Intensity = 2.5f,
            },
        };
        sun.Transform.Rotation = Quaternion.RotationYawPitchRoll(MathUtil.Pi / 5, -MathUtil.Pi / 3.5f, 0);
        scene.Entities.Add(sun);

        var point = new Entity { new LightComponent { Type = new LightPoint { Radius = 6, Color = new ColorRgbProvider(new Color3(0.4f, 0.6f, 1.0f)) }, Intensity = 8 } };
        point.Transform.Position = new Vector3(-2, 1.5f, 3);
        scene.Entities.Add(point);

        scene.Entities.Add(new Entity { new LightComponent { Type = new LightAmbient { Color = new ColorRgbProvider(new Color3(0.5f, 0.6f, 0.8f)) }, Intensity = 0.5f } });

        var camera = new CameraComponent { UseCustomAspectRatio = false };
        var cameraEntity = new Entity { camera };
        cameraEntity.Transform.Position = new Vector3(0, 3.5f, 9);
        cameraEntity.Transform.Rotation = Quaternion.RotationX(-0.33f);
        scene.Entities.Add(cameraEntity);

        var compositor = GraphicsCompositorHelper.CreateDefault(false, camera: camera, clearColor: new Color4(0.3f, 0.35f, 0.45f, 1), graphicsProfile: GraphicsProfile.Level_11_0);
        var meshFeature = TestBedMeshRenderFeature.Install(compositor);
        meshFeature.MaterialTable = materialTable;
        meshFeature.RenderFeatures.Add(new TestBedEffectRenderFeature
        {
            EnableUvTransform = enableUvTransform,
            EnableWetness = enableWetness || stress is { Wetness: true },
            EnableSnow = stress is { Snow: true },
            EnableCel = stress is { Cel: true },
            EnableDebugView = stress is { DebugViewClass: true },
            ShowDebugView = stress is { ShowDebugView: true },
            TextureRegion = new Vector4(0.25f, 0.1f, 0.5f, 0.5f),
        });

        SceneSystem.GraphicsCompositor = compositor;
        SceneSystem.SceneInstance = new SceneInstance(Services, scene);
    }

    private static List<MaterialDescription> CreateMaterials(TestBedTextures textures)
    {
        LayerDescription Rock() => new()
        {
            BaseColor = new SlotDescription { Texture = textures.RockAlbedo, UvScale = new Vector2(2) },
            Normal = new SlotDescription { Texture = textures.RockNormal, UvScale = new Vector2(2) },
            Roughness = new SlotDescription { Texture = textures.RockRoughMetal, Channel = SlotChannel.G, UvScale = new Vector2(2) },
            Metalness = new SlotDescription { Texture = textures.RockRoughMetal, Channel = SlotChannel.B, UvScale = new Vector2(2) },
        };

        return
        [
            // Textures on every input
            new MaterialDescription { Layers = { Rock() } },

            // Constants only: gold
            new MaterialDescription
            {
                Layers =
                {
                    new LayerDescription
                    {
                        BaseColor = SlotDescription.Value(new Vector3(1.0f, 0.78f, 0.34f)),
                        Roughness = SlotDescription.Value(0.3f),
                        Metalness = SlotDescription.Value(1.0f),
                    },
                },
            },

            // Two layers with a mask
            new MaterialDescription
            {
                Layers =
                {
                    Rock(),
                    new LayerDescription
                    {
                        BaseColor = new SlotDescription { Texture = textures.Moss, UvScale = new Vector2(3) },
                        Roughness = SlotDescription.Value(0.8f),
                        Mask = new SlotDescription { Texture = textures.Mask, Channel = SlotChannel.R },
                    },
                },
            },

            // Detail on the second UV channel, with the Add operator and a bias
            new MaterialDescription
            {
                Layers =
                {
                    new LayerDescription
                    {
                        BaseColor = new SlotDescription
                        {
                            Constant = new Vector4(0.2f, 0.25f, 0.4f, 1),
                            Detail = textures.Stripes,
                            DetailUvChannel = 1,
                            DetailUvScale = new Vector2(2),
                            DetailWeight = 0.3f,
                            Op = SlotOp.Add,
                        },
                        Roughness = new SlotDescription { Constant = new Vector4(0.5f), Texture = textures.RockRoughMetal, Channel = SlotChannel.G, Bias = 0.1f },
                    },
                },
            },

            // Emissive
            new MaterialDescription
            {
                Layers =
                {
                    new LayerDescription
                    {
                        BaseColor = SlotDescription.Value(new Vector3(0.1f)),
                        Roughness = SlotDescription.Value(0.5f),
                        Emissive = new SlotDescription { Constant = new Vector4(1.5f, 0.5f, 0.1f, 1), Texture = textures.Stripes, UvScale = new Vector2(2) },
                    },
                },
            },
        ];
    }

    private static Entity CreateModel(Mesh mesh, Material material, Vector3 position)
    {
        var model = new Model { mesh };
        model.Materials.Add(new MaterialInstance(material));
        model.BoundingBox = mesh.BoundingBox;
        var entity = new Entity { new ModelComponent(model) };
        entity.Transform.Position = position;
        return entity;
    }

    protected override void Destroy()
    {
        materialTable?.Dispose();
        textures?.Dispose();
        base.Destroy();
    }

    [SkippableFact]
    public void GeneratedMaterialsScene()
    {
        RunGameTest(new TestBedSceneTests(useMaterialTable: false));
    }

    [SkippableFact]
    public void MaterialTableScene()
    {
        RunGameTest(new TestBedSceneTests(useMaterialTable: true));
    }

    [SkippableFact]
    public void MaterialTableWithUvTransform()
    {
        RunGameTest(new TestBedSceneTests(useMaterialTable: true, enableUvTransform: true));
    }

    [SkippableFact]
    public void MaterialTableWithWetness()
    {
        RunGameTest(new TestBedSceneTests(useMaterialTable: true, enableWetness: true));
    }

    private static Color[] RunStress(StressOptions options)
    {
        var game = new TestBedSceneTests(useMaterialTable: true, stress: options);
        RunGameTest(game);
        return game.capturedPixels;
    }

    private static double ChangedPixels(Color[] first, Color[] second)
    {
        Assert.Equal(first.Length, second.Length);
        return first.Zip(second).Count(x => Math.Abs(x.First.R - x.Second.R) + Math.Abs(x.First.G - x.Second.G) + Math.Abs(x.First.B - x.Second.B) > 8) / (double)first.Length;
    }

    // Through its point, the default model is the same as the Standard model called directly
    [SkippableFact]
    public void TheModelPointWithItsDefaultGivesTheStandardImage()
    {
        var standard = RunStress(new StressOptions());
        var point = RunStress(new StressOptions(ModelShading: true));

        Assert.Equal(0.0, ChangedPixels(standard, point));
    }

    [SkippableFact]
    public void ASecondModelFromAnEffectLayerChangesTheImage()
    {
        var standard = RunStress(new StressOptions(ModelShading: true));
        var cel = RunStress(new StressOptions(ModelShading: true, Cel: true));

        Assert.True(ChangedPixels(standard, cel) > 0.2, $"{ChangedPixels(standard, cel):P1} of the pixels changed");
    }

    // On the point of the mesh path: darkens after next, or skips the shading when it is on
    [SkippableFact]
    public void TheDebugViewOnTheMeshPathChangesOrSkipsTheShading()
    {
        var plain = RunStress(new StressOptions());
        var wrapped = RunStress(new StressOptions(DebugViewClass: true));
        var shown = RunStress(new StressOptions(DebugViewClass: true, ShowDebugView: true));

        Assert.True(ChangedPixels(plain, wrapped) > 0.2, $"{ChangedPixels(plain, wrapped):P1} of the pixels changed");
        var magenta = shown.Count(x => x.R == 255 && x.G == 0 && x.B == 255) / (double)shown.Length;
        Assert.True(magenta > 0.2, $"{magenta:P1} of the pixels are the debug color");
    }

    // The whole mesh path with both modifiers, as a game would have them
    [SkippableFact]
    public void TwoModifiersThatDoNotKnowEachOtherRenderTogether()
    {
        var wet = RunStress(new StressOptions(Wetness: true));
        var both = RunStress(new StressOptions(Wetness: true, Snow: true));

        Assert.True(ChangedPixels(wet, both) > 0.05, $"{ChangedPixels(wet, both):P1} of the pixels changed");
    }
}
