// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Stride.Shaders.Parsing.Upgrade;

namespace Stride.Shaders.Parsing;

/// <summary>
/// Full names of shader classes and effects: <c>Namespace.Name</c>, with generic arguments after them once instantiated.
/// A class with no namespace has its short name as its full name.
/// </summary>
public static class ShaderNames
{
    /// <summary>
    /// The class without its namespace and generic arguments: <c>A.B.C&lt;3,X.Y&gt;</c> gives <c>C</c>.
    /// </summary>
    public static string ShortName(string fullName) => ShortNameWithGenerics(WithoutGenerics(fullName));

    /// <summary>
    /// The class without its namespace: <c>A.B.C&lt;3,X.Y&gt;</c> gives <c>C&lt;3,X.Y&gt;</c>.
    /// </summary>
    public static string ShortNameWithGenerics(string fullName)
    {
        var dot = NamespaceEnd(fullName);
        return dot < 0 ? fullName : fullName[(dot + 1)..];
    }

    /// <summary>
    /// The namespace, or null for a class in the global namespace.
    /// </summary>
    public static string? Namespace(string fullName)
    {
        var dot = NamespaceEnd(fullName);
        return dot < 0 ? null : fullName[..dot];
    }

    public static string Combine(string? @namespace, string name) => string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";

    private static string WithoutGenerics(string name)
    {
        var generics = name.IndexOf('<');
        return generics < 0 ? name : name[..generics];
    }

    // The last dot before the generic arguments
    private static int NamespaceEnd(string name)
    {
        var generics = name.IndexOf('<');
        return generics < 0 ? name.LastIndexOf('.') : name.LastIndexOf('.', generics);
    }
}

/// <summary>
/// A class or effect declared at the top of a source file.
/// </summary>
public readonly record struct ShaderFileDeclaration(string? Namespace, string Name, bool IsEffect)
{
    public string FullName => ShaderNames.Combine(Namespace, Name);
}

/// <summary>
/// What a source file declares, read from its text without compiling it: the build, the loaders and the upgrader use it to know
/// a file's full name. Comment-aware, and it doesn't need the file to parse.
/// </summary>
public static class ShaderFileDeclarations
{
    public static List<ShaderFileDeclaration> Read(string text)
    {
        var source = new ShaderSourceText(text);
        var result = new List<ShaderFileDeclaration>();
        var namespaces = new Stack<string?>();
        string? fileNamespace = null;

        for (int i = 0; i < text.Length;)
        {
            var c = text[i];
            if (!source.IsCode[i] || char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '#')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
            }
            else if (c == '[')
            {
                var end = source.NextCode(']', i);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (c == '}')
            {
                if (namespaces.Count > 0)
                    namespaces.Pop();
                i++;
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                var word = ReadWord(text, ref i, dots: false);
                switch (word)
                {
                    case "namespace":
                        {
                            SkipCodeSpaces(source, ref i);
                            var name = ReadWord(text, ref i, dots: true);
                            SkipCodeSpaces(source, ref i);
                            if (i < text.Length && text[i] == '{')
                                namespaces.Push(name);
                            else
                                fileNamespace = name;
                            i++;
                            break;
                        }
                    case "using":
                        {
                            var end = source.NextCode(';', i);
                            i = end < 0 ? text.Length : end + 1;
                            break;
                        }
                    case "shader" or "class" or "module" or "effect" or "params":
                        {
                            SkipCodeSpaces(source, ref i);
                            var name = ReadWord(text, ref i, dots: false);
                            // The keys of params are C#, and several files can write one params class
                            if (name.Length > 0 && word != "params")
                                result.Add(new(namespaces.Count > 0 ? namespaces.Peek() : fileNamespace, name, word == "effect"));
                            var open = source.NextCode('{', i);
                            var close = open < 0 ? -1 : source.MatchingBrace(open);
                            i = close < 0 ? text.Length : close + 1;
                            break;
                        }
                }
            }
            else
            {
                i++;
            }
        }
        return result;
    }

    /// <summary>
    /// The full name a file is stored and found under: the namespace of its class named like the file, else of its first class, then the file name.
    /// </summary>
    public static string StorageName(string text, string fileName)
    {
        var declarations = Read(text);
        var classes = declarations.Where(x => !x.IsEffect).ToList();
        var candidates = classes.Count > 0 ? classes : declarations;
        var main = candidates.Where(x => x.Name == fileName).Select(x => (ShaderFileDeclaration?)x).FirstOrDefault() ?? candidates.Select(x => (ShaderFileDeclaration?)x).FirstOrDefault();
        return ShaderNames.Combine(main?.Namespace, fileName);
    }

    /// <summary>
    /// Whether a file with this file name holds <paramref name="fullName"/>: it declares it, or it is the name it is stored under.
    /// </summary>
    public static bool Holds(string text, string fileName, string fullName)
        => Read(text).Any(x => x.FullName == fullName) || StorageName(text, fileName) == fullName;

    private static string ReadWord(string text, ref int i, bool dots)
    {
        var start = i;
        while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_' || (dots && text[i] == '.')))
            i++;
        return text[start..i];
    }

    private static void SkipCodeSpaces(ShaderSourceText source, ref int i)
    {
        while (i < source.Text.Length && (!source.IsCode[i] || char.IsWhiteSpace(source.Text[i])))
            i++;
    }
}
