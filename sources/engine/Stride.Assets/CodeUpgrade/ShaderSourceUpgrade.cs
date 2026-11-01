// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Yaml;
using Stride.Rendering;
using Stride.Shaders.Parsing.Upgrade;

namespace Stride.Assets;

/// <summary>
/// Upgrades the <c>.sdsl</c> and <c>.sdfx</c> files of a project to the current shader language (<see cref="ShaderUpgrader"/>).
/// </summary>
internal static class ShaderSourceUpgrade
{
    /// <summary>
    /// The folders that hold the files of a package: the folder of its project, and its asset folders, which can be outside of it.
    /// </summary>
    public static IReadOnlyList<string> Folders(UFile projectFullPath, Package package)
    {
        var folders = new List<string> { projectFullPath.GetFullDirectory().ToOSPath() };
        if (package.RootDirectory is { } root)
            folders.AddRange(package.AssetFolders.Select(x => UPath.Combine(root, x.Path).ToOSPath()).Where(Directory.Exists));
        return folders.Distinct().ToList();
    }

    /// <param name="folders">The folders of the project (<see cref="Folders"/>); their shader, effect and data files are upgraded (<c>bin</c>, <c>obj</c> and the backups of earlier upgrades left out).</param>
    /// <param name="backup">The copy-on-write backup to snapshot each file into before writing it, or <c>null</c>.</param>
    public static void Run(IReadOnlyList<string> folders, UpgradeBackup backup, ILogger log)
    {
        RunSources(folders, backup, log);
        RunData(folders, backup, log);
    }

    // The stored data that names a shader or an effect, after the sources it names
    private static void RunData(IReadOnlyList<string> folders, UpgradeBackup backup, ILogger log)
    {
        var sources = ProjectFiles(folders, "*.sdsl").Concat(ProjectFiles(folders, "*.sdfx")).Select(File.ReadAllText);
        var classes = EngineShaderClasses.Current.WithProject(sources);
        foreach (var file in new[] { "*.sdeffectlog", "*.sdgfxcomp", "*.sdmat", "*.sdscene", "*.sdprefab" }.SelectMany(pattern => ProjectFiles(folders, pattern)))
        {
            var bytes = File.ReadAllBytes(file);
            var text = File.ReadAllText(file);
            string upgraded;
            try
            {
                upgraded = ShaderDataUpgrader.Upgrade(file, text, classes);
            }
            catch (YamlException e)
            {
                log.Warning($"Unable to read the file [{file}]: it keeps the names of its shaders and effects", e);
                continue;
            }
            if (upgraded == text)
                continue;
            try
            {
                backup?.Snapshot(file);
                File.WriteAllText(file, upgraded, new UTF8Encoding(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])));
                log.Info($"Upgraded [{file}] to the full names of shaders and effects");
            }
            catch (Exception e)
            {
                log.Warning($"Unable to write the upgraded file [{file}]", e);
            }
        }
    }

    private static IEnumerable<string> ProjectFiles(IReadOnlyList<string> folders, string pattern)
    {
        return folders.SelectMany(folder => Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories).Where(file =>
        {
            var parts = Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !parts.Contains("bin") && !parts.Contains("obj") && !parts.Any(x => x.StartsWith(".stride-backup", StringComparison.Ordinal));
        })).Distinct();
    }

    private static void RunSources(IReadOnlyList<string> folders, UpgradeBackup backup, ILogger log)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var filesWithBom = new HashSet<string>();
        foreach (var file in ProjectFiles(folders, "*.sdsl").Concat(ProjectFiles(folders, "*.sdfx")))
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
                filesWithBom.Add(file);
            files[file] = File.ReadAllText(file);
        }
        if (files.Count == 0)
            return;

        var result = ShaderUpgrader.Upgrade(files, EngineShaderClasses.Current, implicitUsings: ShaderImplicitUsings.List);
        foreach (var (file, text) in result.Files)
        {
            if (text == files[file])
                continue;
            try
            {
                backup?.Snapshot(file);
                File.WriteAllText(file, text, new UTF8Encoding(filesWithBom.Contains(file)));
                log.Info($"Upgraded shader [{file}] to the current shader language");
            }
            catch (Exception e)
            {
                log.Warning($"Unable to write the upgraded shader [{file}]", e);
            }
        }
        foreach (var note in result.Notes)
            log.Warning($"Shader upgrade: {note}");
    }
}
