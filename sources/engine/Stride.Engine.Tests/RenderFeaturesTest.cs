// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Linq;
using System.Threading.Tasks;

using Xunit;

using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;
using Stride.Rendering.ProceduralModels;

namespace Stride.Engine.Tests;

/// <summary>
/// Renders a scene with the rendering features no sample uses: MSAA, ambient occlusion, depth of field (both bokeh
/// techniques, with autofocus), local reflections, temporal anti-aliasing and a cubemap background (3D, then 2D). It takes
/// no screenshot: it checks that they render, and gives their effects to the shader corpus
/// (sources/shaders/Stride.Shaders.Tests/Corpus).
/// </summary>
public class RenderFeaturesTest : EngineTestBase
{
    private PostProcessingEffects postEffects;
    private BackgroundComponent background;

    public RenderFeaturesTest()
    {
        // MSAA needs Direct3D 11 class hardware, and its resolve shader model 5 (shaders use the game settings profile otherwise)
        GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
        GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
    }

    protected override async Task LoadContent()
    {
        await base.LoadContent();

        // Ambient occlusion, depth of field and local reflections are on by default
        postEffects = new PostProcessingEffects();
        postEffects.DepthOfField.AutoFocus = true;
        var forwardRenderer = (ForwardRenderer)SceneSystem.GraphicsCompositor.SingleView;
        forwardRenderer.MSAALevel = MultisampleCount.X4;
        forwardRenderer.PostEffects = postEffects;

        var material = Content.Load<Material>("01-Default");
        Scene.Entities.Add(CreateModel(new PlaneProceduralModel { Size = new Vector2(20), MaterialInstance = { Material = material } }, Vector3.Zero));
        Scene.Entities.Add(CreateModel(new CubeProceduralModel { MaterialInstance = { Material = material } }, new Vector3(-1, 0.5f, 0)));
        Scene.Entities.Add(CreateModel(new SphereProceduralModel { MaterialInstance = { Material = material } }, new Vector3(1.5f, 0.5f, -3)));

        // One color per face
        const int size = 16;
        var faces = new[] { Color.Red, Color.Green, Color.Blue, Color.Yellow, Color.Cyan, Color.Magenta }
            .Select(color => Enumerable.Repeat(color, size * size).ToArray())
            .ToArray();
        background = new BackgroundComponent { Texture = Texture.NewCube(GraphicsDevice, size, PixelFormat.R8G8B8A8_UNorm, faces) };
        Scene.Entities.Add(new Entity { background });

        Camera.Transform.Position = new Vector3(0, 2, 5);
        Camera.Transform.Rotation = Quaternion.RotationX(-0.3f);
    }

    private Entity CreateModel(PrimitiveProceduralModelBase model, Vector3 position)
    {
        var entity = new Entity { new ModelComponent(new ProceduralModelDescriptor(model).GenerateModel(Services)) };
        entity.Transform.Position = position;
        return entity;
    }

    protected override void RegisterTests()
    {
        base.RegisterTests();

        // A few frames per setup: autofocus and temporal anti-aliasing read the previous frames
        FrameGameSystem.Draw(4, () =>
        {
            postEffects.Antialiasing = new TemporalAntiAliasEffect();
            postEffects.DepthOfField.Technique = BokehTechnique.HexagonalMcIntosh;
            background.Is2D = true;
        });
        FrameGameSystem.Draw(8, () => { });
    }

    [SkippableFact]
    public void RunTestGame()
    {
        RunGameTest(new RenderFeaturesTest());
    }
}
