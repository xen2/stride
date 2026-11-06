using System.Diagnostics.CodeAnalysis;
using Stride.Shaders.Parsing.SDSL.AST;

namespace Stride.Shaders.Parsing.SDSL;


public record struct ShaderMethodParsers : IParser<ShaderMethod>
{
    public readonly bool Match<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out ShaderMethod parsed, in ParseError? orError = null)
        where TScanner : struct, IScanner
    {
        if (Method(ref scanner, result, out var method, in orError))
        {
            parsed = method;
            return true;
        }
        else
        {
            parsed = null!;
            return false;
        }
    }

    public static bool Method<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out ShaderMethod parsed, in ParseError? orError = null)
        where TScanner : struct, IScanner
        => new MethodParser().Match(ref scanner, result, out parsed, in orError);
    public static bool Simple<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out ShaderMethod parsed, in ParseError? orError = null)
        where TScanner : struct, IScanner
        => new SimpleMethodParser().Match(ref scanner, result, out parsed, in orError);



    public static bool MethodParameters<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out List<MethodParameter> parsed, in ParseError? orError = null)
        where TScanner : struct, IScanner
    {
        var position = scanner.Position;
        // HLSL compat: accept `(void)` as empty parameter list (legacy C convention)
        if (
            Parsers.FollowedBy(ref scanner, Tokens.Char(')'), withSpaces: true)
            ||
            (
                Parsers.FollowedBy(ref scanner, Tokens.Literal("void"), withSpaces: true, advance: true)
                && Parsers.FollowedBy(ref scanner, Tokens.Char(')'), withSpaces: true)
            )
        )
        {
            parsed = [];
            return true;
        }
        else

            if (Parsers.Repeat(ref scanner, result, MethodParameter, out List<MethodParameter>? parameters, 0, withSpaces: true, separator: ","))
            {
                parsed = parameters;
                return true;
            }
            else return Parsers.Exit(ref scanner, result, out parsed, position);

    }
    public static bool MethodParameter<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out MethodParameter parsed, in ParseError? orError = null)
        where TScanner : struct, IScanner
    {
        var position = scanner.Position;

        ParameterModifiers modifiers = ParameterModifiers.None;
        if (Tokens.AnyOf(["inout", "in", "out", "const", "point", "lineadj", "line", "triangleadj", "triangle"], ref scanner, out var modifiersString, advance: true) && Parsers.Spaces1(ref scanner, result, out _))
        {
            modifiers = modifiersString.ToParameterModifiers();
        }
        else
        {
            scanner.Position = position;
        }
        if (Parsers.TypeNameIdentifierArraySizeValue(ref scanner, result, out var typename, out var identifier, out var value, advance: true)
        )
        {
            if (
                Parsers.FollowedBy(ref scanner, Tokens.Char(':'), withSpaces: true, advance: true)
                && Parsers.FollowedBy(ref scanner, result, LiteralsParser.Identifier, out Identifier? semantic, withSpaces: true, advance: true)
                && Parsers.Spaces0(ref scanner, result, out _)
            )
            {
                parsed = new(typename, identifier, scanner[position..scanner.Position], modifiers, value, semantic: semantic);
                return true;
            }
            else
            {
                parsed = new(typename, identifier, scanner[position..scanner.Position], modifiers, value);
                return true;
            }
        }
        else return Parsers.Exit(ref scanner, result, out parsed, position);

    }
}

public record struct SimpleMethodParser : IParser<ShaderMethod>
{
    public readonly bool Match<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out ShaderMethod parsed, in ParseError? orError = null)
        where TScanner : struct, IScanner
    {
        var position = scanner.Position;
        if (
            LiteralsParser.TypeName(ref scanner, result, out var typename)
            && Parsers.Spaces1(ref scanner, result, out _, new(SDSLErrorMessages.SDSL0016, scanner[scanner.Position], scanner.Memory))
            && LiteralsParser.Identifier(ref scanner, result, out var methodName)
            && Parsers.FollowedBy(ref scanner, Tokens.Char('('), withSpaces: true, advance: true)
            && Parsers.FollowedByDel(ref scanner, result, ShaderMethodParsers.MethodParameters, out List<MethodParameter>? parameters, withSpaces: true, advance: true)
            && Parsers.FollowedBy(ref scanner, Tokens.Char(')'), withSpaces: true, advance: true)
            && ReturnSemantic(ref scanner, result, out var returnSemantic)
            && Parsers.Spaces0(ref scanner, result, out _)
            && StatementParsers.Block(ref scanner, result, out var body, new(SDSLErrorMessages.SDSL0040, scanner[scanner.Position], scanner.Memory))
        )
        {
            parsed = new ShaderMethod(typename, methodName, scanner[position..scanner.Position])
            {
                Parameters = parameters!,
                ReturnSemantic = returnSemantic,
                Body = (BlockStatement)body
            };
            return true;
        }
        else return Parsers.Exit(ref scanner, result, out parsed, position, orError);
    }

    // Optional `: SEMANTIC` after the parameters; false only when a `:` is not followed by a name
    private static bool ReturnSemantic<TScanner>(ref TScanner scanner, ParseResult result, out Identifier? semantic)
        where TScanner : struct, IScanner
    {
        semantic = null;
        if (!Parsers.FollowedBy(ref scanner, Tokens.Char(':'), withSpaces: true, advance: true))
            return true;
        return Parsers.FollowedByDel(ref scanner, result, LiteralsParser.Identifier, out semantic, withSpaces: true, advance: true);
    }
}


public record struct MethodParser : IParser<ShaderMethod>
{
    /// <summary>
    /// Reads a method again from the text it was read from: a second tree of it, with the same places.
    /// </summary>
    internal static ShaderMethod? ReadAgain(ShaderMethod method)
    {
        var scanner = new Scanner<ScannableString>(method.Info.Original.ToString()) { Position = method.Info.Range.Start.GetOffset(method.Info.Original.Length) };
        return new MethodParser().Match(ref scanner, new ParseResult(), out var parsed) ? parsed : null;
    }

    public readonly bool Match<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out ShaderMethod parsed, in ParseError? orError = null)
        where TScanner : struct, IScanner
    {
        parsed = null!;
        var position = scanner.Position;

        var hasAttributes = ShaderAttributeListParser.AttributeList(ref scanner, result, out var attributes) && Parsers.Spaces0(ref scanner, result, out _);
        var hasModifiers = Parsers.MethodModifiers(ref scanner, result, out var isStaged, out var isStatic, out var isClone, out var isOverride, out var isAbstract, out var kinds, advance: true) && Parsers.Spaces0(ref scanner, result, out _);

        if (!hasModifiers && Extend(ref scanner, result, out parsed))
        {
            if (hasAttributes)
                parsed.Attributes = attributes!.Attributes;
            parsed.Info = scanner[position..scanner.Position];
            return true;
        }

        if (isAbstract)
        {
            if (
                LiteralsParser.TypeName(ref scanner, result, out var typename, orError: new(SDSLErrorMessages.SDSL0017, scanner[scanner.Position], scanner.Memory))
                && Parsers.Spaces1(ref scanner, result, out _)
                && LiteralsParser.Identifier(ref scanner, result, out var methodName, orError: new(SDSLErrorMessages.SDSL0017, scanner[scanner.Position], scanner.Memory))
                && Parsers.Spaces0(ref scanner, result, out _)
            )
            {
                if (Tokens.Char('(', ref scanner, advance: true) && Parsers.Spaces0(ref scanner, result, out _))
                {
                    ShaderMethodParsers.MethodParameters(ref scanner, result, out var parameters);
                    Parsers.Spaces0(ref scanner, result, out _);
                    if (!Tokens.Char(')', ref scanner, advance: true))
                        return Parsers.Exit(ref scanner, result, out parsed, position, new(SDSLErrorMessages.SDSL0018, scanner[scanner.Position], scanner.Memory));

                    Parsers.Spaces0(ref scanner, result, out _);
                    if (!Tokens.Char(';', ref scanner, advance: true))
                    {
                        if (orError != null)
                            return Parsers.Exit(ref scanner, result, out parsed, position, new(SDSLErrorMessages.SDSL0033, scanner[scanner.Position], scanner.Memory));
                        else return Parsers.Exit(ref scanner, result, out parsed, position, orError);
                    }
                    else
                    {
                        parsed = new(typename, methodName, scanner[position..scanner.Position], isAbstract: true) { Kinds = kinds };
                        if (hasAttributes)
                            parsed.Attributes = attributes!.Attributes;
                        if (parameters is not null)
                            parsed.Parameters = parameters;
                        return true;
                    }
                }
                else return Parsers.Exit(ref scanner, result, out parsed, position, orError);
            }
        }
        else
        {
            if (ShaderMethodParsers.Simple(ref scanner, result, out parsed, orError))
            {
                if (hasAttributes)
                    parsed.Attributes = attributes!.Attributes;
                parsed.IsStaged = isStaged;
                parsed.IsClone = isClone;
                parsed.IsOverride = isOverride;
                parsed.IsStatic = isStatic;
                parsed.Kinds = kinds;
                parsed.Info = scanner[position..scanner.Position];
                return true;
            }
        }
        return Parsers.Exit(ref scanner, result, out parsed, position, orError);
    }

    /// <summary>
    /// <c>extend Owner.Method(parameters) { }</c> or <c>patch Owner.Method(parameters) { }</c>. The words are not reserved: <c>patch x;</c> is a member.
    /// A form with no parameters, or with <c>in</c>, <c>after</c> or <c>replaces</c>, is read for its error.
    /// </summary>
    private static bool Extend<TScanner>(ref TScanner scanner, ParseResult result, [MaybeNullWhen(false)] out ShaderMethod parsed)
        where TScanner : struct, IScanner
    {
        parsed = null;
        var position = scanner.Position;
        if (!Tokens.AnyOf(["extend", "patch"], ref scanner, out var keyword, advance: true)
            || !Parsers.Spaces1(ref scanner, result, out _)
            || !SDFX.EffectLinkageRuleParser.DottedName(ref scanner, result, out var path)
            || path.Count < 2)
        {
            scanner.Position = position;
            return false;
        }

        var name = new Identifier(path[^1], scanner[position..scanner.Position]);
        var hasParameters = false;
        List<MethodParameter>? parameters = null;
        if (Parsers.FollowedBy(ref scanner, Tokens.Char('('), withSpaces: true, advance: true))
        {
            hasParameters = true;
            if (!Parsers.FollowedByDel(ref scanner, result, ShaderMethodParsers.MethodParameters, out parameters, withSpaces: true, advance: true)
                || !Parsers.FollowedBy(ref scanner, Tokens.Char(')'), withSpaces: true, advance: true))
            {
                scanner.Position = position;
                return false;
            }
        }

        var phase = "";
        var after = new List<string>();
        var replaces = new List<string>();
        while (true)
        {
            Parsers.Spaces0(ref scanner, result, out _);
            var clause = scanner.Position;
            if (Tokens.AnyOf(["in", "after", "replaces"], ref scanner, out var word, advance: true) && Parsers.Spaces1(ref scanner, result, out _))
            {
                if (word == "in")
                {
                    if (!LiteralsParser.Identifier(ref scanner, result, out var phaseName))
                    {
                        scanner.Position = position;
                        return false;
                    }
                    phase = phaseName.Name;
                }
                else
                {
                    var names = word == "after" ? after : replaces;
                    do
                    {
                        Parsers.Spaces0(ref scanner, result, out _);
                        if (!SDFX.EffectLinkageRuleParser.DottedName(ref scanner, result, out var target))
                        {
                            scanner.Position = position;
                            return false;
                        }
                        names.Add(string.Join('.', target));
                    }
                    while (Parsers.FollowedBy(ref scanner, Tokens.Char(','), withSpaces: true, advance: true));
                }
            }
            else
            {
                scanner.Position = clause;
                break;
            }
        }

        var outer = result.ExtendedMethod;
        result.ExtendedMethod = name.Name;
        BlockStatement? block = null;
        if (Tokens.Char('{', ref scanner) && StatementParsers.Block(ref scanner, result, out var body))
            block = (BlockStatement)body;
        result.ExtendedMethod = outer;
        if (block == null)
        {
            scanner.Position = position;
            return false;
        }

        parsed = new ShaderMethod(new TypeName("void", name.Info), name, scanner[position..scanner.Position], isStaged: true, isOverride: true)
        {
            Parameters = parameters ?? [],
            Body = block,
            Extend = new ExtendClause(keyword == "patch", string.Join('.', path.Take(path.Count - 1)), hasParameters, ClauseAttributes(phase, after, replaces)),
        };
        return true;
    }

    private static string? ClauseAttributes(string phase, List<string> after, List<string> replaces)
    {
        var attributes = (phase.Length > 0 ? $"[Phase({phase})] " : "")
            + (after.Count > 0 ? $"[MixinOrder(After = {(after.Count == 1 ? after[0] : $"{{ {string.Join(", ", after)} }}")})] " : "")
            + (replaces.Count > 0 ? $"[Drops(Mixins = {{ {string.Join(", ", replaces)} }})] " : "");
        return attributes.Length > 0 ? attributes : null;
    }
}

