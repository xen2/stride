// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Linq;

using Xunit;

using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.Regression;
using Stride.Rendering.ComputeEffect.GGXPrefiltering;
using Stride.Rendering.ComputeEffect.LambertianPrefiltering;
using Stride.Rendering.Images;

namespace Stride.Engine.Tests;

/// <summary>
/// Draws the effects that run outside a render feature, without screenshot, so the shader corpus captures their permutations:
/// skybox prefiltering with compute shaders, and the depth range of a texture.
/// </summary>
public class ComputeEffectTests : GameTestBase
{
    public ComputeEffectTests()
    {
        GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
        GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
    }

    private static Texture NewInputCubemap(GraphicsDevice device)
    {
        const int size = 16;
        var faces = new[] { Color.Red, Color.Green, Color.Blue, Color.Yellow, Color.Cyan, Color.Magenta }
            .Select(color => Enumerable.Repeat(color, size * size).ToArray())
            .ToArray();
        return Texture.NewCube(device, size, PixelFormat.R8G8B8A8_UNorm, faces);
    }

    [SkippableFact]
    public void RadiancePrefilteringWithComputeShader()
    {
        PerformDrawTest((game, context) =>
        {
            const int size = 16;
            using var radianceFilter = new RadiancePrefilteringGGX(context.RenderContext);
            using var input = NewInputCubemap(game.GraphicsDevice);
            using var output = Texture.New2D(game.GraphicsDevice, size, size, MathUtil.Log2(size), PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess, 6);
            // Filter mip 0 too: the default copies it, which Direct3D 11 rejects between these two formats.
            radianceFilter.DoNotFilterHighestLevel = false;
            radianceFilter.MipmapGenerationCount = 3;
            radianceFilter.SamplingsCount = 16;
            radianceFilter.RadianceMap = input;
            radianceFilter.PrefilteredRadiance = output;
            radianceFilter.Draw(context);
        }, takeSnapshot: false);
    }

    [SkippableFact]
    public void LambertianPrefilteringWithComputeShader()
    {
        PerformDrawTest((game, context) =>
        {
            using var lambertFilter = new LambertianPrefilteringSH(context.RenderContext);
            using var input = NewInputCubemap(game.GraphicsDevice);
            lambertFilter.HarmonicOrder = 3;
            lambertFilter.RadianceMap = input;
            lambertFilter.Draw(context);
        }, takeSnapshot: false);
    }

    [SkippableFact]
    public void DepthRange()
    {
        PerformDrawTest((game, context) =>
        {
            using var depthMinMax = new DepthMinMax();
            using var depth = Texture.New2D(game.GraphicsDevice, 4, 4, PixelFormat.R32_Float, new float[16]);
            depthMinMax.Initialize(context.RenderContext);
            depthMinMax.SetInput(depth);
            depthMinMax.Draw(context);
        }, takeSnapshot: false);
    }
}
