// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;

using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Shaders;

using Buffer = Stride.Graphics.Buffer;

namespace Stride.MaterialTestBed;

/// <summary>
///   Packs materials into the records of <c>TestBedMaterialTable</c>. All materials use one effect: only the records change.
/// </summary>
public sealed class MaterialTable : IDisposable
{
    // The textures one material can use: TestBedSlots.SlotTexture0 to 7
    private static readonly ObjectParameterKey<Texture>[] TextureKeys =
    [
        TestBedSlotsKeys.SlotTexture0, TestBedSlotsKeys.SlotTexture1, TestBedSlotsKeys.SlotTexture2, TestBedSlotsKeys.SlotTexture3,
        TestBedSlotsKeys.SlotTexture4, TestBedSlotsKeys.SlotTexture5, TestBedSlotsKeys.SlotTexture6, TestBedSlotsKeys.SlotTexture7,
    ];

    private const int NoTexture = -1;

    private readonly List<MaterialRecord> materials = [];
    private readonly List<LayerRecord> layers = [];
    private readonly List<SlotRecord> slots = [];
    private readonly List<MaterialPass> passes = [];
    private readonly Dictionary<MaterialPass, uint> indices = [];
    private readonly ColorSpace colorSpace;
    private readonly Texture lookupTable;
    private readonly Texture fallbackTexture;
    private Buffer materialBuffer;
    private Buffer layerBuffer;
    private Buffer slotBuffer;
    private readonly string shadingClass;

    /// <param name="shadingClass">The class that plugs the shading model into the light loop.</param>
    public MaterialTable(GraphicsDevice device, Texture lookupTable, string shadingClass = null)
    {
        colorSpace = device.ColorSpace;
        this.lookupTable = lookupTable;
        this.shadingClass = shadingClass ?? TestBedStandardShadingKeys.ShaderName;
        fallbackTexture = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { Color.White });
    }

    /// <summary>
    ///   Gets the index of a material of this table in the records, to pass to the shader for each draw.
    /// </summary>
    public bool TryGetIndex(MaterialPass pass, out uint index) => indices.TryGetValue(pass, out index);

    /// <summary>
    ///   Adds a material. Its records reach the GPU with <see cref="Upload"/>.
    /// </summary>
    public Material Add(MaterialDescription description)
    {
        var textures = new List<Texture>();
        var index = (uint)materials.Count;
        materials.Add(new MaterialRecord { LayerOffset = (uint)layers.Count, LayerCount = (uint)description.Layers.Count });

        foreach (var layer in description.Layers)
        {
            var maskSlot = -1;
            if (layer.Mask != null)
            {
                maskSlot = slots.Count;
                slots.Add(Pack(layer.Mask, textures));
            }

            layers.Add(new LayerRecord { MaskSlot = maskSlot, SlotOffset = (uint)slots.Count });

            // The order of TestBedRecords.LayerSlot*
            slots.Add(PackColor(layer.BaseColor, textures));
            slots.Add(Pack(layer.Normal, textures));
            slots.Add(Pack(layer.Roughness, textures));
            slots.Add(Pack(layer.Metalness, textures));
            slots.Add(Pack(layer.Occlusion, textures));
            slots.Add(PackColor(layer.Emissive, textures));
        }

        var pass = new MaterialPass(new ParameterCollection()) { IsLightDependent = true };
        pass.Parameters.Set(MaterialKeys.PixelStageSurfaceShaders, CreatePixelStage());
        pass.Parameters.Set(MaterialKeys.PixelStageStreamInitializer, CreateStreamInitializer());
        pass.Parameters.Set(MaterialKeys.HasNormalMap, true);
        pass.Parameters.Set(MaterialSpecularMicrofacetEnvironmentGGXLUTKeys.EnvironmentLightingDFG_LUT, lookupTable);
        for (int i = 0; i < TextureKeys.Length; i++)
            pass.Parameters.Set(TextureKeys[i], i < textures.Count ? textures[i] : fallbackTexture);

        passes.Add(pass);
        indices.Add(pass, index);

        var material = new Material();
        material.Passes.Add(pass);
        return material;
    }

    /// <summary>
    ///   Creates the buffers of the table and gives them to the materials.
    /// </summary>
    public void Upload(GraphicsDevice device)
    {
        materialBuffer = Buffer.Structured.New(device, materials.ToArray());
        layerBuffer = Buffer.Structured.New(device, layers.ToArray());
        slotBuffer = Buffer.Structured.New(device, slots.ToArray());

        foreach (var pass in passes)
        {
            pass.Parameters.Set(TestBedMaterialTableKeys.MaterialRecords, materialBuffer);
            pass.Parameters.Set(TestBedMaterialTableKeys.LayerRecords, layerBuffer);
            pass.Parameters.Set(TestBedMaterialTableKeys.SlotRecords, slotBuffer);
        }
    }

    // A color constant is given in sRGB, like the colors of a generated material
    private SlotRecord PackColor(SlotDescription slot, List<Texture> textures)
    {
        var record = Pack(slot, textures);
        record.Constant = new Color4(slot.Constant).ToColorSpace(colorSpace);
        return record;
    }

    private static SlotRecord Pack(SlotDescription slot, List<Texture> textures) => new()
    {
        Constant = slot.Constant,
        UvScaleOffset = new Vector4(slot.UvScale, slot.UvOffset.X, slot.UvOffset.Y),
        DetailUvScaleOffset = new Vector4(slot.DetailUvScale, slot.DetailUvOffset.X, slot.DetailUvOffset.Y),
        TextureIndex = TextureIndex(slot.Texture, textures),
        DetailTextureIndex = TextureIndex(slot.Detail, textures),
        SamplerId = (uint)slot.Sampler,
        UvChannel = slot.UvChannel,
        DetailUvChannel = slot.DetailUvChannel,
        Channel = (uint)slot.Channel,
        Op = (uint)slot.Op,
        DetailWeight = slot.DetailWeight,
        Bias = slot.Bias,
    };

    private static int TextureIndex(Texture texture, List<Texture> textures)
    {
        if (texture == null)
            return NoTexture;

        var index = textures.IndexOf(texture);
        if (index >= 0)
            return index;

        if (textures.Count == TextureKeys.Length)
            throw new InvalidOperationException($"A material uses {TextureKeys.Length} textures at most");

        textures.Add(texture);
        return textures.Count - 1;
    }

    private ShaderSource CreatePixelStage()
    {
        var shading = new ShaderMixinSource();
        shading.Mixins.Add(new ShaderClassSource(shadingClass));
        shading.AddComposition("environmentFunction", new ShaderClassSource(MaterialSpecularMicrofacetEnvironmentGGXLUTKeys.ShaderName));

        var lighting = new ShaderMixinSource();
        lighting.Mixins.Add(new ShaderClassSource(MaterialSurfaceLightingAndShadingKeys.ShaderName));
        lighting.AddCompositionToArray("surfaces", shading);

        var root = new ShaderMixinSource();
        root.Mixins.Add(new ShaderClassSource(MaterialSurfaceArrayKeys.ShaderName));
        root.AddCompositionToArray("layers", new ShaderClassSource(TestBedMaterialSurfaceKeys.ShaderName));
        root.AddCompositionToArray("layers", lighting);
        return root;
    }

    private static ShaderSource CreateStreamInitializer()
    {
        var initializer = new ShaderMixinSource();
        initializer.Mixins.Add(new ShaderClassSource(MaterialStreamKeys.ShaderName));
        initializer.Mixins.Add(new ShaderClassSource(MaterialPixelShadingStreamKeys.ShaderName));
        return initializer;
    }

    public void Dispose()
    {
        materialBuffer?.Dispose();
        layerBuffer?.Dispose();
        slotBuffer?.Dispose();
        fallbackTexture.Dispose();
    }
}
