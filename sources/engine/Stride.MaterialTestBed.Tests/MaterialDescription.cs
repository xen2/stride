// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;

using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.MaterialTestBed;

public enum SlotChannel : uint { All, R, G, B, A }

public enum SlotOp : uint { Multiply, Add }

public enum SlotSampler : uint { LinearWrap, AnisotropicWrap }

/// <summary>
///   A tier 1 slot: <c>constant x sample(texture) [op] sample(detail) x detailWeight + bias</c>.
/// </summary>
public sealed class SlotDescription
{
    public Vector4 Constant = Vector4.One;
    public Texture Texture;
    public SlotChannel Channel;
    public SlotSampler Sampler;
    public uint UvChannel;
    public Vector2 UvScale = Vector2.One;
    public Vector2 UvOffset;
    public Texture Detail;
    public uint DetailUvChannel;
    public Vector2 DetailUvScale = Vector2.One;
    public Vector2 DetailUvOffset;
    public float DetailWeight = 1;
    public SlotOp Op;
    public float Bias;

    public static SlotDescription Value(float value) => new() { Constant = new Vector4(value) };

    public static SlotDescription Value(Vector3 value) => new() { Constant = new Vector4(value, 1) };
}

/// <summary>
///   A full set of surface inputs. A layer above the first one is blended over the layers below with its mask.
/// </summary>
public sealed class LayerDescription
{
    public SlotDescription BaseColor = SlotDescription.Value(Vector3.One);
    public SlotDescription Normal = SlotDescription.Value(new Vector3(0.5f, 0.5f, 1));
    public SlotDescription Roughness = SlotDescription.Value(1.0f);
    public SlotDescription Metalness = SlotDescription.Value(0.0f);
    public SlotDescription Occlusion = SlotDescription.Value(1.0f);
    public SlotDescription Emissive = SlotDescription.Value(Vector3.Zero);
    public SlotDescription Mask;
}

/// <summary>
///   A material, described once and built by both paths: <see cref="MaterialTable"/> and <see cref="GeneratedMaterials"/>.
/// </summary>
public sealed class MaterialDescription
{
    public List<LayerDescription> Layers { get; } = [];
}
