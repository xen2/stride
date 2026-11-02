// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Stride.Packaging.Tests;

/// <summary>
/// One package declares a <c>pipeline</c> and an effect, two packages extend them and don't know each other, and a game
/// settles their order with a layer of its own. All are packed with <c>dotnet pack</c>, and the game's effect log is compiled by
/// the asset compiler. Between its builds the game adds and removes plugins, and the compiled effect follows.
/// </summary>
[Collection("Packaging")]
public class ShaderPluginPackagingTests(ITestOutputHelper output) : IDisposable
{
    private readonly string caseDir = Path.Combine(Path.GetTempPath(), "stride-packaging-tests", $"shader-plugins-{Guid.NewGuid():N}");

    public void Dispose() => TestEnvironment.DeleteCaseDirectory(caseDir, output);

    private string GameDir => Path.Combine(caseDir, "ShaderGame");

    private string GameProject => Path.Combine(GameDir, "ShaderGame.Game", "ShaderGame.Game.csproj");

    private string OrderFile => Path.Combine(GameDir, "ShaderGame.Game", "Assets", "GameOrder.sdfx");

    private string NugetCache => Path.Combine(caseDir, "nuget");

    private string Version { get; } = TestEnvironment.ResolveStrideVersion();

    private void Pack(string feed)
    {
        foreach (var name in new[] { "ShaderPipe", "ShaderStepA", "ShaderStepB" })
        {
            var dir = Path.Combine(caseDir, name);
            TestEnvironment.CopyDirectory(Path.Combine(TestEnvironment.FixturesDir(), "ShaderPlugins", name), dir);
            NuGetConsumerFeed.WriteStrictNuGetConfig(dir, [new ExtraFeed("plugin-feed", feed, "ShaderPipe", "ShaderStepA", "ShaderStepB")]);
            var pack = Dotnet.Exec(["pack", Path.Combine(dir, $"{name}.csproj"), "-c", "Debug", "-v:m", "-nr:false", $"-p:StrideEngineVersion={Version}", $"-p:RestorePackagesPath={NugetCache}", "-o", feed], dir, output, timeoutMin: 10);
            Assert.True(pack.ExitCode == 0, $"{name} pack failed with exit {pack.ExitCode}");
        }
    }

    private ExecResult Build(params string[] plugins)
    {
        var project = File.ReadAllText(GameProject).ReplaceLineEndings("\n");
        project = Regex.Replace(project, """ *<PackageReference Include="ShaderStep[AB]" Version="1.0.0" />\r?\n""", "");
        var references = string.Concat(plugins.Select(x => $"""    <PackageReference Include="{x}" Version="1.0.0" />{"\n"}"""));
        project = project.Replace("    <!-- The plugins of a case -->\n", $"    <!-- The plugins of a case -->\n{references}");
        File.WriteAllText(GameProject, project);
        return ConsumerBuild.Run(Path.Combine(GameDir, "ShaderGame.csproj"), GameDir, output, timeoutMin: 10, $"-p:StrideEngineVersion={Version}", $"-p:RestorePackagesPath={NugetCache}");
    }

    // The id of an effect is a hash of what it mixes, and the game finds its bytecode by this id
    private string CompiledEffect()
    {
        var dbDir = Path.Combine(GameDir, "obj", "stride", "assetbuild", "data", "db");
        var index = Directory.GetFiles(dbDir, "index.ShaderGame.*").Single(x => !x.EndsWith(".closure", StringComparison.Ordinal));
        return File.ReadAllLines(index).Single(x => x.StartsWith("__shaders_bytecode__/", StringComparison.Ordinal));
    }

    private string[] Mixins()
    {
        var binDir = Path.Combine(GameDir, "bin", "Debug", "net10.0");
        var run = Dotnet.Exec(["exec", Path.Combine(binDir, "ShaderGame.dll")], binDir, output, timeoutMin: 2);
        Assert.True(run.ExitCode == 0, $"The game failed with exit {run.ExitCode}");
        return run.Output.Split('\n').Where(x => x.StartsWith("MIXIN ", StringComparison.Ordinal)).Select(x => x["MIXIN ".Length..].Trim()).ToArray();
    }

    private void WriteOrder(string classes) => File.WriteAllText(OrderFile, $$"""
        using ShaderStepA;
        using ShaderStepB;

        namespace ShaderPipe
        {
            extend effect PipeEffect
            {
                extend shader PipeOwner { [MixinOrder({{classes}})] ModifyValue; }
            }
        }
        """);

    private static void AssertBuilt(ExecResult result) => Assert.True(result.ExitCode == 0, $"The game should build (exit {result.ExitCode}).");

    [Fact]
    public void TheCompiledEffectFollowsThePluginsOfTheGame()
    {
        var feed = Path.Combine(caseDir, "feed");
        Directory.CreateDirectory(feed);
        Pack(feed);
        TestEnvironment.CopyDirectory(Path.Combine(TestEnvironment.FixturesDir(), "ShaderPlugins", "ShaderGame"), GameDir);
        NuGetConsumerFeed.WriteStrictNuGetConfig(GameDir, [new ExtraFeed("plugin-feed", feed, "ShaderPipe", "ShaderStepA", "ShaderStepB")]);

        AssertBuilt(Build());
        var plain = CompiledEffect();
        Assert.Equal(["Stride.Rendering.SpriteBase", "ShaderPipe.PipeRoot"], Mixins());

        AssertBuilt(Build("ShaderStepA"));
        var withStep = CompiledEffect();
        Assert.NotEqual(plain, withStep);
        Assert.Contains("ShaderStepA.StepA", Mixins());

        // Two plugins that don't know each other, on a stream, in a real asset build
        var conflict = Build("ShaderStepA", "ShaderStepB");
        Assert.NotEqual(0, conflict.ExitCode);
        Assert.Contains("SDSL0137: StepA and StepB are both in phase Scene of PipeOwner.ModifyValue", conflict.Output);

        // The game settles them, and the order is part of the effect
        WriteOrder("StepA, StepB");
        AssertBuilt(Build("ShaderStepA", "ShaderStepB"));
        var ordered = CompiledEffect();

        WriteOrder("StepB, StepA");
        AssertBuilt(Build("ShaderStepA", "ShaderStepB"));
        Assert.NotEqual(ordered, CompiledEffect());

        // Removing them brings back what the game had
        File.Delete(OrderFile);
        AssertBuilt(Build("ShaderStepA"));
        Assert.Equal(withStep, CompiledEffect());

        AssertBuilt(Build());
        Assert.Equal(plain, CompiledEffect());
        Assert.Equal(["Stride.Rendering.SpriteBase", "ShaderPipe.PipeRoot"], Mixins());
    }

    // A clean build finds no such class. An incremental one still finds its source in the database of the previous build
    [Fact]
    public void ABlockAboutARemovedPluginIsAnErrorInACleanBuild()
    {
        var feed = Path.Combine(caseDir, "feed");
        Directory.CreateDirectory(feed);
        Pack(feed);
        TestEnvironment.CopyDirectory(Path.Combine(TestEnvironment.FixturesDir(), "ShaderPlugins", "ShaderGame"), GameDir);
        NuGetConsumerFeed.WriteStrictNuGetConfig(GameDir, [new ExtraFeed("plugin-feed", feed, "ShaderPipe", "ShaderStepA", "ShaderStepB")]);
        WriteOrder("StepA, StepB");

        var result = Build("ShaderStepA");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SDSL0158: the block of GameOrder.sdfx, line 8 names the class StepB, which doesn't exist.", result.Output);
    }
}
