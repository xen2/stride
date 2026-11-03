// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Stride.Assets;

public static partial class CodeUpgrades
{
    private const string EffectSourceName = "Stride.Shaders.EffectSource";

    /// <summary>
    /// <c>mixin.AddMacro(a, b)</c> → <c>context.AddMacro(a, b)</c>, where <c>mixin</c> is the mixin that
    /// <c>IShaderMixinBuilder.Generate</c> gets. On any other mixin only its author knows if it is the root of an effect, so
    /// it stays, and the compile error is what the author sees.
    /// </summary>
    public static SymbolRewrite BuilderMacroToContext()
    {
        return new SymbolRewrite(
            compilation => ResolveMembers(compilation, "Stride.Shaders.ShaderMixinSource", "AddMacro"),
            static (editor, referenceNode, symbol) =>
            {
                if (referenceNode.Parent is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } memberAccess || memberAccess.Name != referenceNode)
                    return;

                if (editor.SemanticModel.GetSymbolInfo(receiver).Symbol is IParameterSymbol { Ordinal: 0, ContainingSymbol: IMethodSymbol { Name: "Generate", Parameters.Length: 2 } generate }
                    && generate.ContainingType.AllInterfaces.Any(x => x.Name == "IShaderMixinBuilder"))
                    editor.ReplaceNode(receiver, SyntaxFactory.IdentifierName(generate.Parameters[1].Name).WithTriviaFrom(receiver));
            });
    }

    /// <summary>
    /// <c>compiler.Compile(x, parameters)</c> → <c>compiler.Compile(new EffectSource(x), parameters)</c>, when the static type
    /// of <c>x</c> says it is a mixin or a class. A <c>ShaderSource</c> can also be a generator, so it stays.
    /// </summary>
    public static SymbolRewrite CompileTakesEffectSource()
    {
        return new SymbolRewrite(
            compilation => new[] { "Stride.Shaders.Compiler.IEffectCompiler", "Stride.Shaders.Compiler.EffectCompilerBase" }
                .SelectMany(type => ResolveMembers(compilation, type, "Compile"))
                .Where(x => x is IMethodSymbol { Parameters.Length: 2 } method && method.Parameters[0].Type.Name == "ShaderSource"),
            static (editor, referenceNode, symbol) =>
            {
                if (GetInvocation(referenceNode) is not { ArgumentList.Arguments: [var source, ..] })
                    return;

                var model = editor.SemanticModel;
                var mixin = model.Compilation.GetTypeByMetadataName("Stride.Shaders.ShaderMixinSource");
                var shaderClass = model.Compilation.GetTypeByMetadataName("Stride.Shaders.ShaderClassCode");
                var type = model.GetTypeInfo(source.Expression).Type;
                if (type is null || mixin is null || shaderClass is null || !(DerivesFrom(type, mixin) || DerivesFrom(type, shaderClass)))
                    return;

                // The result of Generate is already an effect
                if (model.GetSymbolInfo(source.Expression).Symbol is IMethodSymbol { Name: "Generate", ContainingType.Name: "ShaderMixinManager" })
                    return;

                editor.ReplaceNode(source.Expression, SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(EffectSourceName).WithLeadingTrivia(SyntaxFactory.Space))
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(source.Expression.WithoutTrivia()))))
                    .WithTriviaFrom(source.Expression));
            });
    }

    /// <summary>
    /// <c>ShaderMixinManager.Generate(...).Member</c> → <c>ShaderMixinManager.Generate(...).Root.Member</c>.
    /// </summary>
    public static SymbolRewrite GeneratedEffectMemberToRoot()
    {
        return new SymbolRewrite(
            compilation => ResolveMembers(compilation, "Stride.Shaders.ShaderMixinManager", "Generate"),
            static (editor, referenceNode, symbol) =>
            {
                if (GetInvocation(referenceNode) is { Parent: MemberAccessExpressionSyntax memberAccess } invocation && memberAccess.Expression == invocation && memberAccess.Name.Identifier.ValueText != "Root")
                    editor.ReplaceNode(invocation, ToRoot(invocation));
            });
    }

    /// <summary>
    /// An override of <c>EffectCompilerBase.Compile</c> that takes a <c>ShaderMixinSource</c> takes an <c>EffectSource</c>:
    /// the type of the parameter changes, and <c>parameter.Member</c> → <c>parameter.Root.Member</c>. The other uses of the
    /// parameter stay, such as the call to the base method.
    /// </summary>
    public static CodeUpgrade CompilerOverridesTakeEffectSource()
    {
        return async (solution, targets, cancellationToken) =>
        {
            foreach (var projectId in targets)
            {
                var project = solution.GetProject(projectId);
                if (project is null)
                    continue;

                foreach (var documentId in project.DocumentIds)
                {
                    var document = solution.GetDocument(documentId);
                    if (document is null || !SymbolRewriteEngine.IsUpgradableSource(document))
                        continue;

                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    var model = await document.GetSemanticModelAsync(cancellationToken);
                    var compilerBase = model?.Compilation.GetTypeByMetadataName("Stride.Shaders.Compiler.EffectCompilerBase");
                    if (root is null || compilerBase is null)
                        continue;

                    DocumentEditor editor = null;
                    foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(x => x.Identifier.ValueText == "Compile" && x.Modifiers.Any(SyntaxKind.OverrideKeyword)))
                    {
                        if (model.GetDeclaredSymbol(method, cancellationToken) is not { OverriddenMethod: { } overridden, Parameters: [{ Type.Name: "ShaderMixinSource" } parameter, ..] }
                            || !DerivesFrom(overridden.ContainingType, compilerBase)
                            || method.ParameterList.Parameters[0].Type is not { } parameterType)
                            continue;

                        editor ??= await DocumentEditor.CreateAsync(document, cancellationToken);
                        editor.ReplaceNode(parameterType, SyntaxFactory.ParseTypeName(EffectSourceName).WithTriviaFrom(parameterType));
                        foreach (var memberAccess in method.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                        {
                            if (memberAccess.Expression is IdentifierNameSyntax receiver && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(receiver, cancellationToken).Symbol, parameter))
                                editor.ReplaceNode(receiver, ToRoot(receiver));
                        }
                    }

                    if (editor is not null)
                        solution = editor.GetChangedDocument().Project.Solution;
                }
            }
            return solution;
        };
    }

    private static MemberAccessExpressionSyntax ToRoot(ExpressionSyntax expression)
    {
        return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, expression.WithoutTrivia(), SyntaxFactory.IdentifierName("Root")).WithTriviaFrom(expression);
    }

    // The invocation of the method at this reference
    private static InvocationExpressionSyntax GetInvocation(SyntaxNode referenceNode)
    {
        var target = referenceNode.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == referenceNode
            || referenceNode.Parent is MemberBindingExpressionSyntax memberBinding && memberBinding.Name == referenceNode
            ? referenceNode.Parent
            : referenceNode;
        return target.Parent is InvocationExpressionSyntax invocation && invocation.Expression == target ? invocation : null;
    }
}
