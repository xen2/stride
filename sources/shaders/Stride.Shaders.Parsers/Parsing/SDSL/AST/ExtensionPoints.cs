// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Shaders.Core;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Parsing.SDSL.AST;

/// <summary>
/// Extension points: <c>replace</c>, <c>[Phases(A, B)] pipeline</c> or <c>[Phases(A, B)] around</c> on the first definition of a method, with
/// <c>[MixinOrder(X, Y)]</c>, and <c>[Phase(A)]</c>, <c>[MixinOrder(After = X)]</c> and <c>[Drops(...)]</c> on an override.
/// The class records them next to <see cref="OpFunctionMetadataSDSL"/>, and the mixer orders the override chain with them.
/// </summary>
internal static class ExtensionPoints
{
    private const string PhasesAttribute = "Phases";
    private const string PhaseAttribute = "Phase";
    private const string MixinOrderAttribute = "MixinOrder";
    private const string DropsAttribute = "Drops";

    public static bool IsAttribute(string name) => name is PhasesAttribute or PhaseAttribute or MixinOrderAttribute or DropsAttribute;

    /// <summary>
    /// What a method states about its place in a chain, as the mixer reads it: classes by full name, and phases with their prefix in
    /// <see cref="Drops"/>.
    /// </summary>
    private sealed class Contribution
    {
        public List<string> After { get; } = [];
        public List<string> Before { get; } = [];
        public List<string> Drops { get; } = [];
        public List<string> MixinOrder { get; } = [];
    }

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
        var contribution = new Contribution();
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
            else if (name == MixinOrderAttribute)
                ReadMixinOrder(table, attribute, method, methodName, point, contribution);
            else if (name == DropsAttribute)
                ReadKeyedNames(table, attribute, method, methodName, point, contribution.Drops);
        }

        if (point != null)
            builder.Insert(new OpExtensionPointSDSL(point.Kind, point.Owner, string.Join(',', point.Phases)));
        if (phase != null || contribution is { After.Count: > 0 } or { Before.Count: > 0 } or { Drops.Count: > 0 } or { MixinOrder.Count: > 0 })
        {
            builder.Insert(new OpContributionSDSL(phase ?? "", string.Join(',', contribution.After), string.Join(',', contribution.Before), string.Join(',', contribution.Drops),
                string.Join(',', contribution.MixinOrder), method.Name.Info.Line));
        }
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

    // One name, or several in braces
    private static List<Expression> Names(Expression value) => value is ArrayLiteral list ? list.Values : [value];

    /// <summary>
    /// On an override, the classes this contributor runs after or before in its phase, by full name. On the method being extended,
    /// a list of classes in their running order. A class that isn't in the effect is ignored at link, but it has to exist.
    /// </summary>
    private static void ReadMixinOrder(SymbolTable table, AnyShaderAttribute attribute, ShaderMethod method, string methodName, MethodExtensionPoint? point, Contribution contribution)
    {
        var problem = point == null ? "[MixinOrder] goes on an override of an extension point, or the method being extended"
            : point?.Kind == ExtensionPointKindSDSL.Replace ? "one override of a replace point runs, so it has no order"
            : attribute.Parameters.Count == 0 ? "[MixinOrder] takes class names"
            : null;
        if (problem != null)
        {
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, problem)));
            return;
        }

        foreach (var parameter in attribute.Parameters)
        {
            if (parameter is AssignExpression { Target: Identifier { Name: "After" or "Before" } key, Value: var value })
            {
                if (!method.IsOverride)
                    table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "on the method being extended, [MixinOrder] lists the classes in their running order: [MixinOrder(A, B)]. After = and Before = go on an override")));
                else
                    ResolveClasses(table, attribute, methodName, Names(value), key.Name == "After" ? contribution.After : contribution.Before);
            }
            else if (method.IsOverride)
            {
                table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "on an override, [MixinOrder] takes After = and Before =. A list of classes goes on the method being extended")));
            }
            else
            {
                ResolveClasses(table, attribute, methodName, [parameter], contribution.MixinOrder);
            }
        }
    }

    private static void ResolveClasses(SymbolTable table, AnyShaderAttribute attribute, string methodName, List<Expression> names, List<string> into)
    {
        foreach (var name in names)
        {
            if (name is StringLiteral text)
                table.AddError(new(text.Info, string.Format(SDSLErrorMessages.SDSL0255, attribute.Name.Name, methodName, text.Value)));
            else if (DottedName(name) is not { } identifier)
                table.AddError(new(name.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, $"'{name}' is not a class name")));
            else if (table.ResolveClassName(identifier.Name, identifier.Info) is { } fullName)
                into.Add(fullName);
            else
                table.AddError(new(identifier.Info, string.Format(SDSLErrorMessages.SDSL0110, identifier.Name) + table.DidYouMean(identifier.Name)));
        }
    }

    /// <summary>
    /// <c>[Drops]</c> on an override: what doesn't run in this chain when this contributor is in the effect. <c>Mixins</c> takes classes and
    /// <c>Phases</c> phases of the point. A class that isn't in the effect is ignored at link, but it has to exist.
    /// </summary>
    private static void ReadKeyedNames(SymbolTable table, AnyShaderAttribute attribute, ShaderMethod method, string methodName, MethodExtensionPoint? point, List<string> names)
    {
        var name = attribute.Name.Name;
        if (!method.IsOverride || point == null)
        {
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, name, methodName, "[Drops] goes on an override of an extension point. To replace a whole class, put [Replaces(Full.Name)] on the class")));
            return;
        }

        var keys = $"[{name}] takes Mixins = {{ ... }} for classes and Phases = {{ ... }} for phases";
        if (attribute.Parameters.Count == 0)
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0254, name, methodName, keys)));
        foreach (var parameter in attribute.Parameters)
        {
            if (parameter is not AssignExpression { Target: Identifier { Name: "Mixins" or "Phases" } key, Value: var value })
            {
                table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0254, name, methodName, keys)));
                continue;
            }

            foreach (var item in Names(value))
            {
                if (item is StringLiteral text)
                {
                    table.AddError(new(text.Info, string.Format(SDSLErrorMessages.SDSL0255, name, methodName, text.Value)));
                    continue;
                }
                if (DottedName(item) is not { } identifier)
                {
                    table.AddError(new(item.Info, string.Format(SDSLErrorMessages.SDSL0254, name, methodName, $"'{item}' is not a name")));
                    continue;
                }

                var isPhase = point!.Phases.Contains(identifier.Name);
                var fullName = isPhase && key.Name == "Phases" ? null : table.ResolveClassName(identifier.Name, identifier.Info);
                if (key.Name == "Phases")
                {
                    if (isPhase)
                        names.Add($"{MethodExtensionPoint.ReplacedPhasePrefix}{identifier.Name}");
                    else if (fullName != null)
                        table.AddError(new(identifier.Info, string.Format(SDSLErrorMessages.SDSL0254, name, methodName, $"{identifier.Name} is a class: write Mixins = {{ {identifier.Name} }}")));
                    else
                        table.AddError(new(identifier.Info, string.Format(SDSLErrorMessages.SDSL0254, name, methodName, $"{ShaderNames.ShortName(point.Owner)}.{method.Name.Name} has no phase {identifier.Name}. It declares {string.Join(", ", point.Phases)}")));
                }
                else if (fullName != null)
                    names.Add(fullName);
                else if (isPhase)
                    table.AddError(new(identifier.Info, string.Format(SDSLErrorMessages.SDSL0254, name, methodName, $"{identifier.Name} is a phase of {ShaderNames.ShortName(point.Owner)}.{method.Name.Name}: write Phases = {{ {identifier.Name} }}")));
                else
                    table.AddError(new(identifier.Info, string.Format(SDSLErrorMessages.SDSL0110, identifier.Name) + table.DidYouMean(identifier.Name)));
            }
        }
    }

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
