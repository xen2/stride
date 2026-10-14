// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Shaders.Parsing.Upgrade;

namespace Stride.Assets;

/// <summary>
/// Upgrades the <c>.sdsl</c> files of a project to the current shader language (<see cref="ShaderUpgrader"/>).
/// </summary>
internal static class ShaderSourceUpgrade
{
    /// <param name="projectDirectory">The project folder; its <c>.sdsl</c> files are upgraded (<c>bin</c> and <c>obj</c> left out).</param>
    /// <param name="backup">The copy-on-write backup to snapshot each file into before writing it, or <c>null</c>.</param>
    public static void Run(string projectDirectory, UpgradeBackup backup, ILogger log)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var filesWithBom = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(projectDirectory, "*.sdsl", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(projectDirectory, file);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj"))
                continue;

            var bytes = File.ReadAllBytes(file);
            if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
                filesWithBom.Add(file);
            files[file] = File.ReadAllText(file);
        }
        if (files.Count == 0)
            return;

        var result = ShaderUpgrader.Upgrade(files, EngineShaderClasses.Current);
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
