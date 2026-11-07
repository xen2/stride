// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Reflection;
using System.Runtime.InteropServices;
using Stride.Core.Diagnostics;
using Stride.Graphics;
using Stride.Shaders.Compiler;
using Stride.Shaders.Compilers.SDSL;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// <c>[CSharp]</c> on an enum or a struct: the keys generator writes its C# twin (or checks the C# enum it maps to), and the C# code
/// uses it like any type. A twin has the layout of the struct in a structured buffer on every API.
/// </summary>
public class CSharpTwinTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateTempSubdirectory("stride-csharp-twins");

    public void Dispose()
    {
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TwinsAreGeneratedAndKeysUseThem()
    {
        var shader = """
            namespace Stride.Shaders.Tests.Keys;

            shader KeysGeneratorCheck
            {
                [CSharp] enum class SlotChannel : uint { All, R, G = 5 };
                [CSharp] enum class Bias : int { Low = -1, High = 1 };
                enum class DebugView : uint { Off, On };
                [CSharp] struct Inner { float weight; uint flags; };
                [CSharp] struct SlotRecord
                {
                    float4 constant;
                    [Color] float3 tint;
                    SlotChannel channel;
                    Bias bias;
                    Inner inner;
                    int2 offset;
                    uint4 ids;
                    float4x4 transform;
                    uint event;
                };

                cbuffer PerDraw
                {
                    SlotChannel Channel;
                    SlotRecord Record;
                    DebugView View;
                };
            };
            """;
        var csharp = """
            using Stride.Shaders.Tests.Keys;
            static class UsesTheTwins
            {
                static SlotRecord Make() => new() { channel = SlotChannel.G, bias = Bias.Low, inner = new() { weight = 1 }, @event = 2 };
                static object Keys() => (KeysGeneratorCheckKeys.Channel, KeysGeneratorCheckKeys.Record);
            }
            """;

        var (generated, errors) = KeysGeneratorTests.Build(shader, csharp);

        Assert.Empty(errors);
        Assert.Contains("public enum SlotChannel : uint", generated);
        Assert.Contains("G = 5,", generated);
        Assert.Contains("public enum Bias : int", generated);
        Assert.DoesNotContain("enum DebugView :", generated);
        Assert.Contains("public partial struct SlotRecord", generated);
        Assert.Contains("public Color3 tint;", generated);
        Assert.Contains("public SlotChannel channel;", generated);
        Assert.Contains("public Inner inner;", generated);
        Assert.Contains("public uint @event;", generated);
        Assert.Contains("ValueParameterKey<SlotChannel> Channel", generated);
        Assert.Contains("ValueParameterKey<SlotRecord> Record", generated);
        Assert.Contains("// No key for View: the enum DebugView has no C# type", generated);
    }

    [Theory]
    [InlineData("Game.Mode", "")]
    [InlineData("Game.Outer+Nested", "")]
    [InlineData("Game.Mode", ", SkipValueCheck = true")]
    public void MappedEnumMatchesItsCSharpEnum(string mappedName, string arguments)
    {
        var shader = $$"""
            namespace Stride.Shaders.Tests.Keys;

            shader KeysGeneratorCheck
            {
                [CSharp("{{mappedName}}"{{arguments}})] enum class Mode : uint { Off, On };
                cbuffer PerDraw { Mode DrawMode; };
            };
            """;
        // C# may have more values than the shaders
        var csharp = """
            namespace Game
            {
                public enum Mode : uint { Off, On, Default = 7 }
                public static class Outer { public enum Nested : uint { Off, On } }
            }
            """;

        var (generated, errors) = KeysGeneratorTests.Build(shader, csharp);

        Assert.Empty(errors);
        Assert.DoesNotContain("enum Mode", generated);
        Assert.Contains($"ValueParameterKey<global::{mappedName.Replace('+', '.')}> DrawMode", generated);
    }

    [Theory]
    [InlineData("enum class Mode : uint { Off, On, Auto };", "SDSL0288: enum Mode doesn't match the C# enum Game.Mode: C# has no value Auto. Change one of them, or write [CSharp(\"Game.Mode\", SkipValueCheck = true)] if the difference is wanted.")]
    [InlineData("enum class Mode : uint { Off, On = 2 };", "SDSL0288: enum Mode doesn't match the C# enum Game.Mode: On is 2 here and 1 in C#.")]
    [InlineData("enum class Mode : int { Off, On };", "SDSL0288: enum Mode doesn't match the C# enum Game.Mode: it is stored as int here and as uint in C#.")]
    public void MappedEnumThatDiffers(string declaration, string expected)
        => AssertGeneratorError($"[CSharp(\"Game.Mode\")] {declaration}", expected, "namespace Game { public enum Mode : uint { Off, On } }");

    [Theory]
    [InlineData("Game.Missing", "SDSL0287: [CSharp(\"Game.Missing\")] on enum Mode: C# has no type of this name. Check the full name (a nested type is written Outer+Inner), and that the project references the assembly that declares it.")]
    [InlineData("Game.NotAnEnum", "SDSL0287: [CSharp(\"Game.NotAnEnum\")] on enum Mode: Game.NotAnEnum is not an enum.")]
    [InlineData("Game.Outer.Nested", "SDSL0287: [CSharp(\"Game.Outer.Nested\")] on enum Mode: C# has no type of this name.")]
    public void MappedEnumThatDoesNotExist(string mappedName, string expected)
        => AssertGeneratorError($"[CSharp(\"{mappedName}\")] enum class Mode : uint {{ Off, On }};", expected,
            "namespace Game { public class NotAnEnum { } public static class Outer { public enum Nested : uint { Off, On } } }");

    [Theory]
    [InlineData("bool enabled;", "SDSL0289: Field enabled of the [CSharp] struct Record has type bool, which C# stores in one byte and a buffer in four. Use a uint.")]
    [InlineData("half weight;", "SDSL0289: Field weight of the [CSharp] struct Record has type half, which buffers don't hold yet. Use float, int or uint, or their vectors.")]
    [InlineData("double weight;", "SDSL0289: Field weight of the [CSharp] struct Record has type double, which buffers don't hold yet.")]
    [InlineData("uint2 ids;", "SDSL0289: Field ids of the [CSharp] struct Record has type uint2, which has no C# type. Use int2, or uint4.")]
    [InlineData("row_major float4x4 transform;", "SDSL0289: Field transform of the [CSharp] struct Record has type float4x4, row_major: C# keeps a Matrix in the default order. Remove row_major.")]
    [InlineData("float3x3 rotation;", "SDSL0289: Field rotation of the [CSharp] struct Record has type float3x3, which has no C# type. Use float4x4, or one vector per row.")]
    [InlineData("float4 values[2];", "SDSL0289: Field values of the [CSharp] struct Record has type float4[2], an array, which has no C# form here. Write one field per element.")]
    [InlineData("Texture2D map;", "SDSL0289: Field map of the [CSharp] struct Record has type Texture2D, which a C# twin can't hold.")]
    public void FieldTypesOfATwin(string field, string expected)
        => AssertGeneratorError($"[CSharp] struct Record {{ {field} }};", expected);

    [Theory]
    [InlineData("enum class Channel : uint { All };", "Channel channel;", "SDSL0290: Field channel of the [CSharp] struct Record has type Channel, but C# has no type Channel. Add [CSharp] to Channel where it is declared, or map it to a C# enum with [CSharp(\"Full.Name\")].")]
    [InlineData("struct Inner { float weight; };", "Inner inner;", "SDSL0290: Field inner of the [CSharp] struct Record has type Inner, but C# has no type Inner. Add [CSharp] to Inner where it is declared.")]
    public void FieldWithoutATwin(string declaration, string field, string expected)
        => AssertGeneratorError($"{declaration} [CSharp] struct Record {{ {field} }};", expected);

    [Theory]
    [InlineData("[CSharp(SkipValueCheck = true)] enum class Mode : uint { Off };", "SDSL0286: [CSharp] on enum Mode: SkipValueCheck is for an enum mapped to a C# enum written by hand, as in [CSharp(\"Full.Name\", SkipValueCheck = true)]: a generated enum has the values of Mode.")]
    [InlineData("[CSharp(\"Game.Record\")] struct Record { float a; };", "SDSL0286: [CSharp] on struct Record: a struct can't be mapped to a C# struct yet. Write [CSharp], and the C# struct is generated.")]
    [InlineData("[CSharp(SkipValueCheck = true)] struct Record { float a; };", "SDSL0286: [CSharp] on struct Record: SkipValueCheck is for an enum mapped to a C# enum.")]
    [InlineData("[CSharp(Strict = true)] enum class Mode : uint { Off };", "SDSL0286: [CSharp] on enum Mode: Strict is not an argument of [CSharp]: it takes the full name of a C# enum, and SkipValueCheck = true.")]
    [InlineData("[CSharp] partial struct Record { float a; };", "SDSL0286: [CSharp] on struct Record: the fields of a partial struct depend on the effect, so it has no C# twin.")]
    [InlineData("[CSharp] [CSharp] enum class Mode : uint { Off };", "SDSL0286: [CSharp] on enum Mode: write [CSharp] once.")]
    [InlineData("[Shared] enum class Mode : uint { Off };", "SDSL0217: Attribute [Shared] on enum Mode does nothing: the compiler only knows [CSharp] there. Check its spelling.")]
    public void WrongArguments(string declaration, string expected)
    {
        AssertGeneratorError(declaration, expected);

        // The class compile says the same, for shaders that no C# project builds
        Write("TwinArguments", $$"""
            shader TwinArguments
            {
                {{declaration}}
            };
            """);
        var message = Assert.Throws<InvalidOperationException>(() => new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL").LoadExternalBuffer(TestShader.FullName("TwinArguments"), [], out _, out _, out _)).Message;
        Assert.Contains("TwinArguments.sdsl(3,", message);
        Assert.True(message.Contains(expected), message);
    }

    // The generator doesn't see its own code: the twins of another file come through the declared names of that file
    [Fact]
    public void TwinsOfAnotherFileOfTheProject()
    {
        var module = """
            namespace Stride.Shaders.Tests.Keys;

            [Pure]
            module KeysGeneratorRecords
            {
                [CSharp] enum class SlotOp : uint { Multiply, Add };
                [CSharp] struct LayerRecord { int maskSlot; uint slotOffset; };
                enum class DebugView : uint { Off, On };
            };
            """;
        var shader = """
            namespace Stride.Shaders.Tests.Keys;

            shader KeysGeneratorCheck
            {
                import KeysGeneratorRecords;
                [CSharp] struct SlotRecord { SlotOp op; LayerRecord layer; };
                cbuffer PerDraw { SlotOp Op; DebugView View; };
            };
            """;
        var csharp = "using Stride.Shaders.Tests.Keys; static class Uses { static SlotRecord Make() => new() { op = SlotOp.Add, layer = new() { slotOffset = 1 } }; }";

        var (generated, errors) = KeysGeneratorTests.Build([("KeysGeneratorRecords.sdsl", module), ("KeysGeneratorCheck.sdsl", shader)], csharp);

        Assert.Empty(errors);
        Assert.Contains("public global::Stride.Shaders.Tests.Keys.SlotOp op;", generated);
        Assert.Contains("ValueParameterKey<global::Stride.Shaders.Tests.Keys.SlotOp> Op", generated);
        // Not the C# Stride.Graphics.Blend of the generated usings either: an enum of the shaders has no key
        Assert.Contains("// No key for View: the enum DebugView has no C# type", generated);

        var withShadersOnlyField = shader.Replace("LayerRecord layer;", "DebugView view;");
        (_, errors) = KeysGeneratorTests.Build([("KeysGeneratorRecords.sdsl", module), ("KeysGeneratorCheck.sdsl", withShadersOnlyField)], "");
        Assert.Contains(errors, x => x.Contains("SDSL0290: Field view of the [CSharp] struct SlotRecord has type DebugView, but C# has no type DebugView."));
    }

    // C# code that uses a twin fails when the shader has an error: the shader error is in the build output too
    [Fact]
    public void ErrorOfATwinIsShownWithTheErrorsOfTheCode()
    {
        var shader = """
            namespace Stride.Shaders.Tests.Keys;

            shader KeysGeneratorCheck
            {
                [CSharp] struct SlotRecord { bool enabled; };
            };
            """;

        var (generated, errors) = KeysGeneratorTests.Build(shader, "using Stride.Shaders.Tests.Keys; static class Uses { static SlotRecord record; }");

        Assert.Equal("// The errors of this file are in the build output\n", generated);
        Assert.Contains(errors, x => x.Contains("KeysGeneratorCheck.sdsl(5,") && x.Contains("SDSL0289"));
        Assert.Contains(errors, x => x.Contains("error CS0246"));
    }

    /// <summary>
    /// For each kind of field, the C# twin has the offsets of the struct in a structured buffer, as Direct3D 12 and Vulkan read it
    /// (Direct3D 11 packs a structured buffer the same way, tight).
    /// </summary>
    [Theory]
    [InlineData(GraphicsPlatform.Direct3D12)]
    [InlineData(GraphicsPlatform.Vulkan)]
    public void TwinHasTheOffsetsOfTheCompiler(GraphicsPlatform platform)
    {
        var shader = """
            namespace Stride.Shaders.Tests.Keys;

            shader TwinLayout
            {
                stream float4 ColorTarget : SV_Target0;

                [CSharp] enum class Channel : uint { All, R };
                [CSharp] enum class Bias : int { Low = -1, High = 1 };
                [CSharp] struct Inner { float weight; float3 direction; };
                [CSharp] struct Record
                {
                    float f;
                    float3 v3;
                    float2 v2;
                    int i;
                    float4 v4;
                    uint u;
                    int3 i3;
                    Channel channel;
                    int2 i2;
                    Bias bias;
                    int4 i4;
                    [Color] float3 color3;
                    uint4 u4;
                    Inner inner;
                    [Color] float4 color4;
                    float4x4 m;
                    float last;
                };

                rgroup PerDraw { StructuredBuffer<Record> Records; }

                void PSMain() { streams.ColorTarget = Records[0].v4; }
            };
            """;

        // The C# twin, compiled and loaded
        var (generated, errors, output) = KeysGeneratorTests.BuildCompilation([("TwinLayout.sdsl", shader)], "");
        Assert.Empty(errors);
        using var stream = new MemoryStream();
        Assert.True(output.Emit(stream).Success);
        var record = Assembly.Load(stream.ToArray()).GetType("Stride.Shaders.Tests.Keys.Record")!;
        var fields = record.GetFields();

        // The struct in the SPIR-V of the effect
        Write("TwinLayout", shader);
        var mixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName));
        mixer.ShaderLoader.LoadExternalBuffer(TestShader.FullName("Stride.Shaders.Tests.Keys.TwinLayout"), [], out _, out _, out _);
        var log = new LoggerResult();
        Assert.True(mixer.MergeSDSL(new ShaderClassSource("Stride.Shaders.Tests.Keys.TwinLayout"), EffectCompiler.GetMixerOptions(platform), log, out var bytecode, out _, out _, out _),
            string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)));
        var (offsets, stride) = StructOffsets(bytecode.ToArray(), "Record");

        Assert.Equal(fields.Select(x => (x.Name, (int)Marshal.OffsetOf(record, x.Name))), fields.Select(x => (x.Name, offsets[x.Name])));
        Assert.Equal(Marshal.SizeOf(record), stride);
    }

    // The member offsets of a struct, and the stride of the structured buffer of it
    private static (Dictionary<string, int> Offsets, int Stride) StructOffsets(byte[] spirv, string structName)
    {
        var words = MemoryMarshal.Cast<byte, uint>(spirv).ToArray();
        var names = new Dictionary<uint, string>();
        var memberNames = new Dictionary<(uint, uint), string>();
        var memberOffsets = new Dictionary<(uint, uint), int>();
        var arrayStrides = new Dictionary<uint, int>();
        var runtimeArrays = new Dictionary<uint, uint>();
        for (var index = 5; index < words.Length; index += (int)(words[index] >> 16))
        {
            var opcode = words[index] & 0xFFFF;
            var operands = words.AsSpan(index + 1, (int)(words[index] >> 16) - 1);
            switch (opcode)
            {
                case 5: // OpName
                    names[operands[0]] = LiteralString(operands[1..]);
                    break;
                case 6: // OpMemberName
                    memberNames[(operands[0], operands[1])] = LiteralString(operands[2..]);
                    break;
                case 71 when operands[1] == 6: // OpDecorate ArrayStride
                    arrayStrides[operands[0]] = (int)operands[2];
                    break;
                case 72 when operands[2] == 35: // OpMemberDecorate Offset
                    memberOffsets[(operands[0], operands[1])] = (int)operands[3];
                    break;
                case 29: // OpTypeRuntimeArray
                    runtimeArrays[operands[0]] = operands[1];
                    break;
            }
        }

        // The struct type is the one with members (an OpName also names variables)
        var structId = names.Where(x => x.Value == structName).Select(x => x.Key).Single(id => memberOffsets.Keys.Any(x => x.Item1 == id));
        var offsets = memberOffsets.Where(x => x.Key.Item1 == structId).ToDictionary(x => memberNames[x.Key], x => x.Value);
        var stride = runtimeArrays.Where(x => x.Value == structId).Select(x => arrayStrides[x.Key]).Distinct().Single();
        return (offsets, stride);

        static string LiteralString(ReadOnlySpan<uint> words)
            => System.Text.Encoding.UTF8.GetString(MemoryMarshal.AsBytes(words)).Split('\0')[0];
    }

    // The declaration is on line 5 of the shader
    private static void AssertGeneratorError(string declaration, string expected, string csharp = "")
    {
        var shader = $$"""
            namespace Stride.Shaders.Tests.Keys;

            shader KeysGeneratorCheck
            {
                {{declaration}}
            };
            """;

        var (_, errors) = KeysGeneratorTests.Build(shader, csharp);

        Assert.True(errors.Any(x => x.Contains("KeysGeneratorCheck.sdsl(5,") && x.Contains(expected)), string.Join(Environment.NewLine, errors));
    }

    private void Write(string name, string code) => File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), code);
}
