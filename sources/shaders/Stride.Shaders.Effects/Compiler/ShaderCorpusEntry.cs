// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stride.Graphics;

namespace Stride.Shaders.Compiler
{
    /// <summary>
    /// One permutation of the shader corpus: the effect given to the shader mixer, before the platform and profile macros
    /// are added. Stored as JSON so it can be committed and diffed, see <see cref="ReadFile"/> / <see cref="WriteFile"/>.
    /// </summary>
    public sealed class ShaderCorpusEntry
    {
        private string? hash;

        public ShaderCorpusEntry(string effectName, GraphicsProfile profile, EffectSource effect)
        {
            EffectName = effectName;
            Profile = profile;
            Effect = effect;
        }

        public string EffectName { get; }

        public GraphicsProfile Profile { get; }

        public EffectSource Effect { get; }

        /// <summary>
        /// Where this permutation was seen (effect log path, capture tag). Not part of <see cref="Hash"/>.
        /// </summary>
        public SortedSet<string> Sources { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// SHA-256 of the effect name, profile and mixin tree, without its linkage rules, which come from the same effect and parameters.
        /// </summary>
        public string Hash => hash ??= ComputeHash();

        /// <summary>
        /// Stable, readable identifier: the effect name and the first 10 hex digits of <see cref="Hash"/>.
        /// </summary>
        public string Id => $"{EffectName}.{Hash[..10]}";

        private string ComputeHash()
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                WriteEntry(writer, this, includeDetails: false);
            return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
        }

        public static List<ShaderCorpusEntry> ReadFile(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.EnumerateArray().Select(ReadEntry).ToList();
        }

        /// <summary>
        /// Writes the entries sorted by <see cref="Id"/>, so the file only changes where permutations change.
        /// </summary>
        public static void WriteFile(string path, IEnumerable<ShaderCorpusEntry> entries)
        {
            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartArray();
            foreach (var entry in entries.OrderBy(x => x.Id, StringComparer.Ordinal))
                WriteEntry(writer, entry, includeDetails: true);
            writer.WriteEndArray();
        }

        public static string ToJson(ShaderCorpusEntry entry)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                WriteEntry(writer, entry, includeDetails: true);
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public static ShaderCorpusEntry FromJson(string json)
        {
            using var document = JsonDocument.Parse(json);
            return ReadEntry(document.RootElement);
        }

        private static void WriteEntry(Utf8JsonWriter writer, ShaderCorpusEntry entry, bool includeDetails)
        {
            writer.WriteStartObject();
            writer.WriteString("effect", entry.EffectName);
            writer.WriteString("profile", entry.Profile.ToString());
            if (includeDetails && entry.Sources.Count > 0)
            {
                writer.WriteStartArray("sources");
                foreach (var source in entry.Sources)
                    writer.WriteStringValue(source);
                writer.WriteEndArray();
            }
            writer.WritePropertyName("mixin");
            WriteSource(writer, entry.Effect.Root, entry.Effect.Macros);
            if (includeDetails && entry.Effect.Linkage.Count > 0)
            {
                writer.WriteStartArray("linkage");
                foreach (var rule in entry.Effect.Linkage)
                {
                    writer.WriteStartObject();
                    writer.WriteString("kind", rule.Kind.ToString());
                    foreach (var (name, value) in new[] { ("shader", rule.Shader), ("owner", rule.Owner), ("method", rule.Method), ("parameters", rule.Parameters), ("phase", rule.Phase) })
                    {
                        if (value != null)
                            writer.WriteString(name, value);
                    }
                    foreach (var (name, values) in new[] { ("names", rule.Names), ("after", rule.After), ("before", rule.Before), ("phases", rule.Phases), ("mixins", rule.Mixins) })
                    {
                        if (values.Count == 0)
                            continue;
                        writer.WriteStartArray(name);
                        foreach (var value in values)
                            writer.WriteStringValue(value);
                        writer.WriteEndArray();
                    }
                    if (rule.Level != 0)
                        writer.WriteNumber("level", rule.Level);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        private static ShaderCorpusEntry ReadEntry(JsonElement element)
        {
            var root = element.GetProperty("mixin");
            var mixin = (ShaderMixinSource)ReadSource(root, isRoot: true);
            var effectName = element.GetProperty("effect").GetString()!;
            mixin.Name = effectName;
            var effect = new EffectSource(mixin);
            if (root.TryGetProperty("macros", out var macros))
            {
                foreach (var macro in macros.EnumerateArray())
                    effect.Macros.Add(new ShaderMacro(macro[0].GetString()!, macro[1].GetString()!));
            }
            if (element.TryGetProperty("linkage", out var linkage))
            {
                foreach (var rule in linkage.EnumerateArray())
                {
                    string? Text(string name) => rule.TryGetProperty(name, out var value) ? value.GetString() : null;
                    List<string> Names(string name) => rule.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
                    effect.Linkage.Add(new ShaderLinkageRule
                    {
                        Kind = Enum.Parse<ShaderLinkageKind>(Text("kind")!),
                        Shader = Text("shader")!,
                        Owner = Text("owner"),
                        Method = Text("method")!,
                        Parameters = Text("parameters"),
                        Phase = Text("phase"),
                        Names = Names("names"),
                        After = Names("after"),
                        Before = Names("before"),
                        Phases = Names("phases"),
                        Mixins = Names("mixins"),
                        Level = rule.TryGetProperty("level", out var level) ? level.GetInt32() : 0,
                    });
                }
            }
            var entry = new ShaderCorpusEntry(effectName, Enum.Parse<GraphicsProfile>(element.GetProperty("profile").GetString()!), effect);
            if (element.TryGetProperty("sources", out var sources))
            {
                foreach (var source in sources.EnumerateArray())
                    entry.Sources.Add(source.GetString()!);
            }
            return entry;
        }

        // Format: a class without generics is a string, other classes are { class, generics?, code? },
        // a mixin is { mixins, compositions? } and a composition array is a JSON array. The root mixin also holds the macros of the effect.
        private static void WriteSource(Utf8JsonWriter writer, ShaderSource source, List<ShaderMacro>? effectMacros = null)
        {
            switch (source)
            {
                case ShaderClassSource { GenericArguments: null or { Length: 0 } } classSource:
                    writer.WriteStringValue(classSource.ClassName);
                    break;
                case ShaderClassCode classCode:
                    writer.WriteStartObject();
                    writer.WriteString("class", classCode.ClassName);
                    if (classCode.GenericArguments is { Length: > 0 })
                    {
                        writer.WriteStartArray("generics");
                        foreach (var argument in classCode.GenericArguments)
                            writer.WriteStringValue(argument);
                        writer.WriteEndArray();
                    }
                    if (classCode is ShaderClassString classString)
                        writer.WriteString("code", classString.ShaderSourceCode);
                    writer.WriteEndObject();
                    break;
                case ShaderArraySource arraySource:
                    writer.WriteStartArray();
                    foreach (var value in arraySource.Values)
                        WriteSource(writer, value);
                    writer.WriteEndArray();
                    break;
                case ShaderMixinSource mixinSource:
                    writer.WriteStartObject();
                    writer.WriteStartArray("mixins");
                    foreach (var mixin in mixinSource.Mixins)
                        WriteSource(writer, mixin);
                    writer.WriteEndArray();
                    if (mixinSource.Compositions.Count > 0)
                    {
                        writer.WriteStartObject("compositions");
                        foreach (var composition in mixinSource.Compositions)
                        {
                            writer.WritePropertyName(composition.Key);
                            WriteSource(writer, composition.Value);
                        }
                        writer.WriteEndObject();
                    }
                    if (effectMacros is { Count: > 0 })
                    {
                        writer.WriteStartArray("macros");
                        foreach (var macro in effectMacros)
                        {
                            writer.WriteStartArray();
                            writer.WriteStringValue(macro.Name);
                            writer.WriteStringValue(macro.Definition);
                            writer.WriteEndArray();
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                    break;
                default:
                    throw new NotSupportedException($"Shader source type {source.GetType().Name} can't be stored in the shader corpus");
            }
        }

        private static ShaderSource ReadSource(JsonElement element, bool isRoot = false)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return new ShaderClassSource(element.GetString()!);
                case JsonValueKind.Array:
                    var arraySource = new ShaderArraySource();
                    foreach (var value in element.EnumerateArray())
                        arraySource.Add(ReadSource(value));
                    return arraySource;
                case JsonValueKind.Object when element.TryGetProperty("class", out var className):
                    var generics = element.TryGetProperty("generics", out var genericsElement)
                        ? genericsElement.EnumerateArray().Select(x => x.GetString()!).ToArray()
                        : null;
                    if (element.TryGetProperty("code", out var code))
                        return new ShaderClassString(className.GetString()!, code.GetString()!) { GenericArguments = generics };
                    return new ShaderClassSource(className.GetString()!) { GenericArguments = generics };
                case JsonValueKind.Object:
                    if (!isRoot && element.TryGetProperty("macros", out _))
                        throw new InvalidDataException("A mixin inside a composition holds macros in the shader corpus: macros belong to the whole effect");
                    var mixinSource = new ShaderMixinSource();
                    foreach (var mixin in element.GetProperty("mixins").EnumerateArray())
                        mixinSource.Mixins.Add((ShaderClassCode)ReadSource(mixin));
                    if (element.TryGetProperty("compositions", out var compositions))
                    {
                        foreach (var composition in compositions.EnumerateObject())
                            mixinSource.Compositions.Add(composition.Name, ReadSource(composition.Value));
                    }
                    return mixinSource;
                default:
                    throw new InvalidDataException($"Unexpected JSON value {element.ValueKind} in shader corpus");
            }
        }
    }
}
