using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Parsing.SDSL;
using Stride.Shaders.Parsing.SDSL.AST;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;
namespace Stride.Shaders.Parsing.SDFX.AST;

public partial class EffectFlow(TextLocation info) : EffectStatement(info)
{
    public override void Compile(SymbolTable table, CompilerUnit compiler)
    {
        // Base class — subclasses override
    }
}

public partial class EffectForEach(TypeName typename, Identifier variable, Expression collection, Statement body, TextLocation info) : EffectFlow(info)
{
    public TypeName Typename { get; set; } = typename;
    public Identifier Variable { get; set; } = variable;
    public Expression Collection { get; set; } = collection;
    public Statement Body { get; set; } = body;

    public override void Compile(SymbolTable table, CompilerUnit compiler)
    {
        var (builder, context) = compiler;

        // Compile the collection expression to get its ID
        var collectionValue = Collection.Compile(table, compiler);

        // Emit foreach header
        var iterVarId = context.Bound++;
        var typeId = 0; // Element type resolved at interpretation time
        builder.Insert(new OpForeachSDSL(typeId, iterVarId, collectionValue.Id));

        // Compile body
        Body.Compile(table, compiler);

        // Emit foreach end marker
        builder.Insert(new OpForeachEndSDSL());
    }

    public override string ToString()
    {
        return $"foreach({Typename} {Variable} in {Collection})\n{Body}";
    }
}
