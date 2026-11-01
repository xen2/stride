// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Xunit;

namespace Stride.Assets.Tests;

/// <summary>
/// Which files the upgrade of a project reaches.
/// </summary>
public class TestShaderSourceUpgrade : IDisposable
{
    private sealed class ShaderUpgrader : CodePackageUpgrader
    {
        public override void DeclareUpgrades(UpgradeRegistry registry) => registry.Shader("4.5.0.0");
    }

    private readonly string directory = Directory.CreateTempSubdirectory("stride-shader-upgrade-").FullName;

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Write(string path, string text)
    {
        var fullPath = Path.Combine(directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text);
        return fullPath;
    }

    private void Upgrade(params string[] assetFolders)
    {
        var package = new Package { Meta = { Name = "Game" }, FullPath = Path.Combine(directory, "Game", "Game.sdpkg") };
        foreach (var folder in assetFolders)
            package.AssetFolders.Add(new AssetFolder(folder));
        _ = new SolutionProject(package, Guid.NewGuid(), Write("Game/Game.csproj", "<Project />"));

        var dependency = new PackageDependency("Stride.Engine", new PackageVersionRange(new PackageVersion("4.4.1.0")));
        Assert.True(new ShaderUpgrader().UpgradeBeforeAssembliesLoaded(new PackageLoadParameters(), new PackageSession(), new LoggerResult(), package, dependency, null));
    }

    // A standard project keeps its data in ../Assets/Shared and its effect log names engine shaders
    [Fact]
    public void TheAssetFoldersOfThePackageAreUpgraded()
    {
        var log = Write("Assets/Shared/EffectCompileLog.sdeffectlog", "EffectName: StrideForwardShadingEffect\n");
        var scene = Write("Assets/Shared/Scene.sdscene", "    MixinReference: Own\n");
        var effect = Write("Game/Effects/Own.sdsl", "namespace Game.Effects; shader Own { stage float Value; };\n");
        var inAssets = Write("Assets/Shared/InAssets.sdsl", "shader InAssets { stage float Value; };\n");
        var material = Write("Game/Local.sdmat", "    MixinReference: InAssets\n");

        Upgrade("../Assets/Shared", "Effects", "NoSuchFolder");

        Assert.Equal("EffectName: Stride.Rendering.StrideForwardShadingEffect\n", File.ReadAllText(log));
        Assert.Equal("    MixinReference: Game.Effects.Own\n", File.ReadAllText(scene));
        Assert.Contains("module float Value;", File.ReadAllText(effect));
        Assert.Contains("module float Value;", File.ReadAllText(inAssets));
        Assert.Equal("    MixinReference: InAssets\n", File.ReadAllText(material));
    }

    [Fact]
    public void AKeyNamedInAnAssetFolderTakesTheFullNameOfItsClass()
    {
        var log = Write("Assets/Shared/EffectCompileLog.sdeffectlog", "    Own.Value: 1\n");
        Write("Game/Effects/Own.sdsl", "namespace Game.Effects; shader Own { float Value; };\n");

        Upgrade("../Assets/Shared");

        Assert.Equal("    Game.Effects.Own.Value: 1\n", File.ReadAllText(log));
    }

    [Theory]
    [InlineData("Game/bin/Old.sdeffectlog")]
    [InlineData("Game/obj/Old.sdeffectlog")]
    [InlineData("Assets/obj/Old.sdeffectlog")]
    [InlineData("Game/.stride-backup-20261102-073632/Old.sdeffectlog")]
    public void BuildOutputAndBackupsAreLeftAlone(string path)
    {
        const string text = "EffectName: StrideForwardShadingEffect\n";
        var file = Write(path, text);

        Upgrade("../Assets");

        Assert.Equal(text, File.ReadAllText(file));
    }

    // A project with no .sdpkg has the default folder, which often doesn't exist
    [Fact]
    public void AnAssetFolderThatDoesNotExistIsSkipped()
    {
        Upgrade("Assets");
    }

    // Projects get these names through the archetype of the default compositor
    [Fact]
    public void TrackedCompositorsNameEngineEffectsOrThePlaceholder()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, "sources", "engine", "Stride.Assets.Tests")))
            root = root.Parent ?? throw new DirectoryNotFoundException("Could not find the root of the repository");

        var sources = Path.Combine(root.FullName, "sources");
        var names = Directory.EnumerateFiles(sources, "*.sdgfxcomp", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(sources, file).Split(Path.DirectorySeparatorChar).Any(x => x is "bin" or "obj"))
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"^\s*EffectName: (.+?)\s*$", RegexOptions.Multiline).Select(x => (File: file, Name: x.Groups[1].Value)))
            .ToList();

        Assert.NotEmpty(names);
        Assert.All(names, x => Assert.True(x.Name is "Test" or "null" || x.Name.StartsWith("Stride.", StringComparison.Ordinal), $"{x.File}: {x.Name}"));
    }
}
