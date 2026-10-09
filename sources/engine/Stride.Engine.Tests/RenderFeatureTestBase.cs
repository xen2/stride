// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Colors;
using Stride.Rendering.Lights;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.ProceduralModels;

namespace Stride.Engine.Tests;

/// <summary>
/// The scene of the rendering feature tests: the knight with its textured materials, a glossy sphere, an emissive cube,
/// a floor and a directional light, seen from a camera close enough for depth of field.
/// </summary>
public abstract class RenderFeatureTestBase : EngineTestBase
{
    protected RenderFeatureTestBase()
    {
        // MSAA, compute shaders and voxels need Direct3D 11 class hardware and shader model 5 (shaders use the game
        // settings profile otherwise)
        GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
        GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;

        // A fixed time step: effects that jitter with time (e.g. local reflections) render the same on every run
        IsFixedTimeStep = true;
        ForceOneUpdatePerDraw = true;
        IsDrawDesynchronized = false;
    }

    protected void CreateScene()
    {
        // Some ambient light, but mostly the directional one: shading, occlusion and bounces show
        AmbientLight.Intensity = 0.3f;

        // The knight, standing on the floor, 1.8 units tall
        var knightModel = Content.Load<Model>("knight Model");
        var bounds = knightModel.BoundingBox;
        var scale = 1.8f / (bounds.Maximum.Y - bounds.Minimum.Y);
        var knight = new Entity { new ModelComponent { Model = knightModel } };
        knight.Transform.RotationEulerXYZ = new Vector3(0, MathUtil.Pi / 6, 0);
        knight.Transform.Scale = new Vector3(scale);
        knight.Transform.Position = new Vector3(0, -bounds.Minimum.Y * scale, 0);
        Scene.Entities.Add(knight);

        // Glossy enough for the local reflections (their threshold is 0.55)
        var floorMaterial = Material.New(GraphicsDevice, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0.3f, 0.3f, 0.3f, 1.0f))),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.85f)),
                Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0.0f)),
                SpecularModel = new MaterialSpecularMicrofacetModelFeature(),
            },
        }, Content);
        Scene.Entities.Add(CreateModel(new PlaneProceduralModel { Size = new Vector2(10), MaterialInstance = { Material = floorMaterial } }, Vector3.Zero));
        Scene.Entities.Add(CreateModel(new SphereProceduralModel { Radius = 0.4f, MaterialInstance = { Material = Content.Load<Material>("glossMT") } }, new Vector3(1.2f, 0.4f, 0.6f)));
        Scene.Entities.Add(CreateModel(new CubeProceduralModel { Size = new Vector3(0.6f), MaterialInstance = { Material = Content.Load<Material>("glowMT") } }, new Vector3(-1.3f, 0.3f, -0.8f)));

        var light = new Entity { new LightComponent { Type = new LightDirectional { Color = new ColorRgbProvider(Color.White) }, Intensity = 2 } };
        light.Transform.Rotation = Quaternion.RotationYawPitchRoll(MathUtil.Pi / 4, -MathUtil.Pi / 3, 0);
        Scene.Entities.Add(light);

        Camera.Transform.Position = new Vector3(0, 1.6f, 4);
        Camera.Transform.Rotation = Quaternion.RotationX(-0.3f);
    }

    private Entity CreateModel(PrimitiveProceduralModelBase model, Vector3 position)
    {
        var entity = new Entity { new ModelComponent(new ProceduralModelDescriptor(model).GenerateModel(Services)) };
        entity.Transform.Position = position;
        return entity;
    }
}
