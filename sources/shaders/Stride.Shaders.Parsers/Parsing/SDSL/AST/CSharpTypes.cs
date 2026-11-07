// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Shaders.Parsing.SDSL.AST;

/// <summary>
/// <c>[CSharp]</c> on an enum or a struct: the type has a C# twin. <c>[CSharp]</c> generates it, <c>[CSharp("Full.Name")]</c> maps
/// an enum to a C# enum written by hand, which the generator checks unless <c>SkipValueCheck = true</c>. The mixer checks that a
/// struct in a cbuffer has the layout of its twin, unless <c>SkipLayoutCheck = true</c>.
/// </summary>
/// <param name="MappedName">The full name of the C# enum written by hand, or null when the C# type is generated.</param>
/// <param name="SkipValueCheck">Whether the values of a mapped enum are not checked against C#.</param>
/// <param name="SkipLayoutCheck">Whether the layout of a struct in a cbuffer is not checked against its twin.</param>
/// <param name="Info">Where the attribute is written.</param>
public sealed record CSharpTypeAttribute(string? MappedName, bool SkipValueCheck, bool SkipLayoutCheck, TextLocation Info)
{
    /// <summary>
    /// The name of the attribute.
    /// </summary>
    public const string Name = "CSharp";

    /// <summary>
    /// Reads the attributes of an enum or a struct, the only ones that take <c>[CSharp]</c>. Any other attribute there, or a wrong
    /// argument, is an error (SDSL0217, SDSL0286).
    /// </summary>
    /// <param name="attributes">The attributes written before the declaration.</param>
    /// <param name="kind"><c>enum</c> or <c>struct</c>, for messages.</param>
    /// <param name="typeName">The name of the type, for messages.</param>
    /// <param name="isPlainStruct">For a struct, whether it is neither <c>partial</c> nor <c>extend</c>.</param>
    /// <param name="errors">Receives the errors.</param>
    /// <returns>The attribute, or null when there is none or it has errors.</returns>
    public static CSharpTypeAttribute? Read(List<ShaderAttribute> attributes, string kind, string typeName, bool isPlainStruct, List<(string Message, TextLocation Location)> errors)
    {
        CSharpTypeAttribute? result = null;
        foreach (var attribute in attributes)
        {
            if (attribute is not AnyShaderAttribute { Name.Name: Name } csharp)
            {
                errors.Add((string.Format(SDSLErrorMessages.SDSL0217, attribute is AnyShaderAttribute any ? any.Name.Name : attribute.ToString(), $"{kind} {typeName}", $"[{Name}]"), attribute.Info));
                continue;
            }

            var errorCount = errors.Count;
            void Error(string reason) => errors.Add((string.Format(SDSLErrorMessages.SDSL0286, kind, typeName, reason), csharp.Info));

            string? mappedName = null;
            var skipValueCheck = false;
            var skipLayoutCheck = false;
            foreach (var parameter in csharp.Parameters)
            {
                switch (parameter)
                {
                    case StringLiteral text when mappedName == null && kind == "enum":
                        mappedName = text.Value;
                        break;
                    case StringLiteral:
                        Error(kind == "enum" ? "it takes one name" : "a struct can't be mapped to a C# struct yet. Write [CSharp], and the C# struct is generated");
                        break;
                    case AssignExpression { Target: Identifier { Name: "SkipValueCheck" }, Value: BoolLiteral value }:
                        skipValueCheck = value.Value;
                        break;
                    case AssignExpression { Target: Identifier { Name: "SkipLayoutCheck" }, Value: BoolLiteral value }:
                        skipLayoutCheck = value.Value;
                        break;
                    case AssignExpression { Target: Identifier { Name: var argument } }:
                        Error($"{argument} is not an argument of [{Name}]: it takes the full name of a C# enum and SkipValueCheck = true on an enum, SkipLayoutCheck = true on a struct");
                        break;
                    default:
                        Error($"{parameter} is not an argument of [{Name}]: write the full name of a C# enum as a string, as in [{Name}(\"Stride.Rendering.MyEnum\")]");
                        break;
                }
            }

            if (skipValueCheck && mappedName == null)
                Error(kind == "enum"
                    ? $"SkipValueCheck is for an enum mapped to a C# enum written by hand, as in [{Name}(\"Full.Name\", SkipValueCheck = true)]: a generated enum has the values of {typeName}"
                    : "SkipValueCheck is for an enum mapped to a C# enum");
            if (skipLayoutCheck && kind == "enum")
                Error("SkipLayoutCheck is for a struct in a cbuffer: an enum is a 32-bit int or uint everywhere");
            if (kind == "struct" && !isPlainStruct)
                Error("the fields of a partial struct depend on the effect, so it has no C# twin");
            if (result != null)
                Error($"write [{Name}] once");

            if (errors.Count == errorCount)
                result = new(mappedName, skipValueCheck, skipLayoutCheck, csharp.Info);
        }
        return result;
    }
}
