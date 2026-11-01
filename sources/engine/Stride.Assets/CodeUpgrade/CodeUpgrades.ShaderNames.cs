// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Stride.Shaders.Parsing.Upgrade;

namespace Stride.Assets;

public static partial class CodeUpgrades
{
    // The first argument names a shader or an effect
    private static readonly HashSet<string> ClassNameConstructors =
    [
        "Stride.Shaders.ShaderClassSource",
        "Stride.Shaders.ShaderMixinGeneratorSource",
        "Stride.Rendering.Images.ImageEffectShader",
        "Stride.Rendering.Images.ColorTransformBase",
        "Stride.Rendering.Images.ColorTransform",
        "Stride.Rendering.DynamicEffectInstance",
    ];

    private static readonly HashSet<string> ClassNameMethods =
    [
        "Stride.Rendering.EffectSystem.LoadEffect",
    ];

    private static readonly HashSet<string> ClassNameProperties =
    [
        "Stride.Rendering.ComputeEffect.ComputeEffectShader.ShaderSourceName",
        "Stride.Rendering.Images.ImageEffectShader.EffectName",
        "Stride.Rendering.DynamicEffectInstance.EffectName",
        "Stride.Rendering.SimpleGroupToRenderStageSelector.EffectName",
        "Stride.Rendering.TransparentRenderStageSelector.EffectName",
        "Stride.Rendering.Shadows.ShadowMapRenderStageSelector.EffectName",
        "Stride.Rendering.Materials.ComputeColors.ComputeShaderClassBase<T>.MixinReference",
        "Stride.Particles.Materials.ParticleMaterial.EffectName",
    ];

    // The first argument names a key
    private static readonly HashSet<string> KeyNameMethods =
    [
        "Stride.Rendering.ParameterKeys.FindByName",
        "Stride.Rendering.ParameterKeys.TryFindByName",
        "Stride.Rendering.RootEffectRenderFeature.CreateFrameCBufferOffsetSlot",
        "Stride.Rendering.RootEffectRenderFeature.CreateViewCBufferOffsetSlot",
        "Stride.Rendering.RootEffectRenderFeature.CreateDrawCBufferOffsetSlot",
    ];

    /// <summary>
    /// Migrates the string literals that name a shader or an effect of the engine or of the project, or the key of one of their
    /// members, to their full names: <c>new ShaderClassSource("Texturing")</c> → <c>new ShaderClassSource("Stride.Rendering.Texturing")</c>.
    /// Only the arguments and assignments of the APIs that take such a name, by symbol: a string that merely looks like a name doesn't change.
    /// </summary>
    /// <param name="engine">The engine classes and effects. A class of the project wins over an engine class of the same name.</param>
    public static CodeUpgrade ShaderNames(EngineShaderClasses engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return async (solution, targets, cancellationToken) =>
        {
            var classes = await WithProjectClasses(engine, solution, cancellationToken);
            foreach (var projectId in targets)
            {
                foreach (var documentId in solution.GetProject(projectId)?.DocumentIds ?? [])
                {
                    var document = solution.GetDocument(documentId);
                    if (document is null || !SymbolRewriteEngine.IsUpgradableSource(document))
                        continue;

                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    var model = await document.GetSemanticModelAsync(cancellationToken);
                    if (root is null || model is null)
                        continue;

                    var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
                    foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(x => x.IsKind(SyntaxKind.StringLiteralExpression)))
                    {
                        if (NewShaderName(literal, model, classes, cancellationToken) is { } name && name != literal.Token.ValueText)
                            replacements[literal] = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(name)).WithTriviaFrom(literal);
                    }

                    if (replacements.Count > 0)
                        solution = document.WithSyntaxRoot(root.ReplaceNodes(replacements.Keys, (node, _) => replacements[node])).Project.Solution;
                }
            }
            return solution;
        };
    }

    // The shaders and effects are the additional files of the projects, for the source generator
    private static async Task<EngineShaderClasses> WithProjectClasses(EngineShaderClasses engine, Solution solution, CancellationToken cancellationToken)
    {
        var sources = new List<string>();
        foreach (var document in solution.Projects.SelectMany(x => x.AdditionalDocuments).Where(x => Path.GetExtension(x.Name).ToLowerInvariant() is ".sdsl" or ".sdfx"))
            sources.Add((await document.GetTextAsync(cancellationToken)).ToString());
        return engine.WithProject(sources);
    }

    private static string NewShaderName(LiteralExpressionSyntax literal, SemanticModel model, EngineShaderClasses classes, CancellationToken cancellationToken)
    {
        // Root.Child for an effect, Class<generics> for a shader: the first part is the name
        string FullClassName(string name)
        {
            var end = name.IndexOfAny(['.', '<']);
            var root = end < 0 ? name : name[..end];
            return classes.FullName(root) is { } fullName ? fullName + name[root.Length..] : name;
        }

        var value = literal.Token.ValueText;
        if (model.GetTypeInfo(literal, cancellationToken).ConvertedType?.ToDisplayString() == "Stride.Shaders.ShaderClassSource")
            return FullClassName(value);

        switch (literal.Parent)
        {
            case AssignmentExpressionSyntax assignment when assignment.Right == literal:
                return NamesClass(model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol, model, cancellationToken) ? FullClassName(value) : null;

            // A class can name its effect by overriding the property: => "X", return "X", { get; } = "X", or a field the property uses
            case ArrowExpressionClauseSyntax or ReturnStatementSyntax or EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax }:
                return literal.FirstAncestorOrSelf<PropertyDeclarationSyntax>() is { } declaration && NamesClass(model.GetDeclaredSymbol(declaration, cancellationToken), model, cancellationToken)
                    ? FullClassName(value)
                    : null;

            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable }:
                return NamesClass(model.GetDeclaredSymbol(variable, cancellationToken), model, cancellationToken) ? FullClassName(value) : null;

            case ArgumentSyntax { Parent: ArgumentListSyntax arguments } argument:
                var index = arguments.Arguments.IndexOf(argument);
                if (model.GetSymbolInfo(arguments.Parent!, cancellationToken).Symbol is not IMethodSymbol method)
                    return null;

                if (method.MethodKind == MethodKind.Constructor)
                {
                    var type = method.ContainingType.ToDisplayString();
                    if (!ClassNameConstructors.Contains(type))
                        return null;
                    if (index == 0)
                        return FullClassName(value);
                    // The other arguments of a ShaderClassSource are its generic arguments: a key, or plain text that doesn't change
                    return type == "Stride.Shaders.ShaderClassSource" ? classes.ToFullKeyName(value) : null;
                }

                var name = FullMemberName(method);
                if (index == 0 && ClassNameMethods.Contains(name))
                    return FullClassName(value);
                return index == 0 && KeyNameMethods.Contains(name) ? classes.ToFullKeyName(value) : null;

            default:
                return null;
        }
    }

    // A property that takes the name of a class, or a field that such a property of its type uses
    private static bool NamesClass(ISymbol symbol, SemanticModel model, CancellationToken cancellationToken) => symbol switch
    {
        IPropertySymbol property => IsClassNameProperty(property),
        IFieldSymbol field => field.ContainingType.GetMembers().OfType<IPropertySymbol>().Where(IsClassNameProperty).Any(property => property.DeclaringSyntaxReferences.Any(reference =>
            reference.SyntaxTree == model.SyntaxTree && reference.GetSyntax(cancellationToken).DescendantNodes().OfType<IdentifierNameSyntax>().Any(x => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(x, cancellationToken).Symbol, field)))),
        _ => false,
    };

    private static bool IsClassNameProperty(IPropertySymbol property)
    {
        for (; property != null; property = property.OverriddenProperty)
        {
            if (ClassNameProperties.Contains(FullMemberName(property)))
                return true;
        }
        return false;
    }

    private static string FullMemberName(ISymbol symbol) => $"{symbol.ContainingType.OriginalDefinition.ToDisplayString()}.{symbol.Name}";
}
