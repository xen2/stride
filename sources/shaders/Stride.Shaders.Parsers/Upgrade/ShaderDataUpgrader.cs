// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Text.RegularExpressions;

namespace Stride.Shaders.Parsing.Upgrade;

/// <summary>
/// Rewrites the stored data that names a shader, an effect or the key of a shader member to the full names: effect logs,
/// graphics compositors, materials, and the particle materials of scenes and prefabs. By YAML key, never on a value that only looks like a name: a stage name or a placeholder
/// such as <c>EffectName: Test</c> stays. Running it again changes nothing.
/// </summary>
public static class ShaderDataUpgrader
{
    private static readonly Regex EffectLogName = new(@"^(\s*(?:- )?(?:ClassName|EffectName|ComputeEffectShader\.ComputeShaderName): )(\S+?)(\s*)$", RegexOptions.Multiline);
    private static readonly Regex GeneratorSourceName = new(@"(!ShaderMixinGeneratorSource\s*\r?\n\s*Name: )(\S+?)(\s*)$", RegexOptions.Multiline);
    private static readonly Regex GenericArgument = new(@"^(\s*- )([A-Za-z_]\w*\.[\w.\[\]]+?)(\s*)$", RegexOptions.Multiline);
    private static readonly Regex UsedKey = new(@"^(\s*)([A-Za-z_]\w*\.[\w.\[\]]+?)(: )", RegexOptions.Multiline);
    // Assets with an archetype mark a member they override: EffectName*: or EffectName!:
    private static readonly Regex CompositorEffectName = new(@"^(\s*EffectName[*!]*: )(\S+?)(\s*)$", RegexOptions.Multiline);
    private static readonly Regex MixinReference = new(@"^(\s*MixinReference[*!]*: )([\w.]+?)(\s*)$", RegexOptions.Multiline);

    /// <summary>
    /// Whether <see cref="Upgrade"/> reads this kind of file.
    /// </summary>
    public static bool IsDataFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".sdeffectlog" or ".sdgfxcomp" or ".sdmat" or ".sdscene" or ".sdprefab";

    /// <summary>
    /// The upgraded text of a data file.
    /// </summary>
    /// <param name="path">The path of the file: its extension decides what it holds.</param>
    /// <param name="text">The text of the file.</param>
    /// <param name="classes">The engine classes and effects, with those of the project (<see cref="EngineShaderClasses.WithProject"/>).</param>
    public static string Upgrade(string path, string text, EngineShaderClasses classes)
    {
        string ToFullName(Match match) => Replace(match, FullName(match.Groups[2].Value, classes));
        string ToFullKey(Match match) => Replace(match, classes.ToFullKeyName(match.Groups[2].Value));

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".sdeffectlog":
                text = EffectLogName.Replace(text, ToFullName);
                text = GeneratorSourceName.Replace(text, ToFullName);
                text = GenericArgument.Replace(text, ToFullKey);
                return UsedKey.Replace(text, ToFullKey);
            case ".sdgfxcomp":
                return CompositorEffectName.Replace(text, ToFullName);
            case ".sdmat" or ".sdscene" or ".sdprefab":
                return MixinReference.Replace(text, ToFullName);
            default:
                return text;
        }
    }

    private static string Replace(Match match, string value) => $"{match.Groups[1].Value}{value}{match.Groups[3].Value}";

    // Root.Child for an effect: the first segment is the name, and it is short in old data
    private static string FullName(string name, EngineShaderClasses classes)
    {
        var dot = name.IndexOf('.');
        var root = dot < 0 ? name : name[..dot];
        var fullName = classes.FullName(root);
        return fullName != null ? fullName + name[root.Length..] : name;
    }
}
