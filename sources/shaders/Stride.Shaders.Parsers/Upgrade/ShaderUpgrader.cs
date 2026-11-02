// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Text.RegularExpressions;
using Stride.Shaders.Parsing.Analysis;

namespace Stride.Shaders.Parsing.Upgrade;

/// <summary>
/// Rewrites the <c>.sdsl</c> files of a project written for an older Stride version. Each syntax change adds a step; a step
/// changes only the old pattern, so running it again, or on new code, changes nothing.
/// </summary>
public static class ShaderUpgrader
{
    /// <summary>
    /// The steps, in order.
    /// </summary>
    public static IReadOnlyList<string> Steps { get; } =
    [
        // 4.5: the `clone` method modifier has no effect since 4.4 (SDSL0212)
        "drop-clone",
        // 4.5: `stage` is now spelled `module`
        "stage-to-module",
        // 4.4: a `compose` or a `stream` declared inside a `cbuffer` or `rgroup` is not a member of it (SDSL0050); 4.3 compiled it as
        // declared after the block
        "compose-out-of-buffer",
        // 4.5: the value of a composition (`compose ComputeFloat C = 0.5;`) never did anything (SDSL0236)
        "drop-compose-value",
        // 4.4: an implementation of an abstract method is marked `override` (SDSL0114); after the rename, the parser rejects `stage`
        "add-override",
        // 4.5: module code of a class used in compositions can only call module methods
        "module-helpers",
        // 4.5: classes with no instance part that other classes import are `module X`, imported with `import X;`
        "module-units",
        // 4.5: an override of a module method needs a kind on its first definition (SDSL0246)
        "add-kinds",
        // 4.5: engine shaders are in namespaces: a class out of scope needs a `using`
        "add-usings",
        // 4.5: the key of a member of a namespaced class has the full name of the class
        "full-key-names",
    ];

    /// <summary>
    /// Whether a path is an effect source: only the steps about names read it.
    /// </summary>
    public static bool IsEffectFile(string path) => path.EndsWith(".sdfx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the steps up to <paramref name="lastStep"/> (all by default) on <paramref name="files"/> (path → text). The result has
    /// the same files; running it again changes nothing.
    /// </summary>
    /// <param name="engine">The engine classes the project uses (<see cref="EngineShaderClasses.Current"/>); null when the files are the engine.</param>
    /// <param name="dependencies">
    /// The sources of the libraries the files use (path → text): read to know their classes (parents, modules, methods), never
    /// changed. They are expected to be upgraded already; one written for an older version gets a note.
    /// </param>
    /// <param name="implicitUsings">The namespaces every class sees without a <c>using</c>.</param>
    public static ShaderUpgradeResult Upgrade(IReadOnlyDictionary<string, string> files, EngineShaderClasses? engine, string? lastStep = null,
        IReadOnlyDictionary<string, string>? dependencies = null, IReadOnlyList<string>? implicitUsings = null)
    {
        var effects = files.Where(x => IsEffectFile(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        var texts = files.Where(x => !IsEffectFile(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        var notes = new List<string>();
        var stepCount = lastStep == null ? Steps.Count : Steps.ToList().IndexOf(lastStep) + 1;
        if (stepCount == 0)
            throw new ArgumentException($"Unknown step {lastStep}", nameof(lastStep));

        // Dependencies are read with the text steps done, so that an older spelling still parses
        var dependencyTexts = new Dictionary<string, string>();
        foreach (var (path, text) in dependencies ?? new Dictionary<string, string>())
        {
            var read = WithTextSteps(text);
            if (read != text)
                notes.Add($"{path}: dependency written for an older Stride version, read as if upgraded but not changed; upgrade it first");
            dependencyTexts[path] = read;
        }

        foreach (var step in Steps.Take(stepCount))
        {
            switch (step)
            {
                case "add-override":
                    Apply(texts, project => AddOverride(project, texts, notes), engine, notes, dependencyTexts);
                    break;
                case "module-helpers":
                    Apply(texts, project => MarkModuleHelpers(project, texts, notes), engine, notes, dependencyTexts);
                    break;
                case "module-units":
                    Apply(texts, project => ConvertModules(project, texts, notes), engine, notes, dependencyTexts);
                    break;
                case "add-kinds":
                    Apply(texts, project => AddKinds(project, texts, notes), engine, notes, dependencyTexts);
                    break;
                // The engine's own sources were written in their namespaces
                case "add-usings":
                    if (engine != null)
                        AddUsings(texts, effects, dependencyTexts, engine, implicitUsings ?? []);
                    break;
                case "full-key-names":
                    if (engine != null)
                        FullKeyNames(texts, engine.WithProject(texts.Values.Concat(effects.Values).Concat(dependencyTexts.Values)));
                    break;
                default:
                    var textStep = TextSteps[Steps.ToList().IndexOf(step)];
                    foreach (var path in texts.Keys.ToList())
                        texts[path] = textStep(texts[path]);
                    break;
            }
        }

        foreach (var (path, text) in effects)
            texts[path] = text;
        return new ShaderUpgradeResult(texts, notes.Distinct().ToList());
    }

    /// <summary>
    /// A file that names a class or effect of the engine, the project or a dependency out of its scope gets the <c>using</c> of its
    /// namespace, or the full name when that <c>using</c> would make the name ambiguous.
    /// </summary>
    private static void AddUsings(Dictionary<string, string> texts, Dictionary<string, string> effects, Dictionary<string, string> dependencies, EngineShaderClasses engine, IReadOnlyList<string> implicitUsings)
    {
        var projectSources = texts.Values.Concat(effects.Values).Concat(dependencies.Values).ToList();
        var projectNames = projectSources.SelectMany(ShaderFileDeclarations.Read).Select(x => x.FullName).ToHashSet();
        var known = engine.Classes.Select(x => x.FullName ?? x.Name).Concat(engine.Effects.Values).Concat(projectNames).ToHashSet();
        // A project class hides an engine class of its name, as it did when all names were global
        var classes = engine.WithProject(projectSources);

        foreach (var files in new[] { texts, effects })
        {
            foreach (var path in files.Keys.ToList())
            {
                var source = new ShaderSourceText(files[path]);
                var declarations = ShaderFileDeclarations.Read(source.Text);
                var usings = source.CodeMatches(@"(?<![\w.])using\s+([\w.]+)\s*;").Select(x => x.Groups[1].Value).Where(x => x != "params").ToList();
                var added = new SortedSet<string>(StringComparer.Ordinal);
                var scope = new ShaderNameScope(declarations.FirstOrDefault().Namespace, [], usings);

                foreach (var match in source.CodeMatches(@"(?<![\w.])[A-Za-z_]\w*"))
                {
                    var name = match.Value;
                    if (classes.FullName(name) is not { } fullName || fullName == name || declarations.Any(x => x.Name == name)
                        || scope.Resolve(name, implicitUsings, known.Contains, out _) != null
                        || IsDeclaredAsMember(source, name))
                        continue;

                    var withUsing = new ShaderNameScope(scope.Namespace, [], [.. usings, .. added, ShaderNames.Namespace(fullName)!]);
                    withUsing.Resolve(name, implicitUsings, known.Contains, out var ambiguous);
                    if (ambiguous != null)
                        source.Replace(match.Index, match.Length, fullName);
                    else
                        added.Add(ShaderNames.Namespace(fullName)!);
                }

                if (added.Count > 0)
                    source.Insert(UsingPosition(source), string.Concat(added.Select(x => $"using {x};{source.NewLine}")));
                if (source.HasEdits)
                    files[path] = source.Apply();
            }
        }
    }

    // A member or local of that name: "float4 Name;" or "Map<A,B> Name;", and not a keyword that takes a class or a list of parents "A, Name,"
    private static bool IsDeclaredAsMember(ShaderSourceText source, string name)
        => source.CodeMatches($@"(?<![\w.])([A-Za-z_](?:[\w<>,]*[\w>])?)\s+{name}\s*[;=,)(\[]").Any(x => x.Groups[1].Value is not ("import" or "mixin" or "compose" or "child" or "return"));

    // After the last using, or before the first code of the file
    private static int UsingPosition(ShaderSourceText source)
    {
        var lastUsing = source.CodeMatches(@"(?<![\w.])using\s+[\w.]+\s*;").Where(x => x.Groups[0].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last() != "params;").LastOrDefault();
        var firstDeclaration = source.CodeMatches(@"\b(?:namespace|shader|module|effect|internal|partial)\b").FirstOrDefault();
        if (lastUsing != null && (firstDeclaration == null || lastUsing.Index < firstDeclaration.Index))
        {
            var lineEnd = source.Text.IndexOf('\n', lastUsing.Index);
            return lineEnd < 0 ? source.Text.Length : lineEnd + 1;
        }

        for (var i = 0; i < source.Text.Length; i++)
        {
            if (source.IsCode[i] && !char.IsWhiteSpace(source.Text[i]))
                return i;
        }
        return source.Text.Length;
    }

    /// <summary>
    /// The texts that name a key: <c>[Link("Class.Member")]</c>, and a generic argument <c>Class&lt;Other.Member&gt;</c>.
    /// </summary>
    private static void FullKeyNames(Dictionary<string, string> texts, EngineShaderClasses classes)
    {
        foreach (var path in texts.Keys.ToList())
        {
            var source = new ShaderSourceText(texts[path]);
            foreach (var match in source.CodeMatches(@"\[\s*Link\s*\(\s*""([^""]+)""").Concat(source.CodeMatches(@"(?<=[<,]\s*)([A-Za-z_]\w*\.[A-Za-z_][\w.\[\]]*)(?=\s*[,>])")))
            {
                var key = match.Groups[1];
                var fullKey = classes.ToFullKeyName(key.Value);
                if (fullKey != key.Value)
                    source.Replace(key.Index, key.Length, fullKey);
            }
            if (source.HasEdits)
                texts[path] = source.Apply();
        }
    }

    // The steps that change text without reading the classes, in the order of Steps (they come first)
    private static readonly Func<string, string>[] TextSteps = [DropClone, StageToModule, ComposeOutOfBuffer, DropComposeValue];

    // An older spelling parses after them
    internal static string WithTextSteps(string text) => TextSteps.Aggregate(text, (current, step) => step(current));

    private static void Apply(Dictionary<string, string> texts, Func<ShaderUpgradeProject, Dictionary<string, ShaderSourceText>> step, EngineShaderClasses? engine,
        List<string> notes, IReadOnlyDictionary<string, string> dependencies)
    {
        var project = new ShaderUpgradeProject(texts, engine, notes, dependencies);
        foreach (var (path, text) in step(project))
        {
            if (text.HasEdits)
                texts[path] = text.Apply();
        }
    }

    /// <summary>
    /// The <c>clone</c> method modifier is removed, not in comments or strings. An effect's <c>mixin clone</c> is left as it is.
    /// </summary>
    internal static string DropClone(string text)
    {
        var source = new ShaderSourceText(text);
        foreach (var match in source.CodeMatches(@"(?<![\w.])(?<!\bmixin\s+)clone\s+(?=[A-Za-z_])"))
            source.Replace(match.Index, match.Length, "");
        return source.Apply();
    }

    /// <summary>
    /// A method that implements an abstract method of a parent (same name and parameter types) without <c>override</c> gets it: a
    /// project, dependency or engine class (the engine list has the abstract methods).
    /// </summary>
    private static Dictionary<string, ShaderSourceText> AddOverride(ShaderUpgradeProject project, Dictionary<string, string> texts, List<string> notes)
    {
        var sources = new Dictionary<string, ShaderSourceText>();
        foreach (var shaderClass in project.ProjectClasses)
        {
            foreach (var method in shaderClass.Methods.Where(x => !x.IsOverride && !x.IsAbstract))
            {
                var implements = project.Ancestors(shaderClass.Name).Any(ancestor => project.Classes.TryGetValue(ancestor, out var owner)
                    && owner.Methods.Any(x => x.IsAbstract && x.Name == method.Name && x.Signature == method.Signature));
                if (!implements)
                    continue;
                if (shaderClass.Methods.Count(x => x.Name == method.Name) > 1)
                {
                    notes.Add($"{shaderClass.Path}: {shaderClass.Name}.{method.Name} implements an abstract method but has overloads; add 'override' by hand");
                    continue;
                }

                var source = Source(sources, texts, shaderClass.Path!);
                foreach (var classText in source.FindClass(shaderClass.Name))
                {
                    foreach (var declaration in MethodDeclarations(source, classText, method.Name, "override"))
                        source.Insert(declaration, "override ");
                }
            }
        }
        return sources;
    }

    /// <summary>
    /// The first definition of a module method that has overrides gets a kind, when it is in the sources being upgraded:
    /// <c>pipeline</c> if an override reaches it with <c>base</c>, else <c>replace</c>. Without phases, the two link the same way, so
    /// a class of another project that this upgrade doesn't see changes nothing. A method of a dependency stays as it is: its author has to do it.
    /// </summary>
    private static Dictionary<string, ShaderSourceText> AddKinds(ShaderUpgradeProject project, Dictionary<string, string> texts, List<string> notes)
    {
        var sources = new Dictionary<string, ShaderSourceText>();
        var done = new HashSet<(string, string, string)>();
        foreach (var shaderClass in project.ProjectClasses)
        {
            foreach (var method in shaderClass.Methods.Where(x => x.IsOverride))
            {
                foreach (var owner in project.Ancestors(shaderClass.Name).Select(x => project.Classes.GetValueOrDefault(x)).OfType<ShaderUpgradeClass>())
                {
                    var isDefinition = owner.Methods.Any(x => x.Name == method.Name && x.Signature == method.Signature && !x.IsOverride && x.IsModule && !x.IsAbstract);
                    if (!isDefinition || owner.DeclaredKinds.Contains((method.Name, method.Signature)))
                        continue;
                    if (owner.IsDependency)
                    {
                        notes.Add($"{shaderClass.Path}: {shaderClass.Name} overrides {method.Name} of {owner.Name}, in a library ({owner.Path}), and it has no kind: error SDSL0246 until the author of {owner.Name} writes replace, pipeline or around on it");
                        continue;
                    }
                    if (!done.Add((owner.Name, method.Name, method.Signature)))
                        continue;

                    var callsBase = new Regex($@"\bbase\s*\.\s*{Regex.Escape(method.Name)}\s*\(");
                    var overriders = project.Classes.Values.Where(x => project.Ancestors(x.Name).Contains(owner.Name) && x.Methods.Any(y => y.IsOverride && y.Name == method.Name && y.Signature == method.Signature));
                    var kind = overriders.Any(x => x.Methods.Any(y => callsBase.IsMatch(y.Body))) ? "pipeline" : "replace";

                    var source = Source(sources, texts, owner.Path!);
                    foreach (var classText in source.FindClass(owner.Name))
                    {
                        foreach (var declaration in MethodDeclarations(source, classText, method.Name, kind))
                        {
                            if (DeclaredSignature(source.Text, declaration) == method.Signature)
                                source.Insert(ReturnType(source.Text, declaration), $"{kind} ");
                        }
                    }
                }
            }
        }
        return sources;
    }

    // After the modifiers of the declaration at start
    private static int ReturnType(string text, int start)
    {
        var modifiers = Regex.Match(text[start..], @"\A(?:(?:module|static)\s+)*");
        return start + modifiers.Length;
    }

    /// <summary>
    /// The signature of the declaration at <paramref name="start"/>, as in <see cref="ShaderUpgradeClass.Methods"/>: the parameter types,
    /// comma-separated, without spaces.
    /// </summary>
    private static string DeclaredSignature(string text, int start)
    {
        var open = text.IndexOf('(', start);
        var types = new List<string>();
        var depth = 0;
        var item = open + 1;
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '<')
                depth++;
            else if (text[i] == '>')
                depth--;
            if ((text[i] != ',' || depth != 0) && text[i] != ')')
                continue;

            // No modifier, no name, no default value
            var parameter = text[item..i].Split('=')[0];
            parameter = Regex.Replace(parameter, @"(?<![\w.])(?:inout|in|out|const|point|lineadj|line|triangleadj|triangle)\s+", "");
            parameter = Regex.Replace(parameter, @"\s+[A-Za-z_]\w*\s*(?:\[[^\]]*\]\s*)*(?::\s*\w+\s*)?$", "");
            if (!string.IsNullOrWhiteSpace(parameter))
                types.Add(Regex.Replace(parameter, @"\s+", ""));
            item = i + 1;
            if (text[i] == ')')
                break;
        }
        return string.Join(",", types);
    }

    /// <summary>
    /// <c>stage</c> → <c>module</c> where it is a modifier (followed by another word), not in comments or strings.
    /// </summary>
    internal static string StageToModule(string text)
    {
        var source = new ShaderSourceText(text);
        foreach (var match in source.CodeMatches(@"(?<![\w.])stage(?=\s+[A-Za-z_])"))
            source.Replace(match.Index, match.Length, "module");
        return source.Apply();
    }

    /// <summary>
    /// A <c>compose</c> or <c>stream</c> declared inside a <c>cbuffer</c> or <c>rgroup</c> block moves out, right after the block,
    /// as 4.3 compiled it. Its line is removed from the block; comments and other members stay.
    /// </summary>
    internal static string ComposeOutOfBuffer(string text)
    {
        var source = new ShaderSourceText(text);
        foreach (var block in source.CodeMatches(@"(?<![\w.])(?:cbuffer|rgroup)\s+[A-Za-z_][\w.]*\s*\{"))
        {
            var open = block.Index + block.Length - 1;
            var close = source.MatchingBrace(open);
            if (close < 0)
                continue;

            var moved = new List<string>();
            foreach (var member in source.CodeMatches(@"(?<![\w.])(?:\[[^\]]*\]\s*)*(?:(?:module|stage)\s+)?(?:compose|stream|patchstream)\s[^;{}]*;", open + 1, close))
            {
                // Starts a member: comments can be before it
                var previous = member.Index - 1;
                while (!source.IsCode[previous] || char.IsWhiteSpace(text[previous]))
                    previous--;
                if (source.Depth(open, member.Index) != 1 || text[previous] is not ('{' or ';' or '}'))
                    continue;
                moved.Add(member.Value);

                // The whole line when the member is alone on it
                var lineStart = text.LastIndexOf('\n', member.Index - 1) + 1;
                var lineEnd = text.IndexOf('\n', member.Index + member.Length);
                lineEnd = lineEnd < 0 ? text.Length : lineEnd + 1;
                if (string.IsNullOrWhiteSpace(text[lineStart..member.Index]) && string.IsNullOrWhiteSpace(text[(member.Index + member.Length)..lineEnd]))
                    source.Replace(lineStart, lineEnd - lineStart, "");
                else
                    source.Replace(member.Index, member.Length, "");
            }
            if (moved.Count == 0)
                continue;

            // After the block, and the `;` that may follow it, at the indentation of the block
            var after = close + 1;
            var semicolon = after;
            while (semicolon < text.Length && text[semicolon] is ' ' or '\t')
                semicolon++;
            if (semicolon < text.Length && text[semicolon] == ';')
                after = semicolon + 1;
            var blockLine = text.LastIndexOf('\n', block.Index) + 1;
            var indentation = text[blockLine..block.Index];
            source.Insert(after, string.Concat(moved.Select(x => source.NewLine + indentation + x)));
        }
        return source.Apply();
    }

    /// <summary>
    /// The value of a composition (<c>compose ComputeFloat Strength = 0.5;</c>) is removed: the effect gives the composition its
    /// shader, and VL reads its default from <c>[Default(...)]</c>.
    /// </summary>
    internal static string DropComposeValue(string text)
    {
        var source = new ShaderSourceText(text);
        foreach (var match in source.CodeMatches(@"(?<![\w.])compose\s+[A-Za-z_][\w.]*\s*(?:<[^;{}=]*>)?\s+[A-Za-z_]\w*(?:\s*\[\s*\w*\s*\])?(\s*=[^;]*);"))
            source.Replace(match.Groups[1].Index, match.Groups[1].Length, "");
        return source.Apply();
    }

    /// <summary>
    /// In a class used in compositions, module code can't call an instance method: a plain method called from module code of the
    /// class or its parents gets <c>module</c>, and so do its overrides.
    /// </summary>
    private static Dictionary<string, ShaderSourceText> MarkModuleHelpers(ShaderUpgradeProject project, Dictionary<string, string> texts, List<string> notes)
    {
        var marked = new HashSet<(string Owner, string Method)>();
        var pending = new Stack<(string Context, string Caller, string Body)>();
        foreach (var shaderClass in project.ProjectClasses.Where(x => project.IsInstance(x.Name) && !x.IsModule))
        {
            foreach (var method in shaderClass.Methods.Where(x => x.IsModule))
                pending.Push((shaderClass.Name, $"{shaderClass.Name}.{method.Name}", method.Body));
        }

        while (pending.Count > 0)
        {
            var (context, caller, body) = pending.Pop();
            foreach (Match call in Regex.Matches(body, @"(?<![\w.])(?:(?:this|base)\s*\.\s*)?([A-Za-z_]\w*)\s*\("))
            {
                var name = call.Groups[1].Value;
                if (project.MethodOwner(context, name) is not { IsEngine: false } owner)
                    continue;
                var target = owner.Methods.First(x => x.Name == name && !x.IsOverride);
                if (target.IsModule || project.BecomesModule(owner.Name))
                    continue;
                // A dependency is not changed: its upgraded version decides
                if (owner.IsDependency)
                {
                    notes.Add($"{caller}: module code calls {owner.Name}.{name}, an instance method of a dependency ({owner.Path}); this works only if the class of {caller} is mixed at the root, not used in a composition");
                    continue;
                }
                // A method of a compose type is called through slots, one chain per instance: it can't be module
                if (project.IsComposeType(owner.Name))
                {
                    notes.Add($"{owner.Path}: module code {caller} calls {owner.Name}.{name}, an instance method; this works only if the class of {caller} is mixed at the root, not used in a composition");
                    continue;
                }
                if (marked.Add((owner.Name, name)))
                    pending.Push((owner.Name, $"{owner.Name}.{name}", target.Body));
            }
        }

        // Overrides of a marked method are module code too
        foreach (var shaderClass in project.ProjectClasses)
        {
            foreach (var method in shaderClass.Methods.Where(x => x.IsOverride && !x.IsModule))
            {
                if (project.Ancestors(shaderClass.Name).Any(x => marked.Contains((x, method.Name))))
                    marked.Add((shaderClass.Name, method.Name));
            }
        }

        var sources = new Dictionary<string, ShaderSourceText>();
        foreach (var (owner, method) in marked)
        {
            var shaderClass = project.Classes[owner];
            var source = Source(sources, texts, shaderClass.Path!);
            foreach (var classText in source.FindClass(owner))
            {
                foreach (var declaration in MethodDeclarations(source, classText, method, "module"))
                    source.Insert(declaration, "module ");
            }
        }
        return sources;
    }

    /// <summary>
    /// A class with no instance part that another class imports (inherits it or refers to it by name) becomes <c>module X</c>,
    /// with no <c>module</c> modifier inside; a parent that is a module becomes <c>import X;</c> in the body; a class used by
    /// name that is a shader is added to the parents, which is what the compiler did silently.
    /// </summary>
    private static Dictionary<string, ShaderSourceText> ConvertModules(ShaderUpgradeProject project, Dictionary<string, string> texts, List<string> notes)
    {
        var sources = new Dictionary<string, ShaderSourceText>();
        foreach (var shaderClass in project.ProjectClasses)
        {
            var source = Source(sources, texts, shaderClass.Path!);
            var declarations = source.FindClass(shaderClass.Name).ToList();
            if (declarations.Count == 0)
                notes.Add($"{shaderClass.Path}: class {shaderClass.Name} not found in the text, not upgraded");
            foreach (var classText in declarations)
                ConvertDeclaration(project, shaderClass, source, classText, notes);
        }
        return sources;
    }

    private static void ConvertDeclaration(ShaderUpgradeProject project, ShaderUpgradeClass shaderClass, ShaderSourceText source, ShaderClassText classText, List<string> notes)
    {
        // A shader that relies on being mixed at the root, root-only before through a parent that becomes a module
        if (project.NeedsRootMarker(shaderClass.Name))
            InsertRootAttribute(source, classText);

        if (project.BecomesModule(shaderClass.Name))
        {
            source.Replace(classText.Keyword, classText.KeywordText.Length, "module");
            foreach (var modifier in source.CodeMatches(@"(?<![\w.])module\s+(?=[A-Za-z_])", classText.Open, classText.Close))
                source.Replace(modifier.Index, modifier.Length, "");
        }
        else
        {
            RemoveBaseFromModuleCalls(project, shaderClass, source, classText, notes);
        }

        var (colon, parents) = source.ParseParents(classText);
        // A shader used in compositions sees the streams of a [Root] module through its streams module (SDSL0119 otherwise)
        var imports = new List<string>();
        foreach (var parent in parents.Where(x => project.IsModuleAfterUpgrade(x.Name)))
        {
            if (project.StreamsModuleFor(shaderClass.Name, parent.Name) is { } streams)
            {
                notes.Add($"{shaderClass.Path}: {shaderClass.Name} is used in compositions: it imports {streams} instead of {parent.Name} ([Root])");
                imports.Add(streams);
            }
            else
            {
                imports.Add(parent.Text);
            }
        }
        var kept = parents.Where(x => !project.IsModuleAfterUpgrade(x.Name)).Select(x => x.Text).ToList();

        var ancestors = project.Ancestors(shaderClass.Name);
        var added = shaderClass.StaticReferences
            .Where(x => project.Classes.ContainsKey(x.Key) && !project.IsModuleAfterUpgrade(x.Key) && !ancestors.Contains(x.Key))
            .Select(x => x.Value)
            .ToList();

        if (imports.Count == 0 && added.Count == 0)
            return;

        // Parents
        var newParents = kept.Concat(added).ToList();
        if (colon >= 0 && parents.Count > 0)
        {
            var removeStart = colon;
            while (removeStart > classText.NameEnd && char.IsWhiteSpace(source.Text[removeStart - 1]))
                removeStart--;
            source.Replace(removeStart, parents[^1].End - removeStart, newParents.Count > 0 ? $" : {string.Join(", ", newParents)}" : "");
        }
        else if (newParents.Count > 0)
        {
            var headerEnd = classText.Open;
            while (headerEnd > classText.NameEnd && char.IsWhiteSpace(source.Text[headerEnd - 1]))
                headerEnd--;
            source.Insert(headerEnd, $" : {string.Join(", ", newParents)}");
        }

        // Imports, first in the body
        if (imports.Count > 0)
        {
            var newLine = source.NewLine;
            var lineEnd = source.Text.IndexOf('\n', classText.Open);
            if (lineEnd >= 0 && lineEnd < classText.Close && source.Text[(classText.Open + 1)..lineEnd].Trim().Length == 0)
            {
                var indentation = source.BodyIndentation(classText.Open);
                var nextLineEnd = source.Text.IndexOf('\n', lineEnd + 1);
                var nextLine = source.Text[(lineEnd + 1)..(nextLineEnd < 0 ? source.Text.Length : nextLineEnd)].Trim();
                var separator = nextLine.Length > 0 && !nextLine.StartsWith('}') && !nextLine.StartsWith("import ") ? newLine : "";
                source.Insert(lineEnd + 1, string.Concat(imports.Select(x => $"{indentation}import {x};{newLine}")) + separator);
            }
            else
            {
                source.Insert(classText.Open + 1, string.Concat(imports.Select(x => $" import {x};")));
            }
        }
    }

    // Before the declaration (attributes come before `internal` and `partial`): on its own line when the declaration starts its line
    private static void InsertRootAttribute(ShaderSourceText source, ShaderClassText classText)
    {
        var lineStart = source.Text.LastIndexOf('\n', Math.Max(classText.Keyword - 1, 0)) + 1;
        var start = lineStart + Regex.Match(source.Text[lineStart..classText.Keyword], @"(?:(?:internal|partial)\s+)*$").Index;
        var indentation = source.Text[lineStart..start];
        if (indentation.Trim().Length == 0)
            source.Insert(lineStart, $"{indentation}[Root]{source.NewLine}");
        else
            source.Insert(start, "[Root] ");
    }

    /// <summary>
    /// <c>base.X()</c> where <c>X</c> is a method of a parent that becomes a module: from instance code it is an error in a composition
    /// (SDSL0208), and it means the same as <c>X()</c> when nothing overrides <c>X</c>, so <c>base.</c> is removed.
    /// </summary>
    private static void RemoveBaseFromModuleCalls(ShaderUpgradeProject project, ShaderUpgradeClass shaderClass, ShaderSourceText source, ShaderClassText classText, List<string> notes)
    {
        foreach (var call in source.CodeMatches(@"(?<![\w.])base\s*\.\s*([A-Za-z_]\w*)\s*\(", classText.Open, classText.Close))
        {
            var name = call.Groups[1].Value;
            if (shaderClass.Methods.Any(x => x.Name == name))
                continue;
            var owner = shaderClass.Parents.Select(parent => project.MethodOwner(parent, name)).FirstOrDefault(x => x != null);
            if (owner == null || owner.IsEngine || !project.IsModuleAfterUpgrade(owner.Name))
                continue;
            if (project.ProjectClasses.Any(x => x.Methods.Any(m => m.Name == name && m.IsOverride)))
            {
                notes.Add($"{shaderClass.Path}: {shaderClass.Name} calls base.{name}() of the module {owner.Name}, and {name} is overridden: check the call by hand");
                continue;
            }
            source.Replace(call.Index, call.Groups[1].Index - call.Index, "");
        }
    }

    private static ShaderSourceText Source(Dictionary<string, ShaderSourceText> sources, Dictionary<string, string> texts, string path)
    {
        if (!sources.TryGetValue(path, out var source))
            sources[path] = source = new ShaderSourceText(texts[path]);
        return source;
    }

    /// <summary>
    /// Where to insert <paramref name="modifier"/> for each declaration of <paramref name="method"/> in the class body that doesn't have
    /// it: the start of the declaration (after its attributes).
    /// </summary>
    private static IEnumerable<int> MethodDeclarations(ShaderSourceText source, ShaderClassText classText, string method, string modifier)
    {
        foreach (var match in source.CodeMatches($@"(?<![\w.]){Regex.Escape(method)}\s*\(", classText.Open, classText.Close))
        {
            if (source.Depth(classText.Open, match.Index) != 1)
                continue;

            // The declaration starts after the previous `;`, `{`, `}` or attribute
            var start = match.Index;
            while (start > classText.Open && !(source.IsCode[start - 1] && source.Text[start - 1] is ';' or '{' or '}' or ']'))
                start--;
            while (start < match.Index)
            {
                if (char.IsWhiteSpace(source.Text[start]) || !source.IsCode[start])
                {
                    start++;
                }
                // A preprocessor line before the declaration
                else if (source.Text[start] == '#')
                {
                    var lineEnd = source.Text.IndexOf('\n', start);
                    start = lineEnd < 0 || lineEnd >= match.Index ? match.Index : lineEnd + 1;
                }
                else
                {
                    break;
                }
            }

            // Already has the modifier, or not a declaration (no return type before the name)
            var declaration = source.Text[start..match.Index];
            if (Regex.IsMatch(declaration, $@"(?<![\w.]){modifier}(?!\w)") || !Regex.IsMatch(declaration, @"[\w>\]]\s+$"))
                continue;
            yield return start;
        }
    }
}

/// <param name="Files">Every file (path → text), upgraded.</param>
/// <param name="Notes">What the upgrade couldn't do or decide, to show to the user.</param>
public sealed record ShaderUpgradeResult(IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> Notes);
