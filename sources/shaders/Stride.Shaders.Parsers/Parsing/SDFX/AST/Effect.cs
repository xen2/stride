using CommunityToolkit.HighPerformance;
using Stride.Shaders.Core;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Parsing.SDSL;
using Stride.Shaders.Parsing.SDSL.AST;
using Stride.Shaders.Spirv;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;

namespace Stride.Shaders.Parsing.SDFX.AST;


public partial class ShaderEffect(TypeName name, bool isPartial, TextLocation info) : ShaderDeclaration(info)
{
    public TypeName Name { get; set; } = name;

    public BlockStatement? Block { get; set; }
    public bool IsPartial { get; set; } = isPartial;

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
/// A wiring line: <c>order</c>, <c>bind</c>, <c>unbind</c> or <c>phase</c>.
/// </summary>
/// <param name="keyword">The word that starts the line.</param>
/// <param name="path">The dotted name after it, cut at the dots.</param>
/// <param name="names">The class names, or for <c>phase</c> the phases after and before it (empty when not given).</param>
public partial class EffectWiring(string keyword, List<string> path, List<string> names, TextLocation info) : EffectStatement(info)
{
    public string Keyword { get; } = keyword;
    public List<string> Path { get; } = path;
    public List<string> Names { get; } = names;

    public override void Compile(SymbolTable table, CompilerUnit compiler) => throw new NotImplementedException();

    public override string ToString() => $"{Keyword} {string.Join('.', Path)} {string.Join(", ", Names)}";
}

/// <summary>
/// <c>feature K.Flag { } else { }</c>: an <c>if</c> on a parameter that a tool can list as a structural axis.
/// </summary>
public partial class EffectFeature(Expression condition, BlockStatement block, BlockStatement? otherwise, TextLocation info) : EffectStatement(info)
{
    public Expression Condition { get; } = condition;
    public BlockStatement Block { get; } = block;
    public BlockStatement? Otherwise { get; } = otherwise;

    public override void Compile(SymbolTable table, CompilerUnit compiler) => throw new NotImplementedException();

    public override string ToString() => $"feature {Condition}";
}

/// <summary>
/// <c>optional K.Slot { }</c>: what an effect mixes when C# gives a value to a parameter. The value is data, its presence is an axis.
/// </summary>
public partial class EffectOptional(Expression slot, BlockStatement block, TextLocation info) : EffectStatement(info)
{
    public Expression Slot { get; } = slot;
    public BlockStatement Block { get; } = block;

    public override void Compile(SymbolTable table, CompilerUnit compiler) => throw new NotImplementedException();

    public override string ToString() => $"optional {Slot}";
}

/// <summary>
/// <c>variant Name { }</c>: the effect with what the block adds. Unlike <c>mixin child</c>, it has all the statements of the effect, wherever it is written.
/// </summary>
public partial class EffectVariant(Identifier name, BlockStatement block, TextLocation info) : EffectStatement(info)
{
    public Identifier Name { get; } = name;
    public BlockStatement Block { get; } = block;

    public override void Compile(SymbolTable table, CompilerUnit compiler) => throw new NotImplementedException();

    public override string ToString() => $"variant {Name}";
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
