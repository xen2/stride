// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Text;

namespace Stride.Shaders.Parsing.Upgrade;

/// <summary>
/// The engine shader classes and effects as the upgrade of a project needs them: kind (module, shader or effect), compose type,
/// <c>[Root]</c>, parents, abstract methods, full name, generated keys, contributions, extension points. When a project is upgraded, the engine shaders on disk are those of its old version, so this list of the
/// current engine is embedded instead (<c>EngineShaders.txt</c>, checked against the engine sources by a test).
/// </summary>
public sealed class EngineShaderClasses
{
    private const string ResourceName = "Stride.Shaders.Parsing.Upgrade.EngineShaders.txt";

    internal List<ShaderUpgradeClass> Classes { get; } = [];

    /// <summary>
    /// The full names of the engine shaders and effects that Stride 4.5 removed, because nothing used them.
    /// </summary>
    public static IReadOnlySet<string> Removed { get; } = new HashSet<string>
    {
        "Stride.Rendering.BackgroundVelocity",
        "Stride.Rendering.BackgroundVelocityEffect",
        "Stride.Rendering.ComputeColorScaler",
        "Stride.Rendering.Deferred.GBuffer",
        "Stride.Rendering.FlattenLayers",
        "Stride.Rendering.SelectedSprite",
        "Stride.Rendering.SelectedSpriteShader",
        "Stride.Rendering.ShadingColor",
        "Stride.Rendering.SwapUV",
        "Stride.Rendering.TransformationMatrix",
        "Stride.Rendering.TransformationWVP",
        "Stride.Rendering.TransformationZero",
    };

    /// <summary>
    /// The end of the message of a name that can't be found, when it is the short or full name of a removed engine shader; otherwise empty.
    /// </summary>
    public static string RemovedHint(string name)
        => Removed.Contains(name) || Removed.Any(x => ShaderNames.ShortName(x) == name)
            ? $" {name} was removed from the engine in Stride 4.5. To keep using it, copy it from the sources of Stride 4.4 into your project."
            : "";

    /// <summary>
    /// The full names of the effects, by short name.
    /// </summary>
    public Dictionary<string, string> Effects { get; } = [];

    /// <summary>
    /// The full name of a class or effect named by its short name, or null.
    /// </summary>
    public string? FullName(string shortName)
        => Effects.GetValueOrDefault(shortName) ?? Classes.FirstOrDefault(x => x.Name == shortName)?.FullName;

    /// <summary>
    /// The name of a key in the form of today. The key of a member of a namespaced class has the full name of the class,
    /// with its composition path after it. A key declared in C#, or of a global class, doesn't change.
    /// </summary>
    public string ToFullKeyName(string keyName)
    {
        var dot = keyName.IndexOf('.');
        if (dot <= 0)
            return keyName;

        var memberEnd = keyName.IndexOfAny(['.', '['], dot + 1);
        var member = memberEnd < 0 ? keyName[(dot + 1)..] : keyName[(dot + 1)..memberEnd];
        var shaderClass = Classes.FirstOrDefault(x => x.Name == keyName[..dot]);
        return shaderClass is { FullName: { } fullName } && fullName != shaderClass.Name && shaderClass.Keys.Contains(member)
            ? $"{fullName}{keyName[dot..]}"
            : keyName;
    }

    /// <summary>
    /// The full form of a text that starts like the key of a member of a namespaced class, or null. What follows the name of the
    /// class is the start of one of its keys, or nothing: <c>Texturing.</c> and <c>Texturing.Tex</c> match, <c>Texturing.Other</c> doesn't.
    /// </summary>
    public string? KeyNameStart(string text)
    {
        var dot = text.IndexOf('.');
        if (dot <= 0)
            return null;

        var memberEnd = text.IndexOfAny(['.', '['], dot + 1);
        var member = memberEnd < 0 ? text[(dot + 1)..] : text[(dot + 1)..memberEnd];
        var shaderClass = Classes.FirstOrDefault(x => x.Name == text[..dot]);
        return shaderClass is { FullName: { } fullName } && fullName != shaderClass.Name && shaderClass.Keys.Any(x => x.StartsWith(member, StringComparison.Ordinal))
            ? $"{fullName}{text[dot..]}"
            : null;
    }

    /// <summary>
    /// This list with the classes and effects of a project on top: a project class wins over an engine class of the same name,
    /// and the first of two project classes of one name wins.
    /// </summary>
    /// <param name="sources">The texts of the project's <c>.sdsl</c> and <c>.sdfx</c> files. One that doesn't parse gives its names, not its keys.</param>
    public EngineShaderClasses WithProject(IEnumerable<string> sources)
    {
        var result = new EngineShaderClasses();
        result.Classes.AddRange(Classes);
        foreach (var (name, fullName) in Effects)
            result.Effects[name] = fullName;

        var project = new HashSet<string>();
        foreach (var source in sources)
        {
            var parsed = ShaderUpgradeProject.Parse("", ShaderUpgrader.WithTextSteps(source), []).ToList();
            foreach (var declaration in ShaderFileDeclarations.Read(source).Where(x => project.Add(x.Name)))
            {
                result.Effects.Remove(declaration.Name);
                result.Classes.RemoveAll(x => x.Name == declaration.Name);
                if (declaration.IsEffect)
                    result.Effects[declaration.Name] = declaration.FullName;
                else
                    result.Classes.Add(new ShaderUpgradeClass { Name = declaration.Name, FullName = declaration.FullName, Keys = parsed.FirstOrDefault(x => x.Name == declaration.Name)?.Keys ?? [] });
            }
        }
        return result;
    }

    /// <summary>
    /// The list of the engine this assembly belongs to.
    /// </summary>
    public static EngineShaderClasses Current { get; } = Load();

    private static EngineShaderClasses Load()
    {
        using var stream = typeof(EngineShaderClasses).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing resource {ResourceName}");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Reads the format of <see cref="Write"/>: one class per line, <c>name, generic parameter count, module|shader|effect, compose|-,
    /// root|-, parents, abstract methods, full name, generated keys, contributions, extension points</c> separated by tabs; <c>#</c> starts a comment
    /// line. An abstract method or an extension point is <c>Name(parameter types)</c>, several separated by <c>;</c>. A contribution is the name of a
    /// module method the class overrides or extends.
    /// </summary>
    public static EngineShaderClasses Parse(string text)
    {
        var result = new EngineShaderClasses();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            var columns = trimmed.Split('\t');
            if (columns[2] == "effect")
            {
                result.Effects[columns[0]] = columns[7];
                continue;
            }

            var shaderClass = new ShaderUpgradeClass
            {
                Name = columns[0],
                GenericParameterCount = int.Parse(columns[1]),
                IsModule = columns[2] == "module",
                IsComposeType = columns[3] == "compose",
                IsRoot = columns[4] == "root",
                Parents = columns.Length > 5 && columns[5].Length > 0 ? [.. columns[5].Split(',')] : [],
                FullName = columns.Length > 7 ? columns[7] : columns[0],
                Keys = columns.Length > 8 && columns[8].Length > 0 ? [.. columns[8].Split(',')] : [],
            };
            foreach (var method in columns.Length > 6 && columns[6].Length > 0 ? columns[6].Split(';') : [])
            {
                var open = method.IndexOf('(');
                shaderClass.Methods.Add((method[..open], shaderClass.IsModule, IsOverride: false, IsAbstract: true, method[(open + 1)..^1], ""));
            }
            foreach (var contribution in columns.Length > 9 && columns[9].Length > 0 ? columns[9].Split(',') : [])
                shaderClass.Contributions.Add(contribution);
            foreach (var point in columns.Length > 10 && columns[10].Length > 0 ? columns[10].Split(';') : [])
            {
                var open = point.IndexOf('(');
                shaderClass.DeclaredKinds.Add((point[..open], point[(open + 1)..^1]));
            }
            result.Classes.Add(shaderClass);
        }
        return result;
    }

    /// <summary>
    /// The list for the given engine sources (path → text), already upgraded.
    /// </summary>
    public static string Write(IReadOnlyDictionary<string, string> engineFiles)
    {
        var notes = new List<string>();
        var classFiles = engineFiles.Where(x => !ShaderUpgrader.IsEffectFile(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        var project = new ShaderUpgradeProject(classFiles, null, notes);
        var declarations = engineFiles.Values.SelectMany(ShaderFileDeclarations.Read).ToList();
        if (notes.Count > 0)
            throw new InvalidOperationException($"Engine shaders can't be read:\n{string.Join("\n", notes)}");

        var result = new StringBuilder();
        result.Append("# Engine shader classes for the shader source upgrade (ShaderUpgrader): name, generic parameter count, module|shader|effect, compose|-, root|- ([Root]), parents, abstract methods, full name, generated keys, contributions, extension points.\n");
        result.Append("# Generated from the engine .sdsl files by the explicit test ShaderUpgradeTests.UpdateEngineShaderClasses.\n");
        foreach (var shaderClass in project.ProjectClasses.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var fullName = declarations.FirstOrDefault(x => !x.IsEffect && x.Name == shaderClass.Name).FullName ?? shaderClass.Name;
            result.Append($"{shaderClass.Name}\t{shaderClass.GenericParameterCount}\t{(shaderClass.IsModule ? "module" : "shader")}\t{(project.IsComposeType(shaderClass.Name) ? "compose" : "-")}\t{(shaderClass.IsRoot ? "root" : "-")}\t{string.Join(",", shaderClass.Parents)}\t{string.Join(";", shaderClass.Methods.Where(x => x.IsAbstract).Select(x => $"{x.Name}({x.Signature})"))}\t{fullName}\t{string.Join(",", shaderClass.Keys.Order(StringComparer.Ordinal))}\t{string.Join(",", shaderClass.Contributions.Order(StringComparer.Ordinal))}\t{string.Join(";", shaderClass.DeclaredKinds.Select(x => $"{x.Name}({x.Signature})").Order(StringComparer.Ordinal))}\n");
        }
        foreach (var effect in declarations.Where(x => x.IsEffect).DistinctBy(x => x.Name).OrderBy(x => x.Name, StringComparer.Ordinal))
            result.Append($"{effect.Name}\t0\teffect\t-\t-\t\t\t{effect.FullName}\t\n");
        return result.ToString();
    }
}
