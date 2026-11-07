// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using Stride.Shaders.Core;
using Stride.Shaders.Parsing.SDSL.AST;
using Stride.Shaders.Spirv;

namespace Stride.Shaders.Parsing.SDFX;

/// <summary>
/// The C# twins of <c>[CSharp]</c> enums and structs: a generated enum or struct, or a C# enum written by hand whose values are checked.
/// </summary>
public partial class EffectCodeWriter
{
    /// <summary>
    /// Answers about C# types, by question (see <see cref="EffectGenerator"/>): <c>Full.Name~</c> gives the underlying type and values
    /// of a C# enum (<c>uint:A=0,B=1</c>) or <c>notenum</c>; <c>Name?</c> gives <c>enum</c> or <c>struct</c> for a type generated from a
    /// shader of a referenced project; <c>%Name=</c> gives <c>enum:CSharpName</c>, <c>struct:CSharpName</c> or <c>shaders</c> for an enum
    /// or struct of another file of the project. Null: the twins are written, nothing is checked.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CSharpAnswers { get; init; }

    // The enums and structs of this file: the kind and the C# name of the twin, null for an enum of the shaders only
    private readonly Dictionary<string, (string Kind, string? CSharpName)> fileTypes = [];
    private readonly HashSet<Node> processedTypes = [];

    private string GeneratedCodeAttribute => $"[global::System.CodeDom.Compiler.GeneratedCode(\"{typeof(EffectCodeWriter).Assembly.GetName().Name}\", null)]";

    /// <summary>
    /// The C# twin of an enum or struct: of this file, or of another file of the project. Null when there is none, or not known here.
    /// </summary>
    private (string? Kind, string? CSharpName) CSharpTwinOf(string name)
    {
        if (fileTypes.TryGetValue(name, out var local))
            return local;
        if (CSharpAnswers != null && CSharpAnswers.TryGetValue($"%{name}=", out var answer))
            return answer == "shaders" ? ("enum", null) : (answer[..answer.IndexOf(':')], answer[(answer.IndexOf(':') + 1)..]);
        return default;
    }

    /// <summary>
    /// Writes the C# twins of the <c>[CSharp]</c> enums and structs of a class, before its keys.
    /// </summary>
    private void WriteCSharpTypes(ShaderClass shaderClass)
    {
        var enums = new List<(ShaderEnum Enum, CSharpTypeAttribute Attribute)>();
        foreach (var shaderEnum in shaderClass.Elements.OfType<ShaderEnum>())
        {
            VisitShaderEnum(shaderEnum);
            var name = shaderEnum.Name.Name;
            var attribute = ReadAttribute(shaderEnum.Attributes, "enum", name, true);
            if (attribute == null)
            {
                fileTypes[name] = ("enum", null);
                DeclaredClasses.Add($"%{name}=shaders");
                continue;
            }

            var csharpName = attribute.MappedName != null ? $"global::{attribute.MappedName.Replace('+', '.')}" : name;
            fileTypes[name] = ("enum", csharpName);
            DeclaredClasses.Add($"%{name}=enum:{(attribute.MappedName != null ? csharpName : $"global::{currentNamespace}.{name}")}");
            enums.Add((shaderEnum, attribute));
        }

        var structs = new List<ShaderStruct>();
        foreach (var shaderStruct in shaderClass.Elements.OfType<ShaderStruct>())
        {
            VisitShaderStruct(shaderStruct);
            var name = shaderStruct.TypeName.Name;
            if (ReadAttribute(shaderStruct.Attributes, "struct", name, shaderStruct.Kind == ShaderStructKind.Plain) == null)
                continue;
            fileTypes[name] = ("struct", name);
            DeclaredClasses.Add($"%{name}=struct:global::{currentNamespace}.{name}");
            structs.Add(shaderStruct);
        }

        var visibility = shaderClass.Internal ? "internal" : "public";
        foreach (var (shaderEnum, attribute) in enums)
        {
            if (shaderEnum.Type is not EnumType enumType)
                continue;
            if (attribute.MappedName != null)
            {
                CheckMappedEnum(enumType, attribute);
                continue;
            }

            WriteLine(GeneratedCodeAttribute);
            WriteLine($"{visibility} enum {enumType.Name} : {ScalarCSharpType(enumType.UnderlyingType.Type)}");
            OpenBrace();
            foreach (var value in enumType.Values)
                WriteLine($"{CSharpName(value.Name)} = {value.Value.ToString(CultureInfo.InvariantCulture)},");
            CloseBrace();
            WriteLine();
        }

        foreach (var shaderStruct in structs)
        {
            var fields = new List<string>();
            foreach (var member in shaderStruct.Members)
            {
                if (FieldType(shaderStruct, member) is { } fieldType)
                    fields.Add($"public {fieldType} {CSharpName(member.Name.Name)};");
            }

            WriteLine(GeneratedCodeAttribute);
            WriteLine("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Sequential)]");
            WriteLine($"{visibility} partial struct {shaderStruct.TypeName.Name}");
            OpenBrace();
            foreach (var field in fields)
                WriteLine(field);
            CloseBrace();
            WriteLine();
        }
    }

    private CSharpTypeAttribute? ReadAttribute(List<ShaderAttribute> attributes, string kind, string name, bool isPlain)
    {
        var errors = new List<(string Message, TextLocation Location)>();
        var attribute = CSharpTypeAttribute.Read(attributes, kind, name, isPlain, errors);
        logging.AddRange(errors);
        return attribute;
    }

    // A field named like a C# keyword keeps its name
    private static string CSharpName(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? $"@{name}" : name;

    /// <summary>
    /// Every value of the enum is in the C# enum, with the same number, and both store it as the same type (SDSL0287, SDSL0288).
    /// The C# enum may have more values.
    /// </summary>
    private void CheckMappedEnum(EnumType enumType, CSharpTypeAttribute attribute)
    {
        var question = $"{attribute.MappedName}~";
        CSharpTypeCandidates.Add(question);
        if (CSharpAnswers == null)
            return;

        if (!CSharpAnswers.TryGetValue(question, out var answer) || answer == "notenum")
        {
            logging.Add((string.Format(SDSLErrorMessages.SDSL0287, enumType.Name, attribute.MappedName, answer == null ? "C# has no type of this name" : $"{attribute.MappedName} is not an enum"), attribute.Info));
            return;
        }
        if (attribute.SkipValueCheck)
            return;

        var underlyingType = answer[..answer.IndexOf(':')];
        var values = EnumType.ParseValues(answer[(answer.IndexOf(':') + 1)..]);
        var difference = underlyingType != ScalarCSharpType(enumType.UnderlyingType.Type)
            ? $"it is stored as {ScalarCSharpType(enumType.UnderlyingType.Type)} here and as {underlyingType} in C#"
            : null;
        foreach (var value in enumType.Values)
        {
            if (difference != null)
                break;
            var csharpValue = values.Find(x => x.Name == value.Name);
            if (csharpValue.Name == null)
                difference = $"C# has no value {value.Name}";
            else if (csharpValue.Value != value.Value)
                difference = $"{value.Name} is {value.Value} here and {csharpValue.Value} in C#";
        }

        if (difference != null)
            logging.Add((string.Format(SDSLErrorMessages.SDSL0288, enumType.Name, attribute.MappedName, difference), attribute.Info));
    }

    /// <summary>
    /// The C# type of a field of a <c>[CSharp]</c> struct, whose layout is the same as in a structured buffer on every API: 32-bit
    /// scalars, their vectors, <c>float4x4</c>, and enums and structs that have a C# twin (SDSL0289, SDSL0290).
    /// </summary>
    private string? FieldType(ShaderStruct owner, ShaderStructMember field)
    {
        void NotAllowed(string reason) => logging.Add((string.Format(SDSLErrorMessages.SDSL0289, owner.TypeName.Name, field.Name.Name, field.TypeName, reason), field.TypeName.Info));
        string? NoCSharpType(string kind)
        {
            var fix = kind == "struct" ? "" : ", or map it to a C# enum with [CSharp(\"Full.Name\")]";
            logging.Add((string.Format(SDSLErrorMessages.SDSL0290, owner.TypeName.Name, field.Name.Name, field.TypeName, fix), field.TypeName.Info));
            return null;
        }

        var isColor = field.Attributes.OfType<AnyShaderAttribute>().Any(x => x.Name.Name == "Color");
        switch (field.Type)
        {
            case ScalarType { Type: Scalar.Float or Scalar.Int or Scalar.UInt } scalar:
                return ScalarCSharpType(scalar.Type);
            case ScalarType { Type: Scalar.Boolean }:
                NotAllowed("which C# stores in one byte and a buffer in four. Use a uint");
                return null;
            case ScalarType or VectorType { BaseType.Type: not (Scalar.Float or Scalar.Int or Scalar.UInt) }:
                NotAllowed("which buffers don't hold yet. Use float, int or uint, or their vectors");
                return null;
            case VectorType { BaseType.Type: Scalar.Float, Size: var size }:
                return isColor && size is 3 or 4 ? $"Color{size}" : $"Vector{size}";
            case VectorType { BaseType.Type: Scalar.Int, Size: var size }:
                return $"Int{size}";
            case VectorType { BaseType.Type: Scalar.UInt, Size: 4 }:
                return "UInt4";
            case VectorType { BaseType.Type: Scalar.UInt, Size: var size }:
                NotAllowed($"which has no C# type. Use int{size}, or uint4");
                return null;
            case MatrixType { BaseType.Type: Scalar.Float, Rows: 4, Columns: 4 } when field.TypeModifier == TypeModifier.RowMajor:
                NotAllowed("row_major: C# keeps a Matrix in the default order. Remove row_major");
                return null;
            case MatrixType { BaseType.Type: Scalar.Float, Rows: 4, Columns: 4 }:
                return "Matrix";
            case MatrixType:
                NotAllowed("which has no C# type. Use float4x4, or one vector per row");
                return null;
            case ArrayType:
                NotAllowed("an array, which has no C# form here. Write one field per element");
                return null;
            case StructType { PartialOwner: not null }:
                NotAllowed("a partial struct, whose fields depend on the effect");
                return null;
            case EnumType or StructType or ExternalType:
                break;
            default:
                NotAllowed("which a C# twin can't hold");
                return null;
        }

        var typeName = field.Type.ToString();
        var (kind, csharpName) = CSharpTwinOf(typeName);
        if (csharpName != null)
            return csharpName;
        if (kind != null)
            return NoCSharpType(kind);

        // A type of a referenced project: a twin generated there
        var candidates = CSharpTypeNames(typeName).Select(x => $"{x}?").ToList();
        CSharpTypeCandidates.UnionWith(candidates);
        if (CSharpAnswers == null)
            return typeName;
        if (candidates.Select(x => CSharpAnswers.TryGetValue(x, out var answer) ? answer : null).FirstOrDefault(x => x != null) is { })
            return typeName;
        return NoCSharpType(field.Type is StructType ? "struct" : "enum");
    }
}
