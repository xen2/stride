// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Runtime.InteropServices;

using Stride.Core.Mathematics;

namespace Stride.MaterialTestBed;

// The C# side of TestBedRecords.sdsl: the same members in the same order.

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MaterialRecord
{
    public uint ShadingModel;
    public uint Flags;
    public uint LayerOffset;
    public uint LayerCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LayerRecord
{
    public int MaskSlot;
    public uint BlendMode;
    public uint SlotOffset;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SlotRecord
{
    public Vector4 Constant;
    public Vector4 UvScaleOffset;
    public Vector4 DetailUvScaleOffset;
    public int TextureIndex;
    public int DetailTextureIndex;
    public uint SamplerId;
    public uint UvChannel;
    public uint DetailUvChannel;
    public uint Channel;
    public uint Op;
    public uint GraphId;
    public float DetailWeight;
    public float Bias;
    public uint Reserved0;
    public uint Reserved1;
}
