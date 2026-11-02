using System.Collections.Immutable;
using Stride.Shaders.Parsing;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Core;

/// <summary>
/// A module method declared for overriding, with how its overrides combine.
/// </summary>
/// <param name="Owner">The full name of the class of the first definition.</param>
/// <param name="Phases">In the order the call reaches them.</param>
/// <param name="ParameterNames">The names of the parameters of the first definition: the error of an <c>extend</c> with no parameters names them.</param>
public sealed record MethodExtensionPoint(ExtensionPointKindSDSL Kind, string Owner, string[] Phases, string[] ParameterNames)
{
    /// <summary>The phase of a <c>pipeline</c> or <c>around</c> written without phases, and of the default body of a pipeline that declares it.</summary>
    public const string DefaultPhase = "Default";

    /// <summary>What a contribution records when it is written with <c>extend</c>.</summary>
    public const string ExtendForm = "extend";

    /// <summary>
    /// What the second form of an <c>extend</c> records: one whose phase its class can't place compiles twice, calling <c>base</c> first
    /// and last, and the mixer keeps the one that fits.
    /// </summary>
    public const string BaseLastForm = "extend, base last";

    /// <summary>The implicit phase after every declared phase, in the order of the mix: where a contributor without a phase is.</summary>
    public const string ImplicitPhase = "External";

    /// <summary>What marks a phase in the names of <c>[Drops]</c> and <c>[Unbind]</c>, which are otherwise full names of classes.</summary>
    public const char ReplacedPhasePrefix = '@';

    /// <summary>What a contribution to a pipeline records when its body calls <c>base</c> as its first statement.</summary>
    public const string BaseCallFirst = "first";

    /// <summary>What a contribution to a pipeline records when its body calls <c>base</c> as its last statement.</summary>
    public const string BaseCallLast = "last";

    /// <summary>The start of the phases that only the engine adds.</summary>
    public const string EnginePhasePrefix = "Stride.";

    /// <summary>The phase of the default body of a pipeline: <see cref="DefaultPhase"/>, else the first one.</summary>
    public int DefaultPhaseIndex => Math.Max(Array.IndexOf(Phases, DefaultPhase), 0);

    /// <summary>
    /// The error of a phase added by a class or an effect that doesn't own the point, or null. The owner declares plain names, so an
    /// added phase has a prefix, and <see cref="EnginePhasePrefix"/> needs an adder of the engine, which only uses <c>Stride</c> namespaces.
    /// </summary>
    /// <param name="phase">The added phase.</param>
    /// <param name="adderFullName">The full name of the class or the effect that adds it.</param>
    /// <param name="by">The adder, for the message.</param>
    /// <param name="pointName">The point, for the message.</param>
    public static string? CheckAddedPhase(string phase, string adderFullName, string by, string pointName)
    {
        if (!phase.Contains('.'))
            return string.Format(SDSLErrorMessages.SDSL0252, phase, by, pointName, $"MyLibrary.{phase}");
        if (phase.StartsWith(EnginePhasePrefix, StringComparison.Ordinal) && !adderFullName.StartsWith(EnginePhasePrefix, StringComparison.Ordinal))
            return string.Format(SDSLErrorMessages.SDSL0253, phase, by);
        return null;
    }
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
/// <param name="Id">The symbol identifier.</param>
/// <param name="Type">The symbol type.</param>
/// <param name="IdRef">The SPIR-V identifier associated with the symbol.</param>
/// <param name="AccessChain">The optional index used to access the symbol.</param>
/// <param name="MemberAccessWithImplicitThis">The type used for implicit member access, if any.</param>
/// <param name="GroupMembers">Only used for specific <see cref="Type"/> such as <see cref="FunctionGroupType"/></param>
/// <param name="MethodDefaultParameters">The method's default parameter values, if any.</param>
/// <param name="ExternalConstant">The external constant expression, if any.</param>
/// <param name="OwnerType">The shader that owns the symbol, if any.</param>
/// <param name="ExtensionPoint">How the overrides of a module method combine, if it declares it.</param>
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
