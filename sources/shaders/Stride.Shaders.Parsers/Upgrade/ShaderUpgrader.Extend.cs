// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Text.RegularExpressions;

namespace Stride.Shaders.Parsing.Upgrade;

public static partial class ShaderUpgrader
{
    private sealed record ExtendPoint(string Kind, string Owner, string[] Phases, bool IsModule);

    private sealed record ExtendClass(string Name, List<string> Parents, Dictionary<string, ExtendPoint> Points, int Start, int End);

    private static readonly Regex ClassHeader = new(@"\b(shader|module)\s+(\w+)\s*(?:<[^>{]*>)?\s*(?::\s*([^{;]+?))?\s*\{", RegexOptions.Compiled);
    private static readonly Regex ImportLine = new(@"\bimport\s+([\w.]+)", RegexOptions.Compiled);
    private static readonly Regex PointDeclaration = new(@"(?:\[Phases\(([^)]*)\)\]\s*)?((?:module\s+)?)\b(pipeline|around|replace)\s+((?:module\s+)?)[\w<>,\[\] ]+?\s+(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex OverrideMethod = new(@"(?<attributes>(?:[ \t]*\[[^\]\r\n]*\][ \t]*\r?\n)*)(?<indent>[ \t]*)(?<modifiers>(?:(?:module|override)\s+)+)(?<type>[\w<>,\[\] ]+?)\s+(?<name>\w+)\s*\((?<parameters>[^)]*)\)(?<space>\s*)\{", RegexOptions.Compiled);
    private static readonly Regex Attribute = new(@"\[(\w+)(?:\((.*)\))?\]", RegexOptions.Compiled);

    /// <summary>
    /// The code of a file, with comments and strings blanked: offsets stay valid, and the expressions below don't have to know about them.
    /// </summary>
    private static string CodeOnly(string text)
    {
        var source = new ShaderSourceText(text);
        return string.Create(text.Length, (text, source.IsCode), static (span, state) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = state.IsCode[i] || state.text[i] is '\r' or '\n' ? state.text[i] : ' ';
        });
    }

    private static int MatchingBrace(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{')
                depth++;
            else if (code[i] == '}' && --depth == 0)
                return i;
        }
        return -1;
    }

    private static string ShortClassName(string name)
    {
        var generic = name.IndexOf('<');
        var plain = (generic >= 0 ? name[..generic] : name).Trim();
        return plain[(plain.LastIndexOf('.') + 1)..];
    }

    // A comma inside generics doesn't separate the items
    private static List<string> SplitList(string list)
    {
        var items = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= list.Length; i++)
        {
            if (i == list.Length || (list[i] == ',' && depth == 0))
            {
                if (!string.IsNullOrWhiteSpace(list[start..i]))
                    items.Add(list[start..i].Trim());
                start = i + 1;
            }
            else if (list[i] == '<')
                depth++;
            else if (list[i] == '>')
                depth--;
        }
        return items;
    }

    private static List<ExtendClass> ReadExtendClasses(string code)
    {
        var classes = new List<ExtendClass>();
        foreach (Match header in ClassHeader.Matches(code))
        {
            var open = header.Index + header.Length - 1;
            var close = MatchingBrace(code, open);
            if (close < 0)
                continue;

            var body = code[open..close];
            var parents = header.Groups[3].Success ? SplitList(header.Groups[3].Value).Select(ShortClassName).ToList() : [];
            parents.AddRange(ImportLine.Matches(body).Select(x => ShortClassName(x.Groups[1].Value)));

            var isModuleClass = header.Groups[1].Value == "module";
            var points = new Dictionary<string, ExtendPoint>();
            foreach (Match point in PointDeclaration.Matches(body))
            {
                var kind = point.Groups[3].Value;
                var phases = point.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                points[point.Groups[5].Value] = new ExtendPoint(kind, header.Groups[2].Value, phases.Length > 0 ? phases : kind == "replace" ? [] : ["Default"],
                    isModuleClass || point.Groups[2].Length > 0 || point.Groups[4].Length > 0);
            }
            classes.Add(new ExtendClass(header.Groups[2].Value, parents, points, open, close));
        }
        return classes;
    }

    /// <summary>
    /// An override with a phase, or an override of a module <c>replace</c> point, becomes an <c>extend</c>. What the compat form can do and
    /// <c>extend</c> can't stays as it is, which is allowed: a pipeline step with no <c>base</c> or with <c>base</c> in the middle, <c>base</c> from
    /// a helper, an override without a phase, whose place is outside every phase.
    /// </summary>
    private static void ConvertToExtend(Dictionary<string, string> texts, IReadOnlyDictionary<string, string> dependencies)
    {
        var known = new Dictionary<string, ExtendClass>();
        foreach (var text in texts.Values.Concat(dependencies.Values))
        {
            if (!text.Contains("pipeline", StringComparison.Ordinal) && !text.Contains("around", StringComparison.Ordinal) && !text.Contains("replace", StringComparison.Ordinal) && !text.Contains(" : ", StringComparison.Ordinal) && !text.Contains("import", StringComparison.Ordinal))
                continue;
            foreach (var shaderClass in ReadExtendClasses(CodeOnly(text)))
                known.TryAdd(shaderClass.Name, shaderClass);
        }

        foreach (var path in texts.Keys.ToList())
        {
            var text = texts[path];
            if (!text.Contains("override", StringComparison.Ordinal))
                continue;

            var code = CodeOnly(text);
            var edits = new List<(int Start, int Length, string Text)>();
            foreach (var shaderClass in ReadExtendClasses(code))
            {
                foreach (Match method in OverrideMethod.Matches(code, shaderClass.Start))
                {
                    if (method.Index > shaderClass.End)
                        break;
                    if (method.Groups["modifiers"].Value.Contains("override", StringComparison.Ordinal))
                        ConvertMethod(text, code, shaderClass, method, known, edits);
                }
            }

            foreach (var (start, length, replacement) in edits.OrderByDescending(x => x.Start))
                text = text.Remove(start, length).Insert(start, replacement);
            texts[path] = text;
        }
    }

    private static ExtendPoint? FindPoint(ExtendClass shaderClass, string method, Dictionary<string, ExtendClass> known, HashSet<string> visited)
    {
        foreach (var parent in shaderClass.Parents)
        {
            if (!visited.Add(parent) || !known.TryGetValue(parent, out var parentClass))
                continue;
            if (FindPoint(parentClass, method, known, visited) is { } inherited)
                return inherited;
            if (parentClass.Points.TryGetValue(method, out var point))
                return point;
        }
        return null;
    }

    private static void ConvertMethod(string text, string code, ExtendClass shaderClass, Match method, Dictionary<string, ExtendClass> known, List<(int Start, int Length, string Text)> edits)
    {
        var name = method.Groups["name"].Value;
        if (FindPoint(shaderClass, name, known, []) is not { IsModule: true } point)
            return;

        var phase = Attribute.Matches(method.Groups["attributes"].Value).FirstOrDefault(x => x.Groups[1].Value == "Phase")?.Groups[2].Value.Trim();
        // The place of an added phase is only known at link. Without a phase, the two forms mean different places.
        if (phase != null && phase.Contains('=', StringComparison.Ordinal))
            return;
        if (phase == null && point.Kind != "replace")
            return;

        var open = method.Index + method.Length - 1;
        var close = MatchingBrace(code, open);
        if (close < 0)
            return;

        // base from a helper of the class can't be written with next
        var baseCall = new Regex($@"\bbase\s*\.\s*{Regex.Escape(name)}\s*\(");
        var calls = baseCall.Matches(code, open).Where(x => x.Index < close).ToList();
        var inClass = baseCall.Matches(code, shaderClass.Start).Count(x => x.Index < shaderClass.End);
        if (inClass != calls.Count)
            return;

        var parameters = method.Groups["parameters"].Value.Trim();
        if (point.Kind == "pipeline")
        {
            if (method.Groups["type"].Value.Trim() != "void" || !RemoveStep(text, code, open, close, calls, parameters, point, phase, edits))
                return;
        }
        else
        {
            foreach (var call in calls)
                edits.Add((call.Index, call.Length, "next("));
        }

        var modifiers = method.Groups["modifiers"].Index;
        edits.Add((modifiers, method.Groups["space"].Index - modifiers, $"extend {point.Owner}.{name}({parameters})"));
    }

    /// <summary>
    /// A pipeline step calls <c>base</c> once, as the first or the last statement of its body, with its own parameters: <c>extend</c> does that.
    /// </summary>
    private static bool RemoveStep(string text, string code, int open, int close, List<Match> calls, string parameters, ExtendPoint point, string? phase, List<(int Start, int Length, string Text)> edits)
    {
        if (calls.Count != 1)
            return false;

        var call = calls[0];
        var end = code.IndexOf(';', call.Index);
        if (end < 0 || end > close)
            return false;

        var names = SplitList(parameters).Select(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1]);
        var arguments = SplitList(code[(call.Index + call.Length)..end].TrimEnd().TrimEnd(')'));
        if (!names.SequenceEqual(arguments))
            return false;

        var isFirst = string.IsNullOrWhiteSpace(code[(open + 1)..call.Index]);
        // Not the body of an if: a statement ends right before it
        var previous = code[(open + 1)..call.Index].TrimEnd();
        var isLast = string.IsNullOrWhiteSpace(code[(end + 1)..close]) && (previous.Length == 0 || previous[^1] is ';' or '}');
        if (!isFirst && !isLast)
            return false;

        // extend calls base last before the default body, and first from its phase on. A step that does otherwise keeps saying it.
        var defaultIndex = Math.Max(Array.IndexOf(point.Phases, "Default"), 0);
        if (Array.IndexOf(point.Phases, phase) < defaultIndex ? !isLast : !isFirst)
            return false;

        // The whole line, and the blank line that set the call apart from the body
        var start = call.Index;
        while (start > 0 && text[start - 1] is ' ' or '\t')
            start--;
        var stop = end + 1;
        while (stop < text.Length && text[stop] is ' ' or '\t')
            stop++;
        if (stop < text.Length && text[stop] == '\r')
            stop++;
        if (stop < text.Length && text[stop] == '\n')
            stop++;
        if (isFirst && !isLast)
        {
            var next = stop;
            while (next < close && text[next] is ' ' or '\t' or '\r')
                next++;
            if (next < close && text[next] == '\n')
                stop = next + 1;
        }
        else if (isLast && !isFirst)
        {
            if (start >= 1 && text[start - 1] == '\n')
            {
                var line = start - 1;
                if (line >= 1 && text[line - 1] == '\r')
                    line--;
                var before = line;
                while (before > open && text[before - 1] is ' ' or '\t')
                    before--;
                if (before > open && text[before - 1] == '\n')
                    start = before;
            }
        }
        edits.Add((start, stop - start, ""));
        return true;
    }
}
