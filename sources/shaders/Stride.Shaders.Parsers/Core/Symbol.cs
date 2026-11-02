using System.Collections.Immutable;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Core;

/// <summary>
/// A module method declared for overriding, with how its overrides combine.
/// </summary>
/// <param name="Owner">The full name of the class of the first definition.</param>
/// <param name="Phases">In the order the call reaches them.</param>
/// <param name="ParameterNames">The names of the parameters of the first definition: an <c>extend</c> uses them without repeating them.</param>
public sealed record MethodExtensionPoint(ExtensionPointKindSDSL Kind, string Owner, string[] Phases, string[] ParameterNames)
{
    /// <summary>The phase of a <c>[Pipeline]</c> or <c>[Around]</c> written without phases, and of the default body of a pipeline that declares it.</summary>
    public const string DefaultPhase = "Default";

    /// <summary>What a contribution records when it is written with <c>extend</c>.</summary>
    public const string ExtendForm = "extend";

    /// <summary>What marks a phase in the targets of <c>[Replaces]</c>, which are otherwise full names of classes.</summary>
    public const char ReplacedPhasePrefix = '@';

    /// <summary>The phase of the default body of a pipeline: <see cref="DefaultPhase"/>, else the first one.</summary>
    public int DefaultPhaseIndex => Math.Max(Array.IndexOf(Phases, DefaultPhase), 0);
}


public enum SymbolKind
{
    Shader,
    Struct,
    Method,
    MethodGroup,
    Variable,
    Constant,
    ConstantGeneric,
    Composition,
    CBuffer,
    TBuffer,
    RGroup,
    SamplerState,
    SamplerComparisonState,
    ExternalType,
}

public enum Storage : ushort
{
    None,
    Uniform,
    UniformConstant,
    Stream,
    Function,
    Generic,
}

public enum StreamIO : byte
{
    Input,
    Output
}

public record struct SymbolID(string Name, SymbolKind Kind, Storage Storage = 0, bool IsStage = false);
public record struct StreamInfo(ushort EntryPoint, StreamIO Stream);

public record struct MethodSymbolDefaultParameters(ConstantExpression[] DefaultValues);

public record struct ExternalConstant(ConstantExpression Expression);

/// <summary>
/// Defines a symbol.
/// </summary>
/// <param name="GroupMembers">Only used for specific <see cref="Type"/> such as <see cref="FunctionGroupType"/></param>
public record Symbol(SymbolID Id, SymbolType Type, int IdRef, int? AccessChain = null, SymbolType? MemberAccessWithImplicitThis = null, ImmutableArray<Symbol> GroupMembers = default, MethodSymbolDefaultParameters? MethodDefaultParameters = null, ExternalConstant? ExternalConstant = null, ShaderDefinition? OwnerType = null, MethodExtensionPoint? ExtensionPoint = null)
{
    public int IdRef { get; set; } = IdRef;
    public SymbolType Type { get; set; } = Type;
}



public record struct MixinParentSymbol();
public record struct MixinChildSymbol();
public record struct StructSymbol();
public record struct MethodSymbol();
public record struct VariableSymbol();
public record struct ConstantSymbol();
public record struct ConstantGenericSymbol();
public record struct CompositionSymbol();
public record struct CBufferSymbol();
public record struct TBufferSymbol();
public record struct RGroupSymbol();
