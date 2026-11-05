// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Xunit;

using Stride.Core.Mathematics;
using Stride.Engine;
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

    private string ImageName => !useMaterialTable ? "GeneratedMaterials" : enableUvTransform ? "UvTransform" : enableWetness ? "Wetness" : "MaterialTable";

    private TestBedTextures textures;
    private MaterialTable materialTable;

    public TestBedSceneTests() : this(false)
    {
    }

    private TestBedSceneTests(bool useMaterialTable, bool enableUvTransform = false, bool enableWetness = false)
    {
        this.useMaterialTable = useMaterialTable;
        this.enableUvTransform = enableUvTransform;
        this.enableWetness = enableWetness;

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
        FrameGameSystem.TakeScreenshot(2, ImageName);
    }

    protected override async Task LoadContent()
    {
        await base.LoadContent();

        textures = new TestBedTextures(GraphicsDevice);
        var descriptions = CreateMaterials(textures);

        var materials = new List<Material>();
        if (useMaterialTable)
        {
            materialTable = new MaterialTable(GraphicsDevice, Content.Load<Texture>(LookupTableUrl));
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
            EnableWetness = enableWetness,
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
}
