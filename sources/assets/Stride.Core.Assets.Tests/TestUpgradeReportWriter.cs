// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.IO;
using Xunit;

namespace Stride.Core.Assets.Tests;

public class TestUpgradeReportWriter
{
    private static readonly DateTime Time = new(2026, 11, 21, 14, 32, 0);
    private static readonly string ProjectDirectory = Path.Combine(Path.GetTempPath(), "Game");

    private const string LeftoversSection = """
        ## 2026-11-21 14:32 · Stride 4.4.3 → 4.5.0 · 3 items left

        5 steps: 2 done, 1 failed, 1 partial, 1 not run. Also upgraded: MyPlugin 1.0.0 → 2.0.0.

        ### Failed

        - [ ] `Stride-4.5.0-C2` Fix the callbacks ([guide](https://doc/4.5.html#stride-4.5.0-c2))
          - The rule threw.

        ### Partial

        - [ ] `Stride-4.5.0-P1` Update the effects ([guide](https://doc/4.5.html#stride-4.5.0-p1))
          - `Effects/Main.sdfx` is left unchanged

        ### Not run

        - [ ] `Stride-4.5.0-P2` Add the plugin packages ([guide](https://doc/4.5.html#stride-4.5.0-p2))

        <details>
        <summary>Done (2)</summary>

        - `Stride-4.5.0-C1` Rename `A` to `B` ([guide](https://doc/4.5.html#stride-4.5.0-c1)) (3 files)
        - `MyPlugin-2.0.0-P1` Move the settings

        </details>

        """;

    [Fact]
    public void ANewFileListsTheLeftoversThenTheStepsDone()
    {
        var text = UpgradeReportWriter.AddToProjectFile(null, LeftoversReport(), Time);

        Assert.Equal(Lf($"""
            # Upgrade report: Game.csproj

            Upgrades of this project left the items below to do by hand. Delete this file once every item is handled. The newest upgrade is first.

            {LeftoversSection}
            """), text);
    }

    [Fact]
    public void TheNewestUpgradeGoesOnTopOfTheOnesTheFileLists()
    {
        var existing = UpgradeReportWriter.AddToProjectFile(null, LeftoversReport(), Time).ReplaceLineEndings("\r\n");

        var text = UpgradeReportWriter.AddToProjectFile(existing, CleanReport(), Time.AddDays(3));

        Assert.Equal(Lf($"""
            # Upgrade report: Game.csproj

            Upgrades of this project left the items below to do by hand. Delete this file once every item is handled. The newest upgrade is first.

            ## 2026-11-24 14:32 · Stride 4.5.0 → 4.5.7 · 1 step done

            1 step: 1 done.

            <details>
            <summary>Done (1)</summary>

            - `Stride-4.5.7-C1` Rename `C` to `D` (1 file)

            </details>

            {LeftoversSection}
            """), text);
    }

    [Fact]
    public void AFileWithoutUpgradeGetsTheUpgradeAtItsEnd()
    {
        var text = UpgradeReportWriter.AddToProjectFile("# Notes\n\nHandled by hand.\n\n", CleanReport(), Time);

        Assert.StartsWith("# Notes\n\nHandled by hand.\n\n## 2026-11-21 14:32 · Stride 4.5.0 → 4.5.7 · 1 step done\n", text);
    }

    [Fact]
    public void TheUpgradeReportListsEachProject()
    {
        var report = new UpgradeReport();
        report.Projects.Add(CleanReport());

        Assert.Equal(Lf("""
            # Upgrade report: Game.sln

            The upgrade of 2026-11-21 14:32.

            ## Game.csproj · Stride 4.5.0 → 4.5.7 · 1 step done

            1 step: 1 done.

            <details>
            <summary>Done (1)</summary>

            - `Stride-4.5.7-C1` Rename `C` to `D` (1 file)

            </details>

            """), UpgradeReportWriter.FormatUpgrade(report, "Game.sln", Time));
        Assert.Equal("# Upgrade report: Game.sln\n\nThe upgrade of 2026-11-21 14:32. No project needed an upgrade.\n", UpgradeReportWriter.FormatUpgrade(new UpgradeReport(), "Game.sln", Time));
    }

    [Fact]
    public void TheLogHasOneLineThenTheSteps()
    {
        var project = LeftoversReport();

        Assert.Equal("Stride 4.4.3 to 4.5.0: 3 items left", UpgradeReportWriter.FormatSummary(project));
        Assert.Equal(
            [
                "  done     Stride-4.5.0-C1 Rename `A` to `B`",
                "  failed   Stride-4.5.0-C2 Fix the callbacks",
                "  partial  Stride-4.5.0-P1 Update the effects (1 file left unchanged)",
                "  not run  Stride-4.5.0-P2 Add the plugin packages",
                "  done     MyPlugin-2.0.0-P1 Move the settings",
            ], UpgradeReportWriter.FormatStepLines(project));
        Assert.Equal("Stride 4.5.0 to 4.5.7: 1 step done", UpgradeReportWriter.FormatSummary(CleanReport()));
    }

    private static ProjectUpgradeReport LeftoversReport()
    {
        var project = new ProjectUpgradeReport(new UFile(Path.Combine(ProjectDirectory, "Game.csproj")));
        var stride = new UpgraderReport("Stride", new PackageVersion("4.4.3"), new PackageVersion("4.5.0"),
        [
            Step("Stride-4.5.0-C1", "Rename `A` to `B`"),
            Step("Stride-4.5.0-C2", "Fix the callbacks"),
            Step("Stride-4.5.0-P1", "Update the effects"),
            Step("Stride-4.5.0-P2", "Add the plugin packages"),
        ]);
        stride.Steps[0].Status = UpgradeStepStatus.Done;
        stride.Steps[0].ChangedFiles = 3;
        stride.Steps[1].Status = UpgradeStepStatus.Failed;
        stride.Steps[1].Error = "The rule\nthrew.";
        stride.Steps[2].Status = UpgradeStepStatus.Partial;
        stride.Steps[2].FailedUnits.Add(Path.Combine(ProjectDirectory, "Effects", "Main.sdfx"));
        project.Upgraders.Add(stride);

        var plugin = new UpgraderReport("MyPlugin", new PackageVersion("1.0.0"), new PackageVersion("2.0.0"), [new UpgradeStepInfo("MyPlugin-2.0.0-P1", "Move the settings", null)]);
        plugin.Steps[0].Status = UpgradeStepStatus.Done;
        project.Upgraders.Add(plugin);
        return project;

        static UpgradeStepInfo Step(string id, string title) => new(id, title, $"https://doc/4.5.html#{id.ToLowerInvariant()}");
    }

    private static ProjectUpgradeReport CleanReport()
    {
        var project = new ProjectUpgradeReport(new UFile(Path.Combine(ProjectDirectory, "Game.csproj")));
        var stride = new UpgraderReport("Stride", new PackageVersion("4.5.0"), new PackageVersion("4.5.7"), [new UpgradeStepInfo("Stride-4.5.7-C1", "Rename `C` to `D`", null)]);
        stride.Steps[0].Status = UpgradeStepStatus.Done;
        stride.Steps[0].ChangedFiles = 1;
        project.Upgraders.Add(stride);
        return project;
    }

    // The raw string literals of this file have the line endings of the checkout
    private static string Lf(string text) => text.ReplaceLineEndings("\n");
}
