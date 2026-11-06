// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Linq;

using Xunit;

using Stride.Core.Mathematics;
using Stride.Rendering;

namespace Stride.Graphics.Tests;

[EffectKeys]
public static class TextureArrayTestKeys
{
    public static readonly ObjectParameterKey<Texture> Textures = ParameterKeys.NewObject<Texture>();
    public static readonly ValueParameterKey<uint> Index = ParameterKeys.NewValue<uint>();
}

public class TestTextureArray : GraphicTestGameBase
{
    private static readonly Color[] Colors = [Color.Red, Color.Lime, Color.Blue, Color.Yellow];

    public TestTextureArray()
    {
        // Test shaders compile at the test settings' profile otherwise, which has no Texture2D.Load
        GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
    }

    /// <summary>
    /// A texture array set through the parameters of an <see cref="EffectInstance"/>, one texture per element. Direct3D 11 reads
    /// element 2, a literal index; the other APIs read the element of an index from a cbuffer.
    /// </summary>
    [SkippableFact]
    public void EffectInstanceReadsTheElementOfTheIndex()
    {
        PerformTest(game =>
        {
            var device = game.GraphicsDevice;
            var commandList = game.GraphicsContext.CommandList;
            Assert.True(device.Features.MaxTextureArraySize >= Colors.Length, device.Features.ToString());

            var textures = Colors.Select(color => Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { color })).ToArray();
            using var effect = new EffectInstance(game.EffectSystem.LoadEffect("Stride.Graphics.Tests.TextureArrayEffect").WaitForResult());
            effect.Parameters.Set(TextureArrayTestKeys.Textures, textures);

            // One target per draw, read after the last one
            var indices = GraphicsDevice.Platform == GraphicsPlatform.Direct3D11 ? new uint[] { 2 } : [2, 3, 0];
            var targets = indices.Select(_ => Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource)).ToArray();
            for (int i = 0; i < indices.Length; i++)
            {
                effect.Parameters.Set(TextureArrayTestKeys.Index, indices[i]);
                commandList.SetRenderTargetAndViewport(null, targets[i]);
                game.GraphicsContext.DrawQuad(effect);
            }

            Assert.Equal(indices.Select(index => Colors[index]), targets.Select(target => target.GetData<Color>(commandList)[0]));

            foreach (var texture in textures.Concat(targets))
                texture.Dispose();
        });
    }
}
