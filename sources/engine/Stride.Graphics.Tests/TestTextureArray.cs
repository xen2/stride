// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Linq;

using Xunit;

using Stride.Core.Mathematics;
using Stride.Rendering;
using Stride.Shaders;

namespace Stride.Graphics.Tests;

[EffectKeys]
public static class TextureArrayTestKeys
{
    public static readonly ObjectParameterKey<Texture> Textures = ParameterKeys.NewObject<Texture>();
    public static readonly ValueParameterKey<uint> Index = ParameterKeys.NewValue<uint>();
    public static readonly ObjectParameterKey<Texture> ViewTextures = ParameterKeys.NewObject<Texture>();
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

    /// <summary>
    /// Two effects bind one Descriptor Set with a texture array, as the draws of a material do. On Vulkan, the array is in a
    /// native set of its own, written once per command list and source set: each pipeline still reads its own element.
    /// </summary>
    [SkippableFact]
    public void TwoEffectsShareTheDescriptorSetOfAnArray()
    {
        PerformTest(game =>
        {
            var device = game.GraphicsDevice;
            var commandList = game.GraphicsContext.CommandList;

            var textures = Colors.Select(color => Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { color })).ToArray();
            using var second = new EffectInstance(game.EffectSystem.LoadEffect("Stride.Graphics.Tests.TextureArraySecondElementEffect").WaitForResult());
            using var third = new EffectInstance(game.EffectSystem.LoadEffect("Stride.Graphics.Tests.TextureArrayThirdElementEffect").WaitForResult());
            second.UpdateEffect(device);
            third.UpdateEffect(device);
            Assert.Equal(second.DescriptorReflection.Layouts.Select(layout => layout.Name), third.DescriptorReflection.Layouts.Select(layout => layout.Name));

            // The Descriptor Sets of the first effect, bound to both
            var layouts = second.DescriptorReflection.Layouts.Select(layout => layout.Layout).ToArray();
            using var pool = DescriptorPool.New(device, [new DescriptorTypeCount(EffectParameterClass.ShaderResourceView, 16)]);
            var descriptorSets = new DescriptorSet[layouts.Length];
            for (int i = 0; i < layouts.Length; i++)
            {
                descriptorSets[i] = DescriptorSet.New(device, pool, DescriptorSetLayout.New(device, layouts[i]));
                var slot = 0;
                foreach (var entry in layouts[i].Entries)
                {
                    for (int element = 0; element < entry.ArraySize; element++, slot++)
                    {
                        if (entry.Key == TextureArrayTestKeys.Textures)
                            descriptorSets[i].SetShaderResourceView(slot, textures[element]);
                    }
                }
            }

            EffectInstance[] effects = [second, third, second];
            var targets = effects.Select(_ => Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource)).ToArray();
            for (int i = 0; i < effects.Length; i++)
            {
                var pipelineState = new MutablePipelineState(device);
                pipelineState.State.SetDefaults();
                pipelineState.State.RootSignature = effects[i].RootSignature;
                pipelineState.State.EffectBytecode = effects[i].Effect.Bytecode;
                pipelineState.State.InputElements = PrimitiveQuad.VertexDeclaration.CreateInputElements();
                pipelineState.State.PrimitiveType = PrimitiveQuad.PrimitiveType;

                commandList.SetRenderTargetAndViewport(null, targets[i]);
                pipelineState.State.Output.CaptureState(commandList);
                pipelineState.Update();
                commandList.SetPipelineState(pipelineState.CurrentState);
                commandList.SetDescriptorSets(0, descriptorSets);
                device.PrimitiveQuad.Draw(commandList);
            }

            Assert.Equal(new[] { Colors[1], Colors[2], Colors[1] }, targets.Select(target => target.GetData<Color>(commandList)[0]));

            foreach (var texture in textures.Concat(targets))
                texture.Dispose();
        });
    }

    /// <summary>
    /// An effect with texture arrays in two rgroups, drawn twice with the same Descriptor Set for the first group and another one
    /// for the second: each draw reads the elements of its own sets.
    /// </summary>
    [SkippableFact]
    public void ArraysOfTwoGroupsFollowBothDescriptorSets()
    {
        PerformTest(game =>
        {
            var device = game.GraphicsDevice;
            var commandList = game.GraphicsContext.CommandList;

            var textures = Colors.Select(color => Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { color })).ToArray();
            using var effect = new EffectInstance(game.EffectSystem.LoadEffect("Stride.Graphics.Tests.TextureArrayTwoGroupsEffect").WaitForResult());
            effect.UpdateEffect(device);

            // The second group gets another Descriptor Set for the second draw: element 1 is red, then blue
            var layouts = effect.DescriptorReflection.Layouts.Select(layout => layout.Layout).ToArray();
            using var pool = DescriptorPool.New(device, [new DescriptorTypeCount(EffectParameterClass.ShaderResourceView, 32)]);
            DescriptorSet[] NewDescriptorSets(Texture viewElement)
            {
                var descriptorSets = new DescriptorSet[layouts.Length];
                for (int i = 0; i < layouts.Length; i++)
                {
                    descriptorSets[i] = DescriptorSet.New(device, pool, DescriptorSetLayout.New(device, layouts[i]));
                    var slot = 0;
                    foreach (var entry in layouts[i].Entries)
                    {
                        for (int element = 0; element < entry.ArraySize; element++, slot++)
                        {
                            if (entry.Key == TextureArrayTestKeys.Textures)
                                descriptorSets[i].SetShaderResourceView(slot, textures[element]);
                            else if (entry.Key == TextureArrayTestKeys.ViewTextures)
                                descriptorSets[i].SetShaderResourceView(slot, element == 1 ? viewElement : textures[3]);
                        }
                    }
                }
                return descriptorSets;
            }
            var first = NewDescriptorSets(textures[0]);
            var second = NewDescriptorSets(textures[2]);
            var materialLayout = effect.DescriptorReflection.Layouts.FindIndex(layout => layout.Name == "PerMaterial");
            second[materialLayout] = first[materialLayout];

            var pipelineState = new MutablePipelineState(device);
            pipelineState.State.SetDefaults();
            pipelineState.State.RootSignature = effect.RootSignature;
            pipelineState.State.EffectBytecode = effect.Effect.Bytecode;
            pipelineState.State.InputElements = PrimitiveQuad.VertexDeclaration.CreateInputElements();
            pipelineState.State.PrimitiveType = PrimitiveQuad.PrimitiveType;

            DescriptorSet[][] draws = [first, second];
            var targets = draws.Select(_ => Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource)).ToArray();
            for (int i = 0; i < draws.Length; i++)
            {
                commandList.SetRenderTargetAndViewport(null, targets[i]);
                pipelineState.State.Output.CaptureState(commandList);
                pipelineState.Update();
                commandList.SetPipelineState(pipelineState.CurrentState);
                commandList.SetDescriptorSets(0, draws[i]);
                device.PrimitiveQuad.Draw(commandList);
            }

            Assert.Equal(new[] { Color.Yellow, Color.Cyan }, targets.Select(target => target.GetData<Color>(commandList)[0]));

            foreach (var texture in textures.Concat(targets))
                texture.Dispose();
        });
    }
}
