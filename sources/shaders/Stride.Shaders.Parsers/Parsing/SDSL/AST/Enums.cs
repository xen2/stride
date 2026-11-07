// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Shaders.Core;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Spirv;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;

namespace Stride.Shaders.Parsing.SDSL.AST;

/// <summary>
/// <c>enum class X : uint { A, B = 3 }</c>: a closed set of named values, declared in a class.
/// </summary>
public partial class ShaderEnum(Identifier name, TextLocation info) : ShaderElement(info)
{
    /// <summary>
    /// The name of the enum.
    /// </summary>
    public Identifier Name { get; set; } = name;

    /// <summary>
    /// Whether it is written <c>enum class</c>, the only form there is (a plain <c>enum</c> is SDSL0270).
    /// </summary>
    public bool IsClass { get; set; }

    /// <summary>
    /// The type after the colon, <c>int</c> or <c>uint</c>.
    /// </summary>
    public TypeName? UnderlyingTypeName { get; set; }

    /// <summary>
    /// The values, in declaration order.
    /// </summary>
    public List<ShaderEnumValue> Values { get; set; } = [];

    /// <summary>
    /// The attributes written before <c>enum</c>: <c>[CSharp]</c> (<see cref="CSharpTypeAttribute"/>).
    /// </summary>
    public List<ShaderAttribute> Attributes { get; set; } = [];

    /// <summary>
    /// Checks the declaration (SDSL0270 to SDSL0274) and declares the <see cref="EnumType"/> in the class.
    /// </summary>
    public override void ProcessSymbol(SymbolTable table, SpirvContext context)
    {
        if (!IsClass)
            table.AddError(new(Name.Info, string.Format(SDSLErrorMessages.SDSL0270, Name.Name)));

        var underlyingType = ScalarType.UInt;
        if (UnderlyingTypeName == null)
            table.AddError(new(Name.Info, string.Format(SDSLErrorMessages.SDSL0271, Name.Name, "has no underlying type")));
        else if (UnderlyingTypeName.Name is "int" or "uint" && !UnderlyingTypeName.IsArray)
            underlyingType = UnderlyingTypeName.Name == "int" ? ScalarType.Int : ScalarType.UInt;
        else
            table.AddError(new(UnderlyingTypeName.Info, string.Format(SDSLErrorMessages.SDSL0271, Name.Name, $"has the underlying type {UnderlyingTypeName}")));

        var (min, max) = underlyingType == ScalarType.Int ? ((Int128)int.MinValue, (Int128)int.MaxValue) : (0, uint.MaxValue);
        var values = new List<EnumValue>();
        Int128 next = 0;
        foreach (var value in Values)
        {
            var number = next;
            if (value.Value != null && !TryGetInteger(value.Value, out number) || number < min || number > max)
            {
                table.AddError(new((value.Value ?? value.Name).Info, string.Format(SDSLErrorMessages.SDSL0272, Name.Name, value.Name.Name, value.Value?.Info.Text.ToString() ?? number.ToString(), min, max, underlyingType)));
                number = min;
            }
            next = number + 1;

            if (values.Exists(x => x.Name == value.Name.Name))
                table.AddError(new(value.Name.Info, string.Format(SDSLErrorMessages.SDSL0274, $"{Name.Name}.{value.Name.Name}", $"in {Name.Name}")));
            else if (values.Exists(x => x.Value == (long)number))
                table.AddError(new(value.Name.Info, string.Format(SDSLErrorMessages.SDSL0273, Name.Name, value.Name.Name, number, values.Find(x => x.Value == (long)number).Name)));
            else
                values.Add(new(value.Name.Name, (long)number));
        }

        var owner = table.CurrentShader?.Name ?? string.Empty;
        if (table.DeclaredTypes.TryGetValue(Name.Name, out var existing) && existing is EnumType or StructType)
        {
            var declarer = existing is EnumType { Owner: var enumOwner } ? enumOwner : owner;
            var where = declarer == owner ? $"in {ShaderNames.ShortName(owner)}" : $"in {ShaderNames.ShortName(owner)} and in {ShaderNames.ShortName(declarer)}";
            table.AddError(new(Name.Info, string.Format(SDSLErrorMessages.SDSL0274, Name.Name, where)));
            return;
        }

        var enumType = new EnumType(Name.Name, owner, underlyingType, values);
        Type = enumType;
        table.DeclaredTypes[Name.Name] = enumType;
        // In the class's SPIR-V even when the class doesn't use it, for the classes that inherit or import it
        context.GetOrRegister(enumType);
    }

    /// <summary>
    /// The number of an integer literal, negated or in parentheses.
    /// </summary>
    private static bool TryGetInteger(Expression expression, out Int128 value)
    {
        switch (expression)
        {
            case IntegerLiteral literal:
                value = literal.Suffix is { Size: 64, Signed: false } ? literal.ULongValue : literal.Value;
                return true;
            case PrefixExpression { Operator: Operator.Minus, Expression: var operand } when TryGetInteger(operand, out var positive):
                value = -positive;
                return true;
            case ParenthesisExpression parenthesis:
                return TryGetInteger(parenthesis.Expression, out value);
            default:
                value = 0;
                return false;
        }
    }

    public override string ToString() => $"enum {(IsClass ? "class " : "")}{Name}{(UnderlyingTypeName != null ? $" : {UnderlyingTypeName}" : "")} {{ {string.Join(", ", Values)} }}";
}

/// <summary>
/// A value in the declaration of an enum: <c>B = 3</c>.
/// </summary>
public partial class ShaderEnumValue(Identifier name, Expression? value, TextLocation info) : Node(info)
{
    /// <summary>
    /// The name of the value.
    /// </summary>
    public Identifier Name { get; set; } = name;

    /// <summary>
    /// The number written after <c>=</c>; the next number after the previous value when there is none.
    /// </summary>
    public Expression? Value { get; set; } = value;

    public override string ToString() => Value != null ? $"{Name} = {Value}" : Name.ToString();
}

/// <summary>
/// A value of an enum: <c>SlotChannel.R</c>. It is a constant of the enum type.
/// </summary>
public partial class EnumValueExpression(EnumType enumType, Identifier value, TextLocation info) : Expression(info)
{
    /// <summary>
    /// The enum of the value.
    /// </summary>
    public EnumType EnumType { get; } = enumType;

    /// <summary>
    /// The name of the value, after the dot.
    /// </summary>
    public Identifier Value { get; } = value;

    public override void ProcessSymbol(SymbolTable table, SymbolType? expectedType = null)
    {
        if (!EnumType.TryGetValue(Value.Name, out _))
            table.AddError(new(Value.Info, string.Format(SDSLErrorMessages.SDSL0280, EnumType.Name, Value.Name, string.Join(", ", EnumType.Values.Select(x => x.Name)))));
        Type = EnumType;
    }

    public override SpirvValue CompileImpl(SymbolTable table, CompilerUnit compiler)
    {
        EnumType.TryGetValue(Value.Name, out var value);
        return Enums.CompileConstant(compiler.Context, EnumType, value.Value);
    }

    public override string ToString() => $"{EnumType.Name}.{Value}";
}

/// <summary>
/// The rules of enums where they meet the rest of the language: casts, operators and <c>switch</c>.
/// </summary>
internal static class Enums
{
    /// <summary>
    /// The constant of a value of an enum.
    /// </summary>
    public static SpirvValue CompileConstant(SpirvContext context, EnumType enumType, long value)
        => context.CompileConstant(enumType, enumType.UnderlyingType == ScalarType.Int ? (int)value : (object)(uint)value);

    /// <summary>
    /// <c>SlotChannel.R</c>, when <paramref name="name"/> is an enum this class sees. A local, a parameter or a member of the
    /// same name is SDSL0279, so the expression has one meaning.
    /// </summary>
    public static EnumValueExpression? TryResolveValue(SymbolTable table, Identifier name, Expression accessor, TextLocation info)
    {
        if (accessor is not Identifier value || !table.DeclaredTypes.TryGetValue(name.Name, out var type) || type is not EnumType enumType)
            return null;

        if (table.TryResolveSymbol(name.Name, out var symbol))
        {
            var what = symbol.Id.Kind switch
            {
                SymbolKind.Method or SymbolKind.MethodGroup => $"the method {name.Name}",
                SymbolKind.Shader => $"the class {name.Name}",
                _ => $"the variable {name.Name}",
            };
            throw new StatementCompileException(new(name.Info, string.Format(SDSLErrorMessages.SDSL0279, name.Name, what, value.Name)));
        }

        return new EnumValueExpression(enumType, value, info);
    }

    /// <summary>
    /// <c>(E)x</c> or <c>(T)e</c>: an enum is cast from an int or uint and to a number, never from or to another enum.
    /// </summary>
    public static SpirvValue Cast(SpirvBuilder builder, SpirvContext context, SpirvValue value, SymbolType castType, TextLocation info)
    {
        var valueType = context.ReverseTypes[value.TypeId];
        if (castType == valueType)
            return value;

        if (castType is EnumType target)
        {
            if (valueType is EnumType source)
                throw Error(info, valueType, castType, $"cast through the type a value is stored as: ({target.Name})({source.UnderlyingType})value");
            if (valueType is not ScalarType { Type: Scalar.Int or Scalar.UInt })
                throw Error(info, valueType, castType, $"an enum is cast from an int or a uint: ({target.Name})({target.UnderlyingType})value");

            // A constant stays one, so (E)1 can be a static const or a case label
            if (context.TryGetConstantValue(value.Id, out var constant, out _) && constant is int or uint)
                return CompileConstant(context, target, constant is int signed ? signed : (uint)constant);
            var underlying = builder.Convert(context, value, target.UnderlyingType);
            return new(builder.InsertData(new OpCopyObject(context.GetOrRegister(target), context.Bound++, underlying.Id)));
        }

        var enumType = (EnumType)valueType;
        if (castType is not ScalarType { Type: Scalar.Int or Scalar.UInt or Scalar.Half or Scalar.Float or Scalar.Double })
            throw Error(info, valueType, castType, "an enum is cast to a scalar number, such as uint or float");

        // The value as its underlying type, then the usual conversion
        var stored = context.TryGetConstantValue(value.Id, out var number, out _)
            ? context.CompileConstant(enumType.UnderlyingType, number)
            : new SpirvValue(builder.InsertData(new OpCopyObject(context.GetOrRegister(enumType.UnderlyingType), context.Bound++, value.Id)));
        return builder.Convert(context, stored, castType);
    }

    /// <summary>
    /// An implicit conversion from or to an enum (SDSL0275): only a cast converts one.
    /// </summary>
    public static Exception ImplicitConversion(SymbolType valueType, SymbolType castType)
        => new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0275, valueType, castType));

    private static StatementCompileException Error(TextLocation info, SymbolType valueType, SymbolType castType, string reason)
        => new(new(info, string.Format(SDSLErrorMessages.SDSL0277, valueType, castType, reason)));
}
