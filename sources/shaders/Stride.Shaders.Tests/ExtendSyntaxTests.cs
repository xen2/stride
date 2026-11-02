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

    private (bool Ok, string Log, string Explanation) Mix(bool wired, params string[] classes)
    {
        Write("Owner", Owner);
        Write("Entry", "module Entry { stream float4 ColorTarget : SV_Target0; void PSMain() { streams.ColorTarget = 0; } }");
        var source = new ShaderMixinSource { Mixins = { new ShaderClassSource("Test.Entry") } };
        foreach (var name in classes)
            source.Mixins.Add(new ShaderClassSource(name));

        var log = new LoggerResult();
        var mixer = new ShaderMixer(new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL") { WarningsAsErrors = false }) { CollectExplanation = true, IsWiredEffect = wired };
        bool ok;
        try
        {
            ok = mixer.MergeSDSL(source, new ShaderMixer.Options(true), log, out _, out _, out _, out _);
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
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExtendThatDropsOthersIsAnError(bool wired)
    {
        WriteModule("Inside", "extend Owner.Wrap in Inner { return next() + 1; }");
        WriteModule("Dropper", "extend Owner.Wrap in Outer { return 2; }");

        var (ok, log, _) = Mix(wired, "Test.Inside", "Test.Dropper");

        Assert.False(ok, log);
        Assert.Contains("SDSL0136: Dropper.Wrap doesn't call next, so Inside never run", log);
        Assert.Contains("Write replaces Inside, or call next", log);
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
    public void ATheCompatFormStaysAWarning()
    {
        WriteModule("Inside", "extend Owner.Wrap in Inner { return next() + 1; }");
        WriteModule("Dropper", "[Phase(Outer)] override float Wrap() { return 2; }");

        var (ok, log, _) = Mix(true, "Test.Inside", "Test.Dropper");

        Assert.True(ok, log);
        Assert.Contains("Warning: SDSL0216", log);
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
    public void RunningTheUpgraderAgainChangesNothing()
    {
        var once = Upgrade("module Step\n{\n    import Owner;\n\n    [Phase(A)]\n    override void Run()\n    {\n        base.Run();\n    }\n}\n");

        Assert.Contains("extend Owner.Run in A", once);
        Assert.Equal(once, Upgrade(once));
    }
}
