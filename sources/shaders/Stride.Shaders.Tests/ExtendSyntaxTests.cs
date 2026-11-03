// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Shaders.Compilers.SDSL;
using Stride.Shaders.Parsing.Upgrade;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// The target syntax of extension points: <c>extend</c>, <c>next</c> and <c>patch</c>, and the upgrader step that writes them. What the
/// running order gives is checked by the render test <c>ExtensionPointExtend</c>.
/// </summary>
public class ExtendSyntaxTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"extend-src-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string Owner = """
        module Owner
        {
            stream float Steps;

            [Pipeline(Early, Default, A, B)]
            void Run() { streams.Steps = 1; }

            [Pipeline(Early, Default, A)]
            void RunWith(float value, inout float other) { other = value; }

            [Around(Outer, Inner)]
            float Wrap() { return 1; }

            [Replace]
            float Pick(float value) { return value; }

            float Plain() { return 1; }
        }
        """;

    private void Write(string name, string code) => File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), $"namespace Test;\n{code}\n");

    private void WriteModule(string name, string body) => Write(name, $"module {name} {{ import Owner; {body} }}");

    private (bool Ok, string Log, string Explanation) Mix(bool isAssetBuild, params string[] classes)
    {
        Write("Owner", Owner);
        Write("Entry", "module Entry { stream float4 ColorTarget : SV_Target0; void PSMain() { streams.ColorTarget = 0; } }");
        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource("Test.Entry") } };
        foreach (var name in classes)
            source.Mixins.Add(new ShaderClassSource(name));

        var log = new LoggerResult();
        var mixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL") { WarningsAsErrors = false }) { CollectExplanation = true, IsAssetBuild = isAssetBuild };
        bool ok;
        try
        {
            ok = mixer.MergeSDSL(new EffectSource(source), new ShaderMixer.Options(true), log, out _, out _, out _, out _);
        }
        catch (InvalidOperationException e)
        {
            ok = false;
            log.Error(e.Message);
        }
        return (ok, string.Join(Environment.NewLine, log.Messages.Select(x => $"{x.Type}: {x.Text}")), mixer.Explanation ?? "");
    }

    private static string[] Block(string explanation, string method)
    {
        var lines = explanation.Split('\n').Select(x => x.TrimEnd()).ToList();
        var start = lines.FindIndex(x => x.Contains($"method {method}("));
        return lines.Skip(start).TakeWhile((x, i) => i == 0 || x.StartsWith("    ", StringComparison.Ordinal)).Select(x => x.Trim()).ToArray();
    }

    [Fact]
    public void AnExtendIsPlacedByItsPhase()
    {
        WriteModule("StepA", "extend Owner.Run in A { streams.Steps = streams.Steps + 1; }");
        WriteModule("StepEarly", "extend Owner.Run in Early { streams.Steps = 3; }");

        var (ok, log, explanation) = Mix(false, "Test.StepA", "Test.StepEarly");

        Assert.True(ok, log);
        Assert.Equal(["module method Run() -> void = Owner > StepA > StepEarly", "pipeline", "Early: StepEarly", "Default: Owner", "A: StepA", "B: (none)"], Block(explanation, "Run"));
    }

    [Fact]
    public void AnExtendWithoutAPhaseIsInThePhaseOfTheDefaultBody()
    {
        WriteModule("Step", "extend Owner.Run { streams.Steps = 3; }");
        WriteModule("Inner", "extend Owner.Wrap { return next() + 1; }");

        var (ok, log, explanation) = Mix(false, "Test.Step", "Test.Inner");

        Assert.True(ok, log);
        Assert.Contains("Default: Owner > Step", Block(explanation, "Run"));
        Assert.Contains("Inner: Inner", Block(explanation, "Wrap"));
    }

    [Fact]
    public void TheClausesAreLikeTheAttributes()
    {
        WriteModule("First", "extend Owner.Run in A { streams.Steps = 2; }");
        WriteModule("Gone", "extend Owner.Run in A { streams.Steps = 4; }");
        WriteModule("Second", "extend Owner.Run in A after First replaces Gone { streams.Steps = 3; }");

        var (ok, log, explanation) = Mix(true, "Test.Second", "Test.Gone", "Test.First");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Contains("A: First > Second   order: after", block);
        Assert.Contains("replaced by Second: Gone", block);
    }

    [Theory]
    [InlineData("extend Owner.RunWith { other = other + value; }")]
    [InlineData("extend Owner.RunWith(float value, inout float other) { other = other + value; }")]
    public void TheParametersComeFromTheMethodOrAreRepeated(string member)
    {
        WriteModule("Step", member);

        var (ok, log, explanation) = Mix(false, "Test.Step");

        Assert.True(ok, log);
        Assert.Contains("Default: Owner > Step", Block(explanation, "RunWith"));
    }

    [Fact]
    public void RepeatedParametersAreChecked()
    {
        WriteModule("Step", "extend Owner.RunWith(float value) { }");

        var (ok, log, _) = Mix(false, "Test.Step");

        Assert.False(ok, log);
    }

    [Fact]
    public void NextReachesTheContributorInward()
    {
        WriteModule("Outside", "extend Owner.Wrap in Outer { return next() * 2; }");
        WriteModule("Inside", "extend Owner.Wrap in Inner { return next() + 1; }");
        WriteModule("Winner", "extend Owner.Pick { return next(value) * 2; }");

        var (ok, log, explanation) = Mix(false, "Test.Outside", "Test.Inside", "Test.Winner");

        Assert.True(ok, log);
        Assert.Equal("module method Wrap() -> float = Owner > Inside > Outside", Block(explanation, "Wrap")[0]);
        Assert.Equal("module method Pick(float) -> float = Owner > Winner", Block(explanation, "Pick")[0]);
    }

    // Only inside an extend or a patch: elsewhere it is a method like any other
    [Fact]
    public void NextIsNotReserved()
    {
        Write("Plain", "module Plain { float next() { return 1; } float Other() { return next(); } }");

        var (ok, log, _) = Mix(false, "Test.Plain");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("extend Owner.Run in A { next(); }", "SDSL0144")]
    [InlineData("extend Owner.Run in A { base.Run(); }", "SDSL0144")]
    [InlineData("extend Owner.Plain { return 2; }", "SDSL0143")]
    [InlineData("extend Owner.Missing { }", "SDSL0143")]
    [InlineData("patch Owner.Run { }", "SDSL0143")]
    [InlineData("patch Owner.Missing { }", "SDSL0143")]
    [InlineData("extend NoSuchClass.Run { }", "SDSL0110")]
    [InlineData("extend Owner.Run in Missing { }", "SDSL0141")]
    [InlineData("extend Owner.Pick in A { return 1; }", "SDSL0141")]
    public void AWrongExtendIsAnError(string member, string code)
    {
        WriteModule("Wrong", member);

        var (ok, log, _) = Mix(false, "Test.Wrong");

        Assert.False(ok, log);
        Assert.Contains(code, log);
        Assert.DoesNotContain("SDSL0175", log);
        Assert.DoesNotContain("SDSL0176", log);
    }

    private void WriteOverloads() => Write("Over", """
        module Over
        {
            [Replace] float2 Value(float a) { return a; }
            [Replace] float Value(float a, float b) { return b; }
            float Plain(float a) { return a; }
            float Plain(float a, float b) { return b; }
        }
        """);

    [Theory]
    [InlineData("extend Over.Value { return 1; }")]
    [InlineData("patch Over.Plain { return 1; }")]
    public void AnOverloadedMethodNeedsItsParameters(string member)
    {
        WriteOverloads();
        Write("Ext", $"module Ext {{ import Over; {member} }}");

        var (ok, log, _) = Mix(false, "Test.Ext");

        Assert.False(ok, log);
        Assert.Contains("SDSL0143", log);
        Assert.Contains("several overloads", log);
        Assert.DoesNotContain("SDSL0175", log);
        Assert.DoesNotContain("SDSL0176", log);
    }

    [Theory]
    [InlineData("extend Over.Value(float a) { return a; }")]
    [InlineData("extend Over.Value(float a, float b) { return b; }")]
    [InlineData("patch Over.Plain(float a) { return a; }")]
    public void TheParametersFindTheOverload(string member)
    {
        WriteOverloads();
        Write("Ext", $"module Ext {{ import Over; {member} }}");

        var (ok, log, _) = Mix(false, "Test.Ext");

        Assert.True(ok, log);
    }

    [Theory]
    [InlineData("float3 a[2]")]
    [InlineData("Texture2D a")]
    public void AParameterThatIsMoreThanANameHasToBeWritten(string parameter)
    {
        Write("Over", $"module Over {{ [Replace] float Value({parameter}) {{ return 0; }} }}");
        Write("Ext", "module Ext { import Over; extend Over.Value { return 1; } }");

        var (ok, log, _) = Mix(false, "Test.Ext");

        Assert.False(ok, log);
        Assert.Contains("write the parameters", log);
        Assert.DoesNotContain("SDSL0176", log);
    }

    // The mixer checks the place of the call to base, and a step before the default body has it last
    [Theory]
    [InlineData("[Phase(Early)] extend Owner.Run { streams.Steps = 2; }")]
    [InlineData("[Phase(Wind, After = Early)] extend Owner.Run { streams.Steps = 2; }")]
    [InlineData("[Phase(Wind, Before = Default)] extend Owner.Run { streams.Steps = 2; }")]
    [InlineData("[Phase(Wind, After = Default)] extend Owner.Run { streams.Steps = 2; }")]
    [InlineData("[Phase(Wind, Before = A)] extend Owner.Run { streams.Steps = 2; }")]
    public void APhaseAttributeOnAnExtendPlacesItsCallToBase(string member)
    {
        WriteModule("Step", member);

        var (ok, log, _) = Mix(false, "Test.Step");

        Assert.True(ok, log);
    }

    [Fact]
    public void AnExtendThatDropsOthersIsAnErrorInAnAssetBuild()
    {
        WriteModule("Inside", "extend Owner.Wrap in Inner { return next() + 1; }");
        WriteModule("Dropper", "extend Owner.Wrap in Outer { return 2; }");

        var (ok, log, _) = Mix(true, "Test.Inside", "Test.Dropper");

        Assert.False(ok, log);
        Assert.Contains("Error: SDSL0136: Dropper.Wrap doesn't call next, so Inside never run", log);
        Assert.Contains("Write replaces Inside, or call next", log);
    }

    [Fact]
    public void AnExtendThatDropsOthersIsInformationInAMixBuiltAtRunTime()
    {
        WriteModule("Inside", "extend Owner.Wrap in Inner { return next() + 1; }");
        WriteModule("Dropper", "extend Owner.Wrap in Outer { return 2; }");

        var (ok, log, _) = Mix(false, "Test.Inside", "Test.Dropper");

        Assert.True(ok, log);
        Assert.Contains("Info: SDSL0136: Dropper.Wrap doesn't call next, so Inside never run", log);
    }

    // In 4.4 the last one mixed wins. The upgrade writes extend for both, and a mix built at run time keeps working.
    [Fact]
    public void TwoOverridesOfAnUpgradedProjectStillRunInAMixBuiltAtRunTime()
    {
        var upgraded = ShaderUpgrader.Upgrade(new Dictionary<string, string>
        {
            ["Target.sdsl"] = "shader Target { stage float Value() { return 1; } };",
            ["First.sdsl"] = "shader First : Target { stage override float Value() { return 2; } };",
            ["Second.sdsl"] = "shader Second : Target { stage override float Value() { return 3; } };",
        }, EngineShaderClasses.Parse(""), implicitUsings: []).Files;
        foreach (var (name, code) in upgraded)
            File.WriteAllText(Path.Combine(sourceDir.FullName, name), code);

        Assert.Contains("extend Target.Value", upgraded["Second.sdsl"]);
        var (ok, log, _) = Mix(false, "First", "Second");

        Assert.True(ok, log);
        Assert.Contains("Info: SDSL0136: Second.Value doesn't call next, so First never run", log);
    }

    // Every extend starts with a copy of its inout parameters around the call to the next one: only what the body does counts
    [Theory]
    [InlineData("other = 1;", "other = 2;", false)]
    [InlineData("float x = value;", "float y = value;", true)]
    public void AnExtendConflictsOverWhatItsBodyDoes(string first, string second, bool ok)
    {
        WriteModule("First", $"extend Owner.RunWith in A {{ {first} }}");
        WriteModule("Second", $"extend Owner.RunWith in A {{ {second} }}");

        var (mixed, log, _) = Mix(isAssetBuild: true, "Test.First", "Test.Second");

        Assert.Equal(ok, mixed);
        Assert.Equal(!ok, log.Contains("SDSL0137: First and Second are both in phase A of Owner.RunWith and share other, which one of them writes", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExtendThatStatesWhatItReplacesIsNotADrop()
    {
        WriteModule("Inside", "extend Owner.Wrap in Inner { return next() + 1; }");
        WriteModule("Replacement", "extend Owner.Wrap in Outer replaces Inside { return 2; }");

        var (ok, log, _) = Mix(false, "Test.Inside", "Test.Replacement");

        Assert.True(ok, log);
    }

    [Fact]
    public void TheCompatFormIsAnErrorInAnAssetBuild()
    {
        WriteModule("Inside", "extend Owner.Wrap in Inner { return next() + 1; }");
        WriteModule("Dropper", "[Phase(Outer)] override float Wrap() { return 2; }");

        var (ok, log, _) = Mix(true, "Test.Inside", "Test.Dropper");

        Assert.False(ok, log);
        Assert.Contains("Error: SDSL0216", log);
    }

    [Fact]
    public void TheCompatFormIsInformationInAMixBuiltAtRunTime()
    {
        WriteModule("Inside", "extend Owner.Wrap in Inner { return next() + 1; }");
        WriteModule("Dropper", "[Phase(Outer)] override float Wrap() { return 2; }");

        var (ok, log, _) = Mix(false, "Test.Inside", "Test.Dropper");

        Assert.True(ok, log);
        Assert.Contains("Info: SDSL0216", log);
    }

    [Fact]
    public void APatchChangesAMethodThatIsNotAnExtensionPoint()
    {
        WriteModule("Patch", "patch Owner.Plain { return next() + 1; }");

        var (ok, log, explanation) = Mix(true, "Test.Patch");

        Assert.True(ok, log);
        Assert.DoesNotContain("SDSL0246", log);
        Assert.Equal("module method Plain() -> float = Owner > Patch", Block(explanation, "Plain")[0]);
    }

    // The words are not reserved
    [Fact]
    public void AMemberCanHaveTheseNames()
    {
        Write("Names", "module Names { struct patch { float a; }; patch extend; float next; }");

        var (ok, log, _) = Mix(false, "Test.Names");

        Assert.True(ok, log);
    }

    private static string Upgrade(string file) => ShaderUpgrader.Upgrade(new Dictionary<string, string>
    {
        ["Owner.sdsl"] = Owner,
        ["File.sdsl"] = file,
    }, engine: null, implicitUsings: []).Files["File.sdsl"].ReplaceLineEndings("\n");

    [Fact]
    public void TheUpgraderWritesAStepThatCallsBaseFirst()
    {
        var result = Upgrade("""
            module Step
            {
                import Owner;

                [Phase(A)]
                override void Run()
                {
                    base.Run();

                    // Kept
                    streams.Steps = 2;
                }
            }
            """);

        Assert.Equal("""
            module Step
            {
                import Owner;

                extend Owner.Run in A
                {
                    // Kept
                    streams.Steps = 2;
                }
            }
            """.ReplaceLineEndings("\n"), result);
    }

    [Fact]
    public void TheUpgraderWritesAStepThatCallsBaseLastWithItsClauses()
    {
        var result = Upgrade("""
            module Step
            {
                import Owner;

                [Phase(Early)]
                [After(Other)]
                [Replaces(Gone, Early)]
                override void Run()
                {
                    streams.Steps = 2;

                    base.Run();
                }
            }
            """);

        Assert.Contains("extend Owner.Run in Early after Other replaces Gone, Early\n    {\n        streams.Steps = 2;\n    }", result);
    }

    [Fact]
    public void TheUpgraderWritesNext()
    {
        var result = Upgrade("""
            module Step
            {
                import Owner;

                [Phase(Outer)]
                override float Wrap()
                {
                    float value = base.Wrap();
                    return value * 2;
                }

                [Replaces(Other)]
                override float Pick(float value)
                {
                    return base.Pick(value);
                }
            }
            """);

        Assert.Contains("extend Owner.Wrap in Outer\n    {\n        float value = next();", result);
        Assert.Contains("extend Owner.Pick(float value) replaces Other\n    {\n        return next(value);", result);
    }

    [Theory]
    // Outside every phase: extend without a phase means something else
    [InlineData("override void Run() { base.Run(); }")]
    // A step can't drop the others, or call base in the middle
    [InlineData("[Phase(A)] override void Run() { streams.Steps = 2; }")]
    [InlineData("[Phase(A)] override void Run() { streams.Steps = 2; base.Run(); streams.Steps = 3; }")]
    [InlineData("[Phase(A)] override void Run() { if (streams.Steps > 0) base.Run(); }")]
    // next can't reach it from another method
    [InlineData("[Phase(Outer)] override float Wrap() { return Helper(); } float Helper() { return base.Wrap(); }")]
    // extend calls base first from the phase of the default body on, and this step runs before it
    [InlineData("[Phase(Default)] override void Run() { streams.Steps = 2; base.Run(); }")]
    public void TheUpgraderLeavesWhatExtendCantSay(string member)
    {
        var text = $"module Step\n{{\n    import Owner;\n\n    {member.Replace("] override", "]\n    override")}\n}}\n";

        Assert.Equal(text, Upgrade(text));
    }

    [Fact]
    public void TheUpgraderKeepsTheParametersOfAStep()
    {
        var result = Upgrade("""
            module Step
            {
                import Owner;

                [Phase(A)]
                override void RunWith(float value, inout float other)
                {
                    base.RunWith(value, other);
                    other = 2;
                }
            }
            """);

        Assert.Contains("extend Owner.RunWith(float value, inout float other) in A\n    {\n        other = 2;", result);
    }

    [Fact]
    public void TheUpgraderAddsTheUsingOfTheClassAnExtendNames()
    {
        var result = ShaderUpgrader.Upgrade(new Dictionary<string, string>
        {
            ["Point.sdsl"] = "namespace Lib.Core;\nmodule Point\n{\n    [Replace]\n    float Value() { return 1; }\n}\n",
            ["Middle.sdsl"] = "namespace Lib.Other;\nusing Lib.Core;\nshader Middle\n{\n    import Point;\n};\n",
            ["Child.sdsl"] = "namespace Game;\nusing Lib.Other;\nshader Child : Middle\n{\n    module override float Value() { return 2; }\n};\n",
        }, EngineShaderClasses.Parse(""), implicitUsings: []);

        var child = result.Files["Child.sdsl"];
        Assert.Contains("extend Point.Value", child);
        Assert.Contains("using Lib.Core;", child);
    }

    // A kind on the first definition would make the patch an error
    [Fact]
    public void TheUpgraderLeavesThePatchOfAMethodOfTheProject()
    {
        const string plain = "module Plain\n{\n    float Value() { return 1; }\n}\n";
        const string patch = "module MyPatch\n{\n    import Plain;\n\n    patch Plain.Value { return 2; }\n}\n";

        var result = ShaderUpgrader.Upgrade(new Dictionary<string, string> { ["Plain.sdsl"] = plain, ["MyPatch.sdsl"] = patch }, engine: null, implicitUsings: []);

        Assert.Equal(plain, result.Files["Plain.sdsl"].ReplaceLineEndings("\n"));
        Assert.Equal(patch, result.Files["MyPatch.sdsl"].ReplaceLineEndings("\n"));
    }

    [Fact]
    public void RunningTheUpgraderAgainChangesNothing()
    {
        var once = Upgrade("module Step\n{\n    import Owner;\n\n    [Phase(A)]\n    override void Run()\n    {\n        base.Run();\n    }\n}\n");

        Assert.Contains("extend Owner.Run in A", once);
        Assert.Equal(once, Upgrade(once));
    }
}
