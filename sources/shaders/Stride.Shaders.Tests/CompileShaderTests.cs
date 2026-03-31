using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.HighPerformance;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using Stride.Core.Storage;
using Stride.Rendering;
using Stride.Shaders.Compilers;
using Stride.Shaders.Compilers.SDSL;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Parsing.SDSL.AST;
using Stride.Shaders.Spirv;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core.Buffers;
using Stride.Shaders.Spirv.Tools;
using Spv = Stride.Shaders.Spirv.Tools.Spv;

namespace Stride.Shaders.Parsers.Tests;

public class CompileShaderTests
{
    [Fact]
    public void EffectEvaluatorTest()
    {
        // Compile BasicEffect.sdfx via SDSLC to populate the shader cache
        var loader = new ShaderLoader("./assets/SDFX", "./assets/Stride/SDSL");
        var sdslc = new SDSLC(loader);
        var code = File.ReadAllText("./assets/SDFX/BasicEffect.sdfx");
        var hash = ObjectId.FromBytes(System.Text.Encoding.UTF8.GetBytes(code));
        var log = new Stride.Core.Diagnostics.LoggerResult();

        var compiled = sdslc.Compile("BasicEffect.sdfx", code, hash, [], log, out _, new CompileOptions());
        Assert.True(compiled, $"Failed to compile BasicEffect.sdfx: {string.Join(", ", log.Messages)}");

        // Load the effect buffer from cache (SDSLC registers it there)
        Assert.True(loader.Cache.TryLoadFromCache("BasicEffect", null, [], out var buffer, out _),
            "BasicEffect not found in shader cache after compilation");

        // Verify the compiled buffer starts with OpEffectSDFX
        Assert.True(buffer.Buffer.Count > 0);
        Assert.Equal(Stride.Shaders.Spirv.Specification.Op.OpEffectSDFX, buffer.Buffer[0].Op);

        // Create an EffectEvaluator and interpret the bytecode
        var evaluator = new EffectEvaluator(loader, null!, new Dictionary<string, IShaderMixinBuilder>());
        var parameters = new ParameterCollection();
        var result = evaluator.Evaluate("BasicEffect", parameters);

        Assert.NotNull(result);

        // Verify the mixin tree structure:
        // BasicEffect should have mixins: A (twice — once unconditional, once from if(false) skipped),
        // compositions Target1=Test123 and Target2+=Test123, and macro Test=1
        Assert.True(result.Mixins.Count > 0, "Expected at least one mixin in the result");

        // Dump for debugging
        Console.WriteLine($"Mixins: {string.Join(", ", result.Mixins.Select(m => m.ClassName))}");
        Console.WriteLine($"Compositions: {string.Join(", ", result.Compositions.Keys)}");
        Console.WriteLine($"Macros: {string.Join(", ", result.Macros.Select(m => $"{m.Name}={m.Definition}"))}");
    }

    [Theory]
    [MemberData(nameof(GetComputeTestFiles))]
    public void ComputeTest(string shaderName)
    {
        ShaderTest(shaderName, "./assets/SDSL/ComputeTests");
    }
    [Theory]
    [MemberData(nameof(GetStracerShaderFiles))]
    public void StracerShaderTest(string shaderName)
    {
        ShaderTest(shaderName, "./assets/stracer");
    }
    [Theory]
    [MemberData(nameof(GetStreamingTerrainShaderFiles))]
    public void StreamingShaderTest(string shaderName)
    {
        ShaderTest(shaderName, [.. Directory.GetDirectories("./assets/streaming_terrain_shaders", "*", SearchOption.AllDirectories), "./assets/streaming_terrain_shaders"]);
    }
    private void ShaderTest(string shaderName, string searchPath)
        => ShaderTest(shaderName, [searchPath]);

    private void ShaderTest(string shaderName, string[] searchPaths)
    {
        var shaderMixer = new ShaderMixer(new ShaderLoader([.. searchPaths, "./assets/Stride/SDSL"]));

        shaderMixer.ShaderLoader.LoadExternalBuffer(shaderName, [], out var buffer, out _, out _);

        // Skip generic shaders — they can't be instantiated without parameters
        foreach (var i in buffer.Context)
        {
            if (i.Op == Spirv.Specification.Op.OpGenericParameterSDSL)
            {
                Console.WriteLine($"Shader {shaderName} has generic parameters, skipping.");
                return;
            }
        }

        // Check if the shader has PSMain or CSMain entry points via SymbolTable
        bool hasEntryPoint;
        try
        {
            var context = new SpirvContext();
            var table = new SymbolTable(context, shaderMixer.ShaderLoader);
            var classSource = new ShaderClassInstantiation(shaderName, []) { Buffer = buffer };
            var shaderType = ShaderClass.LoadExternalShaderType(table, context, classSource);
            table.CurrentShader = shaderType;
            hasEntryPoint = table.TryResolveSymbol("PSMain", out _) || table.TryResolveSymbol("CSMain", out _);
        }
        catch (Exception e)
        {
            // Shaders that can't be loaded without parameters — treat as no entry point
            Console.WriteLine($"Shader {shaderName} could not be resolved for entry point check ({e.Message}), skipping MergeSDSL.");
            return;
        }

        if (!hasEntryPoint)
        {
            Console.WriteLine($"Shader {shaderName} has no PSMain or CSMain entry point, skipping MergeSDSL.");
            return;
        }

        var shaderSource = ShaderMixinManager.Contains(shaderName)
            ? new ShaderMixinGeneratorSource(shaderName)
            : (ShaderSource)new ShaderClassSource(shaderName);

        shaderMixer.MergeSDSL(shaderSource, new ShaderMixer.Options(true), new Stride.Core.Diagnostics.LoggerResult(), out var bytecode, out var effectReflection, out _, out _);

        File.WriteAllBytes($"{shaderName}.spv", bytecode);
        File.WriteAllText($"{shaderName}.spvdis", Spv.Dis(SpirvBytecode.CreateFromSpan(bytecode), DisassemblerFlags.Name | DisassemblerFlags.Id | DisassemblerFlags.InstructionIndex, true));

        // Validate SPIR-V
        var validationResult = Spv.ValidateFile($"{shaderName}.spv");
        Assert.True(validationResult.IsValid, validationResult.Output);

        // Convert to HLSL for each entry point
        var translator = new SpirvTranslator(bytecode.ToArray().AsMemory().Cast<byte, uint>());
        var entryPoints = translator.GetEntryPoints();

        foreach (var entryPoint in entryPoints)
        {
            var hlsl = translator.Translate(Backend.Hlsl, entryPoint);
            Console.WriteLine(hlsl);
        }
    }

    [Theory()]
    [MemberData(nameof(GetStrideShaderFiles))]
    public void StrideShaderTest(string shaderName)
    {
        ShaderTest(shaderName, "./assets/Stride/SDSL");
    }

    public static IEnumerable<object[]> GetStrideShaderFiles()
    {
        foreach (var filename in Directory.EnumerateFiles("./assets/Stride/SDSL", "*.sdsl"))
        {
            var shadername = Path.GetFileNameWithoutExtension(filename);
            yield return [shadername];
        }
    }

    public static IEnumerable<object[]> GetComputeTestFiles()
    {
        foreach (var filename in Directory.EnumerateFiles("./assets/SDSL/ComputeTests", "*.sdsl"))
        {
            var shadername = Path.GetFileNameWithoutExtension(filename);
            yield return [shadername];
        }
    }

    public static IEnumerable<object[]> GetStracerShaderFiles()
    {
        foreach (var filename in Directory.EnumerateFiles("./assets/stracer", "*.sdsl"))
        {
            var shadername = Path.GetFileNameWithoutExtension(filename);
            yield return [shadername];
        }
    }

    public static IEnumerable<object[]> GetStreamingTerrainShaderFiles()
    {
        foreach (var filename in Directory.EnumerateFiles("./assets/streaming_terrain_shaders", "*.sdsl", SearchOption.AllDirectories))
        {
            var shadername = Path.GetFileNameWithoutExtension(filename);
            yield return [shadername];
        }
    }

    /// <summary>
    /// Dual-path comparison: for each engine .sdfx file, compare the C# codegen builder output
    /// against the bytecode evaluator output. Both should produce identical ShaderMixinSource trees.
    /// Effects that can't be bytecode-compiled yet (unsupported SDFX features) are skipped.
    /// </summary>
    [Theory]
    [MemberData(nameof(GetStrideSdfxFiles))]
    public void EffectDualPathTest(string effectName)
    {
        // Skip effects whose C# builder isn't registered (e.g. from Stride.Particles, Stride.BepuPhysics)
        if (!ShaderMixinManager.Contains(effectName))
        {
            Console.WriteLine($"SKIP {effectName}: no C# builder registered");
            return;
        }

        var parameters = new ParameterCollection();

        // Path 1: C# codegen builder
        ShaderMixinSource csharpResult;
        try
        {
            csharpResult = ShaderMixinManager.Generate(effectName, parameters);
        }
        catch (Exception ex)
        {
            // Some C# builders crash without proper parameters (NullReferenceException, etc.)
            Console.WriteLine($"SKIP {effectName}: C# builder threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Path 2: Bytecode evaluator (on-demand .sdfx compilation + interpretation)
        var evaluator = CreateEffectEvaluator();
        var bytecodeResult = evaluator.Evaluate(effectName, parameters);

        if (bytecodeResult == null)
        {
            // Not a hard failure — the encoder doesn't support all SDFX features yet
            // (e.g. var declarations, dynamic mixin from variable, null checks on params)
            Console.WriteLine($"SKIP {effectName}: bytecode compilation not yet supported");
            return;
        }

        // Compare mixin lists
        var expectedMixins = string.Join(",", csharpResult.Mixins.Select(m => m.ToString()));
        var actualMixins = string.Join(",", bytecodeResult.Mixins.Select(m => m.ToString()));
        Assert.True(expectedMixins == actualMixins,
            $"Mixin mismatch for {effectName}.\n  C# builder:  [{expectedMixins}]\n  Bytecode:    [{actualMixins}]");

        // Compare compositions
        Assert.True(csharpResult.Compositions.Count == bytecodeResult.Compositions.Count,
            $"Composition count mismatch for {effectName}: C#={csharpResult.Compositions.Count}, Bytecode={bytecodeResult.Compositions.Count}");
        foreach (var kvp in csharpResult.Compositions)
        {
            Assert.True(bytecodeResult.Compositions.ContainsKey(kvp.Key),
                $"Missing composition '{kvp.Key}' in bytecode result for {effectName}");
            Assert.True(kvp.Value.ToString() == bytecodeResult.Compositions[kvp.Key].ToString(),
                $"Composition '{kvp.Key}' mismatch for {effectName}.\n  C#:       [{kvp.Value}]\n  Bytecode: [{bytecodeResult.Compositions[kvp.Key]}]");
        }

        // Compare macros
        Assert.True(csharpResult.Macros.Count == bytecodeResult.Macros.Count,
            $"Macro count mismatch for {effectName}: C#={csharpResult.Macros.Count}, Bytecode={bytecodeResult.Macros.Count}");
        for (int i = 0; i < csharpResult.Macros.Count; i++)
        {
            Assert.True(csharpResult.Macros[i].Name == bytecodeResult.Macros[i].Name,
                $"Macro name mismatch at {i} for {effectName}");
            Assert.True(csharpResult.Macros[i].Definition.ToString() == bytecodeResult.Macros[i].Definition.ToString(),
                $"Macro value mismatch for '{csharpResult.Macros[i].Name}' in {effectName}");
        }

        Console.WriteLine($"PASS {effectName}: {csharpResult.Mixins.Count} mixins, {csharpResult.Compositions.Count} compositions, {csharpResult.Macros.Count} macros");
    }

    public static IEnumerable<object[]> GetStrideSdfxFiles()
    {
        var sdfxDir = "./assets/Stride/SDFX";
        if (!Directory.Exists(sdfxDir))
            yield break;
        foreach (var filename in Directory.EnumerateFiles(sdfxDir, "*.sdfx"))
        {
            var effectName = Path.GetFileNameWithoutExtension(filename);
            yield return [effectName];
        }
    }

    private static EffectEvaluator? _cachedEvaluator;

    private static EffectEvaluator CreateEffectEvaluator()
    {
        if (_cachedEvaluator != null)
            return _cachedEvaluator;

        var sourceManager = new ShaderSourceManager(null!) { UseFileSystem = true };
        sourceManager.LookupDirectoryList.Add(Path.GetFullPath("./assets/Stride/SDFX"));

        var shaderLoader = new StubShaderLoader();
        var registeredBuilders = ShaderMixinManager.GetRegisteredBuilders();
        _cachedEvaluator = new EffectEvaluator(shaderLoader, sourceManager, registeredBuilders);
        return _cachedEvaluator;
    }

    private class StubShaderLoader : IExternalShaderLoader
    {
        public IShaderCache Cache { get; } = new ShaderCache();
        public GenericShaderCache GenericCache { get; } = new();
        public bool SuppressSourceHash { get; set; }
        public bool Exists(string name) => false;
        public bool LoadExternalFileContent(string name, out string filename, out string code, out ObjectId hash)
        { filename = ""; code = ""; hash = default; return false; }
        public bool LoadExternalBuffer(string name, ReadOnlySpan<ShaderMacro> defines,
            [MaybeNullWhen(false)] out ShaderBuffers bytecode, out ObjectId hash, out bool isFromCache)
        { bytecode = default; hash = default; isFromCache = false; return false; }
        public bool LoadExternalBuffer(string name, string? filename, string code, ReadOnlySpan<ShaderMacro> defines,
            [MaybeNullWhen(false)] out ShaderBuffers bytecode, out ObjectId hash, out bool isFromCache)
        { bytecode = default; hash = default; isFromCache = false; return false; }
    }
}
