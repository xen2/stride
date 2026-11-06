using CommunityToolkit.HighPerformance;
using Stride.Shaders.Core;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Parsing.SDSL;
using Stride.Shaders.Parsing.SDSL.AST;
using Stride.Shaders.Spirv;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;

namespace Stride.Shaders.Parsing.SDFX.AST;


public partial class ShaderEffect(TypeName name, bool isLayer, TextLocation info) : ShaderDeclaration(info)
{
    public TypeName Name { get; set; } = name;

    public BlockStatement? Block { get; set; }

    /// <summary>Whether it is written <c>extend effect</c>: it adds to the effect of this name, which another project declares.</summary>
    public bool IsLayer { get; set; } = isLayer;

    public override string ToString() => Block?.ToString() ?? "(empty)";

    public void Compile(SymbolTable table, CompilerUnit compiler)
    {
        var (builder, context) = compiler;

        builder.Insert(new OpEffectSDFX(Name.Name));
        Block?.Compile(table, compiler);
    }

    internal static ConstantExpression[] CompileGenerics(SymbolTable table, SpirvContext context, ShaderExpressionList? generics)
    {
        var genericCount = generics != null ? generics.Values.Count : 0;
        var genericValues = new ConstantExpression[genericCount];
        if (genericCount > 0)
        {
            int genericIndex = 0;
            foreach (var generic in generics!)
            {
                if (generic is not Literal literal)
                    throw new InvalidOperationException($"Generic value {generic} is not a literal");
                generic.ProcessSymbol(table);
                var compiledValue = generic.CompileConstantValue(table, context);
                genericValues[genericIndex++] = ConstantExpression.ParseFromBuffer(compiledValue.Id, context.GetBuffer(), context);
            }
        }

        return genericValues;
    }
}

public abstract class EffectStatement(TextLocation info) : Statement(info)
{
}

public partial class ShaderSourceDeclaration(Identifier name, TextLocation info, Expression? value = null) : EffectStatement(info)
{
    public Identifier Name { get; set; } = name;
    public Expression? Value { get; set; } = value;
    public bool IsCollection => Name.Name.Contains("Collection");

    public override void Compile(SymbolTable table, CompilerUnit compiler)
    {
        throw new NotImplementedException();
    }

    public override string ToString()
    {
        return $"ShaderSourceCollection {Name} = {Value}";
    }
}

public partial class UsingParams(Expression name, TextLocation info) : EffectStatement(info)
{
    public Expression ParamsName { get; set; } = name;

    public override void ProcessSymbol(SymbolTable table)
    {
        ParamsName.ProcessSymbol(table);
    }

    public override void Compile(SymbolTable table, CompilerUnit compiler)
    {
        var (builder, _) = compiler;

        var paramsName = ParamsName.Compile(table, compiler);
        builder.Insert(new OpParamsUseSDFX(paramsName.Id));
    }

    public override string ToString()
    {
        return $"using params {ParamsName}";
    }
}

public partial class EffectDiscardStatement(TextLocation info) : EffectStatement(info)
{
    public override void Compile(SymbolTable table, CompilerUnit compiler)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// A linkage rule of 4.4 previews: <c>order</c>, <c>bind</c>, <c>unbind</c> or <c>phase</c>. It is read for its error, which gives the block that replaces it.
/// </summary>
/// <param name="keyword">The word that starts the rule.</param>
/// <param name="path">The dotted name after it, cut at the dots.</param>
/// <param name="names">The class names, or for <c>phase</c> the phases after and before it (empty when not given).</param>
public partial class EffectLinkageRule(string keyword, List<string> path, List<string> names, TextLocation info) : EffectStatement(info)
{
    public string Keyword { get; } = keyword;
    public List<string> Path { get; } = path;
    public List<string> Names { get; } = names;

    public override void Compile(SymbolTable table, CompilerUnit compiler) => throw new NotImplementedException();

    public override string ToString() => $"{Keyword} {string.Join('.', Path)} {string.Join(", ", Names)}";

    /// <summary>
    /// The block that replaces this rule.
    /// </summary>
    public string NewForm()
    {
        var isUnbindOfClass = Keyword == "unbind" && Names.Count == 1;
        var methodIndex = Path.Count - (isUnbindOfClass ? 1 : 2);
        var owner = string.Join('.', Path.Take(methodIndex));
        var method = Path[methodIndex];
        var phase = Path[^1];
        static string List(List<string> names) => names.Count == 1 ? names[0] : $"{{ {string.Join(", ", names)} }}";
        return Keyword switch
        {
            "order" => $"extend shader {owner} {{ [MixinOrder({string.Join(", ", Names)})] {method}; }}",
            "bind" => $"extend shader {Names[0]} {{ [Phase({phase})] {owner}.{method}; }}",
            "unbind" when isUnbindOfClass => $"extend shader {owner} {{ [Unbind(Mixins = {{ {Names[0]} }})] {method}; }}",
            "unbind" => $"extend shader {owner} {{ [Unbind(Phases = {{ {phase} }})] {method}; }}",
            _ => $"[Phase(Vendor.{phase}{(Names[0].Length > 0 ? $", After = {Names[0]}" : "")}{(Names[1].Length > 0 ? $", Before = {Names[1]}" : "")})] on a class that joins it, or extend shader X {{ [Phase(Vendor.{phase}, ...)] {owner}.{method}; }}",
        };
    }
}

/// <summary>
/// <c>extend shader Shader { [Attribute] Method; [Attribute] Owner.Method; }</c> in an effect: attributes given to the methods of a class,
/// for this effect. Each replaces the attribute of the same kind that the class writes.
/// </summary>
/// <param name="shader">The class, as written.</param>
/// <param name="members">The methods, with their attributes.</param>
public partial class EffectShaderBlock(string shader, List<EffectBlockMember> members, TextLocation info) : EffectStatement(info)
{
    public string Shader { get; } = shader;
    public List<EffectBlockMember> Members { get; } = members;

    public override void Compile(SymbolTable table, CompilerUnit compiler) => throw new NotImplementedException();

    public override string ToString() => $"extend shader {Shader} {{ {string.Join(" ", Members)} }}";

    /// <summary>
    /// The rules of the block, one per attribute, with the errors of the attributes a block can't take.
    /// </summary>
    /// <param name="resolveClass">Gives the full name of a class as written, or the name as written.</param>
    /// <param name="source">Where the block is written, for messages.</param>
    /// <param name="errors">The errors, with where they are.</param>
    public List<ShaderLinkageRule> ReadRules(Func<string, string> resolveClass, string source, List<(string Message, TextLocation Location)> errors)
    {
        var rules = new List<ShaderLinkageRule>();
        var shader = resolveClass(Shader);
        foreach (var member in Members)
        {
            var where = $"{Shader} {{ ... {member.Name}; }}";
            foreach (var attribute in member.Attributes)
            {
                var name = attribute.Name.Name;
                void Error(string message, TextLocation? location = null) => errors.Add((message, location ?? attribute.Info));

                var problem = name switch
                {
                    "MixinOrder" or "Unbind" or "Phase" => null,
                    "Drops" => "an effect takes contributors out with [Unbind] on the method, extend shader Owner { [Unbind(Mixins = { X })] Method; }",
                    "After" => "write [MixinOrder(After = X)] on the method the class contributes to",
                    _ => $"a block takes [Phase], [MixinOrder] and [Unbind], and [{name}] is none of them",
                };
                problem ??= (name, member.Owner != null) switch
                {
                    ("Unbind", true) => $"[Unbind] goes on the method itself: extend shader {member.Owner} {{ [Unbind(...)] {member.Method}; }}",
                    ("Phase", false) => $"[Phase] goes on a method the class contributes to, written Owner.{member.Method}",
                    _ => null,
                };
                if (problem != null)
                {
                    Error(string.Format(SDSLErrorMessages.SDSL0256, where, problem));
                    continue;
                }
                if (attribute.Parameters.Select(StringIn).FirstOrDefault(x => x != null) is { } text)
                {
                    Error(string.Format(SDSLErrorMessages.SDSL0255, name, $"extend shader {where}", text.Value), text.Info);
                    continue;
                }

                var rule = new ShaderLinkageRule { Shader = shader, Owner = member.Owner != null ? resolveClass(member.Owner) : null, Method = member.Method, Parameters = member.Parameters, Source = source };
                var isRead = name switch
                {
                    "MixinOrder" => ReadMixinOrder(rule, attribute, member, Error),
                    "Unbind" => ReadUnbind(rule, attribute, Error),
                    _ => ReadPhase(rule, attribute, where, Error),
                };
                if (isRead)
                    rules.AddRange(OnePerKey(rule));
            }
        }
        return rules;

        bool ReadMixinOrder(ShaderLinkageRule rule, AnyShaderAttribute attribute, EffectBlockMember member, Action<string, TextLocation?> error)
        {
            rule.Kind = ShaderLinkageKind.MixinOrder;
            foreach (var parameter in attribute.Parameters)
            {
                var (key, values) = parameter is AssignExpression { Target: Identifier { Name: "After" or "Before" } target, Value: var value } ? (target.Name, Names(value)) : (null, [parameter]);
                if ((key != null) != (member.Owner != null))
                {
                    error(string.Format(SDSLErrorMessages.SDSL0135, "MixinOrder", $"extend shader {Shader} {{ ... {member.Name}; }}", member.Owner != null
                        ? "on a method the class contributes to, [MixinOrder] takes After = and Before ="
                        : "on a method of the class, [MixinOrder] lists the classes in their running order: [MixinOrder(A, B)]"), parameter.Info);
                    return false;
                }
                foreach (var item in values)
                {
                    if (ExtensionPoints.DottedName(item) is not { } identifier)
                    {
                        error(string.Format(SDSLErrorMessages.SDSL0135, "MixinOrder", $"extend shader {Shader} {{ ... {member.Name}; }}", $"'{item}' is not a class name"), item.Info);
                        return false;
                    }
                    (key == null ? rule.Names : key == "After" ? rule.After : rule.Before).Add(resolveClass(identifier.Name));
                }
            }
            if (member.Owner == null && rule.Names.Count < 2 || member.Owner != null && rule.After.Count + rule.Before.Count == 0)
            {
                error(string.Format(SDSLErrorMessages.SDSL0135, "MixinOrder", $"extend shader {Shader} {{ ... {member.Name}; }}", member.Owner == null ? "[MixinOrder] lists at least two classes" : "[MixinOrder] takes the classes it runs after or before"), null);
                return false;
            }
            return true;
        }

        bool ReadUnbind(ShaderLinkageRule rule, AnyShaderAttribute attribute, Action<string, TextLocation?> error)
        {
            rule.Kind = ShaderLinkageKind.Unbind;
            var keys = "[Unbind] takes Mixins = { ... } for classes and Phases = { ... } for phases";
            if (attribute.Parameters.Count == 0)
            {
                error(string.Format(SDSLErrorMessages.SDSL0254, "Unbind", $"extend shader {Shader}", keys), null);
                return false;
            }
            foreach (var parameter in attribute.Parameters)
            {
                if (parameter is not AssignExpression { Target: Identifier { Name: "Mixins" or "Phases" } key, Value: var value })
                {
                    error(string.Format(SDSLErrorMessages.SDSL0254, "Unbind", $"extend shader {Shader}", keys), parameter.Info);
                    return false;
                }
                foreach (var item in Names(value))
                {
                    if (ExtensionPoints.DottedName(item) is not { } identifier)
                    {
                        error(string.Format(SDSLErrorMessages.SDSL0254, "Unbind", $"extend shader {Shader}", $"'{item}' is not a name"), item.Info);
                        return false;
                    }
                    if (key.Name == "Mixins")
                        rule.Mixins.Add(resolveClass(identifier.Name));
                    else
                        rule.Phases.Add(identifier.Name);
                }
            }
            return true;
        }

        // A phase added in a block is added by the class of the block
        bool ReadPhase(ShaderLinkageRule rule, AnyShaderAttribute attribute, string where, Action<string, TextLocation?> error)
        {
            rule.Kind = ShaderLinkageKind.Phase;
            if (attribute.Parameters.Count == 0 || ExtensionPoints.DottedName(attribute.Parameters[0]) is not { } phase)
            {
                error(string.Format(SDSLErrorMessages.SDSL0135, "Phase", $"extend shader {where}", "[Phase] takes one phase name"), null);
                return false;
            }
            rule.Phase = phase.Name;
            foreach (var parameter in attribute.Parameters.Skip(1))
            {
                if (parameter is AssignExpression { Target: Identifier { Name: "After" or "Before" } key, Value: var value } && ExtensionPoints.DottedName(value) is { } neighbour)
                {
                    (key.Name == "After" ? rule.After : rule.Before).Add(neighbour.Name);
                }
                else
                {
                    error(string.Format(SDSLErrorMessages.SDSL0135, "Phase", $"extend shader {where}", "[Phase] takes one phase name, and After = and Before = with one phase name each to add a phase"), parameter.Info);
                    return false;
                }
            }
            if (rule.After.Count + rule.Before.Count > 0 && MethodExtensionPoint.CheckAddedPhase(rule.Phase, shader, $"extend shader {where}", $"{rule.Owner}.{rule.Method}") is { } problem)
            {
                error(problem, phase.Info);
                return false;
            }
            return true;
        }

        static List<Expression> Names(Expression value) => value is ArrayLiteral list ? list.Values : [value];

        // One rule per key, as the C# calls give them: UnbindPhases and UnbindMixins, MixinOrderAfter and MixinOrderBefore
        static IEnumerable<ShaderLinkageRule> OnePerKey(ShaderLinkageRule rule)
        {
            if (rule.Kind == ShaderLinkageKind.Unbind && rule.Phases.Count > 0 && rule.Mixins.Count > 0)
                return [With(rule, x => x.Mixins = []), With(rule, x => x.Phases = [])];
            if (rule is { Kind: ShaderLinkageKind.MixinOrder, IsOnContribution: true } && rule.After.Count > 0 && rule.Before.Count > 0)
                return [With(rule, x => x.Before = []), With(rule, x => x.After = [])];
            return [rule];

            static ShaderLinkageRule With(ShaderLinkageRule rule, Action<ShaderLinkageRule> change)
            {
                var copy = new ShaderLinkageRule
                {
                    Kind = rule.Kind, Shader = rule.Shader, Owner = rule.Owner, Method = rule.Method, Parameters = rule.Parameters, Phase = rule.Phase,
                    Names = [.. rule.Names], After = [.. rule.After], Before = [.. rule.Before], Phases = [.. rule.Phases], Mixins = [.. rule.Mixins], Level = rule.Level, Source = rule.Source,
                };
                change(copy);
                return copy;
            }
        }

        static StringLiteral? StringIn(Expression parameter) => parameter switch
        {
            StringLiteral text => text,
            AssignExpression { Value: StringLiteral text } => text,
            AssignExpression { Value: ArrayLiteral list } => list.Values.OfType<StringLiteral>().FirstOrDefault(),
            ArrayLiteral list => list.Values.OfType<StringLiteral>().FirstOrDefault(),
            _ => null,
        };
    }
}

/// <summary>
/// A method of an <see cref="EffectShaderBlock"/> with its attributes: <c>Method</c>, <c>Owner.Method</c>, and the parameter types of an overload.
/// </summary>
/// <param name="owner">The class of the first definition of a method the class contributes to, as written, or null.</param>
/// <param name="parameters">The parameter types, separated by commas, or null.</param>
public sealed class EffectBlockMember(List<AnyShaderAttribute> attributes, string? owner, string method, string? parameters, TextLocation info)
{
    public List<AnyShaderAttribute> Attributes { get; } = attributes;
    public string? Owner { get; } = owner;
    public string Method { get; } = method;
    public string? Parameters { get; } = parameters;
    public TextLocation Info { get; } = info;

    /// <summary>The method as written.</summary>
    public string Name => $"{(Owner != null ? $"{Owner}." : "")}{Method}{(Parameters != null ? $"({Parameters})" : "")}";

    public override string ToString() => $"{string.Concat(Attributes.Select(x => $"[{x}] "))}{Name};";
}

/// <summary>
/// Type of a mixin.
/// </summary>
public enum MixinStatementType
{
    /// <summary>
    /// The default mixin (standard mixin).
    /// </summary>
    Default,

    /// <summary>
    /// The compose mixin used to set a composition (using =).
    /// </summary>
    ComposeSet,

    /// <summary>
    /// The compose mixin used to add a composition (using +=).
    /// </summary>
    ComposeAdd,

    /// <summary>
    /// The child mixin used to specify a children shader.
    /// </summary>
    Child,

    /// <summary>
    /// The clone mixin to clone the current mixins where the clone is emitted.
    /// </summary>
    Clone,

    /// <summary>
    /// The remove mixin to remove a mixin from current mixins.
    /// </summary>
    Remove,

    /// <summary>
    /// The macro mixin to declare a variable to be exposed in the mixin
    /// </summary>
    Macro,


}

public partial class Mixin(Specification.MixinKindSDFX kind, Identifier? target, Expression value, TextLocation info) : Statement(info)
{
    public Specification.MixinKindSDFX Kind { get; } = kind;
    public Identifier? Target { get; } = target;
    public Expression Value { get; } = value;
    public override string ToString() => $"{Type} {Target} {Value}";

    public override void ProcessSymbol(SymbolTable table)
    {
    }

    public override void Compile(SymbolTable table, CompilerUnit compiler)
    {
        var (builder, context) = compiler;

        throw new NotImplementedException();
        //builder.Insert(new OpMixinSDFX(Kind, Target?.Name ?? "", Value., Value.Generics));
    }
}
