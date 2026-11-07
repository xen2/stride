// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using Stride.Shaders.Parsing.SDSL.AST;

namespace Stride.Shaders.Parsing.SDSL;

/// <summary>
/// <c>enum class X : uint { A, B = 3 };</c>. A plain <c>enum</c> and a missing underlying type parse too: the class reports them.
/// </summary>
public record struct ShaderEnumParser : IParser<ShaderEnum>
{
    /// <summary>
    /// Parses an enum declaration of a class.
    /// </summary>
    public static bool Enum<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out ShaderEnum parsed, in ParseError? orError = null)
        where TScanner : struct, IScanner
        => new ShaderEnumParser().Match(ref scanner, result, out parsed, in orError);

    public readonly bool Match<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out ShaderEnum parsed, in ParseError? orError = null) where TScanner : struct, IScanner
    {
        var position = scanner.Position;
        if (!(Tokens.Literal("enum", ref scanner, advance: true) && Parsers.Spaces1(ref scanner, result, out _)))
            return Parsers.Exit(ref scanner, result, out parsed, position, orError);

        var isClass = Parsers.SequenceOf(ref scanner, ["class"]) && Tokens.Literal("class", ref scanner, advance: true) && Parsers.Spaces1(ref scanner, result, out _);
        if (!LiteralsParser.Identifier(ref scanner, result, out var name))
            return Parsers.Exit(ref scanner, result, out parsed, position, new(SDSLErrorMessages.SDSL0017, scanner[scanner.Position], scanner.Memory));

        parsed = new ShaderEnum(name, scanner[position..scanner.Position]) { IsClass = isClass };
        if (Parsers.FollowedBy(ref scanner, Tokens.Char(':'), withSpaces: true, advance: true))
        {
            Parsers.Spaces0(ref scanner, result, out _);
            if (!LiteralsParser.TypeName(ref scanner, result, out var underlyingType))
                return Parsers.Exit(ref scanner, result, out parsed, position, new(SDSLErrorMessages.SDSL0017, scanner[scanner.Position], scanner.Memory));
            parsed.UnderlyingTypeName = underlyingType;
        }

        if (!Parsers.FollowedBy(ref scanner, Tokens.Char('{'), withSpaces: true, advance: true))
            return Parsers.Exit(ref scanner, result, out parsed, position, new(SDSLErrorMessages.SDSL0001, scanner[scanner.Position], scanner.Memory));

        Parsers.Spaces0(ref scanner, result, out _);
        while (LiteralsParser.Identifier(ref scanner, result, out var valueName))
        {
            var valueStart = valueName.Info.Range.Start.Value;
            Expression? value = null;
            if (Parsers.FollowedBy(ref scanner, Tokens.Char('='), withSpaces: true, advance: true))
            {
                Parsers.Spaces0(ref scanner, result, out _);
                if (!ExpressionParser.Expression(ref scanner, result, out value))
                    return Parsers.Exit(ref scanner, result, out parsed, position, new(SDSLErrorMessages.SDSL0015, scanner[scanner.Position], scanner.Memory));
            }
            parsed.Values.Add(new ShaderEnumValue(valueName, value, scanner[valueStart..scanner.Position]));
            Parsers.Spaces0(ref scanner, result, out _);
            if (!Tokens.Char(',', ref scanner, advance: true))
                break;
            Parsers.Spaces0(ref scanner, result, out _);
        }

        if (!Parsers.FollowedBy(ref scanner, Tokens.Char('}'), withSpaces: true, advance: true))
            return Parsers.Exit(ref scanner, result, out parsed, position, new(SDSLErrorMessages.SDSL0019, scanner[scanner.Position], scanner.Memory));
        Parsers.FollowedBy(ref scanner, Tokens.Char(';'), withSpaces: true, advance: true);
        parsed.Info = scanner[position..scanner.Position];
        return true;
    }
}
