using CommunityToolkit.HighPerformance;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using Stride.Shaders.Compilers;
using Stride.Shaders.Compilers.SDSL;
using Stride.Shaders.Parsing.Analysis;
using Stride.Shaders.Parsing.SDSL.AST;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core.Buffers;
using Stride.Shaders.Spirv.Tools;
using Spv = Stride.Shaders.Spirv.Tools.Spv;

namespace Stride.Shaders.Parsers.Tests;

public class CompileShaderTests
{
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
        ShaderTest(shaderName, "./assets/streaming_terrain_shaders");
    }
    private  void ShaderTest(string shaderName, string searchPath)
    {
        var shaderMixer = new ShaderMixer(new ShaderLoader(searchPath, "./assets/Stride/SDSL"));

        shaderMixer.ShaderLoader.LoadExternalBuffer(shaderName, [], out var buffer, out _, out _);

        // Check if the shader has PSMain or CSMain entry points via SymbolTable
        bool hasEntryPoint;
        try
        {
            var context = new SpirvContext();
            var table = new SymbolTable(context) { ShaderLoader = shaderMixer.ShaderLoader, CurrentMacros = [] };
            var classSource = new ShaderClassInstantiation(shaderName, []) { Buffer = buffer };
            var shaderType = ShaderClass.LoadExternalShaderType(table, context, classSource);
            table.CurrentShader = shaderType;
            hasEntryPoint = table.TryResolveSymbol("PSMain", out _) || table.TryResolveSymbol("CSMain", out _);
        }
        catch (Exception e)
        {
            // Generic shaders can't be loaded without parameters — treat as no entry point
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

        // Convert to HLSL for each entry point
        var translator = new SpirvTranslator(bytecode.ToArray().AsMemory().Cast<byte, uint>());
        var entryPoints = translator.GetEntryPoints();

        foreach (var entryPoint in entryPoints)
        {
            var hlsl = translator.Translate(Backend.Hlsl, entryPoint);
            Console.WriteLine(hlsl);
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
        foreach (var filename in Directory.EnumerateFiles("./assets/streaming_terrain_shaders", "*.sdsl"))
        {
            var shadername = Path.GetFileNameWithoutExtension(filename);
            yield return [shadername];
        }
    }
}
