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
