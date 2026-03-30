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
        if (Block == null) return;

        // Register effect params types in the symbol table so expressions like
        // "BasicParams.MixA" resolve normally via AccessorChainExpression.
        table.Push();
        foreach (var s in Block.Statements)
        {
            if (s is UsingParams usingParams)
            {
                var paramsName = usingParams.ParamsName.ToString()!;
                table.CurrentFrame[paramsName] = new Symbol(
                    new SymbolID(paramsName, SymbolKind.Variable),
                    new EffectParamsType(paramsName), 0);
            }
        }

        // Process symbols then compile each statement
        foreach (var s in Block.Statements)
            s.ProcessSymbol(table);
        foreach (var s in Block.Statements)
            s.Compile(table, compiler);

        table.Pop();
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

    public override void ProcessSymbol(SymbolTable table)
    {
        Value?.ProcessSymbol(table);
    }

    public override void Compile(SymbolTable table, CompilerUnit compiler)
    {
        if (Value != null)
            Value.Compile(table, compiler);
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
        var (builder, context) = compiler;

        // Emit the params type name as a string constant — this is metadata only,
        // the evaluator doesn't need to resolve it at interpret time.
        var nameStr = ParamsName.ToString()!;
        var nameId = context.Bound++;
        context.Add(new OpConstantStringSDSL(nameId, nameStr));
        builder.Insert(new OpParamsUseSDFX(nameId));
    }

    public override string ToString()
    {
        return $"using params {ParamsName}";
    }
}

public partial class EffectDiscardStatement(TextLocation info) : EffectStatement(info)
{
    public override void ProcessSymbol(SymbolTable table) { }

    public override void Compile(SymbolTable table, CompilerUnit compiler)
    {
        compiler.Builder.Insert(new OpDiscardSDFX());
    }
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

        // Extract mixin name and generic parameters from Value expression
        ExtractGenericParameters(Value, out var mixinName, out var genericParameters);

        // Emit mixin name as a string constant
        var nameStr = mixinName.ToString()!;
        var nameId = context.Bound++;
        context.Add(new OpConstantStringSDSL(nameId, nameStr));

        // Emit target name as a string constant (for compositions, child, macro)
        int targetId = 0;
        if (Target != null)
        {
            targetId = context.Bound++;
            context.Add(new OpConstantStringSDSL(targetId, Target.Name));
        }
        else if (Kind == Specification.MixinKindSDFX.Child)
        {
            // mixin child MyEffect => target defaults to the mixin name
            targetId = nameId;
        }

        // Compile generic parameters to constant IDs
        int[] genericIds = [];
        if (genericParameters != null && genericParameters.Values.Count > 0)
        {
            genericIds = new int[genericParameters.Values.Count];
            for (int i = 0; i < genericParameters.Values.Count; i++)
            {
                var generic = genericParameters.Values[i];
                generic.ProcessSymbol(table);
                var compiledValue = generic.CompileConstantValue(table, context);
                genericIds[i] = compiledValue.Id;
            }
        }

        builder.Insert(new OpMixinSDFX(Kind, targetId, nameId, new LiteralArray<int>(genericIds)));
    }

    /// <summary>
    /// Separates the mixin name from generic parameters in the Value expression.
    /// Mirrors EffectCodeWriter.ExtractGenericParameters.
    /// </summary>
    private static void ExtractGenericParameters(Expression value, out Expression mixinName, out ShaderExpressionList? genericParameters)
    {
        // Pattern: A.B<Param1, Param2>
        if (value is AccessorChainExpression ace && ace.Accessors.Count > 0 && ace.Accessors[^1] is GenericIdentifier gi1)
        {
            mixinName = new AccessorChainExpression(ace.Source, ace.Info) { Accessors = [.. ace.Accessors[..^1], gi1.Name] };
            genericParameters = gi1.Generics;
        }
        // Pattern: A<Param1, Param2>
        else if (value is GenericIdentifier gi2)
        {
            mixinName = gi2.Name;
            genericParameters = gi2.Generics;
        }
        else
        {
            mixinName = value;
            genericParameters = null;
        }
    }
}
