// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.IO;

namespace Stride.Core.Assets;

/// <summary>
/// The status of an upgrade step in an <see cref="UpgradeReport"/>.
/// </summary>
public enum UpgradeStepStatus
{
    /// <summary>The step did not run, because the upgrade stopped before it.</summary>
    NotRun,

    /// <summary>The step ran on every file it changes.</summary>
    Done,

    /// <summary>The step ran, but left some files unchanged (see <see cref="UpgradeStepResult.FailedUnits"/>).</summary>
    Partial,

    /// <summary>The step failed, and changed nothing.</summary>
    Failed,
}

/// <summary>
/// A step an upgrader declares, as <see cref="PackageUpgrader.GetUpgradeSteps"/> lists it.
/// </summary>
/// <param name="Id">The ID of the step, e.g. <c>Stride-4.5.0-P2</c>.</param>
/// <param name="Title">What the step changes, in one line of Markdown.</param>
/// <param name="HelpLink">The page of the upgrade guide about the step, or <c>null</c>.</param>
public sealed record UpgradeStepInfo(string Id, string Title, string? HelpLink);

/// <summary>
/// What an upgrade step did to a project.
/// </summary>
public sealed class UpgradeStepResult
{
    internal UpgradeStepResult(string? id, string title, string? helpLink)
    {
        Id = id;
        Title = title;
        HelpLink = helpLink;
    }

    /// <summary>The ID of the step, or <c>null</c> for a part of the upgrade that is not a declared step (a hook of the upgrader).</summary>
    public string? Id { get; }

    /// <summary>What the step changes, in one line of Markdown.</summary>
    public string Title { get; }

    /// <summary>The page of the upgrade guide about the step, or <c>null</c>.</summary>
    public string? HelpLink { get; }

    /// <summary>The status of the step.</summary>
    public UpgradeStepStatus Status { get; set; }

    /// <summary>The number of files the step changed.</summary>
    public int ChangedFiles { get; set; }

    /// <summary>The files the step could not change, which it left as they were.</summary>
    public List<string> FailedUnits { get; } = [];

    /// <summary>Why the step failed, or <c>null</c>.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// What the steps of one upgrader did to a project.
/// </summary>
public sealed class UpgraderReport
{
    /// <summary>A report with the <paramref name="steps"/> of the upgrader, not run yet.</summary>
    public UpgraderReport(string name, PackageVersion fromVersion, PackageVersion? toVersion, IEnumerable<UpgradeStepInfo> steps)
    {
        Name = name;
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Steps.AddRange(steps.Select(x => new UpgradeStepResult(x.Id, x.Title, x.HelpLink)));
    }

    /// <summary>The <see cref="PackageUpgraderAttribute.Name"/> of the upgrader.</summary>
    public string Name { get; }

    /// <summary>The version the project is upgraded from.</summary>
    public PackageVersion FromVersion { get; }

    /// <summary>The version the project is upgraded to.</summary>
    public PackageVersion? ToVersion { get; }

    /// <summary>The steps that apply to the project, in the order they run, then the parts of the upgrade that failed outside a step.</summary>
    public List<UpgradeStepResult> Steps { get; } = [];

    /// <summary>The step with the ID <paramref name="id"/>, or <c>null</c>.</summary>
    public UpgradeStepResult? Find(string id) => Steps.FirstOrDefault(x => x.Id == id);

    /// <summary>
    /// Adds a part of the upgrade that failed outside a declared step, such as the update of the package references.
    /// </summary>
    public UpgradeStepResult AddFailure(string title, string? error = null)
    {
        var result = new UpgradeStepResult(null, title, null) { Status = UpgradeStepStatus.Failed, Error = error };
        Steps.Add(result);
        return result;
    }
}

/// <summary>
/// What the upgrade did to one project.
/// </summary>
public sealed class ProjectUpgradeReport
{
    internal ProjectUpgradeReport(UFile projectFullPath)
    {
        ProjectFullPath = projectFullPath;
    }

    /// <summary>The full path of the project file.</summary>
    public UFile ProjectFullPath { get; }

    /// <summary>The upgraders that upgraded the project, in the order they ran.</summary>
    public List<UpgraderReport> Upgraders { get; } = [];
}

/// <summary>
/// What the upgrade of a session did to each project: the steps of each upgrader with their status.
/// </summary>
public sealed class UpgradeReport
{
    private readonly Lock gate = new();

    /// <summary>The upgraded projects.</summary>
    public List<ProjectUpgradeReport> Projects { get; } = [];

    /// <summary>
    /// The report of <paramref name="upgrader"/> on the project <paramref name="projectFullPath"/>, upgraded from
    /// <paramref name="fromVersion"/>: added the first time, with the steps that apply not run yet.
    /// </summary>
    public UpgraderReport GetOrAdd(UFile projectFullPath, PackageUpgrader upgrader, PackageVersion fromVersion)
    {
        ArgumentNullException.ThrowIfNull(projectFullPath);
        ArgumentNullException.ThrowIfNull(upgrader);
        ArgumentNullException.ThrowIfNull(fromVersion);
        lock (gate)
        {
            var project = Projects.FirstOrDefault(x => x.ProjectFullPath == projectFullPath);
            if (project is null)
            {
                project = new ProjectUpgradeReport(projectFullPath);
                Projects.Add(project);
            }

            var report = project.Upgraders.FirstOrDefault(x => x.Name == upgrader.Attribute.Name);
            if (report is null)
            {
                report = new UpgraderReport(upgrader.Attribute.Name, fromVersion, upgrader.Attribute.UpdatedVersionRange?.MinVersion, upgrader.GetUpgradeSteps(fromVersion));
                project.Upgraders.Add(report);
            }
            return report;
        }
    }

    /// <summary>The report of the upgrader named <paramref name="upgraderName"/> on the project, or <c>null</c>.</summary>
    public UpgraderReport? Find(UFile projectFullPath, string upgraderName)
    {
        lock (gate)
            return Projects.FirstOrDefault(x => x.ProjectFullPath == projectFullPath)?.Upgraders.FirstOrDefault(x => x.Name == upgraderName);
    }
}
