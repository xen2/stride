// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Core.Storage;
using Stride.Rendering;
using Stride.Shaders.Compilers.SDSL;
using Stride.Shaders.Spirv.Building;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Mistakes reported when a shader class compiles, before any effect mixes it.
/// </summary>
public class ShaderDefinitionCheckTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateTempSubdirectory("stride-shader-definition");

    public void Dispose()
    {
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string Contract = """
        namespace Stride.Shaders.Tests;

        shader CheckContract
        {
            float4 Compute() { return 0; }
        }
        """;

    [Fact]
    public void UnknownMethodThroughACompositionNamesTheCompositionType()
    {
        Write("CheckContract", Contract);
        Write("CheckRoot", """
            namespace Stride.Shaders.Tests;

            shader CheckRoot
            {
                compose CheckContract contract;
                float4 Shading() { return contract.Missing(); }
            }
            """);

        var (ok, log) = Compile("CheckRoot");
        Assert.False(ok);
        Assert.Contains("SDSL0109: Could not resolve method Missing in type CheckContract", log);
    }

    // SDSL0200: without an inherited method, 'override' starts a new method that calls through a parent never reach
    [Theory]
    [InlineData("override float4 Compute() { return 1; }", null)]
    [InlineData("override float4 Other() { return 1; }", "Error: CheckOverride.sdsl(5,21): SDSL0200: CheckOverride.Other() is marked 'override', but no inherited class declares Other")]
    public void OverrideNeedsAnInheritedMethod(string method, string? expected)
    {
        Write("CheckContract", Contract);
        Write("CheckOverride", $$"""
            namespace Stride.Shaders.Tests;

            shader CheckOverride : CheckContract
            {
                {{method}}
            }
            """);

        var (ok, log) = Compile("CheckOverride");
        AssertMistake(expected, "SDSL0200", ok, log);
    }

    // SDSL0217: a misspelled attribute does nothing on a class or a cbuffer; libraries read attributes of their own
    [Theory]
    [InlineData("[Root] shader CheckAttribute { }", null)]
    [InlineData("[Pure] module CheckAttribute { }", null)]
    [InlineData("[Replaces(Stride.Rendering.ShaderBase)] shader CheckAttribute : Stride.Rendering.ShaderBase { }", null)]
    [InlineData("shader CheckAttribute { [PushConstant] cbuffer Block { module float Value; } }", null)]
    [InlineData("[Summary(\"Blurs\")] [Category(\"Filter\")] shader CheckAttribute { }", null)]
    [InlineData("[Route] shader CheckAttribute { }", null)]
    [InlineData("shader CheckAttribute { [Tags(\"Any\")] cbuffer Block { float Value; } }", null)]
    [InlineData("shader CheckAttribute { [Link(\"Value\")] float Value; }", null)]
    [InlineData("[root] shader CheckAttribute { }", "Error: CheckAttribute.sdsl(3,1): SDSL0217: Attribute [root] on CheckAttribute does nothing: the compiler only knows [Root] and [Pure] and [Replaces] there. Check its spelling.")]
    [InlineData("shader CheckAttribute { [PushConstants] cbuffer Block { float Value; } }", "Error: CheckAttribute.sdsl(3,25): SDSL0217: Attribute [PushConstants] on cbuffer Block does nothing: the compiler only knows [PushConstant] there. Check its spelling.")]
    [InlineData("shader CheckAttribute { [PushConstnt] cbuffer Block { float Value; } }", "Error: CheckAttribute.sdsl(3,25): SDSL0217: Attribute [PushConstnt] on cbuffer Block does nothing: the compiler only knows [PushConstant] there. Check its spelling.")]
    public void MisspelledAttributeOnClassOrCBufferIsReported(string shader, string? expected)
    {
        Write("CheckAttribute", $$"""
            namespace Stride.Shaders.Tests;

            {{shader}}
            """);

        var (ok, log) = Compile("CheckAttribute");
        AssertMistake(expected, "SDSL0217", ok, log);
    }

    // SDSL0176 (from CheckOverriddenMethod): an inherited method of the same name but another signature
    [Fact]
    public void OverrideWithAnotherSignatureIsAnError()
    {
        Write("CheckContract", Contract);
        Write("CheckOverride", """
            namespace Stride.Shaders.Tests;

            shader CheckOverride : CheckContract
            {
                override float4 Compute(float x) { return x; }
            }
            """);

        var (ok, log) = Compile("CheckOverride");
        Assert.False(ok);
        Assert.Contains("SDSL0176: CheckOverride.Compute(float) is marked 'override', but no inherited method has this name and these parameters. Inherited: CheckContract.Compute().", log);
    }

    // SDSL0201: base.X() looks for the previous method of the same chain, so it needs an inherited X
    [Theory]
    [InlineData("override float4 Compute() { return base.Compute() + 1; }", null)]
    [InlineData("float4 Other() { return base.Other(); }", "Error: CheckBase.sdsl(5,34): SDSL0201: base.Other() in CheckBase: no inherited class declares Other()")]
    public void BaseCallNeedsAnInheritedMethod(string method, string? expected)
    {
        Write("CheckContract", Contract);
        Write("CheckBase", $$"""
            namespace Stride.Shaders.Tests;

            shader CheckBase : CheckContract
            {
                {{method}}
            }
            """);

        var (ok, log) = Compile("CheckBase");
        AssertMistake(expected, "SDSL0201", ok, log);
    }

    private const string Generic = """
        namespace Stride.Shaders.Tests;

        shader CheckGeneric<int N, int M> : CheckContract
        {
            override float4 Compute() { return N + M; }
        }
        """;

    // SDSL0202: values past the generic parameters of a parent are ignored
    [Theory]
    [InlineData("CheckGeneric<1, 2>", null)]
    [InlineData("CheckGeneric<1, 2, 3>", "Error: CheckChild.sdsl(3,21): SDSL0202: CheckGeneric has 2 generic parameter(s) (N, M), but 3 given")]
    public void ParentGetsOneValuePerGenericParameter(string parent, string? expected)
    {
        Write("CheckContract", Contract);
        Write("CheckGeneric", Generic);
        Write("CheckChild", $$"""
            namespace Stride.Shaders.Tests;

            shader CheckChild : {{parent}}
            {
            }
            """);

        var (ok, log) = Compile("CheckChild");
        AssertMistake(expected, "SDSL0202", ok, log);
    }

    // SDSL0117: too few values for the generic parameters of a parent
    [Fact]
    public void ParentWithTooFewGenericValuesIsAnError()
    {
        Write("CheckContract", Contract);
        Write("CheckGeneric", Generic);
        Write("CheckChild", """
            namespace Stride.Shaders.Tests;

            shader CheckChild : CheckGeneric<1>
            {
            }
            """);

        var (ok, log) = Compile("CheckChild");
        Assert.False(ok);
        Assert.Contains("SDSL0117: CheckGeneric has 2 generic parameter(s) (N, M), but 1 given.", log);
    }

    // SDSL0117 from a mixin tree, e.g. an effect log written for an older version of the shader
    [Fact]
    public void EffectWithTooFewGenericValuesIsAnError()
    {
        Write("CheckContract", Contract);
        Write("CheckGeneric", Generic);
        Write("CheckMixRoot", """
            namespace Stride.Shaders.Tests;

            shader CheckMixRoot : ComputeShaderBase
            {
                compose CheckContract contract;
                RWBuffer<float4> Out;
                override void Compute() { Out[0] = contract.Compute(); }
            }
            """);

        var source = new ShaderMixinSource
        {
            Mixins = { new ShaderClassSource(TestShader.Written("CheckMixRoot")) },
            Compositions = { ["contract"] = new ShaderMixinSource { Mixins = { new ShaderClassSource(TestShader.Written("CheckGeneric"), "1") } } },
        };
        var log = new LoggerResult();
        var ok = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL")).MergeSDSL(new EffectSource(source), new ShaderMixer.Options(true), log, out _, out _, out _, out _);
        Assert.False(ok);
        Assert.Contains("SDSL0117: CheckGeneric has 2 generic parameter(s) (N, M), but 1 given (1).", string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)));
    }

    // SDSL0203: the effect reflection can only make a float3 or float4 a color
    [Theory]
    [InlineData("cbuffer PerMaterial { [Color] float3 Tint; }", null)]
    [InlineData("cbuffer PerMaterial { [Color] float3 Tints[4]; }", null)]
    [InlineData("cbuffer PerMaterial { [Color] float Tint; }", "Error: CheckColor.sdsl(5,27): SDSL0203: [Color] on Tint, of type float")]
    [InlineData("[Color] float2 Tint;", "Error: CheckColor.sdsl(5,5): SDSL0203: [Color] on Tint, of type float2")]
    public void ColorIsAFloat3OrFloat4(string member, string? expected)
    {
        Write("CheckColor", $$"""
            namespace Stride.Shaders.Tests;

            shader CheckColor
            {
                {{member}}
            }
            """);

        var (ok, log) = Compile("CheckColor");
        AssertMistake(expected, "SDSL0203", ok, log);
    }

    // The warnings are kept with the compiled class: an effect compiled later from the cache reports them too. SDSL0219 is a lint on valid HLSL
    [Fact]
    public void WarningsAreReportedForEachEffectFromTheCache()
    {
        Write("CheckContract", Contract);
        Write("CheckOverride", """
            namespace Stride.Shaders.Tests;

            shader CheckOverride : CheckContract
            {
                override float4 Compute() { return (1, 0, 0, 1); }
            }
            """);

        var loader = new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL");
        foreach (var expectedFromCache in new[] { false, true })
        {
            var log = new LoggerResult();
            loader.Log = log;
            Assert.True(loader.LoadExternalBuffer(TestShader.Written("CheckOverride"), [], out _, out _, out var isFromCache));
            Assert.Equal(expectedFromCache, isFromCache);
            // Once per effect (logger), however many times the class is loaded
            Assert.True(loader.LoadExternalBuffer(TestShader.Written("CheckOverride"), [], out _, out _, out _));
            Assert.Single(log.Messages, m => m.Type == LogMessageType.Warning && m.Text.Contains("SDSL0219"));
        }
    }

    // With WarningsAsErrors (STRIDE_SHADER_WARNINGS_AS_ERRORS), a class with warnings fails to load
    [Fact]
    public void WarningsAsErrorsFailsTheClass()
    {
        Write("CheckContract", Contract);
        Write("CheckOverride", """
            namespace Stride.Shaders.Tests;

            shader CheckOverride : CheckContract
            {
                override float4 Compute() { return (1, 0, 0, 1); }
            }
            """);

        var (ok, log) = Compile("CheckOverride", warningsAsErrors: true);
        Assert.False(ok);
        Assert.Contains("Error: CheckOverride.sdsl(5,", log);
        Assert.Contains("SDSL0219", log);
    }

    // Imported and inherited modules count as inherited classes, so overrides and base calls in the module model don't warn;
    // a module unit is checked like a shader
    [Theory]
    [InlineData("CheckImporter", null)]
    [InlineData("CheckInheritsModule", null)]
    [InlineData("CheckModuleOverride", null)]
    [InlineData("CheckCompanionOverride", null)]
    [InlineData("CheckBadModule", "SDSL0200: CheckBadModule.Missing() is marked 'override'")]
    public void ModulesCountAsInheritedClasses(string name, string? expected)
    {
        Write("CheckModule", """
            namespace Stride.Shaders.Tests;

            module CheckModule
            {
                pipeline float4 Helper() { return 1; }
            }
            """);
        Write("CheckImporter", """
            namespace Stride.Shaders.Tests;

            shader CheckImporter
            {
                import CheckModule;
                override module float4 Helper() { return base.Helper() + 1; }
            }
            """);
        Write("CheckInheritsModule", """
            namespace Stride.Shaders.Tests;

            shader CheckInheritsModule : CheckModule
            {
                override module float4 Helper() { return base.Helper() * 2; }
            }
            """);
        Write("CheckModuleOverride", """
            namespace Stride.Shaders.Tests;

            module CheckModuleOverride
            {
                import CheckModule;
                override float4 Helper() { return base.Helper() * 3; }
            }
            """);
        Write("CheckCompanion", """
            namespace Stride.Shaders.Tests;

            shader CheckCompanion
            {
                module pipeline float4 Knob() { return 0; }
            }
            """);
        Write("CheckCompanionOverride", """
            namespace Stride.Shaders.Tests;

            shader CheckCompanionOverride : CheckCompanion
            {
                override module float4 Knob() { return base.Knob() + 1; }
            }
            """);
        Write("CheckBadModule", """
            namespace Stride.Shaders.Tests;

            module CheckBadModule
            {
                import CheckModule;
                override float4 Missing() { return base.Helper(); }
            }
            """);

        var (ok, log) = Compile(name);
        Assert.Equal(expected == null, ok);
        if (expected == null)
            Assert.DoesNotContain("SDSL02", log);
        else
            Assert.Contains(expected, log);
    }

    // Classes for the module rules: a [Root] module and shader, a module importing the [Root] module, a shader with instance members
    private void WriteRootClasses()
    {
        Write("CheckContract", Contract);
        Write("CheckModule", """
            namespace Stride.Shaders.Tests;

            module CheckModule
            {
                float4 Helper() { return 1; }
            }
            """);
        Write("CheckRootModule", """
            namespace Stride.Shaders.Tests;

            [Root]
            module CheckRootModule
            {
                float4 Entry() { return 0; }
            }
            """);
        Write("CheckBridge", """
            namespace Stride.Shaders.Tests;

            module CheckBridge
            {
                import CheckRootModule;
            }
            """);
        Write("CheckRootShader", """
            namespace Stride.Shaders.Tests;

            [Root]
            shader CheckRootShader
            {
            }
            """);
        Write("CheckImportsRoot", """
            namespace Stride.Shaders.Tests;

            shader CheckImportsRoot
            {
                import CheckRootModule;
            }
            """);
        Write("CheckParent", """
            namespace Stride.Shaders.Tests;

            shader CheckParent
            {
                float Radius;
                module replace float4 Knob() { return 0; }
                float4 Value() { return Radius; }
            }
            """);
    }

    // Root-only: [Root], a shader parent that is root-only, or a direct import of a [Root] module; a module passes nothing on
    [Theory]
    [InlineData("[Root] module CheckRootOnly { }", true)]
    [InlineData("module CheckRootOnly { import CheckRootModule; }", false)]
    [InlineData("shader CheckRootOnly { import CheckRootModule; }", true)]
    [InlineData("shader CheckRootOnly : CheckRootModule { }", true)]
    [InlineData("shader CheckRootOnly { import CheckBridge; }", false)]
    [InlineData("shader CheckRootOnly : CheckRootShader { }", true)]
    [InlineData("shader CheckRootOnly : CheckImportsRoot { }", true)]
    [InlineData("shader CheckRootOnly : CheckParent { }", false)]
    public void RootOnlyFollowsShaderParentsAndDirectImports(string declaration, bool expected)
    {
        WriteRootClasses();
        Write("CheckRootOnly", $"""
            namespace Stride.Shaders.Tests;

            {declaration}
            """);

        var log = new LoggerResult();
        var loader = new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL") { Log = log };
        Assert.True(loader.LoadExternalBuffer(TestShader.Written("CheckRootOnly"), [], out var buffer, out _, out _), string.Join(Environment.NewLine, log.Messages));
        Assert.Equal(expected, Spirv.Building.SpirvBuilder.GetShaderFlags(buffer).HasFlag(Spirv.Specification.ShaderFlagsMask.Root));
    }

    // SDSL0119: a root-only shader in a composition
    [Theory]
    [InlineData("[Root] shader CheckComposed : CheckContract { }", "SDSL0119: CheckComposed can only be mixed at the root of an effect ([Root] on it, or on a class it inherits or imports), but it is in the composition contract.")]
    [InlineData("shader CheckComposed : CheckContract { import CheckRootModule; }", "SDSL0119: CheckComposed can only be mixed at the root")]
    [InlineData("shader CheckComposed : CheckContract { import CheckBridge; }", null)]
    public void RootOnlyShaderInACompositionIsAnError(string declaration, string? expected)
    {
        WriteRootClasses();
        Write("CheckComposed", $"""
            namespace Stride.Shaders.Tests;

            {declaration}
            """);
        Write("CheckMixRoot", """
            namespace Stride.Shaders.Tests;

            shader CheckMixRoot : ComputeShaderBase
            {
                compose CheckContract contract;
                RWBuffer<float4> Out;
                override void Compute() { Out[0] = contract.Compute(); }
            }
            """);

        var source = new ShaderMixinSource
        {
            Mixins = { new ShaderClassSource(TestShader.Written("CheckMixRoot")) },
            Compositions = { ["contract"] = new ShaderMixinSource { Mixins = { new ShaderClassSource(TestShader.Written("CheckComposed")) } } },
        };
        var log = new LoggerResult();
        bool ok;
        try
        {
            ok = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL")).MergeSDSL(new EffectSource(source), new ShaderMixer.Options(true), log, out _, out _, out _, out _);
        }
        catch (InvalidOperationException e)
        {
            ok = false;
            log.Error(e.Message);
        }
        var text = string.Join(Environment.NewLine, log.Messages.Select(m => m.Text));
        if (expected == null)
        {
            Assert.True(ok, text);
        }
        else
        {
            Assert.False(ok);
            Assert.Contains(expected, text);
        }
    }

    // SDSL0247: a module goes to the root, so a call through the slot finds a method by name there
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AModuleInACompositionIsAnError(bool isAssetBuild)
    {
        WriteRootClasses();
        Write("CheckSlotModule", """
            namespace Stride.Shaders.Tests;

            module CheckSlotModule { float4 Compute() { return 1; } }
            """);
        Write("CheckMixRoot", """
            namespace Stride.Shaders.Tests;

            shader CheckMixRoot : ComputeShaderBase
            {
                compose CheckContract contract;
                RWBuffer<float4> Out;
                override void Compute() { Out[0] = contract.Compute(); }
            }
            """);

        var source = new ShaderMixinSource
        {
            Mixins = { new ShaderClassSource(TestShader.Written("CheckMixRoot")) },
            Compositions = { ["contract"] = new ShaderMixinSource { Mixins = { new ShaderClassSource(TestShader.Written("CheckSlotModule")) } } },
        };

        var log = new LoggerResult();
        var ok = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL")) { IsAssetBuild = isAssetBuild }
            .MergeSDSL(new EffectSource(source), new ShaderMixer.Options(true), log, out _, out _, out _, out _);

        Assert.False(ok);
        Assert.Contains(log.Messages, x => x.Text.Contains("SDSL0247: CheckSlotModule is a module, but it is in the composition contract.", StringComparison.Ordinal));
    }

    // SDSL0119 names the composition from the root's slot down, as an effect writes it
    [Fact]
    public void RootOnlyShaderInANestedCompositionNamesThePathFromTheRoot()
    {
        WriteRootClasses();
        Write("CheckComposed", """
            namespace Stride.Shaders.Tests;

            [Root] shader CheckComposed : CheckContract { }
            """);
        Write("CheckOuter", """
            namespace Stride.Shaders.Tests;

            shader CheckOuter : CheckContract
            {
                compose CheckContract inner[];
                override float4 Compute() { return inner[0].Compute(); }
            }
            """);
        Write("CheckMixRoot", """
            namespace Stride.Shaders.Tests;

            shader CheckMixRoot : ComputeShaderBase
            {
                compose CheckContract contract;
                RWBuffer<float4> Out;
                override void Compute() { Out[0] = contract.Compute(); }
            }
            """);

        var inner = new ShaderArraySource();
        inner.Add(new ShaderMixinSource { Mixins = { new ShaderClassSource(TestShader.Written("CheckComposed")) } });
        var source = new ShaderMixinSource
        {
            Mixins = { new ShaderClassSource(TestShader.Written("CheckMixRoot")) },
            Compositions = { ["contract"] = new ShaderMixinSource { Mixins = { new ShaderClassSource(TestShader.Written("CheckOuter")) }, Compositions = { ["inner"] = inner } } },
        };
        var log = new LoggerResult();
        bool ok;
        try
        {
            ok = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL")).MergeSDSL(new EffectSource(source), new ShaderMixer.Options(true), log, out _, out _, out _, out _);
        }
        catch (InvalidOperationException e)
        {
            ok = false;
            log.Error(e.Message);
        }
        Assert.False(ok);
        Assert.Contains("but it is in the composition contract.inner[0].", string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)));
    }

    // SDSL0204: module code runs once per effect, so in a shader used in compositions it can't read one instance's members
    [Theory]
    [InlineData("shader CheckRead", "float Radius; module float4 Shading() { return Radius; }", "Error: CheckRead.sdsl(6,52): SDSL0204: CheckRead.Shading() is module code and reads Radius, an instance member of CheckRead.")]
    [InlineData("shader CheckRead", "cbuffer PerMaterial { float Radius; } module float4 Shading() { return Radius; }", "SDSL0204: CheckRead.Shading() is module code and reads Radius, an instance member of CheckRead.")]
    [InlineData("shader CheckRead", "compose CheckContract color; module float4 Shading() { return color.Compute(); }", "SDSL0204: CheckRead.Shading() is module code and reads color, an instance member of CheckRead.")]
    [InlineData("shader CheckRead : CheckParent", "module float4 Shading() { return Radius; }", "SDSL0204: CheckRead.Shading() is module code and reads Radius, an instance member of CheckParent.")]
    [InlineData("shader CheckRead : CheckParent", "module float4 Shading() { return CheckParent.Radius; }", "SDSL0204: CheckRead.Shading() is module code and reads Radius, an instance member of CheckParent.")]
    [InlineData("shader CheckRead : CheckParent", "float4 Other() { return CheckParent.Radius; }", null)]
    [InlineData("shader CheckRead", "import CheckBridge; float Radius; module float4 Shading() { return Radius; }", "SDSL0204")]
    [InlineData("shader CheckRead", "module float Radius; module float4 Shading() { return Radius; }", null)]
    [InlineData("shader CheckRead", "float Radius; float4 Shading() { return Radius; }", null)]
    [InlineData("shader CheckRead", "float Radius; module float4 Shading() { float Radius = 1; return Radius; }", null)]
    [InlineData("shader CheckRead", "float Radius; module float4 Shading(float Radius) { return Radius; }", null)]
    [InlineData("shader CheckRead", "static const float Radius = 1; module float4 Shading() { return Radius; }", null)]
    [InlineData("[Root] shader CheckRead", "float Radius; module float4 Shading() { return Radius; }", null)]
    [InlineData("shader CheckRead : CheckRootShader", "float Radius; module float4 Shading() { return Radius; }", null)]
    [InlineData("shader CheckRead", "import CheckRootModule; float Radius; module float4 Shading() { return Radius; }", null)]
    public void ModuleCodeReadsNoInstanceMember(string declaration, string members, string? expected)
    {
        WriteRootClasses();
        Write("CheckRead", $$"""
            namespace Stride.Shaders.Tests;

            {{declaration}}
            {
                float4 Unused() { return 0; }
                {{members}}
            }
            """);

        var (ok, log) = Compile("CheckRead");
        AssertMistake(expected, "SDSL0204", ok, log);
    }

    // SDSL0205: module code can't call an instance method of its class or a parent
    [Theory]
    [InlineData("shader CheckCall", "float4 Helper() { return 1; } module float4 Shading() { return Helper(); }", "Error: CheckCall.sdsl(5,68): SDSL0205: CheckCall.Shading() is module code and calls CheckCall.Helper(), an instance method.")]
    [InlineData("shader CheckCall", "float4 Helper() { return 1; } module float4 Shading() { return this.Helper(); }", "SDSL0205: CheckCall.Shading() is module code and calls CheckCall.Helper()")]
    [InlineData("shader CheckCall : CheckParent", "module float4 Shading() { return Value(); }", "SDSL0205: CheckCall.Shading() is module code and calls CheckParent.Value()")]
    [InlineData("shader CheckCall", "module float4 Helper() { return 1; } module float4 Shading() { return Helper(); }", null)]
    [InlineData("shader CheckCall", "float4 Helper() { return 1; } float4 Shading() { return Helper(); }", null)]
    [InlineData("shader CheckCall : CheckParent", "module float4 Shading() { return Knob(); }", null)]
    [InlineData("[Root] shader CheckCall", "float4 Helper() { return 1; } module float4 Shading() { return Helper(); }", null)]
    public void ModuleCodeCallsNoInstanceMethod(string declaration, string members, string? expected)
    {
        WriteRootClasses();
        Write("CheckCall", $$"""
            namespace Stride.Shaders.Tests;

            {{declaration}}
            {
                {{members}}
            }
            """);

        var (ok, log) = Compile("CheckCall");
        AssertMistake(expected, "SDSL0205", ok, log);
    }

    // SDSL0206 / SDSL0207: an override has the kind of the method it overrides (in a composition, they are in different nodes)
    [Theory]
    [InlineData("shader CheckKind : CheckParent", "override float4 Knob() { return 1; }", "Error: CheckKind.sdsl(5,21): SDSL0206: CheckKind.Knob() overrides the module method CheckParent.Knob but is not 'module'.")]
    [InlineData("shader CheckKind", "import CheckModule; override float4 Helper() { return 1; }", "SDSL0206: CheckKind.Helper() overrides the module method CheckModule.Helper but is not 'module'.")]
    [InlineData("shader CheckKind : CheckParent", "override module float4 Value() { return 1; }", "SDSL0207: CheckKind.Value() is 'module' but overrides the instance method CheckParent.Value.")]
    [InlineData("shader CheckKind : CheckParent", "override module float4 Knob() { return 1; }", null)]
    [InlineData("shader CheckKind : CheckParent", "override float4 Value() { return 1; }", null)]
    [InlineData("[Root] shader CheckKind : CheckParent", "override float4 Knob() { return 1; } override module float4 Value() { return 1; }", null)]
    public void OverrideHasTheKindOfItsParent(string declaration, string member, string? expected)
    {
        WriteRootClasses();
        Write("CheckKind", $$"""
            namespace Stride.Shaders.Tests;

            {{declaration}}
            {
                {{member}}
            }
            """);

        var (ok, log) = Compile("CheckKind");
        Assert.Equal(expected == null, ok);
        if (expected == null)
        {
            Assert.DoesNotContain("SDSL0206", log);
            Assert.DoesNotContain("SDSL0207", log);
        }
        else
        {
            Assert.Contains(expected, log);
        }
    }

    // SDSL0208: instance code can't reach a module method through base (the module method is in another node)
    [Theory]
    [InlineData("shader CheckBaseKind : CheckParent", "float4 Other() { return base.Knob(); }", "SDSL0208: base.Knob() in CheckBaseKind.Other(): Knob is a module method, called from instance code.")]
    [InlineData("shader CheckBaseKind : CheckParent", "module float4 Other() { return base.Knob(); }", null)]
    [InlineData("[Root] shader CheckBaseKind : CheckParent", "float4 Other() { return base.Knob(); }", null)]
    public void InstanceCodeCallsNoBaseModuleMethod(string declaration, string member, string? expected)
    {
        WriteRootClasses();
        Write("CheckBaseKind", $$"""
            namespace Stride.Shaders.Tests;

            {{declaration}}
            {
                {{member}}
            }
            """);

        var (ok, log) = Compile("CheckBaseKind");
        AssertMistake(expected, "SDSL0208", ok, log);
    }

    // SDSL0209 / SDSL0210 / SDSL0213: a [Pure] module has functions and constants only, imports pure modules, and runs in any stage
    [Theory]
    [InlineData("static const float Scale = 2; float F(float x) { return abs(x) * Scale; }", null, null)]
    [InlineData("float Scale;", "SDSL0209", "Error: CheckPure.sdsl(6,5): SDSL0209: [Pure] module CheckPure declares the variable Scale: a pure module has functions and constants only.")]
    [InlineData("cbuffer PerFrame { float Time; }", "SDSL0209", "SDSL0209: [Pure] module CheckPure declares PerFrame")]
    [InlineData("Texture2D Tex;", "SDSL0209", "SDSL0209: [Pure] module CheckPure declares the variable Tex")]
    [InlineData("stream float4 Color;", "SDSL0209", "SDSL0209: [Pure] module CheckPure declares the stream Color")]
    [InlineData("import CheckStreams; float4 F() { return streams.Color; }", "SDSL0210", "SDSL0210: [Pure] module CheckPure uses CheckStreams")]
    [InlineData("import CheckModule;", "SDSL0210", "SDSL0210: [Pure] module CheckPure uses CheckModule, which is not a pure module.")]
    [InlineData("float4 F() { return CheckModule.Helper(); }", "SDSL0210", "SDSL0210: [Pure] module CheckPure uses CheckModule")]
    [InlineData("float F() { return Global.Time; }", "SDSL0210", "SDSL0210: [Pure] module CheckPure uses Global")]
    [InlineData("float F(float x) { return CheckPureHelper.Twice(x); }", "SDSL0210", null)]
    [InlineData("import CheckPureHelper; float F(float x) { return Twice(x); }", "SDSL0210", null)]
    [InlineData("float F(float x) { return ddx(x); }", "SDSL0213", "SDSL0213: [Pure] module CheckPure: ddx() in F() works only in a pixel shader")]
    [InlineData("float F(float x) { return fwidth(x); }", "SDSL0213", "SDSL0213: [Pure] module CheckPure: fwidth() in F()")]
    [InlineData("void F(float x) { clip(x); }", "SDSL0213", "SDSL0213: [Pure] module CheckPure: clip() in F()")]
    [InlineData("void F(float x) { if (x < 0) discard; }", "SDSL0213", "SDSL0213: [Pure] module CheckPure: discard in F()")]
    [InlineData("float4 F(Texture2D t, SamplerState s, float2 uv) { return t.Sample(s, uv); }", "SDSL0213", "SDSL0213: [Pure] module CheckPure: Sample() in F()")]
    [InlineData("float4 F(Texture2D t, SamplerState s, float2 uv) { return t.SampleBias(s, uv, 1.0); }", "SDSL0213", "SDSL0213: [Pure] module CheckPure: SampleBias() in F()")]
    [InlineData("float F(Texture2D t, SamplerComparisonState s, float2 uv) { return t.SampleCmp(s, uv, 0.5); }", "SDSL0213", "SDSL0213: [Pure] module CheckPure: SampleCmp() in F()")]
    [InlineData("float F(Texture2D t, SamplerState s, float2 uv) { return t.CalculateLevelOfDetail(s, uv); }", "SDSL0213", "SDSL0213: [Pure] module CheckPure: CalculateLevelOfDetail() in F()")]
    [InlineData("float4 F(Texture2D t, SamplerState s, float2 uv) { return t.SampleLevel(s, uv, 0.0); }", null, null)]
    public void PureModuleHasNoState(string members, string? code, string? expected)
    {
        WriteRootClasses();
        Write("CheckStreams", """
            namespace Stride.Shaders.Tests;

            module CheckStreams
            {
                stream float4 Color;
            }
            """);
        Write("CheckPureHelper", """
            namespace Stride.Shaders.Tests;

            [Pure]
            module CheckPureHelper
            {
                float Twice(float x) { return x * 2; }
            }
            """);
        Write("CheckPure", $$"""
            namespace Stride.Shaders.Tests;

            [Pure]
            module CheckPure
            {
                {{members}}
            }
            """);

        var (ok, log) = Compile("CheckPure");
        if (code == null)
        {
            Assert.True(ok, log);
            Assert.DoesNotContain("SDSL02", log);
        }
        else
        {
            AssertMistake(expected, code, ok, log);
        }
    }

    // Without [Pure], a module may read state and use pixel shader intrinsics
    [Fact]
    public void ModuleWithoutPureIsNotChecked()
    {
        Write("CheckState", """
            namespace Stride.Shaders.Tests;

            module CheckState
            {
                float Scale;
                float F(float x) { return ddx(x) * Scale; }
            }
            """);

        var (ok, log) = Compile("CheckState");
        Assert.True(ok, log);
        Assert.DoesNotContain("SDSL02", log);
    }

    // SDSL0211: only a module can be pure
    [Fact]
    public void PureShaderIsAnError()
    {
        Write("CheckPureShader", """
            namespace Stride.Shaders.Tests;

            [Pure]
            shader CheckPureShader
            {
            }
            """);

        var (ok, log) = Compile("CheckPureShader");
        Assert.False(ok, log);
        Assert.Contains("SDSL0211: [Pure] on shader CheckPureShader: only a module can be pure.", log);
    }

    // SDSL0212: 'clone' is parsed and ignored since Stride 4.4
    [Theory]
    [InlineData("clone float4 F() { return 0; }", "Error: CheckClone.sdsl(5,18): SDSL0212: CheckClone.F(): 'clone' has no effect since Stride 4.4. Remove it.")]
    [InlineData("float4 F() { return 0; }", null)]
    public void CloneIsAnError(string member, string? expected)
    {
        Write("CheckClone", $$"""
            namespace Stride.Shaders.Tests;

            shader CheckClone
            {
                {{member}}
            }
            """);

        var (ok, log) = Compile("CheckClone");
        AssertMistake(expected, "SDSL0212", ok, log);
    }

    private const string Tests = "Stride.Shaders.Tests.";

    // SDSL0146: one file, whichever class comes first
    [Theory]
    [InlineData("shader CycleA : CycleB { } shader CycleB : CycleA { }", "CycleA -> CycleB -> CycleA")]
    [InlineData("shader CycleB : CycleA { } shader CycleA : CycleB { }", "CycleB -> CycleA -> CycleB")]
    [InlineData("shader CycleA : CycleA { }", "CycleA -> CycleA")]
    [InlineData("shader CycleA : Stride.Shaders.Tests.CycleB { } shader CycleB : CycleC { } shader CycleC : CycleA { }", "CycleA -> CycleB -> CycleC -> CycleA")]
    [InlineData("module CycleA { import CycleB; } module CycleB { import CycleA; }", "CycleA -> CycleB -> CycleA")]
    [InlineData("shader CycleA<int N> : CycleB<N> { } shader CycleB<int N> : CycleA<N> { }", "CycleA -> CycleB -> CycleA")]
    public void CycleInOneFileIsAnError(string classes, string cycle)
    {
        Write("CycleA", $"namespace Stride.Shaders.Tests;\n\n{classes}");

        var (ok, log) = Compile("CycleA");
        Assert.False(ok);
        Assert.Contains($"SDSL0146: {Tests}{cycle.Replace(" -> ", " -> " + Tests)}: these classes inherit, import or use each other in a cycle", log);
        Assert.Contains("CycleA.sdsl(3,", log);
    }

    // SDSL0146: two files, from the class asked for
    [Theory]
    [InlineData("shader CycleA : CycleB { }", "shader CycleB : CycleA { }", "CycleA -> CycleB -> CycleA")]
    [InlineData("module CycleA { import CycleB; }", "module CycleB { import CycleA; }", "CycleA -> CycleB -> CycleA")]
    [InlineData("shader CycleA<int N> : CycleB<N> { }", "shader CycleB<int N> : CycleA<N> { }", "CycleA -> CycleB -> CycleA")]
    [InlineData("shader CycleA { float F() { return CycleB.G(); } }", "shader CycleB { float G() { return CycleA.F(); } }", "CycleA -> CycleB -> CycleA")]
    public void CycleInTwoFilesIsAnError(string first, string second, string cycle)
    {
        Write("CycleA", $"namespace Stride.Shaders.Tests;\n\n{first}");
        Write("CycleB", $"namespace Stride.Shaders.Tests;\n\n{second}");

        var (ok, log) = Compile("CycleA");
        Assert.False(ok);
        Assert.Contains($"SDSL0146: {Tests}{cycle.Replace(" -> ", " -> " + Tests)}: these classes inherit, import or use each other in a cycle", log);
    }

    [Fact]
    public void CycleInThreeFilesListsThemAll()
    {
        Write("CycleA", "namespace Stride.Shaders.Tests; shader CycleA : CycleEnter { }");
        Write("CycleEnter", "namespace Stride.Shaders.Tests; shader CycleEnter : CycleB { }");
        Write("CycleB", "namespace Stride.Shaders.Tests; shader CycleB : CycleC { }");
        Write("CycleC", "namespace Stride.Shaders.Tests; shader CycleC : CycleEnter { }");

        // The class that leads to the cycle isn't in it
        var (ok, log) = Compile("CycleA");
        Assert.False(ok);
        Assert.Contains($"SDSL0146: {Tests}CycleEnter -> {Tests}CycleB -> {Tests}CycleC -> {Tests}CycleEnter:", log);
    }

    // SDSL0146: two threads, each compiling one class of the cycle, would wait for each other forever
    [Fact]
    public void CycleOnTwoThreadsIsAnError()
    {
        Write("CycleA", "namespace Stride.Shaders.Tests; shader CycleA : CycleB { }");
        Write("CycleB", "namespace Stride.Shaders.Tests; shader CycleB : CycleA { }");

        var loader = new BothCompilingLoader(sourceDir.FullName);
        var first = Task.Factory.StartNew(() => Assert.Throws<InvalidOperationException>(() => loader.LoadExternalBuffer($"{Tests}CycleA", [], out _, out _, out _)), TaskCreationOptions.LongRunning);
        var second = Task.Factory.StartNew(() => Assert.Throws<InvalidOperationException>(() => loader.LoadExternalBuffer($"{Tests}CycleB", [], out _, out _, out _)), TaskCreationOptions.LongRunning);

        Assert.True(Task.WaitAll([first, second], TimeSpan.FromMinutes(1)), "The two threads wait for each other");
        Assert.Contains("SDSL0146", first.Result.Message);
        Assert.Contains("SDSL0146", second.Result.Message);
    }

    // Both threads hold their class when they go on to its parent
    private sealed class BothCompilingLoader(string path) : ShaderLoader(path)
    {
        private readonly Barrier barrier = new(2);
        private int compiles;

        protected override bool LoadFromCode(string? filename, string code, ObjectId hash, ReadOnlySpan<ShaderMacro> macros, out ShaderBuffers buffer, bool registerInCache = true)
        {
            if (Interlocked.Increment(ref compiles) <= 2)
                barrier.SignalAndWait();
            return base.LoadFromCode(filename, code, hash, macros, out buffer, registerInCache);
        }
    }

    // A class used twice on the way is no cycle
    [Fact]
    public void ClassReachedTwiceIsNoCycle()
    {
        Write("CycleTop", "namespace Stride.Shaders.Tests; shader CycleTop : CycleLeft, CycleRight { }");
        Write("CycleLeft", "namespace Stride.Shaders.Tests; shader CycleLeft : CycleBase { }");
        Write("CycleRight", "namespace Stride.Shaders.Tests; shader CycleRight : CycleBase { }");
        Write("CycleBase", "namespace Stride.Shaders.Tests; shader CycleBase { }");

        var (ok, log) = Compile("CycleTop");
        Assert.True(ok, log);
    }

    // A parent further down the file is no cycle: it compiles first
    [Fact]
    public void ParentLaterInTheFileIsNoCycle()
    {
        Write("CycleA", "namespace Stride.Shaders.Tests; shader CycleHelper : CycleA { float F() { return G(); } } shader CycleA { float G() { return 1; } }");

        var (ok, log) = Compile("CycleA");
        Assert.True(ok, log);
    }

    // A name of another namespace, or one in another class of the file, is not this class
    [Theory]
    [InlineData("namespace Stride.Shaders.Tests; shader CycleA : Other.CycleA { }")]
    [InlineData("namespace Stride.Shaders.Tests; shader CycleHelper { } shader CycleA : CycleHelper { }")]
    public void SameNameElsewhereIsNoCycle(string code)
    {
        Write("CycleA", code);
        Write("Other.CycleA", "namespace Other; shader CycleA { }");

        var (_, log) = Compile("CycleA");
        Assert.DoesNotContain("SDSL0146", log);
    }

    // SDSL0147: a condition runs before generics get their value, so it would always take the same branch
    [Theory]
    [InlineData("int N", "#if N == 3\n#endif", "#if reads N", 5, 1)]
    [InlineData("int N", "#ifdef N\n#endif", "#ifdef reads N", 5, 1)]
    [InlineData("int N", "#ifndef N\n#endif", "#ifndef reads N", 5, 1)]
    [InlineData("int N", "#if defined(N)\n#endif", "#if reads N", 5, 1)]
    [InlineData("int N", "#if defined N\n#endif", "#if reads N", 5, 1)]
    [InlineData("int N", "#if !N\n#endif", "#if reads N", 5, 1)]
    [InlineData("int N", "#if 0\n#elif N\n#endif", "#elif reads N", 6, 1)]
    [InlineData("int N", "#define COUNT N\n#if COUNT == 3\n#endif", "#if reads N", 6, 1)]
    [InlineData("int N", "  #if N\n  #endif", "#if reads N", 5, 3)]
    [InlineData("MemberName N", "#if N == 3\n#endif", "#if reads N", 5, 1)]
    [InlineData("int M, int N", "#if M\n#endif\n#if 0\n\n\n#else\n#ifdef N\n#endif\n#endif", "#ifdef reads N", 11, 1)]
    public void ConditionOnAGenericIsAnError(string parameters, string body, string expected, int line, int column)
    {
        Write("CheckGeneric", $"namespace Stride.Shaders.Tests;\n\nshader CheckGeneric<{parameters}>\n{{\n{body}\n}}");

        var (ok, log) = Compile("CheckGeneric");
        Assert.False(ok);
        Assert.Contains($"CheckGeneric.sdsl({line},{column}): SDSL0147: {expected}, a generic parameter of CheckGeneric.", log);
    }

    // Valid HLSL: only a name that is a generic parameter of the class, and no macro at that point
    [Theory]
    [InlineData("#if OTHER\n#endif")]
    [InlineData("#ifdef OTHER\n#endif")]
    [InlineData("#if defined(OTHER)\n#endif")]
    [InlineData("static const int VALUE = 3;\n#if VALUE\n#endif")]
    [InlineData("#define N 3\n#if N == 3\n#endif")]
    [InlineData("#define N\n#ifdef N\n#endif")]
    [InlineData("#if 0\n#if N\n#endif\n#ifdef N\n#endif\n#endif")]
    [InlineData("#if 1\n#elif N\n#endif")]
    [InlineData("// #if N\n/* #ifdef N */")]
    public void OtherConditionsCompile(string body)
    {
        Write("CheckGeneric", $"namespace Stride.Shaders.Tests;\n\nshader CheckGeneric<int N>\n{{\n{body}\n}}");

        var (ok, log) = Compile("CheckGeneric");
        Assert.True(ok, log);
        Assert.DoesNotContain("SDSL0147", log);
    }

    [Fact]
    public void ConditionOutsideTheClassOfTheGenericCompiles()
    {
        Write("CheckGeneric", """
            namespace Stride.Shaders.Tests;

            #if N
            #endif

            shader CheckGeneric<int N>
            {
            }

            #ifdef N
            #endif

            shader CheckOther
            {
            #if N
            #endif
            }
            """);

        var (ok, log) = Compile("CheckGeneric");
        Assert.True(ok, log);
    }

    private void WriteStreamClasses()
    {
        Write("StA", "namespace Stride.Shaders.Tests; shader StA { stream float tmp; float GetA() { return streams.tmp; } }");
        Write("StB", "namespace Stride.Shaders.Tests; shader StB { stream float tmp; float GetB() { return streams.tmp; } }");
        Write("StMid", "namespace Stride.Shaders.Tests; shader StMid : StA, StB { }");
        Write("StModule", "namespace Stride.Shaders.Tests; module StModule { stream float tmp; }");
        Write("StBase", "namespace Stride.Shaders.Tests; shader StBase { stream float tmp; }");
        Write("StLeft", "namespace Stride.Shaders.Tests; shader StLeft : StBase { }");
        Write("StRight", "namespace Stride.Shaders.Tests; shader StRight : StBase { }");
        Write("StSem1", "namespace Stride.Shaders.Tests; shader StSem1 { stream float tmp : ST_VALUE; }");
        Write("StSem2", "namespace Stride.Shaders.Tests; shader StSem2 { stream float tmp : ST_VALUE; }");
        Write("StOtherSem", "namespace Stride.Shaders.Tests; shader StOtherSem { stream float tmp : ST_OTHER; }");
        Write("StOtherType", "namespace Stride.Shaders.Tests; shader StOtherType { stream float2 tmp : ST_VALUE; }");
        Write("StOtherName", "namespace Stride.Shaders.Tests; shader StOtherName { stream float other : ST_VALUE; }");
        Write("StGeneric", "namespace Stride.Shaders.Tests; shader StGeneric<int N> { stream float tmp; }");
        Write("StGenSem1", "namespace Stride.Shaders.Tests; shader StGenSem1<Semantic S> { stream float tmp : S; }");
        Write("StGenSem2", "namespace Stride.Shaders.Tests; shader StGenSem2<Semantic S> { stream float tmp : S; }");
    }

    // SDSL0152: streams.X in a class that reaches two different streams named X. It is reported on the use: a class that only
    // inherits both harms nothing, each parent uses its own field.
    [Theory]
    [InlineData("StA, StB", "float Get() { return streams.tmp; }", "Error: StCheck.sdsl(5,34): SDSL0152: StCheck sees two streams named tmp, declared by StA and StB. They are separate fields, and streams.tmp means the one of StA (the class's own, else the first inherited or imported). Rename one of them.")]
    [InlineData("StB, StA", "void Set() { streams.tmp = 1; }", "(5,26): SDSL0152: StCheck sees two streams named tmp, declared by StB and StA.")]
    [InlineData("StA, StB", "float Get() { Streams copy = streams; return copy.tmp; }", "(5,55): SDSL0152: StCheck sees two streams named tmp, declared by StA and StB.")]
    [InlineData("StMid", "float Get() { return streams.tmp; }", "(5,34): SDSL0152: StCheck sees two streams named tmp, declared by StA and StB.")]
    [InlineData("StA, StOtherSem", "float Get() { return streams.tmp; }", "SDSL0152: StCheck sees two streams named tmp, declared by StA and StOtherSem.")]
    [InlineData("StSem1, StOtherSem", "float Get() { return streams.tmp; }", "SDSL0152: StCheck sees two streams named tmp, declared by StSem1 and StOtherSem.")]
    [InlineData("StSem1, StOtherType", "float Get() { return streams.tmp; }", "SDSL0152: StCheck sees two streams named tmp, declared by StSem1 and StOtherType.")]
    [InlineData("StGeneric<1>, StGeneric<2>", "float Get() { return streams.tmp; }", "SDSL0152: StCheck sees two streams named tmp, declared by StGeneric<1> and StGeneric<2>.")]
    [InlineData("StGenSem1<ST_A>, StGenSem2<ST_B>", "float Get() { return streams.tmp; }", "SDSL0152: StCheck sees two streams named tmp, declared by StGenSem1<\"ST_A\"> and StGenSem2<\"ST_B\">.")]
    // Inheriting both, with no use
    [InlineData("StA, StB", "float Get() { return GetA() + GetB(); }", null)]
    [InlineData("StMid", "float Get() { return GetA(); }", null)]
    // One declaration reached twice
    [InlineData("StLeft, StRight", "float Get() { return streams.tmp; }", null)]
    // One semantic and one type: one field
    [InlineData("StSem1, StSem2", "float Get() { return streams.tmp; }", null)]
    [InlineData("StGenSem1<ST_A>, StGenSem2<ST_A>", "float Get() { return streams.tmp; }", null)]
    // Two names and one semantic: one field, and each name reaches one declaration
    [InlineData("StSem1, StOtherName", "float Get() { return streams.tmp + streams.other; }", null)]
    public void StreamNameReachedTwice(string parents, string body, string? expected)
    {
        WriteStreamClasses();
        Write("StCheck", $$"""
            namespace Stride.Shaders.Tests;

            shader StCheck : {{parents}}
            {
                {{body}}
            }
            """);

        var (ok, log) = Compile("StCheck");
        AssertMistake(expected, "SDSL0152", ok, log);
    }

    [Fact]
    public void StreamInheritedAndImportedIsReachedTwice()
    {
        WriteStreamClasses();
        Write("StCheck", """
            namespace Stride.Shaders.Tests;

            shader StCheck : StA
            {
                import StModule;
                float Get() { return streams.tmp; }
            }
            """);

        var (ok, log) = Compile("StCheck");
        Assert.False(ok);
        Assert.Contains("(6,34): SDSL0152: StCheck sees two streams named tmp, declared by ", log);
        Assert.Contains("StA", log);
        Assert.Contains("StModule", log);
    }

    // SDSL0152 on a stream the class declares with the name of one it reaches. It is reported on the declaration, with or
    // without a use: the class hides the other field.
    [Theory]
    [InlineData("StA", "stream float tmp;", "(5,18): SDSL0152: StCheck sees two streams named tmp, declared by StCheck and StA. They are separate fields, and streams.tmp means the one of StCheck (the class's own, else the first inherited or imported). Rename one of them.")]
    [InlineData("StA", "stream float tmp; float Get() { return streams.tmp; }", "(5,18): SDSL0152: StCheck sees two streams named tmp, declared by StCheck and StA.")]
    [InlineData("StMid", "stream float tmp;", "(5,18): SDSL0152: StCheck sees two streams named tmp, declared by StCheck and ")]
    // Another semantic, or the same one on another type: two fields
    [InlineData("StSem1", "stream float tmp;", "SDSL0152: StCheck sees two streams named tmp, declared by StCheck and StSem1.")]
    [InlineData("StA", "stream float tmp : ST_A;", "SDSL0152: StCheck sees two streams named tmp, declared by StCheck and StA.")]
    [InlineData("StSem1", "stream float tmp : ST_OTHER;", "SDSL0152: StCheck sees two streams named tmp, declared by StCheck and StSem1.")]
    [InlineData("StSem1", "stream float4 tmp : ST_VALUE;", "SDSL0152: StCheck sees two streams named tmp, declared by StCheck and StSem1.")]
    // One semantic and one type: one field
    [InlineData("StSem1", "stream float tmp : ST_VALUE; float Get() { return streams.tmp; }", null)]
    [InlineData("StGenSem1<ST_A>", "stream float tmp : ST_A; float Get() { return streams.tmp; }", null)]
    // Another name
    [InlineData("StA", "stream float other; float Get() { return streams.tmp + streams.other; }", null)]
    public void StreamDeclaredWithTheNameOfOneItReaches(string parents, string body, string? expected)
    {
        WriteStreamClasses();
        Write("StCheck", $$"""
            namespace Stride.Shaders.Tests;

            shader StCheck : {{parents}}
            {
                {{body}}
            }
            """);

        var (ok, log) = Compile("StCheck");
        AssertMistake(expected, "SDSL0152", ok, log);
    }

    [Fact]
    public void StreamDeclaredWithTheNameOfAnImportedOne()
    {
        WriteStreamClasses();
        Write("StCheck", """
            namespace Stride.Shaders.Tests;

            shader StCheck
            {
                import StModule;
                stream float tmp;
            }
            """);

        var (ok, log) = Compile("StCheck");
        Assert.False(ok);
        Assert.Contains("(6,18): SDSL0152: StCheck sees two streams named tmp, declared by StCheck and ", log);
        Assert.Contains("StModule", log);
    }

    [Fact]
    public void StreamHiddenByAParentStopsItsChild()
    {
        WriteStreamClasses();
        Write("StHides", "namespace Stride.Shaders.Tests; shader StHides : StA { stream float tmp; }");
        Write("StCheck", """
            namespace Stride.Shaders.Tests;

            shader StCheck : StHides
            {
                float Get() { return streams.tmp; }
            }
            """);

        var (ok, log) = Compile("StCheck");
        Assert.False(ok);
        Assert.Contains("SDSL0152: StHides sees two streams named tmp, declared by StHides and StA.", log);
    }

    // The semantic is a generic of the class, and the parent gets that generic: one field whatever the value
    [Fact]
    public void StreamDeclaredWithASemanticFromAGenericOfTheClass()
    {
        WriteStreamClasses();
        Write("StOpen", """
            namespace Stride.Shaders.Tests;

            shader StOpen<Semantic S> : StGenSem1<S>
            {
                stream float tmp : S;
                float Get() { return streams.tmp; }
            }
            """);

        var (ok, log) = Compile("StOpen");
        Assert.True(ok, log);
    }

    // A semantic from a generic of the class being compiled: one value for both parents, so one field
    [Fact]
    public void StreamSemanticFromAGenericOfTheChild()
    {
        WriteStreamClasses();
        Write("StOpen", """
            namespace Stride.Shaders.Tests;

            shader StOpen<Semantic S> : StGenSem1<S>, StGenSem2<S>
            {
                float Get() { return streams.tmp; }
            }
            """);

        var (ok, log) = Compile("StOpen");
        Assert.True(ok, log);
    }

    // The same name in classes of one mix that don't know each other stays legal: each class reaches one stream
    [Fact]
    public void StreamNameInUnrelatedClassesOfOneMix()
    {
        WriteStreamClasses();
        Write("StRoot", """
            namespace Stride.Shaders.Tests;

            shader StRoot
            {
                stream float4 Position : POSITION;
                stream float4 ShadingPosition : SV_Position;
                stream float4 ColorTarget : SV_Target0;
                void VSMain() { streams.ShadingPosition = streams.Position; }
                void PSMain() { streams.ColorTarget = 1; }
            }
            """);

        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource(TestShader.Written("StRoot")), new ShaderClassSource(TestShader.Written("StA")), new ShaderClassSource(TestShader.Written("StB")) } };
        var log = new LoggerResult();
        new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL")).MergeSDSL(new EffectSource(source), new ShaderMixer.Options(true), log, out _, out _, out _, out _);
        Assert.False(log.HasErrors, string.Join(Environment.NewLine, log.Messages));
    }

    // SDSL0153: a class in front of a stream's name. streams.Class.name stopped with "Operation is not valid"
    [Theory]
    [InlineData("return streams.StA.tmp;", "(5,34): SDSL0153: streams.StA.tmp: StA is a class, not a stream. A stream is named without its class: streams.name. If two classes declare a stream of one name, rename one of them.")]
    [InlineData("streams.StB.tmp = 1; return 0;", "(5,27): SDSL0153: streams.StB.tmp: StB is a class, not a stream.")]
    [InlineData("Streams copy = streams; return copy.StA.tmp;", "(5,55): SDSL0153: copy.StA.tmp: StA is a class, not a stream.")]
    public void StreamWithItsClassInFront(string body, string expected)
    {
        WriteStreamClasses();
        Write("StCheck", $$"""
            namespace Stride.Shaders.Tests;

            shader StCheck : StA, StB
            {
                float Get() { {{body}} }
            }
            """);

        var (ok, log) = Compile("StCheck");
        Assert.False(ok);
        Assert.Contains(expected, log);
        Assert.DoesNotContain("Operation is not valid", log);
    }

    // A mistake doesn't compile, and code without it compiles with no mention of it
    private static void AssertMistake(string? expected, string code, bool ok, string log)
    {
        Assert.Equal(expected == null, ok);
        if (expected == null)
            Assert.DoesNotContain(code, log);
        else
            Assert.Contains(expected, log);
    }

    // Compiles one class alone (and the classes it uses), like the first effect that needs it would
    private (bool Ok, string Log) Compile(string name, bool warningsAsErrors = false)
    {
        var log = new LoggerResult();
        var loader = new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL") { Log = log, WarningsAsErrors = warningsAsErrors };
        bool ok;
        try
        {
            ok = loader.LoadExternalBuffer(TestShader.Written(name), [], out _, out _, out _);
        }
        catch (InvalidOperationException)
        {
            ok = false;
        }
        // File names without their folder, so the messages read the same on every machine
        return (ok, string.Join(Environment.NewLine, log.Messages.Select(m => $"{m.Type}: {m.Text.Replace(sourceDir.FullName + Path.DirectorySeparatorChar, "").Replace(sourceDir.FullName + "/", "")}")));
    }

    private void Write(string name, string code)
        => File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), code);
}
