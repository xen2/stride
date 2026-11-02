using Stride.Shaders.Core;
using Stride.Shaders.Parsing;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Parsing.SDSL.AST;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;
using Stride.Shaders.Spirv.Core.Buffers;
using Stride.Shaders.Spirv.Processing;
using Stride.Shaders.Spirv.Tools;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Stride.Core.Diagnostics;
using Stride.Core.Storage;
using Stride.Shaders.Parsing.SDFX.AST;

namespace Stride.Shaders.Compilers.SDSL;

public record struct CompileOptions()
{
    /// <summary>Whether to register the compiled shader in the cache.</summary>
    public bool RegisterInCache { get; init; } = true;
    /// <summary>Original source code before preprocessing, used for OpSource debug info. Falls back to preprocessed code if null.</summary>
    public string? OriginalCode { get; init; }
    /// <summary>The names the conditions of the preprocessor read that were no macro at that point (SDSL0147).</summary>
    public IReadOnlyList<UndefinedConditionName>? UndefinedConditionNames { get; init; }
}

public record struct SDSLC(IExternalShaderLoader ShaderLoader)
{
    public readonly bool Compile(string? filename, string code, ObjectId hash, ReadOnlySpan<ShaderMacro> macros, ILogger log, [MaybeNullWhen(false)] out ShaderBuffers lastBuffer, CompileOptions options = default)
    {
        lastBuffer = default;

        var parsed = SDSLParser.Parse(code);
        if (parsed.Errors.Count > 0)
        {
            // The first error stops the parser: the others are the enclosing parsers giving up after it
            log.Error(parsed.Errors[0].ToString(filename));
            return false;
        }
        if (parsed.AST is not ShaderFile sf)
            return false;

        var fileUsings = sf.RootDeclarations.OfType<UsingShaderNamespace>().Select(x => string.Join('.', x.NamespacePath)).ToList();
        var declarations = sf.Namespaces
            .SelectMany(ns =>
            {
                var scope = new ShaderNameScope(ns.Namespace?.Name, ns.Declarations.OfType<UsingShaderNamespace>().Select(x => string.Join('.', x.NamespacePath)).ToList(), fileUsings);
                return ns.Declarations.Select(x => (Declaration: x, Scope: scope));
            })
            .Concat(sf.RootDeclarations.Select(x => (Declaration: x, Scope: new ShaderNameScope(null, [], fileUsings))))
            .ToList();
        var classes = declarations.Select(x => x.Declaration).OfType<ShaderClass>().ToList();
        var fileName = Path.GetFileNameWithoutExtension(filename);
        if (ReportCycleInFile(filename, declarations, log) | ReportGenericsInConditions(filename, classes, options.UndefinedConditionNames, log))
            return false;
        // Every class of the file is validated against the file, whichever namespace the class is in
        var storageName = filename != null ? ShaderFileDeclarations.StorageName(options.OriginalCode ?? code, fileName!) : null;
        foreach (var (declaration, scope) in declarations)
        {
            if (declaration is ShaderClass shader)
            {
                shader.Scope = scope;
                var compiler = new CompilerUnit();
                SymbolTable table = new(compiler.Context, ShaderLoader)
                {
                    CurrentMacros = [.. macros],
                };

                // Add debug source info (OpString/OpSource for debug mapping, OpSourceHashSDSL for cache validation)
                if (filename != null)
                {
                    var filenameId = compiler.Context.Add(new OpString(compiler.Context.Bound++, filename)).ResultId;
                    // TODO: Add SourceLanguage.SDSL
                    compiler.Context.Add(new OpSource(Spirv.Specification.SourceLanguage.Unknown, 0, filenameId, options.OriginalCode ?? code));
                    compiler.Context.Add(new OpSourceHashSDSL(filenameId, (int)hash.Hash1, (int)hash.Hash2, (int)hash.Hash3, (int)hash.Hash4, storageName));
                    compiler.SourceFileId = filenameId;
                }
                // TODO: Do we want to record macros with a custom OpMacroSDSL? (mostly for debug purposes)

                compiler.Macros.AddRange(macros);
                bool hasErrors = false;

                if (filename != null && shader == classes[0] && classes.All(x => x.Name.Name != fileName))
                    table.AddWarning(new(shader.Name.Info, string.Format(SDSLErrorMessages.SDSL0245, fileName, shader.Name.Name)));

                try
                {
                    shader.Compile(table, compiler);
                }
                catch (StatementCompileException e) when (filename != null)
                {
                    // file(line,col): the form build tools and editors link to
                    log.Error($"{filename}({e.Error.Location.Line},{e.Error.Location.Column}): {e.Error.Message} (in `{e.Error.Location.Text.Span.Trim()}`)", e);
                    hasErrors = true;
                }
                catch (ShaderErrorsLoggedException e)
                {
                    e.CopyTo(log);
                    hasErrors = true;
                }
                catch (Exception e)
                {
                    log.Error(e.Message, e);
                    hasErrors = true;
                }

                // Collect dependency hashes from all shaders loaded during compilation.
                // Each loaded shader's buffer has its own OpSourceHashSDSL — copy them into this shader's context.
                var seenHashes = new HashSet<ObjectId>();
                // Add own hash so we don't duplicate it
                seenHashes.Add(hash);
                foreach (var loadedShader in table.DeclaredShaders.Values)
                {
                    if (loadedShader.Name != shader.FullName
                        && ShaderLoader.Cache.TryLoadFromCache(loadedShader.Name, null, macros, out var depBuffer, out _))
                    {
                        CopySourceHashes(depBuffer, compiler.Context, seenHashes);
                    }
                }

                foreach (var info in table.Infos)
                    log.Info(info.ToString());

                if (table.Errors.Count > 0)
                {
                    foreach (var error in table.Errors)
                        log.Error(Format(filename, error));
                    hasErrors = true;
                }

                // Warnings are kept in the class: the loader reports them each time it loads it, from the cache too
                foreach (var warning in table.Warnings)
                {
                    var message = Format(filename ?? shader.Name.Name, warning);
                    if (hasErrors)
                        log.Warning(message);
                    else
                        compiler.Context.Add(new OpDiagnosticSDSL(message));
                }

                if (hasErrors)
                    return false;

                lastBuffer = compiler.ToShaderBuffers();

                // Ensure all names and types from OpName/OpType instructions are registered
                // in the context dictionaries. The compiler may not explicitly register everything
                // (e.g. names for imported IDs, or types from InsertWithoutDuplicates).
                ShaderClass.ProcessNameAndTypes(lastBuffer.Context, new ShaderLoaderImporter(ShaderLoader));

                if (options.RegisterInCache)
                    ShaderLoader.Cache.RegisterShader(shader.FullName, null, macros, lastBuffer, hash);
            }
            else if (declaration is ShaderEffect or EffectParameters)
            {
                // Ignore (using C# codegen for now)
            }
            else if (declaration is UsingShaderNamespace)
            {
                // Part of the scope
            }
            else
            {
                log.Error($"Compiling declaration [{declaration.GetType()}] is not implemented");
                return false;
            }
        }

        return lastBuffer != null;
    }

    /// <summary>
    /// Puts each class of a file after the classes of the file it inherits or imports, or reports the cycle among them (SDSL0146):
    /// the loader only sees the classes it was asked for by name, not the others of a file.
    /// </summary>
    private static bool ReportCycleInFile(string? filename, List<(ShaderDeclaration Declaration, ShaderNameScope Scope)> declarations, ILogger log)
    {
        var classes = new Dictionary<string, (ShaderClass Class, ShaderNameScope Scope)>();
        foreach (var (declaration, scope) in declarations)
        {
            if (declaration is ShaderClass shader)
                classes.TryAdd(ShaderNames.Combine(scope.Namespace, shader.Name.Name), (shader, scope));
        }

        var ordered = new List<string>();
        var visited = new HashSet<string>();
        var path = new List<string>();
        foreach (var name in classes.Keys)
        {
            if (FindCycle(name) is { } cycle)
            {
                log.Error(Format(filename, new(classes[cycle[0]].Class.Name.Info, string.Format(SDSLErrorMessages.SDSL0146, string.Join(" -> ", cycle)))));
                return true;
            }
        }

        // Two classes of one name are reported when they compile
        if (ordered.Count == declarations.Count(x => x.Declaration is ShaderClass))
        {
            var next = 0;
            for (var i = 0; i < declarations.Count; i++)
            {
                if (declarations[i].Declaration is ShaderClass)
                    declarations[i] = classes[ordered[next++]];
            }
        }
        return false;

        List<string>? FindCycle(string name)
        {
            var index = path.IndexOf(name);
            if (index >= 0)
                return [.. path.Skip(index), name];
            if (!visited.Add(name))
                return null;

            path.Add(name);
            var (shader, scope) = classes[name];
            foreach (var parent in shader.Mixins.Select(x => x.Name).Concat(shader.Elements.OfType<ShaderImport>().Select(x => x.Module.Name)))
            {
                // A name without a namespace can be a class of another file: only the class of its own namespace is known here
                var fullName = parent.Contains('.') ? parent : ShaderNames.Combine(scope.Namespace, parent);
                if (classes.ContainsKey(fullName) && FindCycle(fullName) is { } cycle)
                    return cycle;
            }
            path.RemoveAt(path.Count - 1);
            ordered.Add(name);
            return null;
        }
    }

    /// <summary>
    /// A condition of the preprocessor that reads a generic parameter of its class (SDSL0147). Conditions run before generics get their value.
    /// </summary>
    private static bool ReportGenericsInConditions(string? filename, List<ShaderClass> classes, IReadOnlyList<UndefinedConditionName>? names, ILogger log)
    {
        if (names == null || names.Count == 0)
            return false;

        var found = false;
        foreach (var shader in classes)
        {
            if (shader.Generics == null)
                continue;

            foreach (var name in names)
            {
                if (name.Line >= shader.Info.Line && name.Line <= shader.Info.EndLine && shader.Generics.Parameters.Any(x => x.Name.Name == name.Name))
                {
                    var location = filename != null ? $"{filename}({name.Line},{name.Column}): " : "";
                    log.Error(location + string.Format(SDSLErrorMessages.SDSL0147, name.Directive, name.Name, shader.Name.Name));
                    found = true;
                }
            }
        }
        return found;
    }

    /// <summary>
    /// file(line,col): the form build tools and editors link to, as for statement errors.
    /// </summary>
    private static string Format(string? filename, SemanticError error)
    {
        if (filename == null)
            return error.ToString();

        var text = error.Location.Text.Span.Trim();
        var context = text.Length > 0 && !text.ContainsAny('\r', '\n') ? $" (in `{text}`)" : "";
        return $"{filename}({error.Location.Line},{error.Location.Column}): {error.Message}{context}";
    }

    /// <summary>
    /// Copies OpSourceHashSDSL entries from a dependency's buffer into the target context, skipping duplicates.
    /// </summary>
    private static void CopySourceHashes(ShaderBuffers source, SpirvContext target, HashSet<ObjectId> seenHashes)
    {
        foreach (var inst in source.Context)
        {
            if (inst.Op != Spirv.Specification.Op.OpSourceHashSDSL)
                continue;

            var sourceHash = (OpSourceHashSDSL)inst;
            var depHash = new ObjectId((uint)sourceHash.Hash1, (uint)sourceHash.Hash2, (uint)sourceHash.Hash3, (uint)sourceHash.Hash4);
            if (!seenHashes.Add(depHash))
                continue;

            // Find the filename string from the source context
            foreach (var s in source.Context)
            {
                if (s.Op == Spirv.Specification.Op.OpString && ((OpString)s).ResultId == sourceHash.File)
                {
                    var depFilenameId = target.Add(new OpString(target.Bound++, ((OpString)s).Value)).ResultId;
                    target.Add(new OpSourceHashSDSL(depFilenameId, sourceHash.Hash1, sourceHash.Hash2, sourceHash.Hash3, sourceHash.Hash4, sourceHash.ShaderName));
                    break;
                }
            }
        }
    }
}
