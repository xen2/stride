using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
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
        // the shaders the other files write. The answer is a string, so a C# change regenerates the code only when the answer changes.
        var firstPass = shaderFiles.Combine(implicitUsings).Select((x, _) =>
        {
            var (_, candidates, declared) = Generate(x.Left, null, x.Right);
            return (Candidates: string.Join("\n", candidates), Declared: string.Join("\n", declared));
        });
        var existingTypes = firstPass.Collect().Combine(context.CompilationProvider).Select((x, _) => string.Join("\n", x.Left
            .SelectMany(names => names.Candidates.Split('\n'))
            .Where(name => name.Length > 0 && CSharpNameExists(x.Right, name))
            .Concat(x.Left.SelectMany(names => names.Declared.Split('\n')).Where(name => name.Length > 0).Select(name => $"{name}Keys#"))
            .Concat(x.Left.SelectMany(names => names.Declared.Split('\n')).Where(name => name.Length > 0).GroupBy(name => name).Where(names => names.Count() > 1).Select(names => $"{names.Key}Keys*"))
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)));

        context.RegisterSourceOutput(shaderFiles.Combine(implicitUsings).Combine(existingTypes), (output, x) =>
        {
            var existing = new HashSet<string>(x.Right.Split('\n'));
            output.AddSource(GetSafeHintName(x.Left.Left.Path), Generate(x.Left.Left, existing.Contains, x.Left.Right).Text);
        });
    }

    // A constant, so that the compiler and this generator have one list. Empty for a project that doesn't reference Stride.Rendering.
    private static string ImplicitUsingsOf(Compilation compilation)
        => compilation.GetTypeByMetadataName("Stride.Rendering.ShaderImplicitUsings")?.GetMembers("Namespaces").OfType<IFieldSymbol>().FirstOrDefault()?.ConstantValue as string ?? "";

    /// <summary>
    /// Generates the keys and effects of a shader file.
    /// </summary>
    /// <param name="csharpTypeExists">Whether a C# name exists (see <see cref="EffectCodeWriter.CSharpTypeExists"/>).</param>
    /// <param name="implicitUsings">The implicit usings, separated by commas.</param>
    /// <returns>The code, the C# names it depends on, and the shaders it writes.</returns>
    public static (string Text, IEnumerable<string> Candidates, IEnumerable<string> Declared) Generate(AdditionalText file, Func<string, bool>? csharpTypeExists, string implicitUsings)
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
                return (sb.ToString(), [], []);
            }
            else if (parsed.AST == null)
            {
                return ("#error No AST parsed", [], []);
            }

            // .sdfx files must declare 'effect', not 'shader' — catch the mistake here
            // instead of silently emitting unbuildable C#.
            if (isSdfx && parsed.AST is Parsing.ShaderFile sdfxFile && HasShaderClassDeclaration(sdfxFile))
                return ($"#error '{Path.GetFileName(file.Path)}' contains a 'shader' declaration. Use 'effect' (or 'partial effect') inside .sdfx files; 'shader' / 'partial shader' belongs in .sdsl.", [], []);

            var effectCodeWriter = new EffectCodeWriter
            {
                CSharpTypeExists = csharpTypeExists,
                ImplicitUsings = implicitUsings.Split(',', StringSplitOptions.RemoveEmptyEntries),
            };
            effectCodeWriter.Run(parsed.AST);
            return (effectCodeWriter.Text, effectCodeWriter.CSharpTypeCandidates, effectCodeWriter.DeclaredShaders);
        }
        catch (ShaderPreprocessorException e)
        {
            return ($"#error {e.Message}", [], []);
        }
        catch (Exception e)
        {
            return ($"#error Exception while running {nameof(EffectGenerator)} {e}", [], []);
        }
    }

    // A name ending in a dot is a namespace (see EffectCodeWriter.CSharpTypeExists)
    private static bool CSharpNameExists(Compilation compilation, string name)
    {
        if (name.EndsWith("!", StringComparison.Ordinal))
            return compilation.GetTypeByMetadataName(name.TrimEnd('!')) is { } type
                && type.DeclaringSyntaxReferences.Any(x => x.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax declaration
                    && !declaration.Modifiers.Any(modifier => modifier.Text == "partial"));

        if (name.EndsWith("#", StringComparison.Ordinal))
            return compilation.GetTypeByMetadataName(name.TrimEnd('#'))?.GetMembers("ShaderName").Any() == true;

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
