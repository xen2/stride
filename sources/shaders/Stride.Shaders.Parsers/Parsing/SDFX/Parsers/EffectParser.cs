using System.Diagnostics.CodeAnalysis;
using Stride.Shaders.Parsing.SDFX.AST;
using Stride.Shaders.Parsing.SDSL;
using Stride.Shaders.Parsing.SDSL.AST;

namespace Stride.Shaders.Parsing.SDFX;


public record struct EffectParser : IParser<ShaderEffect>
{
    public readonly bool Match<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out ShaderEffect parsed, in ParseError? orError = null) where TScanner : struct, IScanner
    {
        var position = scanner.Position;

        if (SDSL.Parsers.SequenceOf(ref scanner, ["partial", "effect"]))
            return SDSL.Parsers.Exit(ref scanner, result, out parsed, position, new(SDSLErrorMessages.SDSL0311, scanner[scanner.Position], scanner.Memory));

        var isLayer = Tokens.Literal("extend", ref scanner, advance: true) && SDSL.Parsers.Spaces1(ref scanner, result, out _);
        if (!isLayer)
            scanner.Position = position;

        if (Tokens.Literal("effect", ref scanner, advance: true) && SDSL.Parsers.Spaces1(ref scanner, result, out _))
        {
            if (LiteralsParser.TypeName(ref scanner, result, out var effectName) && SDSL.Parsers.Spaces0(ref scanner, result, out _))
            {
                parsed = new((TypeName)effectName, isLayer, new());
                if (EffectStatementParsers.EffectBlock(ref scanner, result, out var s) && SDSL.Parsers.Spaces0(ref scanner, result, out _))
                {
                    // Optional semi-colon
                    SDSL.Parsers.FollowedBy(ref scanner, Tokens.Char(';'), withSpaces: true, advance: true);
                    parsed.Block = s;
                    return true;
                }
                else return SDSL.Parsers.Exit(ref scanner, result, out parsed, position, new(SDSLErrorMessages.SDSL0011, scanner[scanner.Position], scanner.Memory));
            }
        }
        return SDSL.Parsers.Exit(ref scanner, result, out parsed, position, orError);
    }

    public static bool Effect<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out ShaderEffect parsed, in ParseError? orError = null) where TScanner : struct, IScanner
            => new EffectParser().Match(ref scanner, result, out parsed, orError);
}
