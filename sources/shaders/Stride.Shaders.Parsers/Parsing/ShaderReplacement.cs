// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Stride.Shaders.Parsing.SDSL.AST;

namespace Stride.Shaders.Parsing;

/// <summary>
/// <c>[Replaces(Full.Name)]</c> on a class: it joins every mix of that class, right after it.
/// </summary>
public static class ShaderReplacement
{
    /// <summary>
    /// The full name of the class this class replaces, or null.
    /// </summary>
    public static string? Target(ShaderClass shaderClass)
        => shaderClass.FindAttribute("Replaces") is { Parameters: [var target] } ? DottedName(target) : null;

    // A full name: an identifier, or identifiers with dots between them
    private static string? DottedName(Expression parameter) => parameter switch
    {
        Identifier identifier => identifier.Name,
        AccessorChainExpression { Source: Identifier source } chain when chain.Accessors.TrueForAll(x => x is Identifier)
            => string.Join('.', chain.Accessors.Select(x => ((Identifier)x).Name).Prepend(source.Name)),
        _ => null,
    };

    /// <summary>
    /// The classes of a file, with their namespace.
    /// </summary>
    public static IEnumerable<(string? Namespace, ShaderClass Class)> Classes(ShaderFile file)
        => file.RootDeclarations.OfType<ShaderClass>().Select(x => ((string?)null, x))
            .Concat(file.Namespaces.SelectMany(ns => ns.Declarations.OfType<ShaderClass>().Select(x => (ns.Namespace?.Name, x))));

    /// <summary>
    /// A source file that a build went through.
    /// </summary>
    /// <param name="Text">The text of the file.</param>
    /// <param name="FileName">The file name, without its extension.</param>
    /// <param name="Succeeded">Whether its asset built.</param>
    public readonly record struct BuiltFile(string Text, string FileName, bool Succeeded);

    /// <summary>
    /// The replaced classes whose replacer a build went through and no longer declares them. A build can only add what its files declare, so these stay otherwise.
    /// </summary>
    /// <param name="replacements">What is in effect: the replaced class and its replacer, by full name.</param>
    /// <param name="files">The files the build went through.</param>
    /// <remarks>
    /// A replacer that no file holds was deleted, or is built apart: <see cref="ShaderFileDeclarations.Holds"/> decides who holds it.
    /// A file that failed or doesn't parse says nothing, so its replacement stays until it is fixed.
    /// </remarks>
    public static List<string> NoLongerDeclared(IEnumerable<KeyValuePair<string, string>> replacements, IReadOnlyCollection<BuiltFile> files)
    {
        var result = new List<string>();
        foreach (var (target, replacer) in replacements)
        {
            var holders = files.Where(x => ShaderFileDeclarations.Holds(x.Text, x.FileName, replacer)).ToList();
            if (holders.Count > 0 && holders.All(x => x.Succeeded && !MayDeclare(x.Text, replacer, target)))
                result.Add(target);
        }
        return result;
    }

    private static bool MayDeclare(string text, string replacer, string target)
    {
        var parsed = SDSLParser.Parse(text);
        if (parsed.Errors.Count > 0 || parsed.AST is not ShaderFile file)
            return true;
        return Classes(file).Any(x => ShaderNames.Combine(x.Namespace, x.Class.Name.Name) == replacer && Target(x.Class) == target);
    }

    /// <summary>
    /// What makes a class unable to stand in for its target: an error message, or null.
    /// </summary>
    public static string? Check(string replacerName, ShaderClass replacer, string targetName, ShaderClass target)
    {
        var parent = replacer.Mixins.FirstOrDefault(x => IsTarget(ParentName(x), targetName));
        if (parent == null)
            return string.Format(SDSLErrorMessages.SDSL0305, replacerName, targetName, $"it must inherit {targetName} directly");
        if (replacer.IsModule != target.IsModule)
            return string.Format(SDSLErrorMessages.SDSL0305, replacerName, targetName, target.IsModule ? "a module is replaced by a module" : "a shader is replaced by a shader");
        if (replacer.FindAttribute("Root") != null && target.FindAttribute("Root") == null)
            return string.Format(SDSLErrorMessages.SDSL0305, replacerName, targetName, $"it is [Root] and {targetName} isn't, so {targetName} could no longer be used in a composition");

        var parameters = replacer.Generics?.Parameters ?? [];
        var targetParameters = target.Generics?.Parameters ?? [];
        var arguments = parent is GenericIdentifier { Generics: { } generics } ? generics.Values : [];
        var forwarded = parameters.Count == targetParameters.Count
            && arguments.Count == parameters.Count
            && parameters.Zip(targetParameters).All(x => x.First.TypeName.ToString() == x.Second.TypeName.ToString())
            && arguments.Zip(parameters).All(x => x.First is Identifier argument && argument.Name == x.Second.Name.Name);
        return forwarded ? null : string.Format(SDSLErrorMessages.SDSL0306, replacerName, targetName, string.Join(", ", targetParameters.Select(x => $"{x.TypeName} {x.Name}")));
    }

    private static string ParentName(IdentifierBase parent) => parent is GenericIdentifier generic ? generic.Name.Name : parent.Name;

    // The parent is written as in any class, through the scope of the replacer: any end of the full name
    private static bool IsTarget(string parent, string targetName) => targetName == parent || targetName.EndsWith($".{parent}", StringComparison.Ordinal);
}
