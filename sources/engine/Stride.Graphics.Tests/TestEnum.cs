// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Linq;
using System.Runtime.InteropServices;

using Xunit;

using Stride.Core.Mathematics;
using Stride.Rendering;

namespace Stride.Graphics.Tests;

[EffectKeys]
public static class EnumTestKeys
{
    public static readonly ObjectParameterKey<Buffer> Slots = ParameterKeys.NewObject<Buffer>();
    public static readonly ValueParameterKey<uint> Index = ParameterKeys.NewValue<uint>();
    public static readonly ValueParameterKey<uint> Mode = ParameterKeys.NewValue<uint>();
}

public class TestEnum : GraphicTestGameBase
{
    // The values of Channel in EnumShader.sdsl
    private enum Channel : uint { R, G, B = 5, A }

    [StructLayout(LayoutKind.Sequential)]
    private struct Slot
    {
        public Vector4 Color;
        public Channel Channel;
        public Channel Fallback;
        public uint Pad0;
        public uint Pad1;
    }

    public TestEnum()
    {
        // Test shaders compile at the test settings' profile otherwise, which has no structured buffer
        GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
    }

    /// <summary>
    /// An enum read from a structured buffer and from a cbuffer, through == and a switch that names every value.
    /// </summary>
    [SkippableFact]
    public void EnumValuesFromBuffers()
    {
        PerformTest(game =>
        {
            var device = game.GraphicsDevice;
            var commandList = game.GraphicsContext.CommandList;

            var slots = new[]
            {
                new Slot { Color = Vector4.One, Channel = Channel.R },
                new Slot { Color = Vector4.One, Channel = Channel.G },
                new Slot { Color = Vector4.One, Channel = Channel.B },
                new Slot { Color = Vector4.One, Channel = Channel.A, Fallback = Channel.G },
            };
            using var slotBuffer = Buffer.Structured.New(device, slots);
            using var effect = new EffectInstance(game.EffectSystem.LoadEffect("Stride.Graphics.Tests.EnumEffect").WaitForResult());
            effect.Parameters.Set(EnumTestKeys.Slots, slotBuffer);

            // (index, mode): the last draw takes the channel of the cbuffer
            (uint Index, Channel Mode)[] draws = [(0, Channel.R), (1, Channel.R), (2, Channel.R), (3, Channel.R), (0, Channel.A)];
            Color[] expected = [Color.Red, Color.Lime, Color.Blue, Color.Lime, Color.White];

            // One target per draw, read after the last one
            var targets = draws.Select(_ => Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource)).ToArray();
            for (int i = 0; i < draws.Length; i++)
            {
                effect.Parameters.Set(EnumTestKeys.Index, draws[i].Index);
                effect.Parameters.Set(EnumTestKeys.Mode, (uint)draws[i].Mode);
                commandList.SetRenderTargetAndViewport(null, targets[i]);
                game.GraphicsContext.DrawQuad(effect);
            }

            Assert.Equal(expected, targets.Select(target => target.GetData<Color>(commandList)[0]));

            foreach (var target in targets)
                target.Dispose();
        });
    }
}
