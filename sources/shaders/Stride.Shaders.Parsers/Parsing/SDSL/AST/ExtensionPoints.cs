// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Shaders.Core;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Parsing.SDSL.AST;

/// <summary>
/// Extension points: <c>replace</c>, <c>[Phases(A, B)] pipeline</c> or <c>[Phases(A, B)] around</c> on the first definition of a module method,
/// and <c>[Phase(A)]</c> on an override.
/// The class records them next to <see cref="OpFunctionMetadataSDSL"/>, and the mixer orders the override chain with them.
/// </summary>
internal static class ExtensionPoints
{
    private const string PhasesAttribute = "Phases";
    private const string PhaseAttribute = "Phase";

    public static bool IsAttribute(string name) => name is PhasesAttribute or PhaseAttribute;

    /// <summary>
    /// Checks the attributes of a method and writes what the mixer needs. An override of a declared point carries the point too,
    /// so that a class overriding that override finds it.
    /// </summary>
    public static void Compile(SymbolTable table, SpirvBuilder builder, ShaderMethod method, FunctionType type)
    {
        var attributes = method.Attributes?.OfType<AnyShaderAttribute>().Where(x => IsAttribute(x.Name.Name)).ToList() ?? [];
        var inherited = method.IsOverride ? FindOverridden(table, method.Name.Name, type)?.ExtensionPoint : null;
        var point = inherited;
        string? phase = null;
        var shaderName = table.CurrentShader!.ShortName;
        var methodName = $"{shaderName}.{method.Name.Name}";

        foreach (var kind in method.Kinds)
        {
            if (kind.Name == "collect")
                table.AddError(new(kind.Info, string.Format(SDSLErrorMessages.SDSL0139, methodName)));
            else if (!ReferenceEquals(kind, method.Kinds[0]))
                table.AddError(new(kind.Info, string.Format(SDSLErrorMessages.SDSL0250, kind.Name, methodName, "a method has one kind")));
            else if (method.IsOverride)
                table.AddError(new(kind.Info, string.Format(SDSLErrorMessages.SDSL0142, kind.Name, method.Name.Name, methodName)));
            else if (!method.IsStaged)
                table.AddError(new(kind.Info, string.Format(SDSLErrorMessages.SDSL0250, kind.Name, methodName, "a kind goes on a module method")));
            else if (method.IsAbstract)
                table.AddError(new(kind.Info, string.Format(SDSLErrorMessages.SDSL0250, kind.Name, methodName, "an abstract method has no body to extend, and its overrides implement it. Remove the kind, or give the method a body")));
            else
                point = ReadPoint(kind, attributes, table.CurrentShader.ToClassName());
        }
        CheckPhases(table, attributes, method, methodName);

        foreach (var attribute in attributes)
        {
            var name = attribute.Name.Name;
            if (name == PhaseAttribute)
                phase = ReadPhase(table, attribute, method, methodName, inherited) ?? phase;
        }

        if (point != null)
            builder.Insert(new OpExtensionPointSDSL(point.Kind, point.Owner, string.Join(',', point.Phases)));
        if (phase != null)
            builder.Insert(new OpContributionSDSL(phase));
        if (phase != null && point != null)
            CheckBasePosition(table, method, methodName, point, phase);
    }

    /// <summary>
    /// The method an override is chained to: the last one with this signature in the linearization.
    /// </summary>
    private static Symbol? FindOverridden(SymbolTable table, string name, FunctionType type)
    {
        return table.InheritedShaders
            .SelectMany(x => x.Symbol!.Methods)
            .LastOrDefault(x => x.Symbol.Id.Name == name && x.Symbol.Type is FunctionType inheritedType && inheritedType == type)
            .Symbol;
    }

    private static MethodExtensionPoint ReadPoint(Identifier keyword, List<AnyShaderAttribute> attributes, string owner)
    {
        var kind = keyword.Name switch
        {
            "replace" => ExtensionPointKindSDSL.Replace,
            "pipeline" => ExtensionPointKindSDSL.Pipeline,
            _ => ExtensionPointKindSDSL.Around,
        };
        if (kind == ExtensionPointKindSDSL.Replace)
            return new MethodExtensionPoint(kind, owner, []);

        var phases = attributes.Find(x => x.Name.Name == PhasesAttribute)?.Parameters.OfType<Identifier>().Select(x => x.Name).Distinct().ToList() ?? [];
        if (phases.Count == 0)
            phases.Add(MethodExtensionPoint.DefaultPhase);
        return new MethodExtensionPoint(kind, owner, [.. phases]);
    }

    /// <summary>
    /// <c>[Phases]</c> lists the phases of a <c>pipeline</c> or an <c>around</c>: anywhere else it would do nothing.
    /// </summary>
    private static void CheckPhases(SymbolTable table, List<AnyShaderAttribute> attributes, ShaderMethod method, string methodName)
    {
        var first = true;
        foreach (var attribute in attributes.Where(x => x.Name.Name == PhasesAttribute))
        {
            var problem = !first ? "a method has one list of phases"
                : method.IsOverride ? "the phases are on the first definition"
                : method.Kinds.Exists(x => x.Name == "replace") ? "a replace method has no phases"
                : !method.Kinds.Exists(x => x.Name is "pipeline" or "around") ? "the phases belong to a pipeline or an around method"
                : attribute.Parameters.Count == 0 ? "[Phases] takes the names of the phases"
                : null;
            first = false;
            if (problem != null)
            {
                table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, PhasesAttribute, methodName, problem)));
                continue;
            }

            var seen = new HashSet<string>();
            foreach (var parameter in attribute.Parameters)
            {
                if (parameter is not Identifier identifier)
                    table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, PhasesAttribute, methodName, $"'{parameter}' is not a phase name")));
                else if (!seen.Add(identifier.Name))
                    table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, PhasesAttribute, methodName, $"phase {identifier.Name} is written twice")));
            }
        }
    }

    private static string? ReadPhase(SymbolTable table, AnyShaderAttribute attribute, ShaderMethod method, string methodName, MethodExtensionPoint? point)
    {
        if (attribute.Parameters is [StringLiteral text])
        {
            table.AddError(new(text.Info, string.Format(SDSLErrorMessages.SDSL0255, attribute.Name.Name, methodName, text.Value)));
            return null;
        }
        if (attribute.Parameters is not [Identifier phase])
        {
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "[Phase] takes one phase name")));
            return null;
        }
        if (!method.IsOverride)
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0141, phase.Name, methodName, "[Phase] goes on an override")));
        else if (point == null)
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0141, phase.Name, methodName, $"{method.Name.Name} is not an extension point, so it has no phases")));
        else if (!point.Phases.Contains(phase.Name))
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0141, phase.Name, methodName, $"{ShaderNames.ShortName(point.Owner)}.{method.Name.Name} declares {(point.Phases.Length > 0 ? string.Join(", ", point.Phases) : "no phases")}")));
        else
            return phase.Name;
        return null;
    }

    /// <summary>
    /// A class or a phase, with its namespace or prefix: <c>Lib.First</c>.
    /// </summary>
    internal static Identifier? DottedName(Expression parameter) => parameter switch
    {
        Identifier identifier => identifier,
        AccessorChainExpression { Source: Identifier source } chain when chain.Accessors.TrueForAll(x => x is Identifier)
            => new Identifier(string.Join('.', chain.Accessors.Select(x => ((Identifier)x).Name).Prepend(source.Name)), chain.Info),
        _ => null,
    };

    /// <summary>
    /// A pipeline step in the compat form runs where its <c>base</c> call puts it: before the default body it calls <c>base</c>
    /// last, after it first. A step that doesn't call <c>base</c> replaces the steps before it, which the mixer reports.
    /// </summary>
    private static void CheckBasePosition(SymbolTable table, ShaderMethod method, string methodName, MethodExtensionPoint point, string phase)
    {
        if (point.Kind != ExtensionPointKindSDSL.Pipeline || method.Body == null)
            return;

        var defaultIndex = point.DefaultPhaseIndex;
        var index = Array.IndexOf(point.Phases, phase);
        var position = FindBasePosition(method.Body, method.Name.Name);
        var problem = position switch
        {
            BasePosition.Other => "calls base in the middle of its body",
            BasePosition.First when index < defaultIndex => "calls base first, so it runs after the default body",
            BasePosition.Last when index > defaultIndex => "calls base last, so it runs before the default body",
            _ => null,
        };
        if (problem != null)
            table.AddError(new(method.Name.Info, string.Format(SDSLErrorMessages.SDSL0140, methodName, phase, $"{ShaderNames.ShortName(point.Owner)}.{method.Name.Name}", problem, point.Phases[defaultIndex])));
    }

    private enum BasePosition { None, First, Last, Only, Other }

    private static BasePosition FindBasePosition(BlockStatement body, string name)
    {
        var finder = new BaseCallFinder(name);
        finder.VisitNode(body);
        if (finder.Count == 0)
            return BasePosition.None;
        if (finder.Count > 1)
            return BasePosition.Other;

        var first = IsBaseCall(body.Statements[0]);
        var last = IsBaseCall(body.Statements[^1]);
        return first && last ? BasePosition.Only : first ? BasePosition.First : last ? BasePosition.Last : BasePosition.Other;

        bool IsBaseCall(Statement statement) => statement is ExpressionStatement { Expression: AccessorChainExpression chain } && BaseCallFinder.IsBaseCall(chain, name);
    }

    private sealed class BaseCallFinder(string name) : NodeWalker
    {
        public int Count { get; private set; }

        public static bool IsBaseCall(AccessorChainExpression chain, string name)
            => chain is { Source: Identifier { Name: "base" }, Accessors: [MethodCall call, ..] } && call.Name.Name == name;

        public override void VisitAccessorChainExpression(AccessorChainExpression accessorChainExpression)
        {
            if (IsBaseCall(accessorChainExpression, name))
                Count++;
            base.VisitAccessorChainExpression(accessorChainExpression);
        }
    }
}
