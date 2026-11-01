// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Stride.Rendering.Colors;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;
using Stride.Rendering.Lights;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.ProceduralModels;
using Stride.Rendering.Shadows;
using Stride.Rendering.Tessellation;
using Stride.Rendering.Voxels;
using Stride.Rendering.Voxels.Debug;
using Stride.Rendering.Voxels.VoxelGI;
using Stride.Shaders;
using Stride.Shaders.Compiler;

namespace Stride.Engine.Tests;

/// <summary>
/// One test per rendering feature no sample uses: MSAA, temporal anti-aliasing, ambient occlusion, depth of field, local
/// reflections, cubemap backgrounds, a spot light projecting a texture and thin glass. Each test turns on its feature only.
/// </summary>
/// <remarks>
/// The tests without screenshot draw features only to compile their shaders, so the shader corpus captures their
/// permutations: clear coat, instancing, skinned tangents, mesh velocity, voxel global illumination, the scene editor's
/// render modes, and the color transforms, shading models and compute nodes built in code by the tests.
/// </remarks>
public class RenderFeatureTests : RenderFeatureTestBase
{
    public enum BackgroundMode { None, Cubemap3D, Cubemap2D }

    public enum VoxelMode { None, Default, Variant, ModifiersIsotropic, ModifiersAnisotropic, ModifiersAnisotropicPaired }

    public enum TessellationMode { None, Mesh, Skinned }

    // Temporal anti-aliasing and depth of field autofocus build up over the previous frames
    private const int ScreenshotFrame = 10;

    public MultisampleCount Msaa { get; init; } = MultisampleCount.None;

    /// <summary>
    /// Turns on the post effects of the test, the optional ones start off. Without it, no post effects.
    /// </summary>
    public Action<PostProcessingEffects> PostEffects { get; init; }

    /// <summary>
    /// Adds one sphere per material.
    /// </summary>
    public Func<RenderFeatureTests, MaterialDescriptor[]> Materials { get; init; }

    /// <summary>
    /// Adds a light that casts shadows, and the shadow maps the shared test compositor doesn't render.
    /// </summary>
    public bool Shadows { get; init; }

    /// <summary>
    /// Draws a normal mapped, tessellated material on a mesh or on the skinned knight.
    /// </summary>
    public TessellationMode Tessellation { get; init; }

    public BackgroundMode Background { get; init; }

    /// <summary>
    /// Adds a spot light projecting a texture: C# mixes the projection shaders into the light group's composition node.
    /// </summary>
    public bool ProjectedTexture { get; init; }

    /// <summary>
    /// Adds a glass sphere (thin glass specular model): C# mixes the transmittance stream into the material's shading node.
    /// </summary>
    public bool ThinGlass { get; init; }

    /// <summary>
    /// Adds a clear coat sphere: its first pass mixes the metal flakes surface shaders.
    /// </summary>
    public bool ClearCoat { get; init; }

    /// <summary>
    /// Draws the teapot and the skinned knight with instancing: the instanced normal mapping and skinning transformations.
    /// </summary>
    public bool Instancing { get; init; }

    /// <summary>
    /// Adds the skinned knight with its tangents skinned too. No importer asks for it, so it is set on the meshes by code.
    /// </summary>
    public bool SkinningTangent { get; init; }

    /// <summary>
    /// Adds the per-mesh velocity of temporal anti-aliasing. No compositor registers it, so it is added by code.
    /// </summary>
    public bool MeshVelocity { get; init; }

    /// <summary>
    /// Draws three teapots with the scene editor's single stream render modes: diffuse, tangent normal and world normal.
    /// </summary>
    public bool MaterialStreamFilters { get; init; }

    /// <summary>
    /// Voxel global illumination. The variant uses the voxelization, march set and layout the defaults don't, the modifier
    /// modes the opacity modifiers of one layout, the other march methods and a debug visualization.
    /// </summary>
    public VoxelMode Voxels { get; init; }

    /// <summary>
    /// Compiles the scene editor's effect for a mesh whose effect failed to compile.
    /// </summary>
    public bool EditorErrorEffect { get; init; }

    /// <summary>
    /// Compiles the trees of the editor's effects for selected meshes, selected sprites and 3D texture previews.
    /// </summary>
    public bool EditorEffectTrees { get; init; }

    /// <summary>
    /// False for a render that isn't the same on every run (e.g. it depends on GPU readback timing): it then only
    /// checks that the frames render, without exception or graphics validation error.
    /// </summary>
    public bool Screenshot { get; init; } = true;

    // The teapot, about 4 units from the camera, in focus: the floor in front and behind it blurred
    private static readonly Vector4 DepthOfFieldAreas = new(0.5f, 3.5f, 5f, 10f);

    protected override async Task LoadContent()
    {
        await base.LoadContent();

        var forwardRenderer = (ForwardRenderer)SceneSystem.GraphicsCompositor.SingleView;
        forwardRenderer.MSAALevel = Msaa;
        if (PostEffects != null)
        {
            var postEffects = new PostProcessingEffects();
            postEffects.AmbientOcclusion.Enabled = false;
            postEffects.LocalReflections.Enabled = false;
            postEffects.DepthOfField.Enabled = false;
            postEffects.Bloom.Enabled = false;
            postEffects.LightStreak.Enabled = false;
            postEffects.LensFlare.Enabled = false;
            postEffects.Antialiasing.Enabled = false;
            PostEffects(postEffects);
            forwardRenderer.PostEffects = postEffects;
        }

        CreateScene();

        if (Background != BackgroundMode.None)
        {
            // One color per face
            const int size = 16;
            var faces = new[] { Color.Red, Color.Green, Color.Blue, Color.Yellow, Color.Cyan, Color.Magenta }
                .Select(color => Enumerable.Repeat(color, size * size).ToArray())
                .ToArray();
            Scene.Entities.Add(new Entity
            {
                new BackgroundComponent
                {
                    Texture = Texture.NewCube(GraphicsDevice, size, PixelFormat.R8G8B8A8_UNorm, faces),
                    Is2D = Background == BackgroundMode.Cubemap2D,
                },
            });
        }

        if (ProjectedTexture)
        {
            // A 4x4 checker, above the teapot, pointing down
            const int size = 64;
            var checker = Enumerable.Range(0, size * size).Select(i => (i % size / (size / 4) + i / size / (size / 4)) % 2 == 0 ? Color.White : Color.Orange).ToArray();
            var spot = new Entity
            {
                new LightComponent
                {
                    Type = new LightSpot
                    {
                        Color = new ColorRgbProvider(Color.White),
                        Range = 8,
                        AngleInner = 30,
                        AngleOuter = 35,
                        ProjectiveTexture = Texture.New2D(GraphicsDevice, size, size, PixelFormat.R8G8B8A8_UNorm, checker),
                    },
                    Intensity = 20,
                },
            };
            spot.Transform.Position = new Vector3(0, 4, 0);
            spot.Transform.Rotation = Quaternion.RotationX(-MathUtil.PiOverTwo);
            Scene.Entities.Add(spot);
        }

        if (ThinGlass)
        {
            // In front of the teapot, slightly tinted
            var glassMaterial = Material.New(GraphicsDevice, new MaterialDescriptor
            {
                Attributes =
                {
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0.8f, 0.9f, 1.0f, 1.0f))),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                    MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.95f)),
                    Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0.0f)),
                    SpecularModel = new MaterialSpecularThinGlassModelFeature(),
                },
            }, Content);
            var glass = new Entity { new ModelComponent(new ProceduralModelDescriptor(new SphereProceduralModel { Radius = 0.5f, MaterialInstance = { Material = glassMaterial } }).GenerateModel(Services)) };
            glass.Transform.Position = new Vector3(0.3f, 0.6f, 1.5f);
            Scene.Entities.Add(glass);
        }

        var meshRenderFeature = SceneSystem.GraphicsCompositor.RenderFeatures.OfType<MeshRenderFeature>().First();

        if (ClearCoat)
        {
            var clearCoatMaterial = Material.New(GraphicsDevice, new MaterialDescriptor
            {
                Attributes =
                {
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0.6f, 0.05f, 0.05f, 1.0f))),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                    MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.6f)),
                    Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0.5f)),
                    SpecularModel = new MaterialSpecularMicrofacetModelFeature(),
                    ClearCoat = new MaterialClearCoatFeature(),
                },
            }, Content);
            var sphere = new Entity { new ModelComponent(new ProceduralModelDescriptor(new SphereProceduralModel { Radius = 0.5f, MaterialInstance = { Material = clearCoatMaterial } }).GenerateModel(Services)) };
            sphere.Transform.Position = new Vector3(0.3f, 0.6f, 1.5f);
            Scene.Entities.Add(sphere);
        }

        if (Instancing)
        {
            meshRenderFeature.RenderFeatures.Add(new InstancingRenderFeature());

            // Three copies side by side of the normal mapped teapot, and of the knight
            var teapotModel = new ProceduralModelDescriptor(new TeapotProceduralModel { Size = 0.5f, MaterialInstance = { Material = Content.Load<Material>("c100_chr_ch00_Knight_KINGHT") } }).GenerateModel(Services);
            Scene.Entities.Add(CreateInstanced(teapotModel, new Vector3(-1, 0.3f, 1.5f)));
            Scene.Entities.Add(CreateInstanced(Content.Load<Model>("knight Model"), new Vector3(-1, 0, 0.5f)));

            // No normal map
            var sphereModel = new ProceduralModelDescriptor(new SphereProceduralModel { Radius = 0.3f, MaterialInstance = { Material = Content.Load<Material>("glossMT") } }).GenerateModel(Services);
            Scene.Entities.Add(CreateInstanced(sphereModel, new Vector3(-1, 0.3f, 2.5f)));
        }

        if (Materials != null)
        {
            var descriptors = Materials(this);
            for (int i = 0; i < descriptors.Length; i++)
            {
                var material = Material.New(GraphicsDevice, descriptors[i], Content);
                var sphere = new Entity { new ModelComponent(new ProceduralModelDescriptor(new SphereProceduralModel { Radius = 0.3f, MaterialInstance = { Material = material } }).GenerateModel(Services)) };
                sphere.Transform.Position = new Vector3(i * 0.7f - 1.5f, 0.3f, 1.5f);
                Scene.Entities.Add(sphere);
            }
        }

        if (Shadows)
        {
            var compositor = SceneSystem.GraphicsCompositor;
            var shadowCasterStage = compositor.RenderStages.First(stage => stage.Name == "ShadowMapCaster");
            meshRenderFeature.RenderFeatures.OfType<ForwardLightingRenderFeature>().First().ShadowMapRenderer = new ShadowMapRenderer
            {
                Renderers = { new LightDirectionalShadowMapRenderer { ShadowCasterRenderStage = shadowCasterStage } },
            };
            var light = new Entity { new LightComponent { Type = new LightDirectional { Shadow = { Enabled = true } } } };
            light.Transform.Rotation = Quaternion.RotationX(-MathUtil.PiOverTwo);
            Scene.Entities.Add(light);
        }

        if (Tessellation != TessellationMode.None)
        {
            var normalMap = Texture.New2D(GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { new Color(128, 128, 255) });
            var material = Material.New(GraphicsDevice, new MaterialDescriptor
            {
                Attributes =
                {
                    Tessellation = new MaterialTessellationFlatFeature(),
                    Displacement = new MaterialDisplacementMapFeature(new ComputeFloat(0.5f)),
                    Surface = new MaterialNormalMapFeature(new ComputeTextureColor(normalMap)),
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0.5f, 0.5f, 0.5f, 1.0f))),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                },
            }, Content);

            var teapotModel = new ProceduralModelDescriptor(new TeapotProceduralModel { Size = 0.5f, MaterialInstance = { Material = material } }).GenerateModel(Services);
            if (Tessellation == TessellationMode.Mesh)
            {
                var teapot = new Entity { new ModelComponent(teapotModel) };
                teapot.Transform.Position = new Vector3(-1, 0.3f, 1.5f);
                Scene.Entities.Add(teapot);
            }
            else
            {
                var knightModel = Content.Load<Model>("knight Model");
                var knightComponent = new ModelComponent(knightModel);
                for (int i = 0; i < knightModel.Materials.Count; i++)
                    knightComponent.Materials[i] = material;
                var knight = new Entity { knightComponent };
                knight.Transform.Position = new Vector3(0.5f, 0, 1);
                Scene.Entities.Add(knight);
            }
        }

        if (SkinningTangent)
        {
            var knightModel = Content.Load<Model>("knight Model");
            foreach (var mesh in knightModel.Meshes)
                mesh.Parameters.Set(MaterialKeys.HasSkinningTangent, true);
            var knight = new Entity { new ModelComponent(knightModel) };
            knight.Transform.Position = new Vector3(-0.5f, 0, 1);
            Scene.Entities.Add(knight);
        }

        if (MeshVelocity)
            meshRenderFeature.RenderFeatures.Add(new MeshVelocityRenderFeature());

        if (MaterialStreamFilters)
        {
            var normalStreams = new MaterialNormalMapFeature().GetStreams().ToList();
            var filters = new[] { MaterialDiffuseMapFeature.DiffuseStream.Filter, normalStreams[0].Filter, normalStreams[1].Filter };
            var streamFilterFeature = new MaterialStreamFilterRenderFeature();
            meshRenderFeature.RenderFeatures.Add(streamFilterFeature);

            // One normal mapped teapot per filter
            var knightMaterial = Content.Load<Material>("c100_chr_ch00_Knight_KINGHT");
            for (int i = 0; i < filters.Length; i++)
            {
                var teapot = new ModelComponent(new ProceduralModelDescriptor(new TeapotProceduralModel { Size = 0.5f, MaterialInstance = { Material = knightMaterial } }).GenerateModel(Services));
                var entity = new Entity { teapot };
                entity.Transform.Position = new Vector3(i - 1, 0.3f, 1.5f);
                Scene.Entities.Add(entity);
                streamFilterFeature.Filters.Add(teapot, filters[i]);
            }
        }

        if (Voxels != VoxelMode.None)
        {
            UseVoxelRenderer(meshRenderFeature);

            var emissionOpacity = new VoxelAttributeEmissionOpacity();
            var volume = new VoxelVolumeComponent { Attributes = { emissionOpacity }, VoxelVolumeSize = 8 };
            var voxelLight = new LightVoxel { Volume = volume };
            if (Voxels == VoxelMode.Variant)
            {
                volume.VoxelizationMethod = new VoxelizationMethodSingleAxis();
                emissionOpacity.VoxelLayout = new VoxelLayoutAnisotropic();
                voxelLight.DiffuseMarcher = new VoxelMarchSetRandomHemisphere();
            }
            else if (Voxels != VoxelMode.Default)
            {
                emissionOpacity.VoxelLayout = Voxels switch
                {
                    VoxelMode.ModifiersIsotropic => new VoxelLayoutIsotropic(),
                    VoxelMode.ModifiersAnisotropic => new VoxelLayoutAnisotropic(),
                    _ => new VoxelLayoutAnisotropicPaired(),
                };
                emissionOpacity.Modifiers.Add(new VoxelModifierEmissionOpacityAntiAliasing());
                emissionOpacity.Modifiers.Add(new VoxelModifierEmissionOpacityOpacify());
                emissionOpacity.Modifiers.Add(new VoxelModifierEmissionOpacitySolidify());
                emissionOpacity.LightFalloff = Voxels == VoxelMode.ModifiersIsotropic ? VoxelAttributeEmissionOpacity.LightFalloffs.Sharp : VoxelAttributeEmissionOpacity.LightFalloffs.PhysicallyBased;
                ((VoxelLayoutBase)emissionOpacity.VoxelLayout).StorageMethod = new VoxelStorageMethodIndirect
                {
                    TempStorageFormat = Voxels == VoxelMode.ModifiersIsotropic ? new VoxelFragmentPackFloat16() : new VoxelFragmentPackFloat32(),
                    // Plain stores from several draws to one buffer are a hazard for Vulkan's sync validation
                    Filter = GraphicsDevice.Platform == GraphicsPlatform.Vulkan ? new VoxelBufferWriteMax() : new VoxelBufferWriteAssign(),
                };
                voxelLight.DiffuseMarcher = new VoxelMarchSetHemisphere12(new VoxelMarchBeam());
                voxelLight.SpecularMarcher = new VoxelMarchCone();

                // A debug view of an attribute that only the view samples
                volume.Attributes.Add(Voxels == VoxelMode.ModifiersIsotropic ? new VoxelAttributeSolidity() : new VoxelAttributeDirectionalCoverage());
                volume.VisualizeVoxels = true;
                volume.VisualizeIndex = 1;
                volume.Visualization = Voxels == VoxelMode.ModifiersIsotropic ? new VoxelVisualizationRaw() : new VoxelVisualizationView();
            }
            var volumeEntity = new Entity { volume };
            volumeEntity.Transform.Position = new Vector3(0, 1, 0);
            Scene.Entities.Add(volumeEntity);
            Scene.Entities.Add(new Entity { new LightComponent { Type = voxelLight } });
        }

        if (EditorErrorEffect)
        {
            // What StrideEditorForwardShadingEffect mixes when SceneEditorParameters.IsEffectError is set (the editor's
            // effects are not in this package, its shaders are)
            CompileTree("Stride.Rendering.StrideEditorForwardShadingEffect", new ShaderClassSource("Stride.Rendering.ShaderBase"), new ShaderClassSource("Stride.Rendering.ShadingBase"), new ShaderClassSource("Stride.Rendering.TransformationBase"), new ShaderClassSource("Stride.Rendering.TransformationWAndVP"), new ShaderClassSource("Stride.Rendering.CompilationErrorShader"));
        }

        if (EditorEffectTrees)
        {
            // Highlight, SelectedSprite and PreviewTexture in the editor's sdfx
            CompileTree("Stride.Rendering.StrideEditorForwardShadingEffect.Highlight", new ShaderClassSource("Stride.Rendering.ShaderBase"), new ShaderClassSource("Stride.Rendering.ShadingBase"), new ShaderClassSource("Stride.Rendering.TransformationBase"), new ShaderClassSource("Stride.Rendering.TransformationWAndVP"), new ShaderClassSource("Stride.Rendering.HighlightShader"));
            CompileTree("Stride.Rendering.SelectedSprite", new ShaderClassSource("Stride.Rendering.SpriteBatchShader", false), new ShaderClassSource("Stride.Rendering.SelectedSpriteShader"));
            CompileTree("Stride.Rendering.PreviewTexture", new ShaderClassSource("Stride.Rendering.Sprite3DBase"), new ShaderClassSource("Stride.Rendering.SpriteBatchShader", false));
        }
    }

    private void CompileTree(string effectName, params ShaderClassSource[] shaders)
    {
        var effect = new ShaderMixinSource { Name = effectName };
        effect.Mixins.AddRange(shaders);
        var compilerParameters = new CompilerParameters();
        compilerParameters.EffectParameters.Platform = GraphicsDevice.Platform;
        compilerParameters.EffectParameters.Profile = GraphicsDevice.ShaderProfile ?? GraphicsDevice.Features.RequestedProfile;
        var result = EffectSystem.Compiler.Compile(effect, compilerParameters).Bytecode.WaitForResult();
        Assert.True(result.Bytecode != null, result.CompilationLog?.ToText());
    }

    private Entity CreateInstanced(Model model, Vector3 position)
    {
        var instances = new InstancingUserArray();
        instances.UpdateWorldMatrices([.. Enumerable.Range(0, 3).Select(i => Matrix.Translation(position + new Vector3(i, 0, 0)))]);
        return new Entity { new ModelComponent(model), new InstancingComponent { Type = instances } };
    }

    /// <summary>
    /// Replaces the forward renderer with the voxel one, set up like <c>DefaultGraphicsCompositorVoxels</c>: two voxelization
    /// stages, the voxel render feature and pipeline processor, and the voxel light renderer.
    /// </summary>
    private void UseVoxelRenderer(MeshRenderFeature meshRenderFeature)
    {
        var compositor = SceneSystem.GraphicsCompositor;
        var voxelStages = new List<RenderStage> { new("VoxelizationPassFirst", "Voxelizer"), new("VoxelizationPassSecond", "Voxelizer2") };
        foreach (var stage in voxelStages)
        {
            compositor.RenderStages.Add(stage);
            meshRenderFeature.RenderStageSelectors.Add(new MeshTransparentRenderStageSelector
            {
                OpaqueRenderStage = stage,
                TransparentRenderStage = stage,
                EffectName = "Stride.Rendering.Voxels.StrideForwardShadingEffectVXGI.VoxelizeToFragmentsEffect",
            });
        }
        meshRenderFeature.PipelineProcessors.Add(new VoxelPipelineProcessor { VoxelRenderStage = voxelStages });
        meshRenderFeature.RenderFeatures.Add(new VoxelRenderFeature());
        meshRenderFeature.RenderFeatures.OfType<ForwardLightingRenderFeature>().First().LightRenderers.Add(new LightVoxelRenderer());

        var forwardRenderer = (ForwardRenderer)compositor.SingleView;
        var voxelRenderer = new ForwardRendererVoxels
        {
            Clear = forwardRenderer.Clear,
            LightProbes = forwardRenderer.LightProbes,
            OpaqueRenderStage = forwardRenderer.OpaqueRenderStage,
            TransparentRenderStage = forwardRenderer.TransparentRenderStage,
            PostEffects = forwardRenderer.PostEffects,
            VoxelRenderer = new VoxelRenderer { VoxelStages = voxelStages },
            VoxelVisualization = Voxels >= VoxelMode.ModifiersIsotropic ? new VoxelDebug() : null,
        };
        voxelRenderer.ShadowMapRenderStages.AddRange(forwardRenderer.ShadowMapRenderStages);
        var renderers = ((SceneRendererCollection)((SceneCameraRenderer)compositor.Game).Child).Children;
        renderers[renderers.IndexOf(forwardRenderer)] = voxelRenderer;
        compositor.SingleView = voxelRenderer;
    }

    protected override void RegisterTests()
    {
        base.RegisterTests();
        if (Screenshot)
            FrameGameSystem.TakeScreenshot(ScreenshotFrame);
        else
            FrameGameSystem.Draw(ScreenshotFrame, () => { });
    }

    [SkippableFact]
    public void Msaa4x() => RunGameTest(new RenderFeatureTests { Msaa = MultisampleCount.X4, TestName = nameof(Msaa4x) });

    // The color, normal and specular targets are all multisampled, and resolved one after another
    [SkippableFact]
    public void Msaa4xLocalReflections() => RunGameTest(new RenderFeatureTests { Msaa = MultisampleCount.X4, PostEffects = x => x.LocalReflections.Enabled = true, TestName = nameof(Msaa4xLocalReflections) });

    [SkippableFact]
    public void TemporalAntiAliasing() => RunGameTest(new RenderFeatureTests { PostEffects = x => x.Antialiasing = new TemporalAntiAliasEffect(), TestName = nameof(TemporalAntiAliasing) });

    [SkippableFact]
    public void AmbientOcclusion() => RunGameTest(new RenderFeatureTests { PostEffects = x => x.AmbientOcclusion.Enabled = true, TestName = nameof(AmbientOcclusion) });

    [SkippableFact]
    public void DepthOfFieldTripleRhombi() => RunGameTest(new RenderFeatureTests
    {
        PostEffects = x => { x.DepthOfField.Enabled = true; x.DepthOfField.AutoFocus = false; x.DepthOfField.DOFAreas = DepthOfFieldAreas; x.DepthOfField.Technique = BokehTechnique.HexagonalTripleRhombi; },
        TestName = nameof(DepthOfFieldTripleRhombi),
    });

    [SkippableFact]
    public void DepthOfFieldMcIntosh() => RunGameTest(new RenderFeatureTests
    {
        PostEffects = x => { x.DepthOfField.Enabled = true; x.DepthOfField.AutoFocus = false; x.DepthOfField.DOFAreas = DepthOfFieldAreas; x.DepthOfField.Technique = BokehTechnique.HexagonalMcIntosh; },
        TestName = nameof(DepthOfFieldMcIntosh),
    });

    // The focus follows a depth read back from the GPU, so the frame it reaches depends on timing: no screenshot
    [SkippableFact]
    public void DepthOfFieldAutoFocus() => RunGameTest(new RenderFeatureTests
    {
        PostEffects = x => { x.DepthOfField.Enabled = true; x.DepthOfField.AutoFocus = true; },
        Screenshot = false,
        TestName = nameof(DepthOfFieldAutoFocus),
    });

    [SkippableFact]
    public void LocalReflections() => RunGameTest(new RenderFeatureTests { PostEffects = x => x.LocalReflections.Enabled = true, TestName = nameof(LocalReflections) });

    [SkippableFact]
    public void BackgroundCubemap3D() => RunGameTest(new RenderFeatureTests { Background = BackgroundMode.Cubemap3D, TestName = nameof(BackgroundCubemap3D) });

    [SkippableFact]
    public void BackgroundCubemap2D() => RunGameTest(new RenderFeatureTests { Background = BackgroundMode.Cubemap2D, TestName = nameof(BackgroundCubemap2D) });

    [SkippableFact]
    public void SpotLightProjectedTexture() => RunGameTest(new RenderFeatureTests { ProjectedTexture = true, Screenshot = false, TestName = nameof(SpotLightProjectedTexture) });

    [SkippableFact]
    public void ThinGlassSpecular() => RunGameTest(new RenderFeatureTests { ThinGlass = true, Screenshot = false, TestName = nameof(ThinGlassSpecular) });

    [SkippableFact]
    public void ClearCoatSpecular() => RunGameTest(new RenderFeatureTests { ClearCoat = true, Screenshot = false, TestName = nameof(ClearCoatSpecular) });

    // A skinned model adds run-to-run noise: no screenshot
    [SkippableFact]
    public void InstancingNormalMapAndSkinning() => RunGameTest(new RenderFeatureTests { Instancing = true, Screenshot = false, TestName = nameof(InstancingNormalMapAndSkinning) });

    [SkippableFact]
    public void SkinningWithTangents() => RunGameTest(new RenderFeatureTests { SkinningTangent = true, Screenshot = false, TestName = nameof(SkinningWithTangents) });

    [SkippableFact]
    public void TemporalAntiAliasingMeshVelocity() => RunGameTest(new RenderFeatureTests
    {
        PostEffects = x => x.Antialiasing = new TemporalAntiAliasEffect(),
        MeshVelocity = true,
        Screenshot = false,
        TestName = nameof(TemporalAntiAliasingMeshVelocity),
    });

    [SkippableFact]
    public void EditorMaterialStreamFilters() => RunGameTest(new RenderFeatureTests { MaterialStreamFilters = true, Screenshot = false, TestName = nameof(EditorMaterialStreamFilters) });

    [SkippableTheory]
    [InlineData(typeof(ToneMapACESOperator))]
    [InlineData(typeof(ToneMapDragoOperator))]
    [InlineData(typeof(ToneMapExponentialOperator))]
    [InlineData(typeof(ToneMapHejlDawsonOperator))]
    [InlineData(typeof(ToneMapLogarithmicOperator))]
    [InlineData(typeof(ToneMapMikeDayOperator))]
    [InlineData(typeof(ToneMapReinhardOperator))]
    [InlineData(typeof(ToneMapU2FilmicOperator))]
    public void ToneMapOperators(Type operatorType) => RunGameTest(new RenderFeatureTests
    {
        PostEffects = x => x.ColorTransforms.Transforms.Add(new ToneMap { Operator = (ToneMapOperator)Activator.CreateInstance(operatorType) }),
        Screenshot = false,
        TestName = $"{nameof(ToneMapOperators)}_{operatorType.Name}",
    });

    [SkippableFact]
    public void ColorTransformsFogOutlineAfterimage() => RunGameTest(new RenderFeatureTests
    {
        PostEffects = x =>
        {
            x.ColorTransforms.Transforms.Add(new FilmGrain());
            x.ColorTransforms.Transforms.Add(new Vignetting());
            x.ColorTransforms.Transforms.Add(new Stride.Rendering.Images.Dither.Dither());
            x.Fog.Enabled = true;
            x.Outline.Enabled = true;
            x.Bloom.Enabled = true;
            x.Bloom.Afterimage.Enabled = true;
        },
        Screenshot = false,
        TestName = nameof(ColorTransformsFogOutlineAfterimage),
    });

    [SkippableFact]
    public void MaterialComputeNodes() => RunGameTest(new RenderFeatureTests
    {
        Materials = test =>
        {
            // One balanced tree with every blend operator: a capture file reads back up to a depth of 64
            var operators = Enum.GetValues<BinaryOperator>();
            var nodes = new Queue<IComputeColor>(Enumerable.Range(0, operators.Length + 1).Select(_ => new ComputeColor(new Color4(0.5f, 0.4f, 0.3f, 0.5f))));
            foreach (var binaryOperator in operators)
                nodes.Enqueue(new ComputeBinaryColor(nodes.Dequeue(), nodes.Dequeue(), binaryOperator));
            var blended = nodes.Dequeue();

            var texture = Texture.New2D(test.GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { Color.White });
            return
            [
                LambertMaterial(blended),
                LambertMaterial(new ComputeTextureColor(texture) { UseRandomTextureCoordinates = true }),
                new MaterialDescriptor { Attributes = { Emissive = new MaterialEmissiveMapFeature(new ComputeSphericalHarmonicsColor()) } },
            ];
        },
        Screenshot = false,
        TestName = nameof(MaterialComputeNodes),
    });

    [SkippableFact]
    public void MicrofacetFunctions() => RunGameTest(new RenderFeatureTests
    {
        Materials = _ =>
        [
            MicrofacetMaterial(new MaterialSpecularMicrofacetModelFeature { Visibility = new MaterialSpecularMicrofacetVisibilityCookTorrance(), Fresnel = new MaterialSpecularMicrofacetFresnelNone() }),
            MicrofacetMaterial(new MaterialSpecularMicrofacetModelFeature { Visibility = new MaterialSpecularMicrofacetVisibilityKelemen(), NormalDistribution = new MaterialSpecularMicrofacetNormalDistributionBeckmann() }),
            MicrofacetMaterial(new MaterialSpecularMicrofacetModelFeature { Visibility = new MaterialSpecularMicrofacetVisibilityNeumann(), Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() }),
            MicrofacetMaterial(new MaterialSpecularMicrofacetModelFeature { Visibility = new MaterialSpecularMicrofacetVisibilitySmithBeckmann() }),
            MicrofacetMaterial(new MaterialSpecularMicrofacetModelFeature { Visibility = new MaterialSpecularMicrofacetVisibilitySmithGGXCorrelated() }),
        ],
        Screenshot = false,
        TestName = nameof(MicrofacetFunctions),
    });

    [SkippableFact]
    public void CelShading() => RunGameTest(new RenderFeatureTests
    {
        Materials = test =>
        {
            var ramp = new MaterialCelShadingLightRamp { RampTexture = Texture.New2D(test.GraphicsDevice, 2, 1, PixelFormat.R8G8B8A8_UNorm, new[] { Color.Black, Color.White }) };
            return
            [
                MicrofacetMaterial(new MaterialSpecularCelShadingModelFeature(), new MaterialDiffuseCelShadingModelFeature()),
                MicrofacetMaterial(new MaterialSpecularCelShadingModelFeature { RampFunction = ramp }, new MaterialDiffuseCelShadingModelFeature { RampFunction = ramp }),
            ];
        },
        Screenshot = false,
        TestName = nameof(CelShading),
    });

    [SkippableFact]
    public void HairShading() => RunGameTest(new RenderFeatureTests
    {
        Shadows = true,
        Materials = _ =>
        [
            MicrofacetMaterial(new MaterialSpecularHairModelFeature(), new MaterialDiffuseHairModelFeature()),
            MicrofacetMaterial(
                new MaterialSpecularHairModelFeature
                {
                    ShadingModel = HairShared.HairShadingModel.ScheuermannImproved,
                    HairDirectionFunction = new MaterialHairDirectionFunctionTangent(),
                    HairShadowingFunction = new MaterialHairShadowingFunctionScattering(),
                    LightAttenuationFunction = new MaterialHairLightAttenuationFunctionNone(),
                },
                new MaterialDiffuseHairModelFeature
                {
                    ShadingModel = HairShared.HairShadingModel.ScheuermannImproved,
                    HairDirectionFunction = new MaterialHairDirectionFunctionTangent(),
                    HairShadowingFunction = new MaterialHairShadowingFunctionScattering(),
                    LightAttenuationFunction = new MaterialHairLightAttenuationFunctionNone(),
                }),
        ],
        Screenshot = false,
        TestName = nameof(HairShading),
    });

    [SkippableFact]
    public void SubsurfaceScatteringProfiles() => RunGameTest(new RenderFeatureTests
    {
        Materials = test =>
        {
            var falloff = Texture.New2D(test.GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { Color.Red });
            return
            [
                ScatteringMaterial(new MaterialSubsurfaceScatteringScatteringProfileSkin()),
                ScatteringMaterial(new MaterialSubsurfaceScatteringScatteringProfileCustom()),
                ScatteringMaterial(new MaterialSubsurfaceScatteringScatteringProfileCustom { FalloffMap = new ComputeTextureColor(falloff) }),
            ];
        },
        Screenshot = false,
        TestName = nameof(SubsurfaceScatteringProfiles),
    });

    [SkippableFact]
    public void ShadowCastersWithAlpha() => RunGameTest(new RenderFeatureTests
    {
        Shadows = true,
        Materials = _ =>
        [
            new MaterialDescriptor { Attributes = { Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0.5f, 0.5f, 0.5f, 0.5f))), DiffuseModel = new MaterialDiffuseLambertModelFeature(), Transparency = new MaterialTransparencyCutoffFeature() } },
            new MaterialDescriptor { Attributes = { Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0.5f, 0.5f, 0.5f, 0.5f))), DiffuseModel = new MaterialDiffuseLambertModelFeature(), Transparency = new MaterialTransparencyBlendFeature() } },
        ],
        Screenshot = false,
        TestName = nameof(ShadowCastersWithAlpha),
    });

    [SkippableFact]
    public void TessellationNormalMapping() => RunGameTest(new RenderFeatureTests { Tessellation = TessellationMode.Mesh, Screenshot = false, TestName = nameof(TessellationNormalMapping) });

    [SkippableFact]
    public void TessellationNormalMappingSkinned() => RunGameTest(new RenderFeatureTests { Tessellation = TessellationMode.Skinned, Screenshot = false, TestName = nameof(TessellationNormalMappingSkinned) });

    [SkippableFact]
    public void EditorEffectSubEffects() => RunGameTest(new RenderFeatureTests { EditorEffectTrees = true, Screenshot = false, TestName = nameof(EditorEffectSubEffects) });

    [SkippableFact]
    public void EditorEffectErrorFallback() => RunGameTest(new RenderFeatureTests { EditorErrorEffect = true, Screenshot = false, TestName = nameof(EditorEffectErrorFallback) });

    private const string VoxelGlobalIlluminationUnsupported = "MoltenVK has no geometry shaders for the default voxelization, and the tri-axis voxelization pipeline fails to create there";

    [SkippableFact]
    public void VoxelGlobalIllumination()
    {
        Skip.If(Platform.Type is PlatformType.macOS or PlatformType.iOS, VoxelGlobalIlluminationUnsupported);
        RunGameTest(new RenderFeatureTests { Voxels = VoxelMode.Default, Screenshot = false, TestName = nameof(VoxelGlobalIllumination) });
    }

    [SkippableFact]
    public void VoxelGlobalIlluminationVariant()
    {
        Skip.If(Platform.Type is PlatformType.macOS or PlatformType.iOS, VoxelGlobalIlluminationUnsupported);
        RunGameTest(new RenderFeatureTests { Voxels = VoxelMode.Variant, Screenshot = false, TestName = nameof(VoxelGlobalIlluminationVariant) });
    }

    private static MaterialDescriptor LambertMaterial(IComputeColor diffuse) => new()
    {
        Attributes = { Diffuse = new MaterialDiffuseMapFeature(diffuse), DiffuseModel = new MaterialDiffuseLambertModelFeature() },
    };

    private static MaterialDescriptor MicrofacetMaterial(IMaterialSpecularModelFeature specularModel, IMaterialDiffuseModelFeature diffuseModel = null) => new()
    {
        Attributes =
        {
            Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0.5f, 0.4f, 0.3f, 1.0f))),
            DiffuseModel = diffuseModel ?? new MaterialDiffuseLambertModelFeature(),
            MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.6f)),
            Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0.5f)),
            SpecularModel = specularModel,
        },
    };

    private static MaterialDescriptor ScatteringMaterial(IMaterialSubsurfaceScatteringScatteringProfile profile)
    {
        var material = LambertMaterial(new ComputeColor(new Color4(0.7f, 0.5f, 0.4f, 1.0f)));
        material.Attributes.SubsurfaceScattering = new MaterialSubsurfaceScatteringFeature { ProfileFunction = profile };
        return material;
    }

    /// <summary>
    /// The light probe gizmo's color in the scene editor.
    /// </summary>
    private class ComputeSphericalHarmonicsColor : ComputeValueBase<Color4>, IComputeColor
    {
        public bool HasChanged => false;

        public override ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys) => new ShaderClassSource("Stride.Rendering.LightProbes.ComputeSphericalHarmonics", 5);
    }

    [SkippableFact]
    public void VoxelModifiersIsotropic()
    {
        Skip.If(Platform.Type is PlatformType.macOS or PlatformType.iOS, VoxelGlobalIlluminationUnsupported);
        RunGameTest(new RenderFeatureTests { Voxels = VoxelMode.ModifiersIsotropic, Screenshot = false, TestName = nameof(VoxelModifiersIsotropic) });
    }

    [SkippableFact]
    public void VoxelModifiersAnisotropic()
    {
        Skip.If(Platform.Type is PlatformType.macOS or PlatformType.iOS, VoxelGlobalIlluminationUnsupported);
        RunGameTest(new RenderFeatureTests { Voxels = VoxelMode.ModifiersAnisotropic, Screenshot = false, TestName = nameof(VoxelModifiersAnisotropic) });
    }

    [SkippableFact]
    public void VoxelModifiersAnisotropicPaired()
    {
        Skip.If(Platform.Type is PlatformType.macOS or PlatformType.iOS, VoxelGlobalIlluminationUnsupported);
        RunGameTest(new RenderFeatureTests { Voxels = VoxelMode.ModifiersAnisotropicPaired, Screenshot = false, TestName = nameof(VoxelModifiersAnisotropicPaired) });
    }

    /// <summary>
    /// Sets a material stream filter per model. The scene editor's render modes set one for the whole scene.
    /// </summary>
    private class MaterialStreamFilterRenderFeature : SubRenderFeature
    {
        private StaticObjectPropertyKey<RenderEffect> renderEffectKey;

        public Dictionary<ModelComponent, ShaderSource> Filters { get; } = [];

        protected override void InitializeCore()
        {
            base.InitializeCore();

            renderEffectKey = ((RootEffectRenderFeature)RootRenderFeature).RenderEffectKey;
        }

        public override void PrepareEffectPermutations(RenderDrawContext context)
        {
            var renderEffects = RootRenderFeature.RenderData.GetData(renderEffectKey);
            int effectSlotCount = ((RootEffectRenderFeature)RootRenderFeature).EffectPermutationSlotCount;

            foreach (var renderObject in RootRenderFeature.RenderObjects)
            {
                if (renderObject.Source is not ModelComponent model || !Filters.TryGetValue(model, out var filter))
                    continue;

                for (int i = 0; i < effectSlotCount; ++i)
                {
                    var renderEffect = renderEffects[renderObject.StaticObjectNode * effectSlotCount + i];
                    if (renderEffect != null && renderEffect.IsUsedDuringThisFrame(RenderSystem))
                        renderEffect.EffectValidator.ValidateParameter(MaterialKeys.PixelStageSurfaceFilter, filter);
                }
            }
        }
    }
}
