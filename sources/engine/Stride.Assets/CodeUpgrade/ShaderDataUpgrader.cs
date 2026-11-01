// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Stride.Core;
using Stride.Core.Yaml;
using Stride.Core.Yaml.Events;
using Stride.Shaders.Parsing.Upgrade;

namespace Stride.Assets;

/// <summary>
/// Rewrites the stored data that names a shader, an effect or the key of a shader member to the full names: effect logs,
/// graphics compositors, materials, and the particle materials of scenes and prefabs. A name is found by its place in the YAML
/// document: a stage name or a placeholder such as <c>EffectName: Test</c> stays, and so does the rest of the file. Running it
/// again changes nothing.
/// </summary>
internal static class ShaderDataUpgrader
{
    // Where a scalar stands under the nearest key above it
    private enum Position { Value, SequenceItem, MappingKey }

    private sealed record Slot(Position Position, string Key, Func<string, EngineShaderClasses, string> ToFullName, string? MappingTag = null);

    private static readonly Slot[] EffectLog =
    [
        new(Position.Value, "ClassName", FullName),
        new(Position.Value, "EffectName", FullName),
        new(Position.Value, "ComputeEffectShader.ComputeShaderName", FullName),
        new(Position.Value, "Name", FullName, MappingTag: "!ShaderMixinGeneratorSource"),
        new(Position.SequenceItem, "GenericArguments", FullKey),
        new(Position.MappingKey, "UsedParameters", FullKey),
    ];

    private static readonly Slot[] Compositor = [new(Position.Value, "EffectName", FullName)];

    // By key alone: any class can derive from ComputeShaderClassBase<T>, so its tag is not a known list
    private static readonly Slot[] Material = [new(Position.Value, "MixinReference", FullName)];

    private static readonly Dictionary<string, Slot[]> SlotsByExtension = new()
    {
        [".sdeffectlog"] = EffectLog,
        [".sdgfxcomp"] = Compositor,
        [".sdmat"] = Material,
        [".sdscene"] = Material,
        [".sdprefab"] = Material,
    };

    /// <summary>
    /// The upgraded text of a data file.
    /// </summary>
    /// <param name="path">The path of the file: its extension decides what it holds.</param>
    /// <param name="text">The text of the file.</param>
    /// <param name="classes">The engine classes and effects, with those of the project (<see cref="EngineShaderClasses.WithProject"/>).</param>
    /// <exception cref="YamlException">The text isn't valid YAML.</exception>
    public static string Upgrade(string path, string text, EngineShaderClasses classes)
    {
        if (!SlotsByExtension.TryGetValue(Path.GetExtension(path).ToLowerInvariant(), out var slots))
            return text;

        var edits = new List<(int Start, int End, string Name)>();
        var reader = new EventReader(new Parser(new StringReader(text)));
        reader.Expect<StreamStart>();
        while (reader.Allow<StreamEnd>() == null)
        {
            reader.Expect<DocumentStart>();
            ReadNode(Position.Value, key: null, mappingTag: null);
            reader.Expect<DocumentEnd>();
        }
        return Apply(text, edits);

        void ReadNode(Position position, string? key, string? mappingTag)
        {
            if (reader.Allow<Scalar>() is { } scalar)
            {
                var slot = slots.FirstOrDefault(x => x.Position == position && x.Key == key && (x.MappingTag == null || x.MappingTag == mappingTag));
                if (slot == null || !IsOnlyAName(scalar))
                    return;
                var name = slot.ToFullName(scalar.Value, classes);
                var quote = scalar.Style == ScalarStyle.Plain ? 0 : 1;
                if (name != scalar.Value)
                    edits.Add((scalar.Start.Index + quote, scalar.End.Index - quote, name));
            }
            else if (reader.Allow<SequenceStart>() != null)
            {
                while (reader.Allow<SequenceEnd>() == null)
                    ReadNode(Position.SequenceItem, key, mappingTag: null);
            }
            else if (reader.Allow<MappingStart>() is { } mapping)
            {
                while (reader.Allow<MappingEnd>() == null)
                {
                    // Assets with an archetype mark a member they override: EffectName*: or EffectName!:
                    var childKey = reader.Peek<Scalar>()?.Value.TrimEnd('*', '!');
                    ReadNode(Position.MappingKey, key, mapping.Tag);
                    ReadNode(Position.Value, childKey, mapping.Tag);
                }
            }
            else
            {
                reader.Expect<AnchorAlias>();
            }
        }
    }

    // One word, and the span of the scalar holds only that: an anchor, a tag or the header of a block are part of it
    private static bool IsOnlyAName(Scalar scalar)
        => scalar is { Anchor: null, Tag: null, Style: ScalarStyle.Plain or ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted } && !scalar.Value.Any(char.IsWhiteSpace);

    private static string Apply(string text, List<(int Start, int End, string Name)> edits)
    {
        var result = new StringBuilder(text.Length);
        var position = 0;
        foreach (var (start, end, name) in edits)
        {
            result.Append(text, position, start - position).Append(name);
            position = end;
        }
        return result.Append(text, position, text.Length - position).ToString();
    }

    // Root.Child for an effect: the first segment is the name, and it is short in old data
    private static string FullName(string name, EngineShaderClasses classes)
    {
        var dot = name.IndexOf('.');
        var root = dot < 0 ? name : name[..dot];
        var fullName = classes.FullName(root);
        return fullName != null ? fullName + name[root.Length..] : name;
    }

    private static string FullKey(string key, EngineShaderClasses classes) => classes.ToFullKeyName(key);
}
