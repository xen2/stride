// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.MaterialTestBed;

/// <summary>
///   Procedural textures of the test scene, with full mip chains built on the CPU (box filter).
/// </summary>
public sealed class TestBedTextures : IDisposable
{
    private const int Size = 256;

    public Texture RockAlbedo { get; }
    public Texture RockNormal { get; }

    /// <summary>
    ///   Roughness in green, metalness in blue.
    /// </summary>
    public Texture RockRoughMetal { get; }

    public Texture Moss { get; }
    public Texture Stripes { get; }

    /// <summary>
    ///   The mask of the layered material, in red.
    /// </summary>
    public Texture Mask { get; }

    public TestBedTextures(GraphicsDevice device)
    {
        static float Height(int x, int y) => Fbm((float)x / Size, (float)y / Size, 11);

        RockAlbedo = Create(device, (x, y) =>
        {
            var cracks = MathF.Abs(ValueNoise((float)x / Size, (float)y / Size, 32, 5) - 0.5f) < 0.03f ? 0.4f : 1.0f;
            return ToColor(new Vector3(0.45f, 0.40f, 0.35f) * (0.6f + Height(x, y)) * cracks);
        });
        RockNormal = Create(device, (x, y) =>
        {
            var dx = (Height((x + 1) % Size, y) - Height((x + Size - 1) % Size, y)) * 6.0f;
            var dy = (Height(x, (y + 1) % Size) - Height(x, (y + Size - 1) % Size)) * 6.0f;
            return ToColor(Vector3.Normalize(new Vector3(-dx, -dy, 1)) * 0.5f + new Vector3(0.5f));
        });
        RockRoughMetal = Create(device, (x, y) =>
        {
            var height = Height(x, y);
            return ToColor(new Vector3(0, 0.5f + 0.4f * height, height > 0.62f ? 1 : 0));
        });
        Moss = Create(device, (x, y) =>
        {
            var noise = Fbm((float)x / Size, (float)y / Size, 23);
            return ToColor(Vector3.Lerp(new Vector3(0.20f, 0.30f, 0.12f), new Vector3(0.45f, 0.42f, 0.30f), noise));
        });
        Stripes = Create(device, (x, y) =>
        {
            var stripe = (x / 32 + y / 32) % 2 == 0 ? 0.8f : 0.3f;
            return ToColor(new Vector3(stripe * (0.7f + 0.6f * ValueNoise((float)x / Size, (float)y / Size, 64, 31))));
        });
        Mask = Create(device, (x, y) => ToColor(new Vector3(MathUtil.SmoothStep(MathUtil.Clamp((Fbm((float)x / Size, (float)y / Size, 41) - 0.35f) * 6.0f, 0, 1)))));
    }

    public void Dispose()
    {
        RockAlbedo.Dispose();
        RockNormal.Dispose();
        RockRoughMetal.Dispose();
        Moss.Dispose();
        Stripes.Dispose();
        Mask.Dispose();
    }

    private static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }

    // Tileable value noise with `cells` cells per side
    private static float ValueNoise(float u, float v, int cells, int seed)
    {
        var x = u * cells;
        var y = v * cells;
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        int Wrap(int a) => (a % cells + cells) % cells;
        var a00 = Hash(Wrap(x0), Wrap(y0), seed);
        var a10 = Hash(Wrap(x0 + 1), Wrap(y0), seed);
        var a01 = Hash(Wrap(x0), Wrap(y0 + 1), seed);
        var a11 = Hash(Wrap(x0 + 1), Wrap(y0 + 1), seed);
        return MathUtil.Lerp(MathUtil.Lerp(a00, a10, fx), MathUtil.Lerp(a01, a11, fx), fy);
    }

    private static float Fbm(float u, float v, int seed)
    {
        float sum = 0, amplitude = 0.5f;
        for (int octave = 0, cells = 8; octave < 5; octave++, cells *= 2, amplitude *= 0.5f)
            sum += ValueNoise(u, v, cells, seed + octave) * amplitude;
        return sum;
    }

    private static Color ToColor(Vector3 color) => new(new Vector4(Vector3.Clamp(color, Vector3.Zero, Vector3.One), 1));

    private static Texture Create(GraphicsDevice device, Func<int, int, Color> texel)
    {
        var level0 = new Color[Size * Size];
        Parallel.For(0, Size, y =>
        {
            for (int x = 0; x < Size; x++)
                level0[y * Size + x] = texel(x, y);
        });

        var levelCount = (int)Math.Log2(Size) + 1;
        var levels = new Color[levelCount][];
        levels[0] = level0;
        for (int level = 1; level < levelCount; level++)
        {
            var size = Size >> level;
            var previous = levels[level - 1];
            var current = levels[level] = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var sum = previous[2 * y * 2 * size + 2 * x].ToVector4() + previous[2 * y * 2 * size + 2 * x + 1].ToVector4()
                        + previous[(2 * y + 1) * 2 * size + 2 * x].ToVector4() + previous[(2 * y + 1) * 2 * size + 2 * x + 1].ToVector4();
                current[y * size + x] = new Color(sum * 0.25f);
            }
        }

        var handles = levels.Select(x => GCHandle.Alloc(x, GCHandleType.Pinned)).ToArray();
        try
        {
            var boxes = new DataBox[levelCount];
            for (int level = 0; level < levelCount; level++)
            {
                var size = Size >> level;
                boxes[level] = new DataBox(handles[level].AddrOfPinnedObject(), size * 4, size * size * 4);
            }
            return Texture.New2D(device, Size, Size, levelCount, PixelFormat.R8G8B8A8_UNorm, boxes);
        }
        finally
        {
            foreach (var handle in handles)
                handle.Free();
        }
    }
}
