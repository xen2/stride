// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

using Buffer = Stride.Graphics.Buffer;

namespace Stride.MaterialTestBed;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct TestBedVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector4 Tangent;
    public Vector2 TexCoord0;
    public Vector2 TexCoord1;

    public static readonly VertexDeclaration Layout = new(
        VertexElement.Position<Vector3>(),
        VertexElement.Normal<Vector3>(),
        VertexElement.Tangent<Vector4>(),
        VertexElement.TextureCoordinate<Vector2>(0),
        VertexElement.TextureCoordinate<Vector2>(1));
}

/// <summary>
///   The meshes of the test scene, with two UV channels and tangents.
/// </summary>
public static class TestBedMeshes
{
    // A sphere displaced by a few sines of the direction; UV0 wraps around (one seam), UV1 has another scale
    public static Mesh CreateRock(GraphicsDevice device, int segments, int seed)
    {
        var random = new Random(seed);
        var axes = new Vector4[5];
        for (int i = 0; i < axes.Length; i++)
        {
            var axis = Vector3.Normalize(new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f));
            axes[i] = new Vector4(axis, 1.5f + 3.0f * (float)random.NextDouble());
        }

        Vector3 Surface(float theta, float phi)
        {
            var direction = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
            var radius = 1.0f;
            foreach (var axis in axes)
                radius += 0.06f * MathF.Sin(Vector3.Dot(direction, new Vector3(axis.X, axis.Y, axis.Z)) * axis.W * 2.0f + axis.W);
            return direction * radius;
        }

        var rings = segments / 2;
        var vertices = new List<TestBedVertex>((segments + 1) * (rings + 1));
        var indices = new List<uint>(segments * rings * 6);
        const float Epsilon = 1e-3f;
        for (int ring = 0; ring <= rings; ring++)
        {
            var v = (float)ring / rings;
            var phi = v * MathF.PI;
            for (int segment = 0; segment <= segments; segment++)
            {
                var u = (float)segment / segments;
                var theta = u * MathF.Tau;
                var position = Surface(theta, phi);

                var dTheta = (Surface(theta + Epsilon, phi) - Surface(theta - Epsilon, phi)) / (2 * Epsilon);
                var dPhi = (Surface(theta, Math.Clamp(phi + Epsilon, 0, MathF.PI)) - Surface(theta, Math.Clamp(phi - Epsilon, 0, MathF.PI))) / (2 * Epsilon);
                Vector3 normal;
                Vector3 tangent;
                if (dTheta.LengthSquared() < 1e-8f)
                {
                    normal = new Vector3(0, ring == 0 ? 1 : -1, 0);
                    tangent = new Vector3(-MathF.Sin(theta), 0, MathF.Cos(theta));
                }
                else
                {
                    normal = Vector3.Normalize(Vector3.Cross(dPhi, dTheta));
                    tangent = Vector3.Normalize(dTheta);
                }
                if (Vector3.Dot(normal, position) < 0)
                    normal = -normal;
                var bitangentSign = Vector3.Dot(Vector3.Cross(normal, tangent), dPhi) < 0 ? -1.0f : 1.0f;

                vertices.Add(new TestBedVertex
                {
                    Position = position,
                    Normal = normal,
                    Tangent = new Vector4(tangent, bitangentSign),
                    TexCoord0 = new Vector2(u * 2, v),
                    TexCoord1 = new Vector2(u * 3, v * 1.5f),
                });
            }
        }

        var stride = segments + 1;
        for (int ring = 0; ring < rings; ring++)
        for (int segment = 0; segment < segments; segment++)
        {
            var a = (uint)(ring * stride + segment);
            var b = a + 1;
            var c = (uint)((ring + 1) * stride + segment);
            var d = c + 1;
            indices.AddRange([a, b, c, b, d, c]);
        }
        return CreateMesh(device, vertices, indices);
    }

    public static Mesh CreateGround(GraphicsDevice device, float size, int cells)
    {
        var vertices = new List<TestBedVertex>();
        var indices = new List<uint>();
        for (int z = 0; z <= cells; z++)
        for (int x = 0; x <= cells; x++)
        {
            var u = (float)x / cells;
            var v = (float)z / cells;
            vertices.Add(new TestBedVertex
            {
                Position = new Vector3((u - 0.5f) * size, 0, (v - 0.5f) * size),
                Normal = Vector3.UnitY,
                Tangent = new Vector4(1, 0, 0, 1),
                TexCoord0 = new Vector2(u, v) * size * 0.5f,
                TexCoord1 = new Vector2(u, v) * size * 0.25f,
            });
        }
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            var a = (uint)(z * (cells + 1) + x);
            var b = a + 1;
            var c = (uint)((z + 1) * (cells + 1) + x);
            var d = c + 1;
            indices.AddRange([a, c, b, b, c, d]);
        }
        return CreateMesh(device, vertices, indices);
    }

    private static Mesh CreateMesh(GraphicsDevice device, List<TestBedVertex> vertices, List<uint> indices)
    {
        FixWinding(vertices, indices);

        var bounds = BoundingBox.Empty;
        foreach (var vertex in vertices)
        {
            var position = vertex.Position;
            BoundingBox.Merge(ref bounds, ref position, out bounds);
        }

        var vertexBuffer = Buffer.Vertex.New(device, vertices.ToArray());
        var indexBuffer = Buffer.Index.New(device, indices.ToArray());
        return new Mesh
        {
            Draw = new MeshDraw
            {
                PrimitiveType = PrimitiveType.TriangleList,
                VertexBuffers = [new VertexBufferBinding(vertexBuffer, TestBedVertex.Layout, vertices.Count, TestBedVertex.Layout.VertexStride)],
                IndexBuffer = new IndexBufferBinding(indexBuffer, true, indices.Count),
                DrawCount = indices.Count,
            },
            MaterialIndex = 0,
            BoundingBox = bounds,
            BoundingSphere = BoundingSphere.FromBox(bounds),
        };
    }

    // Front faces are clockwise seen from outside (the default rasterizer state culls counterclockwise)
    private static void FixWinding(List<TestBedVertex> vertices, List<uint> indices)
    {
        for (int i = 0; i < indices.Count; i += 3)
        {
            var p0 = vertices[(int)indices[i]].Position;
            var p1 = vertices[(int)indices[i + 1]].Position;
            var p2 = vertices[(int)indices[i + 2]].Position;
            var normal = vertices[(int)indices[i]].Normal + vertices[(int)indices[i + 1]].Normal + vertices[(int)indices[i + 2]].Normal;
            var faceNormal = Vector3.Cross(p1 - p0, p2 - p0);
            if (faceNormal.LengthSquared() >= 1e-14f && Vector3.Dot(faceNormal, normal) > 0)
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        }
    }
}
