// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Stride.Core.Diagnostics;
using Stride.Core.Storage;
using Stride.Rendering;
using Stride.Shaders;
using Stride.Shaders.Compilers;
using Stride.Shaders.Compilers.SDSL;
using Stride.Shaders.Spirv.Building;

namespace Stride.MaterialTestBed;

/// <summary>
///   Mixes shaders from the sources of the repository under the rules of the asset build, with warnings as errors.
/// </summary>
internal static class TestBedMixer
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static readonly Lazy<Dictionary<string, string>> Index = new(() =>
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "sources", "engine"), "*.sdsl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = file.Replace('\\', '/');
            if (!relative.Contains("/bin/") && !relative.Contains("/obj/"))
                index.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        }
        return index;
    });

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "sources", "engine", "Stride.MaterialTestBed.Tests")))
                return directory.FullName;
        }
        throw new InvalidOperationException($"Can't find the repository root above {AppContext.BaseDirectory}");
    }

    public readonly record struct Result(bool Ok, string Log, string Explanation, byte[] Bytecode)
    {
        /// <summary>The lines of the explanation about a method, the first node that has it.</summary>
        public string[] Block(string method)
        {
            var lines = Explanation.Split('\n').Select(x => x.TrimEnd()).ToList();
            var start = lines.FindIndex(x => x.Contains($"method {method}("));
            return start < 0 ? [] : lines.Skip(start).TakeWhile((x, i) => i == 0 || x.StartsWith("    ", StringComparison.Ordinal)).Select(x => x.Trim()).ToArray();
        }
    }

    /// <summary>Mixes classes in this order. Names without a namespace are classes of the test-bed.</summary>
    public static Result Mix(params string[] classes) => Mix(Effect(classes));

    /// <summary>The effect of the test-bed that has this name.</summary>
    public static Result MixEffect(string name, bool isAssetBuild = true)
        => Mix(ShaderMixinManager.Generate($"Stride.MaterialTestBed.{name}", new ParameterCollection()), isAssetBuild);

    public static EffectSource Effect(params string[] classes)
    {
        var source = new ShaderMixinSource();
        foreach (var name in classes)
            source.Mixins.Add(new ShaderClassSource(FullName(name)));
        return new EffectSource(source);
    }

    public static string FullName(string name) => name.Contains('.') ? name : name is "SpriteBase" or "ShaderBase" or "ShadingBase" ? $"Stride.Rendering.{name}" : $"Stride.MaterialTestBed.{name}";

    /// <param name="sources">Classes that only this mix can see, by file name.</param>
    public static Result Mix(EffectSource effect, bool isAssetBuild = true, IReadOnlyDictionary<string, string> sources = null)
    {
        var log = new LoggerResult();
        var loader = new SourceLoader(sources) { WarningsAsErrors = true, ImplicitUsings = ShaderImplicitUsings.List };
        var mixer = new ShaderMixer(loader) { CollectExplanation = true, IsAssetBuild = isAssetBuild };
        var ok = false;
        byte[] bytecode = [];
        try
        {
            ok = mixer.MergeSDSL(effect, new ShaderMixer.Options(true), log, out var words, out _, out _, out _) && !log.HasErrors;
            bytecode = words.ToArray();
        }
        catch (InvalidOperationException e)
        {
            log.Error(e.Message);
        }
        return new Result(ok, string.Join(Environment.NewLine, log.Messages.Select(x => $"{x.Type}: {x.Text}")), mixer.Explanation ?? "", bytecode);
    }

    private sealed class SourceLoader(IReadOnlyDictionary<string, string> sources) : SourceFileShaderLoader(new ShaderCache())
    {
        protected override bool TryReadSourceFile(string fileName, out string filename, out string code, out ObjectId hash)
        {
            byte[] data;
            if (sources != null && sources.TryGetValue(fileName, out var text))
            {
                filename = $"{fileName}.sdsl";
                data = Encoding.UTF8.GetBytes(text);
            }
            else if (Index.Value.TryGetValue(fileName, out var path))
            {
                filename = path;
                data = File.ReadAllBytes(path);
            }
            else
            {
                filename = code = "";
                hash = default;
                return false;
            }

            hash = ObjectId.FromBytes(data);
            using var reader = new StreamReader(new MemoryStream(data), Encoding.UTF8);
            code = reader.ReadToEnd();
            return true;
        }
    }
}
