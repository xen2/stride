using System.Diagnostics.CodeAnalysis;
using Stride.Shaders.Core;
using Stride.Shaders.Parsing.SDSL.AST;
using Stride.Shaders.Spirv.Building;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace Stride.Shaders.Parsing.Analysis;


public record struct SemanticError(TextLocation Location, string Message)
{
    override public string ToString() => $"{Location}: {Message}";
}

public partial class SymbolTable : ISymbolProvider
{
    public bool ResolveArraySizes { get; set; } = true;
    public bool ResolveExternalTypes { get; set; } = true;

    public Dictionary<string, SymbolType> DeclaredTypes { get; } = [];

    /// <summary>
    /// Maps shader class names to their resolved definitions. Separate from DeclaredTypes
    /// because ShaderDefinition is not a SymbolType.
    /// </summary>
    public Dictionary<string, ShaderDefinition> DeclaredShaders { get; } = [];

    /// <summary>
    /// Maps SPIR-V IDs to resolved shader symbols. Separate from ReverseTypes to avoid mutating cached contexts.
    /// </summary>
    private readonly Dictionary<int, ShaderDefinition> loadedShaders = [];

    public ShaderDefinition? ResolveShader(int id) => loadedShaders.GetValueOrDefault(id);

    public ShaderDefinition? ResolveShader(ShaderSymbol symbol)
    {
        if (Context.Types.TryGetValue(symbol, out var id))
            return ResolveShader(id);
        // Fallback: look up by name in DeclaredShaders (ShaderDefinition is no longer a SymbolType,
        // so it may not have a corresponding entry in Context.Types during compilation)
        if (DeclaredShaders.TryGetValue(symbol.Name, out var result))
            return result;
        // Try with full generic class name (e.g. "LightPointGroup<3>")
        if (symbol.GenericArguments.Length > 0)
            return DeclaredShaders.GetValueOrDefault(symbol.ToClassName());
        return null;
    }

    public void RegisterLoadedShader(int id, ShaderDefinition shader) => loadedShaders[id] = shader;

    public SpirvContext Context { get; init; }

    public RootSymbolFrame RootSymbols { get; }
    public List<SemanticError> Errors { get; } = [];
    public List<SemanticError> Warnings { get; } = [];
    public List<SemanticError> Infos { get; } = [];

    // Used by Identifier.ResolveSymbol
    public SymbolFrame CurrentFrame => CurrentSymbols[^1];
    // Used by Identifier.ResolveSymbol
    public List<SymbolFrame> CurrentSymbols { get; } = new();

    // Only valid during compilation (not during ShaderMixin phase)
    public ShaderDefinition? CurrentShader { get; set; }
    public List<ShaderMacro> CurrentMacros { get; set; } = new();
    // Only valid during compilation (not during ShaderMixin phase)
    public List<ShaderClassInstantiation> InheritedShaders { get; } = new();
    // Only valid during compilation: module, root-only and [Pure] flags of the class being compiled
    public Spirv.Specification.ShaderFlagsMask CurrentShaderFlags { get; set; }
    // Only valid during method body analysis
    public ShaderMethod? CurrentMethod { get; set; }

    /// <summary>
    /// The scope of the class being compiled, for the names it uses.
    /// </summary>
    public ShaderNameScope Scope { get; set; } = ShaderNameScope.Global;

    private readonly Dictionary<string, string?> resolvedClassNames = [];

    /// <summary>
    /// The full name of a class named in the code being compiled, or null when no class has this name in scope.
    /// </summary>
    public string? ResolveClassName(string name, TextLocation location)
    {
        if (ShaderLoader == null)
            return null;
        if (resolvedClassNames.TryGetValue(name, out var fullName))
            return fullName;

        fullName = Scope.Resolve(name, ShaderLoader.ImplicitUsings, ShaderLoader.Exists, out var ambiguousWith);
        if (ambiguousWith != null)
            throw new StatementCompileException(new(location, string.Format(SDSLErrorMessages.SDSL0300, name, fullName, ambiguousWith)));

        resolvedClassNames.Add(name, fullName);
        return fullName;
    }

    /// <summary>
    /// The end of the message of a class name that isn't in scope: the namespaces where a class of this name exists.
    /// </summary>
    public string DidYouMean(string name)
    {
        var found = ShaderLoader?.FindFullNames(ShaderNames.ShortName(name)) ?? [];
        return found.Count == 0 ? "" : $" Did you mean {string.Join(" or ", found.Select(x => ShaderNames.Namespace(x) is { } @namespace ? $"`using {@namespace};`" : x))}?";
    }

    public SymbolTable(SpirvContext context, IExternalShaderLoader shaderLoader)
    {
        Context = context;
        RootSymbols = new();
        Push(RootSymbols);
        ShaderLoader = shaderLoader;
    }

    public void Push() => CurrentSymbols.Add(new());

    public void Push(SymbolFrame symbolFrame) => CurrentSymbols.Add(symbolFrame);

    public IExternalShaderLoader ShaderLoader { get; }

    public SymbolFrame Pop()
    {
        var scope = CurrentSymbols[^1];
        CurrentSymbols.RemoveAt(CurrentSymbols.Count - 1);
        return scope;
    }

    public bool TryResolveSymbol(string name, [MaybeNullWhen(false)] out Symbol symbol)
    {
        for (int i = CurrentSymbols.Count - 1; i >= 0; i--)
            if (CurrentSymbols[i].TryGetValue(name, out symbol))
                return true;

        if (CurrentShader != null && CurrentShader.TryResolveSymbol(name, out symbol))
            return true;

        symbol = null;
        return false;
    }

    public bool TryResolveSymbol(int id, [MaybeNullWhen(false)] out Symbol symbol)
    {
        for (int i = CurrentSymbols.Count - 1; i >= 0; --i)
        {
            foreach (var symbol2 in CurrentSymbols[i])
            {
                if (symbol2.Value.IdRef == id)
                {
                    symbol = symbol2.Value;
                    return true;
                }
                // Check function groups
                if (symbol2.Value.Type is FunctionGroupType)
                {
                    foreach (var symbol3 in symbol2.Value.GroupMembers)
                    {
                        if (symbol3.IdRef == id)
                        {
                            symbol = symbol3;
                            return true;
                        }
                    }
                }
            }
        }

        if (CurrentShader != null && CurrentShader.TryResolveSymbol(id, out symbol))
            return true;

        symbol = null;
        return false;
    }

    public Symbol ResolveSymbol(int id)
    {
        if (!TryResolveSymbol(id, out var symbol))
            throw new NotImplementedException($"Cannot find symbol with ID {id} in main context (current shader is {CurrentShader?.Name}");
        return symbol;
    }

    public Symbol ResolveSymbol(string name)
    {
        if (!TryResolveSymbol(name, out var symbol))
            throw new NotImplementedException($"Cannot find symbol {name} in main context (current shader is {CurrentShader?.Name}");
        return symbol;
    }

    public void AddError(SemanticError error)
    {
        // An expression can be analyzed twice (e.g. the base of an accessor chain): report its error once
        if (!Errors.Contains(error))
            Errors.Add(error);
    }

    public void AddWarning(SemanticError warning)
    {
        Warnings.Add(warning);
    }

    public void AddInfo(SemanticError info)
    {
        Infos.Add(info);
    }
}
