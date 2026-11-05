// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;

using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace Stride.MaterialTestBed;

/// <summary>
///   Builds a <see cref="MaterialDescription"/> with today's material generator, to compare with <see cref="MaterialTable"/>.
/// </summary>
public static class GeneratedMaterials
{
    public static Material Create(GraphicsDevice device, ContentManager content, MaterialDescription description)
    {
        var descriptor = CreateDescriptor(description.Layers[0]);
        for (int i = 1; i < description.Layers.Count; i++)
        {
            var layer = description.Layers[i];
            descriptor.Layers.Add(new MaterialBlendLayer
            {
                Material = new Material { Descriptor = CreateDescriptor(layer) },
                BlendMap = Scalar(layer.Mask),
            });
        }
        return Material.New(device, descriptor, content);
    }

    private static MaterialDescriptor CreateDescriptor(LayerDescription layer) => new()
    {
        Attributes =
        {
            Diffuse = new MaterialDiffuseMapFeature(Color(layer.BaseColor, new ComputeColor(new Color4(layer.BaseColor.Constant)))),
            DiffuseModel = new MaterialDiffuseLambertModelFeature(),
            Surface = new MaterialNormalMapFeature(Color(layer.Normal, new ComputeFloat4(layer.Normal.Constant))),
            // Glossiness is 1 - roughness
            MicroSurface = new MaterialGlossinessMapFeature(Scalar(layer.Roughness)) { Invert = true },
            Specular = new MaterialMetalnessMapFeature(Scalar(layer.Metalness)),
            SpecularModel = new MaterialSpecularMicrofacetModelFeature(),
            Occlusion = new MaterialOcclusionMapFeature { AmbientOcclusionMap = Scalar(layer.Occlusion) },
            Emissive = new MaterialEmissiveMapFeature(Color(layer.Emissive, new ComputeColor(new Color4(layer.Emissive.Constant)))),
        },
    };

    private static IComputeColor Color(SlotDescription slot, IComputeColor constant)
    {
        var value = constant;
        if (slot.Texture != null)
            value = new ComputeBinaryColor(value, Texture(slot.Texture, slot.UvChannel, slot.UvScale, slot.UvOffset), BinaryOperator.Multiply);
        if (slot.Detail != null)
        {
            IComputeColor detail = Texture(slot.Detail, slot.DetailUvChannel, slot.DetailUvScale, slot.DetailUvOffset);
            if (slot.DetailWeight != 1)
                detail = new ComputeBinaryColor(detail, new ComputeFloat4(new Vector4(slot.DetailWeight)), BinaryOperator.Multiply);
            value = new ComputeBinaryColor(value, detail, Operator(slot.Op));
        }
        if (slot.Bias != 0)
            value = new ComputeBinaryColor(value, new ComputeFloat4(new Vector4(slot.Bias)), BinaryOperator.Add);
        return value;
    }

    private static IComputeScalar Scalar(SlotDescription slot)
    {
        IComputeScalar value = new ComputeFloat(slot.Constant.X);
        if (slot.Texture != null)
        {
            var texture = new ComputeTextureScalar(slot.Texture, Coordinate(slot.UvChannel), slot.UvScale, slot.UvOffset) { Channel = Channel(slot.Channel) };
            value = new ComputeBinaryScalar(value, texture, BinaryOperator.Multiply);
        }
        if (slot.Detail != null)
        {
            IComputeScalar detail = new ComputeTextureScalar(slot.Detail, Coordinate(slot.DetailUvChannel), slot.DetailUvScale, slot.DetailUvOffset);
            if (slot.DetailWeight != 1)
                detail = new ComputeBinaryScalar(detail, new ComputeFloat(slot.DetailWeight), BinaryOperator.Multiply);
            value = new ComputeBinaryScalar(value, detail, Operator(slot.Op));
        }
        if (slot.Bias != 0)
            value = new ComputeBinaryScalar(value, new ComputeFloat(slot.Bias), BinaryOperator.Add);
        return value;
    }

    private static ComputeTextureColor Texture(Texture texture, uint uvChannel, Vector2 scale, Vector2 offset)
        => new(texture, Coordinate(uvChannel), scale, offset);

    private static TextureCoordinate Coordinate(uint uvChannel) => uvChannel switch
    {
        0 => TextureCoordinate.Texcoord0,
        1 => TextureCoordinate.Texcoord1,
        _ => throw new ArgumentOutOfRangeException(nameof(uvChannel)),
    };

    private static ColorChannel Channel(SlotChannel channel) => channel switch
    {
        SlotChannel.G => ColorChannel.G,
        SlotChannel.B => ColorChannel.B,
        SlotChannel.A => ColorChannel.A,
        _ => ColorChannel.R,
    };

    private static BinaryOperator Operator(SlotOp op) => op == SlotOp.Add ? BinaryOperator.Add : BinaryOperator.Multiply;
}
