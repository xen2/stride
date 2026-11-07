// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Linq;

using Xunit;

using Stride.Core.Mathematics;
using Stride.Rendering;

namespace Stride.Graphics.Tests;

[EffectKeys]
public static class TwinTestKeys
{
    public static readonly ObjectParameterKey<Buffer> Records = ParameterKeys.NewObject<Buffer>();
    public static readonly ValueParameterKey<uint> Index = ParameterKeys.NewValue<uint>();
    public static readonly ValueParameterKey<TwinLight> Light = ParameterKeys.NewValue<TwinLight>();
}

public class TestCSharpTwin : GraphicTestGameBase
{
    public TestCSharpTwin()
    {
        // Test shaders compile at the test settings' profile otherwise, which has no structured buffer
        GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
    }

    /// <summary>
    /// The C# twins that the generator writes for TwinShader.sdsl fill a structured buffer of a record with a float3 after a float
    /// (not std430), and a cbuffer: the shader reads every field where C# wrote it.
    /// </summary>
    [SkippableFact]
    public void TwinsFillTheBuffers()
    {
        PerformTest(game =>
        {
            var device = game.GraphicsDevice;
            var commandList = game.GraphicsContext.CommandList;

            // 8-bit values, so that the colors compare exactly
            static float Byte(int value) => value / 255.0f;
            var records = new[]
            {
                new TwinRecord { a = Byte(51), b = new Vector3(Byte(102), Byte(153), Byte(204)), channel = TwinChannel.G, e = new Vector2(0.5f, 0.5f), f = Byte(17) },
                new TwinRecord { a = Byte(85), b = new Vector3(Byte(34), Byte(68), Byte(136)), channel = TwinChannel.R, e = new Vector2(Byte(51), 0), f = 0 },
            };
            using var recordBuffer = Buffer.Structured.New(device, records);
            using var effect = new EffectInstance(game.EffectSystem.LoadEffect("Stride.Graphics.Tests.TwinEffect").WaitForResult());
            effect.Parameters.Set(TwinTestKeys.Records, recordBuffer);
            effect.Parameters.Set(TwinTestKeys.Light, new TwinLight { a = Byte(60), b = new Vector3(Byte(10), Byte(20), Byte(30)), c = new Vector4(0, 0, 0, Byte(40)) });

            Color[] expected = [new Color(10, 20, 30, 100), new Color(102, 153, 204, 68), new Color(34, 0, 0, 136)];

            // One target per draw, read after the last one
            var targets = expected.Select(_ => Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource)).ToArray();
            for (uint i = 0; i < targets.Length; i++)
            {
                effect.Parameters.Set(TwinTestKeys.Index, i);
                commandList.SetRenderTargetAndViewport(null, targets[i]);
                game.GraphicsContext.DrawQuad(effect);
            }

            Assert.Equal(expected, targets.Select(target => target.GetData<Color>(commandList)[0]));

            foreach (var target in targets)
                target.Dispose();
        });
    }
}
