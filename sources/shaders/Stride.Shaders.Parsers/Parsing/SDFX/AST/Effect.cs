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
        if (Block != null)
            CompileEffectStatements(Block.Statements, table, builder, context, compiler);
    }

    /// <summary>
    /// Compiles a list of statements in SDFX effect context.
    /// ConditionalFlow is handled specially: conditions that are param access patterns
    /// (e.g. BasicParams.MixA) are compiled as OpLoadParamSDFX instead of regular expressions.
    /// </summary>
    private static void CompileEffectStatements(List<Statement> statements, SymbolTable table, SpirvBuilder builder, SpirvContext context, CompilerUnit compiler)
    {
        foreach (var s in statements)
        {
            if (s is ConditionalFlow cf)
                CompileEffectConditional(cf, table, builder, context, compiler);
            else
                s.Compile(table, compiler);
        }
    }

    private static void CompileEffectConditional(ConditionalFlow cf, SymbolTable table, SpirvBuilder builder, SpirvContext context, CompilerUnit compiler)
    {
        // Compile the condition as an OpLoadParamSDFX if it's a param access pattern
        var conditionId = CompileEffectCondition(cf.If.Condition, table, builder, context, compiler);

        var mergeLabel = context.Bound++;
        var trueLabel = context.Bound++;

        // Handle else/elseif
        int? falseLabel = (cf.ElseIfs.Count > 0 || cf.Else != null) ? context.Bound++ : null;

        builder.Insert(new OpSelectionMerge(mergeLabel, Specification.SelectionControlMask.None));
        builder.Insert(new OpBranchConditional(conditionId, trueLabel, falseLabel ?? mergeLabel, []));

        // True block
        builder.Insert(new OpLabel(trueLabel));
        CompileEffectBody(cf.If.Body, table, builder, context, compiler);
        builder.Insert(new OpBranch(mergeLabel));

        // ElseIf / Else blocks
        if (cf.ElseIfs.Count > 0 || cf.Else != null)
        {
            builder.Insert(new OpLabel(falseLabel!.Value));

            if (cf.ElseIfs.Count > 0)
            {
                // Chain elseifs recursively
                var syntheticCf = new ConditionalFlow(
                    new If(cf.ElseIfs[0].Condition, cf.ElseIfs[0].Body, cf.ElseIfs[0].Info),
                    cf.ElseIfs[0].Info)
                {
                    ElseIfs = cf.ElseIfs.Count > 1 ? [.. cf.ElseIfs.Skip(1)] : [],
                    Else = cf.Else
                };
                CompileEffectConditional(syntheticCf, table, builder, context, compiler);
            }
            else if (cf.Else != null)
            {
                CompileEffectBody(cf.Else.Body, table, builder, context, compiler);
            }

            builder.Insert(new OpBranch(mergeLabel));
        }

        // Merge block
        builder.Insert(new OpLabel(mergeLabel));
    }

    private static int CompileEffectCondition(Expression condition, SymbolTable table, SpirvBuilder builder, SpirvContext context, CompilerUnit compiler)
    {
        // Check for param access pattern: ParamsType.FieldName
        if (condition is AccessorChainExpression ace
            && ace.Source is Identifier paramSource
            && ace.Accessors.Count > 0
            && ace.Accessors[0] is IdentifierBase fieldAccess)
        {
            var resultId = context.Bound++;
            builder.Insert(new OpLoadParamSDFX(resultId, paramSource.Name, fieldAccess.ToString()!));
            return resultId;
        }

        // Fallback: compile normally (for literal conditions, etc.)
        condition.ProcessSymbol(table);
        var value = condition.CompileAsValue(table, compiler);
        return value.Id;
    }

    private static void CompileEffectBody(Statement body, SymbolTable table, SpirvBuilder builder, SpirvContext context, CompilerUnit compiler)
    {
        if (body is BlockStatement block)
            CompileEffectStatements(block.Statements, table, builder, context, compiler);
        else if (body is ConditionalFlow cf)
            CompileEffectConditional(cf, table, builder, context, compiler);
        else
            body.Compile(table, compiler);
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
        var (builder, context) = compiler;

        if (Value is AccessorChainExpression ace
            && ace.Source is Identifier paramSource
            && ace.Accessors.Count > 0
            && ace.Accessors[0] is IdentifierBase fieldAccess)
        {
            // Pattern: var x = ParamsType.FieldName → OpLoadParamSDFX
            builder.Insert(new OpLoadParamSDFX(context.Bound++, paramSource.Name, fieldAccess.ToString()!));
        }
        else if (Value != null)
        {
            // Fallback: compile as a general expression
            Value.Compile(table, compiler);
        }
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
