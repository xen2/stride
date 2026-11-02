// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Shaders.Core;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Parsing.SDSL.AST;

/// <summary>
/// Extension points: <c>[Replace]</c>, <c>[Pipeline(A, B)]</c> or <c>[Around(A, B)]</c> on the first definition of a method, <c>[Phase(A)]</c>, <c>[After(X)]</c> and <c>[Replaces(X)]</c> on an override. The class records them next to <see cref="OpFunctionMetadataSDSL"/>,
/// and the mixer orders the override chain with them.
/// </summary>
internal static class ExtensionPoints
{
    private const string PhaseAttribute = "Phase";
    private const string AfterAttribute = "After";
    private const string ReplacesAttribute = "Replaces";

    public static bool IsAttribute(string name) => name is "Replace" or "Pipeline" or "Around" or "Collect" or PhaseAttribute or AfterAttribute or ReplacesAttribute;

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
        var after = new List<string>();
        var replaces = new List<string>();
        var shaderName = table.CurrentShader!.ShortName;
        var methodName = $"{shaderName}.{method.Name.Name}";

        foreach (var attribute in attributes)
        {
            var name = attribute.Name.Name;
            if (name == "Collect")
                table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0139, methodName)));
            else if (name == PhaseAttribute)
                phase = ReadPhase(table, attribute, method, methodName, inherited) ?? phase;
            else if (name == AfterAttribute)
                ReadAfter(table, attribute, method, methodName, inherited, after);
            else if (name == ReplacesAttribute)
                ReadReplaces(table, attribute, method, methodName, inherited, replaces);
            else if (method.IsOverride)
                table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0142, name, method.Name.Name, methodName)));
            else if (point != null)
                table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, name, methodName, "a method has one kind")));
            else
                point = ReadPoint(table, attribute, methodName, table.CurrentShader.ToClassName());
        }

        if (point != null)
            builder.Insert(new OpExtensionPointSDSL(point.Kind, point.Owner, string.Join(',', point.Phases)));
        if (phase != null || after.Count > 0 || replaces.Count > 0)
            builder.Insert(new OpContributionSDSL(phase ?? "", string.Join(',', after), string.Join(',', replaces)));
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

    private static MethodExtensionPoint? ReadPoint(SymbolTable table, AnyShaderAttribute attribute, string methodName, string owner)
    {
        var kind = attribute.Name.Name switch
        {
            "Replace" => ExtensionPointKindSDSL.Replace,
            "Pipeline" => ExtensionPointKindSDSL.Pipeline,
            _ => ExtensionPointKindSDSL.Around,
        };
        var phases = new List<string>();
        foreach (var parameter in attribute.Parameters)
        {
            if (kind == ExtensionPointKindSDSL.Replace)
            {
                table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "a [Replace] point has no phases")));
                return null;
            }
            if (parameter is not Identifier identifier)
            {
                table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, $"'{parameter}' is not a phase name")));
                return null;
            }
            if (phases.Contains(identifier.Name))
            {
                table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, $"phase {identifier.Name} is written twice")));
                return null;
            }
            phases.Add(identifier.Name);
        }
        if (kind != ExtensionPointKindSDSL.Replace && phases.Count == 0)
            phases.Add(MethodExtensionPoint.DefaultPhase);
        return new MethodExtensionPoint(kind, owner, [.. phases]);
    }

    private static string? ReadPhase(SymbolTable table, AnyShaderAttribute attribute, ShaderMethod method, string methodName, MethodExtensionPoint? point)
    {
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
    /// The classes this contributor runs after, by full name. A class that isn't in the effect is ignored at link, but it has to exist.
    /// </summary>
    private static void ReadAfter(SymbolTable table, AnyShaderAttribute attribute, ShaderMethod method, string methodName, MethodExtensionPoint? point, List<string> after)
    {
        if (!method.IsOverride || point == null)
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "[After] goes on an override of an extension point")));
        else if (point.Kind == ExtensionPointKindSDSL.Replace)
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "one override of a [Replace] point runs, so it has no order")));
        else if (attribute.Parameters.Count == 0)
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "[After] takes class names")));
        else
        {
            foreach (var parameter in attribute.Parameters)
            {
                if (parameter is not Identifier identifier)
                    table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, $"'{parameter}' is not a class name")));
                else if (table.ResolveClassName(identifier.Name, identifier.Info) is { } fullName)
                    after.Add(fullName);
                else
                    table.AddError(new(identifier.Info, string.Format(SDSLErrorMessages.SDSL0110, identifier.Name) + table.DidYouMean(identifier.Name)));
            }
        }
    }

    /// <summary>
    /// What doesn't run in this chain when this contributor is in the effect: a class, or a phase of the point, which wins over a
    /// class of the same name. A class that isn't in the effect is ignored at link, but it has to exist.
    /// </summary>
    private static void ReadReplaces(SymbolTable table, AnyShaderAttribute attribute, ShaderMethod method, string methodName, MethodExtensionPoint? point, List<string> replaces)
    {
        if (!method.IsOverride || point == null)
        {
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "on a method, [Replaces] goes on an override of an extension point. To replace a whole class, put [Replaces(\"Full.Name\")] on the class")));
            return;
        }
        if (attribute.Parameters.Count == 0)
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "[Replaces] takes names of classes or phases")));

        foreach (var parameter in attribute.Parameters)
        {
            if (parameter is StringLiteral)
                table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "on an override, [Replaces] takes names without quotes. A string names a whole class, and goes on the class")));
            else if (parameter is not Identifier identifier)
                table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, $"'{parameter}' is not the name of a class or a phase")));
            else if (point.Phases.Contains(identifier.Name))
                replaces.Add($"{MethodExtensionPoint.ReplacedPhasePrefix}{identifier.Name}");
            else if (table.ResolveClassName(identifier.Name, identifier.Info) is { } fullName)
                replaces.Add(fullName);
            else
                table.AddError(new(identifier.Info, string.Format(SDSLErrorMessages.SDSL0110, identifier.Name) + table.DidYouMean(identifier.Name)));
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
