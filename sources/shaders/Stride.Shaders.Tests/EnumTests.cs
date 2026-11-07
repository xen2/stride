// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Graphics;
using Stride.Shaders.Compiler;
using Stride.Shaders.Compilers.SDSL;
using Spv = Stride.Shaders.Spirv.Tools.Spv;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// <c>enum class X : uint { A, B }</c>: values written <c>X.A</c>, which convert only with a cast and compare with == and !=.
/// The mixer makes an enum its int or uint. Every mistake is an error (SDSL0270 to SDSL0285).
/// </summary>
public class EnumTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateTempSubdirectory("stride-enums");

    public void Dispose()
    {
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(GraphicsPlatform.Direct3D11)]
    [InlineData(GraphicsPlatform.Direct3D12)]
    [InlineData(GraphicsPlatform.Vulkan)]
    public void EnumIsAUintEverywhereItCanBe(GraphicsPlatform platform)
    {
        Write("EnumMix", """
            shader EnumMix
            {
                enum class Channel : uint { All, R, G = 5, B, A = 0x10 };
                enum class Signed : int { Low = -2, Zero = 0, High = 7 };
                struct Slot { float4 constant; Channel channel; Signed bias; };

                stream float4 ColorTarget : SV_Target0;
                stream Channel PickedChannel;
                static const Channel Default = Channel.G;

                rgroup PerMaterial { StructuredBuffer<Slot> Slots; StructuredBuffer<Channel> Channels; }
                cbuffer PerDraw { Channel DrawChannel; uint Index; };
                [PushConstant] cbuffer PerObject { module Channel PushChannel; };

                float4 Select(float4 value, Channel channel)
                {
                    switch (channel)
                    {
                        case Channel.All: return value;
                        case Channel.R: return value.rrrr;
                        case Channel.G: return value.gggg;
                        case Channel.B: return value.bbbb;
                        case Channel.A: return value.aaaa;
                    }
                    return value;
                }

                Channel Next(Channel channel, inout uint count)
                {
                    count++;
                    return channel == Channel.A ? Channel.All : (Channel)((uint)channel + 1);
                }

                void VSMain() { streams.PickedChannel = Default; }

                void PSMain()
                {
                    var slot = Slots[Index];
                    uint count = 0;
                    var channel = Next(slot.channel, count);
                    if (channel != DrawChannel && Channels[0] == PushChannel && slot.bias != Signed.Low)
                        channel = streams.PickedChannel;
                    streams.ColorTarget = Select(slot.constant, channel) * float((uint)channel) + (float)slot.bias + float(count);
                }
            };
            """);

        var (ok, log, bytecode, reflection) = Mix("EnumMix", platform);
        Assert.True(ok, log);

        // The backends see a uint, and the reflection too
        var drawChannel = reflection.ConstantBuffers.SelectMany(x => x.Members).Single(x => x.RawName.EndsWith("DrawChannel"));
        Assert.Equal(EffectParameterType.UInt, drawChannel.Type.Type);
        Assert.Equal(EffectParameterClass.Scalar, drawChannel.Type.Class);
        Assert.Equal(EffectParameterType.UInt, reflection.PushConstants!.Members.Single().Type.Type);

        var validation = Spv.ValidateBinary(bytecode, targetVulkan: platform == GraphicsPlatform.Vulkan);
        Assert.True(validation.IsValid, validation.Output);
    }

    [Fact]
    public void EnumOfAModuleKeepsItsRulesInTheClassesThatUseIt()
    {
        Write("EnumModule", """
            [Pure]
            module EnumModule
            {
                enum class Op : uint { Multiply, Add };
                static const Op DefaultOp = Op.Add;
                float Apply(Op op, float a, float b) { return op == Op.Add ? a + b : a * b; }
            };
            """);
        Write("EnumParent", """
            shader EnumParent
            {
                enum class Mode : int { Off = -1, On = 1 };
                stream float4 ColorTarget : SV_Target0;
                cbuffer PerDraw { Mode DrawMode; };
            };
            """);
        Write("EnumUser", """
            shader EnumUser : EnumParent
            {
                import EnumModule;

                float Pick(Mode mode)
                {
                    switch (mode)
                    {
                        case Mode.Off: return 0;
                        case Mode.On: return EnumModule.Apply(EnumModule.DefaultOp, 1, 2);
                        default: return EnumModule.Apply(Op.Multiply, 3, 4);
                    }
                }

                void PSMain() { streams.ColorTarget = Pick(DrawMode); }
            };
            """);

        var (ok, log, bytecode, _) = Mix("EnumUser", GraphicsPlatform.Vulkan);
        Assert.True(ok, log);
        var validation = Spv.ValidateBinary(bytecode, targetVulkan: true);
        Assert.True(validation.IsValid, validation.Output);

        // A class that uses the enum of another class still applies its rules
        Write("EnumMisuse", """
            shader EnumMisuse : EnumParent
            {
                import EnumModule;
                void PSMain() { uint value = EnumModule.DefaultOp; streams.ColorTarget = value; }
            };
            """);
        var message = CompileError("EnumMisuse");
        Assert.Contains("EnumMisuse.sdsl(4,", message);
        Assert.Contains("SDSL0275: A value of type Op can't become uint by itself. Cast it: (uint)value.", message);
    }

    [Fact]
    public void PlainEnum()
        => AssertDeclarationError("enum Channel : uint { All, R };", "SDSL0270: enum Channel is not an enum class. Write enum class Channel : uint (or : int): its values are written Channel.Value and never become numbers by themselves.");

    [Theory]
    [InlineData("enum class Channel { All, R };", "SDSL0271: enum class Channel has no underlying type. Write enum class Channel : uint or : int: an enum is stored as a 32-bit int or uint, the same on every API.")]
    [InlineData("enum class Channel : float { All, R };", "SDSL0271: enum class Channel has the underlying type float.")]
    [InlineData("enum class Channel : uint16_t { All, R };", "SDSL0271: enum class Channel has the underlying type uint16_t.")]
    public void UnderlyingTypeIsIntOrUint(string declaration, string expected)
        => AssertDeclarationError(declaration, expected);

    [Theory]
    [InlineData("enum class Channel : uint { All = -1 };", "SDSL0272: Channel.All = -1: the value of an enum is an integer literal from 0 to 4294967295 (uint).")]
    [InlineData("enum class Channel : int { All = 0x80000000 };", "SDSL0272: Channel.All = 0x80000000: the value of an enum is an integer literal from -2147483648 to 2147483647 (int).")]
    [InlineData("enum class Channel : uint { All = 1.5 };", "SDSL0272: Channel.All = 1.5:")]
    [InlineData("enum class Channel : uint { All = 1 << 2 };", "SDSL0272: Channel.All = ")]
    [InlineData("enum class Channel : uint { All = 4294967295, R };", "SDSL0272: Channel.R = 4294967296:")]
    public void ValueIsAnIntegerLiteralInRange(string declaration, string expected)
        => AssertDeclarationError(declaration, expected);

    [Fact]
    public void TwoValuesWithOneNumber()
        => AssertDeclarationError("enum class Channel : uint { All, R = 0 };", "SDSL0273: Channel.R is 0, like Channel.All. Give each value of an enum its own number.");

    [Theory]
    [InlineData("enum class Channel : uint { All, All };", "SDSL0274: Channel.All is declared twice (in Channel). Rename one of them.")]
    [InlineData("enum class Channel : uint { All }; enum class Channel : int { R };", "SDSL0274: Channel is declared twice (in EnumDeclaration). Rename one of them.")]
    [InlineData("struct Channel { float a; }; enum class Channel : int { R };", "SDSL0274: Channel is declared twice (in EnumDeclaration). Rename one of them.")]
    public void DeclaredTwice(string declaration, string expected)
        => AssertDeclarationError(declaration, expected);

    [Theory]
    [InlineData("uint value = Channel.R;", "SDSL0275: A value of type Channel can't become uint by itself. Cast it: (uint)value.")]
    [InlineData("Channel value = 1;", "SDSL0275: A value of type int can't become Channel by itself. Cast it: (Channel)value.")]
    [InlineData("Channel value = Channel.R; if (value) return;", "SDSL0275: A value of type Channel can't become bool by itself.")]
    [InlineData("Channel value = Channel.R; float x = value ? 1 : 0;", "SDSL0275: A value of type Channel can't become bool by itself.")]
    [InlineData("float values[2] = { 0, 1 }; float x = values[Channel.R];", "SDSL0275: A value of type Channel can't become uint by itself.")]
    [InlineData("bool same = Channel.R == 1u;", "SDSL0275: A value of type uint can't become Channel by itself.")]
    [InlineData("bool same = Channel.R == Other.X;", "SDSL0275: A value of type Other can't become Channel by itself.")]
    [InlineData("float x = Pick(1u);", "SDSL0161: Argument 1 of the call to Pick(Channel) has type uint, which can't be converted to Channel.")]
    public void NoImplicitConversion(string statement, string expected)
        => AssertStatementError(statement, expected);

    [Theory]
    [InlineData("bool less = Channel.R < Channel.G;", "SDSL0276: Operator < doesn't apply to the enum Channel. An enum takes == and != with a value of the same enum; for flags or arithmetic, use a uint, and cast: (uint)value.")]
    [InlineData("var both = Channel.R | Channel.G;", "SDSL0276: Operator | doesn't apply to the enum Channel.")]
    [InlineData("var next = Channel.R + 1;", "SDSL0276: Operator + doesn't apply to the enum Channel.")]
    [InlineData("var negated = -Channel.R;", "SDSL0276: Operator - doesn't apply to the enum Channel.")]
    [InlineData("Channel value = Channel.R; value++;", "SDSL0276: Operator ++ doesn't apply to the enum Channel.")]
    [InlineData("Channel value = Channel.R; value += Channel.G;", "SDSL0276: Operator + doesn't apply to the enum Channel.")]
    public void OnlyEqualityOperators(string statement, string expected)
        => AssertStatementError(statement, expected);

    [Theory]
    [InlineData("Other value = (Other)Channel.R;", "SDSL0277: Can't cast Channel to Other: cast through the type a value is stored as: (Other)(uint)value.")]
    [InlineData("Channel value = (Channel)1.5f;", "SDSL0277: Can't cast float to Channel: an enum is cast from an int or a uint: (Channel)(uint)value.")]
    [InlineData("float2 value = (float2)Channel.R;", "SDSL0277: Can't cast Channel to float2: an enum is cast to a scalar number, such as uint or float.")]
    [InlineData("bool value = (bool)Channel.R;", "SDSL0277: Can't cast Channel to bool:")]
    public void CastsBetweenAnEnumAndANumber(string statement, string expected)
        => AssertStatementError(statement, expected);

    [Fact]
    public void ScopeOperatorNamesTheDot()
    {
        var message = CompileError("EnumScope", """
            shader EnumScope
            {
                enum class Channel : uint { All, R };
                uint Pick() { return (uint)Channel::R; }
            };
            """);
        Assert.Contains("EnumScope.sdsl(4,", message);
        Assert.Contains("SDSL0278: Channel::R: '::' is not part of the language. Write Channel.R.", message);
    }

    [Theory]
    [InlineData("uint Channel = 1; bool same = Channel.R == Channel.G;", "SDSL0279: Channel is both the enum Channel and the variable Channel here, so Channel.R could mean either. Rename the variable Channel.")]
    public void EnumAndVariableOfOneName(string statement, string expected)
        => AssertStatementError(statement, expected);

    [Fact]
    public void ValueThatDoesNotExist()
        => AssertStatementError("var value = Channel.Q;", "SDSL0280: The enum Channel has no value Q. Its values: All, R, G.");

    [Fact]
    public void EnumGenericParameter()
    {
        var message = CompileError("EnumGeneric", """
            shader EnumGeneric<Channel C>
            {
                enum class Channel : uint { All, R };
            };
            """);
        Assert.Contains("EnumGeneric.sdsl(1,", message);
        Assert.Contains("SDSL0283: EnumGeneric declares generic parameter C of the enum type Channel, which is not supported yet. Declare <int C> or <uint C>, and cast it where it is used: (Channel)C.", message);
    }

    [Fact]
    public void EnumOutsideAShader()
    {
        Write("EnumOutside", """
            namespace Test
            {
                enum class Channel : uint { All, R };
                shader EnumOutside { };
            }
            """);
        var message = CompileError("Test.EnumOutside");
        Assert.Contains("EnumOutside.sdsl(3,", message);
        Assert.Contains("SDSL0284: enum Channel is outside a shader. An enum is declared in a shader or a module, like a struct, and classes that inherit or import it see it.", message);
    }

    [Theory]
    [InlineData("Buffer<Channel> Values;", "SDSL0285: Buffer<Channel>: an enum can't be the element of a typed buffer or a texture. Use Buffer<uint>, and cast what you read: (Channel)value.")]
    [InlineData("Texture2D<Channel> Values;", "SDSL0285: Texture2D<Channel>:")]
    [InlineData("RWBuffer<Channel> Values;", "SDSL0285: RWBuffer<Channel>:")]
    public void EnumIsNotTheElementOfATypedBuffer(string declaration, string expected)
        => AssertDeclarationError($"enum class Channel : uint {{ All, R }}; {declaration}", expected);

    // Atomics take an int or a uint: an enum is no number
    [Fact]
    public void NoAtomicOnAnEnum()
    {
        var message = CompileError("EnumAtomic", """
            shader EnumAtomic
            {
                enum class Channel : uint { All, R };
                rgroup PerDraw { RWStructuredBuffer<Channel> Values; }
                void Run() { uint previous; InterlockedAdd(Values[0], 1u, previous); }
            };
            """);
        Assert.Contains("EnumAtomic.sdsl(5,", message);
        Assert.Contains("SDSL0162: No overload of InterlockedAdd(Channel, uint, uint) can take these arguments", message);
    }

    [Fact]
    public void EnumIsAKeyword()
    {
        var message = CompileError("EnumKeyword", """
            shader EnumKeyword
            {
                uint Pick() { uint enum = 3; return enum; }
            };
            """);
        Assert.Contains("EnumKeyword.sdsl(3,", message);
        Assert.Contains("SDSL0048: 'enum' is a reserved word and can't be a name.", message);
    }

    // The declaration is on line 3 of the shader
    private void AssertDeclarationError(string declaration, string expected)
    {
        Write("EnumDeclaration", $$"""
            shader EnumDeclaration
            {
                {{declaration}}
            };
            """);
        var message = CompileError("EnumDeclaration");
        Assert.Contains("EnumDeclaration.sdsl(3,", message);
        Assert.True(message.Contains(expected), message);
    }

    // The statement is on line 8 of the shader, in a method with the enums Channel and Other
    private void AssertStatementError(string statement, string expected)
    {
        Write("EnumStatement", $$"""
            shader EnumStatement
            {
                enum class Channel : uint { All, R, G };
                enum class Other : uint { X };
                float Pick(Channel channel) { return 0; }
                void Run()
                {
                    {{statement}}
                }
            };
            """);
        var message = CompileError("EnumStatement");
        Assert.Contains("EnumStatement.sdsl(8,", message);
        Assert.True(message.Contains(expected), message);
    }

    private string CompileError(string name, string code)
    {
        Write(name, code);
        return CompileError(name);
    }

    private string CompileError(string name)
        => Assert.Throws<InvalidOperationException>(() => new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL").LoadExternalBuffer(TestShader.FullName(name), [], out _, out _, out _)).Message;

    private void Write(string name, string code) => File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), code);

    private (bool Ok, string Log, byte[] Bytecode, EffectReflection Reflection) Mix(string shaderName, GraphicsPlatform platform)
    {
        var mixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName));
        mixer.ShaderLoader.LoadExternalBuffer(TestShader.FullName(shaderName), [], out _, out _, out _);
        var log = new LoggerResult();
        var ok = mixer.MergeSDSL(new ShaderClassSource(TestShader.FullName(shaderName)), EffectCompiler.GetMixerOptions(platform), log, out var bytecode, out var reflection, out _, out _);
        return (ok && !log.HasErrors, string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)), bytecode.ToArray(), reflection);
    }
}
