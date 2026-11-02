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
/// <c>[MixinOrder(X, Y)]</c> and <c>[Unbind(...)]</c>, and <c>[Phase(A)]</c>, <c>[MixinOrder(After = X)]</c> and <c>[Drops(...)]</c> on an override.
/// The class records them next to <see cref="OpFunctionMetadataSDSL"/>, and the mixer orders the override chain with them.
/// </summary>
internal static class ExtensionPoints
{
    private const string PhasesAttribute = "Phases";
    private const string PhaseAttribute = "Phase";
    private const string MixinOrderAttribute = "MixinOrder";
    private const string DropsAttribute = "Drops";
    private const string UnbindAttribute = "Unbind";

    /// <summary>
    /// Makes an <c>extend</c> or a <c>patch</c> the override it stands for, so that everything after this sees an override: the return type
    /// and the kind of member come from the method. A pipeline step gets its call to <c>base</c>: before the default body it comes last, after it first.
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

        var shortName = ShaderNames.ShortName(owner);
        bool IsOwner(MethodExtensionPoint point) => ShaderNames.WithoutGenerics(point.Owner) == owner;
        var inherited = table.InheritedShaders.SelectMany(x => x.Symbol!.Methods.Select(y => y.Symbol)).Where(x => x.Id.Name == method.Name.Name && x.Type is FunctionType).ToList();
        var declared = inherited.Where(x => x.ExtensionPoint is { } point && IsOwner(point)).ToList();
        var candidates = extend.IsPatch ? inherited.Where(x => x.OwnerType is { } ownerType && ownerType.ToClassName() == owner).ToList() : declared;
        var target = FindOverload(candidates, method);
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
        if (!extend.HasParameters)
        {
            var signatures = candidates.DistinctBy(x => x.Type).Select(x => $"{keyword} {extend.Owner}.{method.Name.Name}({FormatParameters(x)})");
            table.AddError(new(info, string.Format(SDSLErrorMessages.SDSL0143, keyword, shortName, method.Name.Name, $"write the parameters, as an override does: {string.Join(" or ", signatures)}")));
            return;
        }

        var type = (FunctionType)target.Type;
        method.IsStaged = target.Id.IsStage;
        method.ReturnTypeName = new TypeName(type.ReturnType.ToString(), info);

        var attributes = method.Attributes ?? [];
        method.Attributes = attributes;
        if (target.ExtensionPoint is { } extended)
        {
            var phaseAttribute = attributes.OfType<AnyShaderAttribute>().FirstOrDefault(x => x.Name.Name == PhaseAttribute);
            string? phase = null;
            if (phaseAttribute == null && extended.Phases.Length > 0)
            {
                phase = extended.Kind == ExtensionPointKindSDSL.Pipeline ? extended.Phases[extended.DefaultPhaseIndex] : extended.Phases[^1];
                attributes.Add(new AnyShaderAttribute(new(PhaseAttribute, info), info, [new Identifier(phase, info)]));
            }
            if (extended.Kind == ExtensionPointKindSDSL.Pipeline && method.Body != null)
            {
                // A phase that another class adds, or next to one: compile both forms, and the mixer keeps the one that fits
                var isBeforeDefaultBody = IsBeforeDefaultBody(extended, phase, phaseAttribute);
                if (isBeforeDefaultBody == null && MethodParser.ReadAgain(method) is { Body: not null } baseLast)
                {
                    baseLast.IsStaged = method.IsStaged;
                    baseLast.ReturnTypeName = new TypeName(type.ReturnType.ToString(), info);
                    baseLast.IsBaseLastForm = true;
                    AddBaseCall(table, baseLast, extended, isBeforeDefaultBody: true);
                    method.BaseLastForm = baseLast;
                }
                AddBaseCall(table, method, extended, isBeforeDefaultBody ?? false);
            }
        }
    }

    /// <summary>
    /// The method an <c>extend</c> or a <c>patch</c> is about: the overload whose parameters it writes, else the closest one. An overload has
    /// one symbol per class that overrides it.
    /// </summary>
    private static Symbol? FindOverload(List<Symbol> candidates, ShaderMethod method)
    {
        var written = method.Parameters.Select(x => x.TypeName.ToString()).ToList();
        return candidates.LastOrDefault(x => ((FunctionType)x.Type).ParameterTypes.Select(y => ParameterType(y).ToString()).SequenceEqual(written))
            ?? candidates.LastOrDefault(x => ((FunctionType)x.Type).ParameterTypes.Count == written.Count)
            ?? candidates.LastOrDefault();
    }

    // With names when the point gives them
    private static string FormatParameters(Symbol symbol)
    {
        var parameters = ((FunctionType)symbol.Type).ParameterTypes;
        var names = symbol.ExtensionPoint?.ParameterNames;
        return string.Join(", ", parameters.Select((x, i) =>
        {
            var modifier = (x.Modifiers & ParameterModifiers.InOut) switch
            {
                ParameterModifiers.InOut => "inout ",
                ParameterModifiers.Out => "out ",
                _ => "",
            };
            return $"{modifier}{ParameterType(x)}{(names?.Length == parameters.Count ? $" {names[i]}" : "")}";
        }));
    }

    private static SymbolType ParameterType(FunctionParameter parameter) => parameter.Type is PointerType pointer ? pointer.BaseType : parameter.Type;

    /// <summary>
    /// Whether a step runs before the default body: its phase, or the neighbour of the phase it adds. Null for a phase or a neighbour that
    /// another class adds, which is only placed at link.
    /// </summary>
    private static bool? IsBeforeDefaultBody(MethodExtensionPoint point, string? phase, AnyShaderAttribute? phaseAttribute)
    {
        if ((phase ?? (phaseAttribute?.Parameters.FirstOrDefault() is { } first ? DottedName(first)?.Name : null)) is { } name && Array.IndexOf(point.Phases, name) is >= 0 and var index)
            return index < point.DefaultPhaseIndex;

        foreach (var parameter in phaseAttribute?.Parameters.Skip(1) ?? [])
        {
            if (parameter is AssignExpression { Target: Identifier { Name: var side }, Value: var value } && DottedName(value) is { } neighbour && Array.IndexOf(point.Phases, neighbour.Name) is >= 0 and var neighbourIndex)
                return side == "After" ? neighbourIndex < point.DefaultPhaseIndex : neighbourIndex <= point.DefaultPhaseIndex;
        }
        return null;
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

    public static bool IsAttribute(string name) => name is PhasesAttribute or PhaseAttribute or MixinOrderAttribute or DropsAttribute or UnbindAttribute;

    /// <summary>
    /// What a method states about its place in a chain, as the mixer reads it: classes by full name, and phases with their prefix in
    /// <see cref="Drops"/> and <see cref="Unbind"/>.
    /// </summary>
    private sealed class Contribution
    {
        public List<string> After { get; } = [];
        public List<string> Before { get; } = [];
        public List<string> Drops { get; } = [];
        public List<string> MixinOrder { get; } = [];
        public List<string> Unbind { get; } = [];
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
            if (name == PhaseAttribute)
                phase = ReadPhase(table, attribute, method, methodName, inherited, out addedPhase) ?? phase;
            else if (name == MixinOrderAttribute)
                ReadMixinOrder(table, attribute, method, methodName, point, contribution);
            else if (name == DropsAttribute)
                ReadKeyedNames(table, attribute, method, methodName, point, contribution.Drops);
            else if (name == UnbindAttribute)
                ReadKeyedNames(table, attribute, method, methodName, point, contribution.Unbind);
        }

        if (point != null)
            builder.Insert(new OpExtensionPointSDSL(point.Kind, point.Owner, string.Join(',', point.Phases), string.Join(',', point.ParameterNames)));
        var form = method.Extend switch
        {
            { IsPatch: false } when method.IsBaseLastForm => MethodExtensionPoint.BaseLastForm,
            { IsPatch: false } => MethodExtensionPoint.ExtendForm,
            _ => "",
        };
        // The mixer checks every call to base once the place of each phase is known
        var baseCall = method.IsOverride && point?.Kind == ExtensionPointKindSDSL.Pipeline && method.Body != null
            ? FindBasePosition(method.Body, method.Name.Name).ToString().ToLowerInvariant()
            : "";
        if (phase != null || form.Length > 0 || contribution is { After.Count: > 0 } or { Before.Count: > 0 } or { Drops.Count: > 0 } or { MixinOrder.Count: > 0 } or { Unbind.Count: > 0 })
        {
            builder.Insert(new OpContributionSDSL(phase ?? "", string.Join(',', contribution.After), string.Join(',', contribution.Before), string.Join(',', contribution.Drops),
                addedPhase, form, baseCall, string.Join(',', contribution.MixinOrder), string.Join(',', contribution.Unbind), method.Name.Info.Line));
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
                : method.IsOverride ? "the phases are on the first definition. An override adds a phase with [Phase(Vendor.Name, After = Other)]"
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
        if (attribute.Parameters is [StringLiteral text, ..])
        {
            table.AddError(new(text.Info, string.Format(SDSLErrorMessages.SDSL0255, attribute.Name.Name, methodName, text.Value)));
            return null;
        }
        if (attribute.Parameters.Count == 0 || DottedName(attribute.Parameters[0]) is not { } phase)
        {
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "[Phase] takes one phase name")));
            return null;
        }

        // [Phase(Vendor.Wind, After = A, Before = B)] adds a phase. Its neighbours are checked at link: another class can add them.
        string? after = null, before = null;
        foreach (var parameter in attribute.Parameters.Skip(1))
        {
            if (parameter is AssignExpression { Target: Identifier { Name: "After" or "Before" } side, Value: StringLiteral neighbourText })
            {
                table.AddError(new(neighbourText.Info, string.Format(SDSLErrorMessages.SDSL0255, attribute.Name.Name, methodName, neighbourText.Value)));
                return null;
            }
            if (parameter is AssignExpression { Target: Identifier { Name: "After" or "Before" } key, Value: var value } && DottedName(value) is { } neighbour)
            {
                if (key.Name == "After")
                    after = neighbour.Name;
                else
                    before = neighbour.Name;
            }
            else
            {
                table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "[Phase] takes one phase name, and After = and Before = with one phase name each to add a phase")));
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
            if (MethodExtensionPoint.CheckAddedPhase(phase.Name, table.CurrentShader!.ToClassName(), methodName, $"{ShaderNames.ShortName(point.Owner)}.{method.Name.Name}") is { } problem)
            {
                table.AddError(new(phase.Info, problem));
                return null;
            }
            addedPhase = $"{after},{before}";
            return phase.Name;
        }

        if (!method.IsOverride)
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0141, phase.Name, methodName, "[Phase] goes on an override")));
        else if (point == null)
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0141, phase.Name, methodName, $"{method.Name.Name} is not an extension point, so it has no phases")));
        // A name with a dot can be a phase that another class adds: the mixer checks it
        else if (!point.Phases.Contains(phase.Name) && !phase.Name.Contains('.'))
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
                table.AddError(new(parameter.Info, string.Format(SDSLErrorMessages.SDSL0135, attribute.Name.Name, methodName, "on an override, [MixinOrder] takes After = and Before =. A list of classes goes on the method being extended, or in a block of the effect")));
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
    /// <c>[Drops]</c> on an override: what doesn't run in this chain when this contributor is in the effect. <c>[Unbind]</c> on the method
    /// being extended: what doesn't run in it. <c>Mixins</c> takes classes and <c>Phases</c> phases of the point, a phase with a dot
    /// being one that another class can add. A class that isn't in the effect is ignored at link, but it has to exist.
    /// </summary>
    private static void ReadKeyedNames(SymbolTable table, AnyShaderAttribute attribute, ShaderMethod method, string methodName, MethodExtensionPoint? point, List<string> names)
    {
        var name = attribute.Name.Name;
        var problem = name == DropsAttribute
            ? !method.IsOverride || point == null ? "[Drops] goes on an override of an extension point. To replace a whole class, put [Replaces(Full.Name)] on the class" : null
            : method.IsOverride ? "[Unbind] goes on the method being extended, or in a block of the effect. An override drops what it replaces with [Drops]"
            : point == null ? $"{method.Name.Name} is not an extension point" : null;
        if (problem != null)
        {
            table.AddError(new(attribute.Info, string.Format(SDSLErrorMessages.SDSL0135, name, methodName, problem)));
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
                    if (isPhase || (identifier.Name.Contains('.') && fullName == null))
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
