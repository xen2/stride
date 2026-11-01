// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Shaders.Parsing.Analysis;

/// <summary>
/// Where a class or effect is declared, for the lookup of the names it uses. As in C#, closest first: its namespace, the usings
/// inside that namespace, the parent namespaces, the global namespace, then the usings of the file and the implicit usings at one level.
/// </summary>
public sealed class ShaderNameScope(string? @namespace, IReadOnlyList<string> namespaceUsings, IReadOnlyList<string> fileUsings)
{
    public static readonly ShaderNameScope Global = new(null, [], []);

    public string? Namespace { get; } = @namespace;

    public IReadOnlyList<string> NamespaceUsings { get; } = namespaceUsings;

    public IReadOnlyList<string> FileUsings { get; } = fileUsings;

    /// <summary>
    /// The full name of <paramref name="name"/>, or null. <paramref name="ambiguousWith"/> is the second match of two at one using level.
    /// A dotted name is not looked up through usings, which don't bring nested namespaces.
    /// </summary>
    public string? Resolve(string name, IReadOnlyList<string> implicitUsings, Func<string, bool> exists, out string? ambiguousWith)
    {
        ambiguousWith = null;
        var dotted = name.Contains('.');

        if (Namespace != null)
        {
            var own = $"{Namespace}.{name}";
            if (exists(own))
                return own;

            if (!dotted && OneOf(name, NamespaceUsings, exists, out ambiguousWith) is { } fromNamespaceUsing)
                return fromNamespaceUsing;

            for (var parent = ShaderNames.Namespace(Namespace); parent != null; parent = ShaderNames.Namespace(parent))
            {
                var inParent = $"{parent}.{name}";
                if (exists(inParent))
                    return inParent;
            }
        }

        if (exists(name))
            return name;

        return dotted ? null : OneOf(name, FileUsings.Concat(implicitUsings).Distinct(), exists, out ambiguousWith);
    }

    private static string? OneOf(string name, IEnumerable<string> namespaces, Func<string, bool> exists, out string? second)
    {
        string? first = null;
        second = null;
        foreach (var @namespace in namespaces)
        {
            var candidate = $"{@namespace}.{name}";
            if (!exists(candidate))
                continue;
            if (first != null)
            {
                second = candidate;
                return first;
            }
            first = candidate;
        }
        return first;
    }

    /// <summary>
    /// Code around a class compiled again on its own, which gives it this scope. On one line, so that lines stay the same.
    /// </summary>
    public (string Before, string After) WrapperCode()
    {
        var usings = string.Concat(FileUsings.Select(x => $"using {x}; "));
        return Namespace == null
            ? (usings, "")
            : ($"{usings}namespace {Namespace} {{ {string.Concat(NamespaceUsings.Select(x => $"using {x}; "))}", " }");
    }
}
