// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Shaders.Core;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Parsing.SDSL.AST;

/// <summary>
/// Extension points: <c>replace</c>, <c>[Phases(A, B)] pipeline</c> or <c>[Phases(A, B)] around</c> on the first definition of a method, <c>[Phase(A)]</c>, <c>[After(X)]</c> and <c>[Replaces(X)]</c> on an override. The class records them next to <see cref="OpFunctionMetadataSDSL"/>,
/// and the mixer orders the override chain with them.
/// </summary>
internal static class ExtensionPoints
{
    private const string PhasesAttribute = "Phases";
    private const string PhaseAttribute = "Phase";
    private const string AfterAttribute = "After";
    private const string ReplacesAttribute = "Replaces";

    /// <summary>
    /// Makes an <c>extend</c> or a <c>patch</c> the override it stands for, so that everything after this sees an override: the signature
    /// and the kind of member come from the method, the phase and the clauses become attributes. A pipeline step gets its call to
    /// <c>base</c>: before the default body it comes last, after it first.
    /// </summary>
    public static void ResolveExtend(SymbolTable table, ShaderMethod method)
    {
        var extend = method.Extend!;
        var info = method.Name.Info;
        var keyword = extend.IsPatch ? "patch" : "extend";
        var owner = table.ResolveClassName(extend.Owner, info);
        if (owner == null)
        {
            table.AddError(new(info, string.Format(SDSLErrorMessages.SDSL0110, extend.Owner) + table.DidYouMean(extend.Owner)));
            return;
        }

        bool IsOwner(MethodExtensionPoint point) => ShaderNames.WithoutGenerics(point.Owner) == owner;
        var inherited = table.InheritedShaders.SelectMany(x => x.Symbol!.Methods.Select(y => y.Symbol)).Where(x => x.Id.Name == method.Name.Name && x.Type is FunctionType).ToList();
        var declared = inherited.Where(x => x.ExtensionPoint is { } point && IsOwner(point)).ToList();
        var target = FindOverload(extend.IsPatch ? inherited.Where(x => x.OwnerType is { } ownerType && ownerType.ToClassName() == owner).ToList() : declared, method, out var isAmbiguous);
        var shortName = ShaderNames.ShortName(owner);
        if (isAmbiguous)
        {
            table.AddError(new(info, string.Format(SDSLErrorMessages.SDSL0143, keyword, shortName, method.Name.Name, "it has several overloads: write the parameters")));
            return;
        }
        if (extend.IsPatch && target?.ExtensionPoint != null)
        {
            table.AddError(new(info, string.Format(SDSLErrorMessages.SDSL0143, keyword, shortName, method.Name.Name, "it is an extension point: use extend")));
            return;
        }
        if (target == null)
        {
            var problem = extend.IsPatch
                ? "this class doesn't inherit or import such a method"
                : inherited.Count > 0 ? "it is not an extension point: use patch" : "this class doesn't inherit or import such a method";
            table.AddError(new(info, string.Format(SDSLErrorMessages.SDSL0143, keyword, shortName, method.Name.Name, problem)));
            return;
        }

        if (extend.IsPatch && extend.Phase != null)
            table.AddError(new(info, string.Format(SDSLErrorMessages.SDSL0143, keyword, shortName, method.Name.Name, "it is not an extension point, so it has no phases")));

        var type = (FunctionType)target.Type;
        method.IsStaged = target.Id.IsStage;
        method.ReturnTypeName = new TypeName(type.ReturnType.ToString(), info);
        if (!extend.HasParameters)
            method.Parameters = TakeParameters(table, method, type, target.ExtensionPoint, info);

        var attributes = method.Attributes ?? [];
        method.Attributes = attributes;
        if (target.ExtensionPoint is { } extended)
        {
            var phaseAttribute = attributes.OfType<AnyShaderAttribute>().FirstOrDefault(x => x.Name.Name == PhaseAttribute);
            var phase = extend.Phase?.Name;
            if (phase == null && phaseAttribute == null && extended.Phases.Length > 0)
                phase = extended.Kind == ExtensionPointKindSDSL.Pipeline ? extended.Phases[extended.DefaultPhaseIndex] : extended.Phases[^1];
            if (phase != null)
                attributes.Add(new AnyShaderAttribute(new(PhaseAttribute, info), info, [new Identifier(phase, info)]));
            if (extended.Kind == ExtensionPointKindSDSL.Pipeline && method.Body != null)
                AddBaseCall(table, method, extended, IsBeforeDefaultBody(extended, phase, phaseAttribute));
        }
        if (extend.After.Count > 0)
            attributes.Add(new AnyShaderAttribute(new(AfterAttribute, info), info, [.. extend.After]));
        if (extend.Replaces.Count > 0)
            attributes.Add(new AnyShaderAttribute(new(ReplacesAttribute, info), info, [.. extend.Replaces]));
    }

    /// <summary>
    /// The method an <c>extend</c> or a <c>patch</c> is about: the overload whose parameters it writes, else the only one. An overload has one
    /// symbol per class that overrides it.
    /// </summary>
    private static Symbol? FindOverload(List<Symbol> candidates, ShaderMethod method, out bool isAmbiguous)
    {
        isAmbiguous = false;
        if (!method.Extend!.HasParameters)
        {
            isAmbiguous = candidates.Select(x => x.Type).Distinct().Skip(1).Any();
            return candidates.LastOrDefault();
        }

        var written = method.Parameters.Select(x => x.TypeName.ToString()).ToList();
        return candidates.LastOrDefault(x => ((FunctionType)x.Type).ParameterTypes.Select(y => ParameterType(y).ToString()).SequenceEqual(written))
            ?? candidates.LastOrDefault(x => ((FunctionType)x.Type).ParameterTypes.Count == written.Count)
            ?? candidates.LastOrDefault();
    }

    private static SymbolType ParameterType(FunctionParameter parameter) => parameter.Type is PointerType pointer ? pointer.BaseType : parameter.Type;

    private static List<MethodParameter> TakeParameters(SymbolTable table, ShaderMethod method, FunctionType type, MethodExtensionPoint? point, TextLocation info)
    {
        if (type.ParameterTypes.Count == 0)
            return [];
        if (point == null || point.ParameterNames.Length != type.ParameterTypes.Count)
        {
            table.AddError(new(info, string.Format(SDSLErrorMessages.SDSL0143, method.Extend!.IsPatch ? "patch" : "extend", method.Extend.Owner, method.Name.Name, "write the parameters: they can only be left out for an extension point")));
            return [];
        }
        // The other types have more to say than a name
        if (type.ParameterTypes.Select(ParameterType).FirstOrDefault(x => x is not (ScalarType or VectorType or MatrixType or StructType or StreamsType)) is { } other)
        {
            table.AddError(new(info, string.Format(SDSLErrorMessages.SDSL0143, method.Extend!.IsPatch ? "patch" : "extend", method.Extend.Owner, method.Name.Name, $"write the parameters: one of type {other} can't be left out")));
            return [];
        }
        return type.ParameterTypes.Select((x, i) => new MethodParameter(new TypeName(ParameterType(x).ToString(), info), new Identifier(point.ParameterNames[i], info), info, x.Modifiers)).ToList();
    }

    /// <summary>
    /// Whether a step runs before the default body: its phase, or the neighbour of the phase it adds. A neighbour that another class
    /// adds is only known at link, which checks the place of the call to <c>base</c>.
    /// </summary>
    private static bool IsBeforeDefaultBody(MethodExtensionPoint point, string? phase, AnyShaderAttribute? phaseAttribute)
    {
        if ((phase ?? (phaseAttribute?.Parameters.FirstOrDefault() as Identifier)?.Name) is { } name && Array.IndexOf(point.Phases, name) is >= 0 and var index)
            return index < point.DefaultPhaseIndex;

        foreach (var parameter in phaseAttribute?.Parameters.Skip(1) ?? [])
        {
            if (parameter is AssignExpression { Target: Identifier { Name: var side }, Value: Identifier neighbour } && Array.IndexOf(point.Phases, neighbour.Name) is >= 0 and var neighbourIndex)
                return side == "After" ? neighbourIndex < point.DefaultPhaseIndex : neighbourIndex <= point.DefaultPhaseIndex;
        }
        return false;
    }

    private static void AddBaseCall(SymbolTable table, ShaderMethod method, MethodExtensionPoint point, bool isBeforeDefaultBody)
    {
        var info = method.Name.Info;
        var finder = new BaseCallFinder(method.Name.Name);
        finder.VisitNode(method.Body!);
        if (finder.Count > 0)
        {
            table.AddError(new(info, string.Format(SDSLErrorMessages.SDSL0144, table.CurrentShader!.ShortName, ShaderNames.ShortName(point.Owner), method.Name.Name)));
            return;
        }

        var arguments = new ShaderExpressionList(info) { Values = [.. method.Parameters.Select(x => (Expression)new Identifier(x.Name, info))] };
        var call = new AccessorChainExpression(new Identifier("base", info), info) { Accessors = [new MethodCall(new Identifier(method.Name.Name, info), arguments, info)] };
        var statement = new ExpressionStatement(call, info);
        if (isBeforeDefaultBody)
            method.Body!.Statements.Add(statement);
        else
            method.Body!.Statements.Insert(0, statement);
    }

    public static bool IsAttribute(string name) => name is "Replace" or "Pipeline" or "Around" or "Collect" or PhasesAttribute or PhaseAttribute or AfterAttribute or ReplacesAttribute;

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
        var addedPhase = "";
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
                point = ReadPoint(kind, attributes, method, table.CurrentShader.ToClassName());
        }
        CheckPhases(table, attributes, method, methodName);

        foreach (var attribute in attributes)
        {
            var name = attribute.Name.Name;
            if (name == "Collect")
                table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0139, methodName)));
            else if (name is "Replace" or "Pipeline" or "Around")
                table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0249, name, methodName, KeywordForm(attribute, method))));
            else if (name == PhaseAttribute)
                phase = ReadPhase(table, attribute, method, methodName, inherited, out addedPhase) ?? phase;
            else if (name == AfterAttribute)
                ReadAfter(table, attribute, method, methodName, inherited, after);
            else if (name == ReplacesAttribute)
                ReadReplaces(table, attribute, method, methodName, inherited, replaces);
        }

        if (point != null)
            builder.Insert(new OpExtensionPointSDSL(point.Kind, point.Owner, string.Join(',', point.Phases), string.Join(',', point.ParameterNames)));
        var form = method.Extend switch
        {
            { IsPatch: true } => MethodExtensionPoint.PatchForm,
            not null => MethodExtensionPoint.ExtendForm,
            _ => "",
        };
        // The mixer checks this call against the place of an added phase, which is only known at link
        if (addedPhase.Length > 0)
        {
            addedPhase += method.Body == null ? "," : FindBasePosition(method.Body, method.Name.Name) switch
            {
                BasePosition.First => $",{MethodExtensionPoint.BaseCallFirst}",
                BasePosition.Last => $",{MethodExtensionPoint.BaseCallLast}",
                _ => ",",
            };
        }
        if (phase != null || after.Count > 0 || replaces.Count > 0 || form.Length > 0)
            builder.Insert(new OpContributionSDSL(phase ?? "", string.Join(',', after), string.Join(',', replaces), addedPhase, form));
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

    private static string KeywordForm(AnyShaderAttribute attribute, ShaderMethod method)
    {
        var phases = attribute.Parameters.Count > 0 ? $"[{PhasesAttribute}({string.Join(", ", attribute.Parameters)})] " : "";
        return $"{phases}{attribute.Name.Name.ToLowerInvariant()} {method.ReturnTypeName} {method.Name.Name}(...)";
    }

    private static MethodExtensionPoint ReadPoint(Identifier keyword, List<AnyShaderAttribute> attributes, ShaderMethod method, string owner)
    {
        var kind = keyword.Name switch
        {
            "replace" => ExtensionPointKindSDSL.Replace,
            "pipeline" => ExtensionPointKindSDSL.Pipeline,
            _ => ExtensionPointKindSDSL.Around,
        };
        if (kind == ExtensionPointKindSDSL.Replace)
            return new MethodExtensionPoint(kind, owner, [], [.. method.Parameters.Select(x => x.Name.Name)]);

        var phases = attributes.Find(x => x.Name.Name == PhasesAttribute)?.Parameters.OfType<Identifier>().Select(x => x.Name).Distinct().ToList() ?? [];
        if (phases.Count == 0)
            phases.Add(MethodExtensionPoint.DefaultPhase);
        return new MethodExtensionPoint(kind, owner, [.. phases], [.. method.Parameters.Select(x => x.Name.Name)]);
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
                : method.IsOverride ? "the phases are on the first definition. An override adds a phase with [Phase(Name, After = Other)]"
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

    private static string? ReadPhase(SymbolTable table, AnyShaderAttribute attribute, ShaderMethod method, string methodName, MethodExtensionPoint? point, out string addedPhase)
    {
        addedPhase = "";
        if (attribute.Parameters is not [Identifier phase, ..])
        {
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "[Phase] takes one phase name")));
            return null;
        }

        // [Phase(Wind, After = A, Before = B)] adds a phase. Its neighbours are checked at link: another class can add them.
        string? after = null, before = null;
        foreach (var parameter in attribute.Parameters.Skip(1))
        {
            if (parameter is AssignExpression { Target: Identifier { Name: "After" }, Value: Identifier afterName })
                after = afterName.Name;
            else if (parameter is AssignExpression { Target: Identifier { Name: "Before" }, Value: Identifier beforeName })
                before = beforeName.Name;
            else
            {
                table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "[Phase] takes one phase name, and After = and Before = to add a phase")));
                return null;
            }
        }
        if (after != null || before != null)
        {
            if (!method.IsOverride || point == null || point.Kind == ExtensionPointKindSDSL.Replace)
            {
                table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0141, phase.Name, methodName, "a phase is added by an override of a pipeline or around point")));
                return null;
            }
            addedPhase = $"{after},{before}";
            return phase.Name;
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
        // A patch has no point, and two patches of a method need an order
        if (!method.IsOverride || (point == null && method.Extend is not { IsPatch: true }))
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "[After] goes on an override of an extension point")));
        else if (point?.Kind == ExtensionPointKindSDSL.Replace)
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "one override of a replace point runs, so it has no order")));
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
            BasePosition.First when index >= 0 && index < defaultIndex => "calls base first, so it runs after the default body",
            BasePosition.Last when index >= 0 && index > defaultIndex => "calls base last, so it runs before the default body",
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
