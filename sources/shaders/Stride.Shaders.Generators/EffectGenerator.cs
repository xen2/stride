using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Stride.Shaders.Parsing.SDFX;

[Generator]
public class EffectGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var shaderFiles =
            context
                .AdditionalTextsProvider
                .Where(x => Path.GetExtension(x.Path).ToLowerInvariant() is ".sdfx" or ".sdsl");

        var implicitUsings = context.CompilationProvider.Select((compilation, _) => ImplicitUsingsOf(compilation));

        // What the code of a file depends on outside it: the candidate full names of every file, the ones the compilation has, and
        // the shaders and effects the other files write. The answer is a string, so a C# change regenerates the code only when the answer changes.
        var firstPass = shaderFiles.Combine(implicitUsings).Select((x, _) =>
        {
            var (_, candidates, declared, _) = Generate(x.Left, null, x.Right);
            return (Candidates: string.Join("\n", candidates), Declared: string.Join("\n", declared));
        });
        // A declared name that starts with % is an enum or struct of a file and its C# twin (EffectCodeWriter.CSharpAnswers)
        var existingTypes = firstPass.Collect().Combine(context.CompilationProvider).Select((x, _) => string.Join("\n", x.Left
            .SelectMany(names => names.Candidates.Split('\n'))
            .Where(name => name.Length > 0)
            .SelectMany(name => CSharpAnswersOf(x.Right, name))
            .Concat(x.Left.SelectMany(names => names.Declared.Split('\n')).Where(name => name.Length > 0 && name[0] != '%').Select(name => $"{name}Keys#"))
            .Concat(x.Left.SelectMany(names => names.Declared.Split('\n')).Where(name => name.Length > 0 && name[0] != '%').GroupBy(name => name).Where(names => names.Count() > 1).Select(names => $"{names.Key}Keys*"))
            .Concat(x.Left.SelectMany(names => names.Declared.Split('\n')).Where(name => name.Length > 0 && name[0] == '%'))
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)));

        var referencedLayers = context.CompilationProvider.Select((compilation, _) => ReferencedLayersOf(compilation));

        context.RegisterSourceOutput(shaderFiles.Combine(implicitUsings).Combine(existingTypes).Combine(referencedLayers), (output, x) =>
        {
            var existing = new HashSet<string>(x.Left.Right.Split('\n'));
            var file = x.Left.Left.Left;
            var generated = Generate(file, existing.Contains, x.Left.Left.Right, x.Right, AnswersByQuestion(existing));
            // The compiler doesn't show an #error of generated code when the project's own code has errors, which it has as soon as
            // this file generates nothing: the errors are diagnostics of the shader file instead
            foreach (var error in generated.Errors)
                output.ReportDiagnostic(ErrorDiagnostic(file.Path, error));
            output.AddSource(GetSafeHintName(file.Path), generated.Errors.Count > 0 ? "// The errors of this file are in the build output\n" : generated.Text);
        });
    }

    private static readonly ConcurrentDictionary<string, DiagnosticDescriptor> ErrorDescriptors = new();

    // The code of the message is the code of the diagnostic: "SDSL0251: ..."
    private static Diagnostic ErrorDiagnostic(string path, (string Message, int Line, int Column) error)
    {
        var message = error.Message;
        var id = "SDSL";
        if (message.Length > 10 && message.StartsWith("SDSL", StringComparison.Ordinal) && message.Substring(4, 4).All(char.IsDigit) && message.Substring(8, 2) == ": ")
        {
            id = message.Substring(0, 8);
            message = message.Substring(10);
        }
        var descriptor = ErrorDescriptors.GetOrAdd(id, x => new DiagnosticDescriptor(x, "Shader error", "{0}", "Stride.Shaders", DiagnosticSeverity.Error, isEnabledByDefault: true));
        var position = new LinePosition(Math.Max(error.Line - 1, 0), Math.Max(error.Column - 1, 0));
        var location = error.Line > 0 ? Location.Create(path, default, new LinePositionSpan(position, position)) : Location.None;
        return Diagnostic.Create(descriptor, location, message);
    }

    private static string ReferencedLayersOf(Compilation compilation) => string.Join(",", compilation.SourceModule.ReferencedAssemblySymbols
        .Where(assembly => assembly.GetAttributes().Any(x => x.AttributeClass?.Name == "AssemblyMetadataAttribute" && x.ConstructorArguments is [{ Value: EffectCodeWriter.LayerMetadataKey }, ..]))
        .Select(assembly => assembly.Identity.Name)
        .Distinct()
        .OrderBy(name => name, StringComparer.Ordinal));

    // A constant, so that the compiler and this generator have one list. Empty for a project that doesn't reference Stride.Rendering.
    private static string ImplicitUsingsOf(Compilation compilation)
        => compilation.GetTypeByMetadataName("Stride.Rendering.ShaderImplicitUsings")?.GetMembers("Namespaces").OfType<IFieldSymbol>().FirstOrDefault()?.ConstantValue as string ?? "";

    /// <summary>
    /// Generates the keys and effects of a shader file.
    /// </summary>
    /// <param name="csharpTypeExists">Whether a C# name exists (see <see cref="EffectCodeWriter.CSharpTypeExists"/>).</param>
    /// <param name="implicitUsings">The implicit usings, separated by commas.</param>
    /// <param name="referencedLayers">The referenced assemblies that hold a layer (see <see cref="EffectCodeWriter.ReferencedLayers"/>), separated by commas.</param>
    /// <param name="csharpAnswers">The answers about C# twins (see <see cref="EffectCodeWriter.CSharpAnswers"/>).</param>
    /// <returns>The code, the C# names it depends on, the shaders, effects and C# twins it writes, and its errors with their line and column (0 when unknown).</returns>
    public static (string Text, IEnumerable<string> Candidates, IEnumerable<string> Declared, IReadOnlyList<(string Message, int Line, int Column)> Errors) Generate(AdditionalText file, Func<string, bool>? csharpTypeExists, string implicitUsings, string referencedLayers = "", IReadOnlyDictionary<string, string>? csharpAnswers = null)
    {
        var isSdfx = Path.GetExtension(file.Path).Equals(".sdfx", StringComparison.OrdinalIgnoreCase);

        try
        {
            var preprocessedText = MonoGamePreProcessor.Run(file.GetText()?.ToString() ?? throw new InvalidOperationException($"Could not read file content for {file.Path}"), file.Path);
            var parsed = SDSLParser.Parse(preprocessedText);
            if (parsed.Errors.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var error in parsed.Errors)
                    sb.AppendLine($"#error Parse error: {error}");
                return (sb.ToString(), [], [], [.. parsed.Errors.Select(x => (x.Message, x.Location.Line, x.Location.Column))]);
            }
            else if (parsed.AST == null)
            {
                return ("#error No AST parsed", [], [], [("No AST parsed", 0, 0)]);
            }

            // .sdfx files must declare 'effect', not 'shader' — catch the mistake here
            // instead of silently emitting unbuildable C#.
            if (isSdfx && parsed.AST is Parsing.ShaderFile sdfxFile && HasShaderClassDeclaration(sdfxFile))
            {
                var message = $"'{Path.GetFileName(file.Path)}' contains a 'shader' declaration. Use 'effect' (or 'extend effect') inside .sdfx files; 'shader' / 'partial shader' belongs in .sdsl.";
                return ($"#error {message}", [], [], [(message, 0, 0)]);
            }

            var effectCodeWriter = new EffectCodeWriter
            {
                FileName = Path.GetFileName(file.Path),
                CSharpTypeExists = csharpTypeExists,
                CSharpAnswers = csharpAnswers,
                ImplicitUsings = implicitUsings.Split(',', StringSplitOptions.RemoveEmptyEntries),
                ReferencedLayers = referencedLayers.Split(',', StringSplitOptions.RemoveEmptyEntries),
            };
            effectCodeWriter.Run(parsed.AST);
            return (effectCodeWriter.Text, effectCodeWriter.CSharpTypeCandidates, effectCodeWriter.DeclaredClasses, [.. effectCodeWriter.Errors.Select(x => (x.Message, x.Location.Line, x.Location.Column))]);
        }
        catch (ShaderPreprocessorException e)
        {
            return ($"#error {e.Message}", [], [], [(e.Message, 0, 0)]);
        }
        catch (Exception e)
        {
            return ($"#error Exception while running {nameof(EffectGenerator)} {e}", [], [], [($"Exception while running {nameof(EffectGenerator)} {e}", 0, 0)]);
        }
    }

    // The answers of the compilation to a question of a file: the name when it exists (see EffectCodeWriter.CSharpTypeExists), or, for a
    // question about a C# twin, the question and its answer (see EffectCodeWriter.CSharpAnswers)
    private static IEnumerable<string> CSharpAnswersOf(Compilation compilation, string name)
    {
        if (name.EndsWith("~", StringComparison.Ordinal))
        {
            if (compilation.GetTypeByMetadataName(name.TrimEnd('~')) is not { } type)
                return [];
            if (type.TypeKind != TypeKind.Enum || type.EnumUnderlyingType is not { } underlyingType)
                return [$"{name}notenum"];
            var values = type.GetMembers().OfType<IFieldSymbol>().Where(x => x.HasConstantValue).Select(x => $"{x.Name}={Convert.ToInt64(x.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)}");
            return [$"{name}{underlyingType.ToDisplayString()}:{string.Join(",", values)}"];
        }

        // A twin generated from a shader in another project has the GeneratedCode attribute of this generator
        if (name.EndsWith("?", StringComparison.Ordinal))
        {
            var generatorName = typeof(EffectCodeWriter).Assembly.GetName().Name;
            return compilation.GetTypeByMetadataName(name.TrimEnd('?')) is { TypeKind: TypeKind.Enum or TypeKind.Struct } type
                && type.GetAttributes().Any(x => x.AttributeClass?.Name == "GeneratedCodeAttribute" && x.ConstructorArguments is [{ Value: string tool }, ..] && tool == generatorName)
                ? [$"{name}{(type.TypeKind == TypeKind.Enum ? "enum" : "struct")}"]
                : [];
        }

        return CSharpNameExists(compilation, name) ? [name] : [];
    }

    // The answers about C# twins, by question: "Full.Name~", "Name?" and "%Name="
    private static Dictionary<string, string> AnswersByQuestion(IEnumerable<string> answers)
    {
        var result = new Dictionary<string, string>();
        foreach (var answer in answers)
        {
            var end = answer.StartsWith("%", StringComparison.Ordinal) ? answer.IndexOf('=') : answer.IndexOfAny(['~', '?']);
            if (end > 0)
                result.TryAdd(answer[..(end + 1)], answer[(end + 1)..]);
        }
        return result;
    }

    // A name ending in a dot is a namespace (see EffectCodeWriter.CSharpTypeExists)
    private static bool CSharpNameExists(Compilation compilation, string name)
    {
        if (name.IndexOf('@') is var at and >= 0)
        {
            // Two references with this class are two effects of one name: SDSL0302 says so when they meet, not that there is none
            var effect = name.Substring(at + 1);
            return compilation.GetTypesByMetadataName(name.Substring(0, at)).Any(type => type.GetMembers("EffectName").OfType<IFieldSymbol>().Any(x => x.ConstantValue as string == effect));
        }

        if (name.EndsWith("!", StringComparison.Ordinal))
            return compilation.GetTypeByMetadataName(name.TrimEnd('!')) is { } type
                && type.DeclaringSyntaxReferences.Any(x => x.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax declaration
                    && !declaration.Modifiers.Any(modifier => modifier.Text == "partial"));

        if (name.EndsWith("#", StringComparison.Ordinal))
        {
            // The key class of a shader with no namespace is in the default namespace of the generated code: the constant says whose it is
            var keys = name.TrimEnd('#');
            var fullName = keys.Substring(0, keys.Length - "Keys".Length);
            return compilation.GetTypeByMetadataName(keys)?.GetMembers().OfType<IFieldSymbol>().Any(x => x.Name is "ShaderName" or "EffectName" && x.ConstantValue as string == fullName) == true;
        }

        if (!name.EndsWith(".", StringComparison.Ordinal))
            return compilation.GetTypeByMetadataName(name) != null;

        INamespaceSymbol? current = compilation.GlobalNamespace;
        foreach (var part in name.TrimEnd('.').Split('.'))
            current = current?.GetMembers(part).OfType<INamespaceSymbol>().FirstOrDefault();
        return current != null;
    }

    private static bool HasShaderClassDeclaration(Parsing.ShaderFile ast)
    {
        foreach (var d in ast.RootDeclarations)
            if (d is Parsing.SDSL.AST.ShaderClass)
                return true;
        foreach (var ns in ast.Namespaces)
            foreach (var d in ns.Declarations)
                if (d is Parsing.SDSL.AST.ShaderClass)
                    return true;
        return false;
    }

    public static string GetSafeHintName(string absolutePath)
    {
        // 1. Get the file name without extension (e.g., "MyConfig")
        string fileName = Path.GetFileName(absolutePath);

        // 2. Sanitize: Replace characters that are invalid in file names
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitizedName = new string(fileName
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());

        // 3. Create a deterministic hash of the full path to avoid collisions
        // for files with the same name in different directories.
        byte[] hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(absolutePath));
        string hash = Convert.ToHexString(hashBytes.AsSpan(0, 4));

        // 4. Combine into a stable hint name (e.g., "MyConfig_A1B2C3D4.g.cs")
        return $"{sanitizedName}_{hash}.g.cs";
    }
}
