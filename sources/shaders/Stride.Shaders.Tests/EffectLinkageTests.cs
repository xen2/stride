// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Stride.Core.IO;
using Stride.Core.Storage;
using Stride.Rendering;
using Stride.Shaders.Parsing;
using Stride.Shaders.Parsing.SDFX;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Linkage rules of an effect: what the generator writes for them, and the layers of an <c>extend effect</c>.
/// </summary>
public class EffectLinkageTests
{
    private static string Generate(string effect)
    {
        var parsed = SDSLParser.Parse($"namespace Test {{ {effect} }}");
        Assert.Empty(parsed.Errors);
        var writer = new EffectCodeWriter();
        Assert.True(writer.Run(parsed.AST!), writer.Text);
        return writer.Text;
    }

    // An error of the parser, or the #error lines the writer gives
    private static string Errors(string file)
    {
        var parsed = SDSLParser.Parse(file);
        if (parsed.Errors.Count > 0)
            return string.Join("\n", parsed.Errors.Select(x => x.Message));
        var writer = new EffectCodeWriter();
        Assert.False(writer.Run(parsed.AST!), writer.Text);
        return writer.Text;
    }

    [Theory]
    [InlineData("extend shader Owner { [MixinOrder(X, Y)] Run; }", "context.ExtendShader(\"Owner\", \"effect MyEffect, line 1\").Method(\"Run\").MixinOrder(\"X\", \"Y\");")]
    [InlineData("extend shader Owner { [Unbind(Phases = { A, B }, Mixins = { X })] Run; }", ".Method(\"Run\").UnbindPhases(\"A\", \"B\");")]
    [InlineData("extend shader Owner { [Unbind(Phases = { A, B }, Mixins = { X })] Run; }", ".Method(\"Run\").UnbindMixins(\"X\");")]
    [InlineData("extend shader Owner { [Unbind(Mixins = { X })] Run; }", ".Method(\"Run\").UnbindMixins(\"X\");")]
    [InlineData("extend shader X { [Phase(A)] Owner.Run; }", "context.ExtendShader(\"X\", \"effect MyEffect, line 1\").Method(\"Owner.Run\").Phase(\"A\", after: null, before: null);")]
    [InlineData("extend shader X { [Phase(Lib.Wind, After = A, Before = B)] Owner.Run; }", ".Method(\"Owner.Run\").Phase(\"Lib.Wind\", after: \"A\", before: \"B\");")]
    [InlineData("extend shader X { [MixinOrder(After = A, Before = { B, C })] Owner.Run; }", ".Method(\"Owner.Run\").MixinOrderAfter(\"A\");")]
    [InlineData("extend shader X { [MixinOrder(After = A, Before = { B, C })] Owner.Run; }", ".Method(\"Owner.Run\").MixinOrderBefore(\"B\", \"C\");")]
    [InlineData("extend shader Some.Space.X { [Phase(A)] [MixinOrder(After = Some.Y)] Some.Owner.Run; }", "context.ExtendShader(\"Some.Space.X\", \"effect MyEffect, line 1\").Method(\"Some.Owner.Run\").MixinOrderAfter(\"Some.Y\");")]
    [InlineData("extend shader Owner { [MixinOrder(X, Y)] Run(float3, inout int); }", ".Method(\"Run(float3,int)\").MixinOrder(\"X\", \"Y\");")]
    [InlineData("extend shader Owner { [MixinOrder(X, Y)] Run(float3 position, inout int count); }", ".Method(\"Run(float3,int)\").MixinOrder(\"X\", \"Y\");")]
    public void ABlockIsWrittenAsCallsOfItsAttributes(string line, string expected)
    {
        Assert.Contains(expected, Generate($"effect MyEffect {{ {line} }}"));
    }

    [Fact]
    public void ABlockCanBeInACondition()
    {
        Assert.Contains("context.ExtendShader(\"Owner\"", Generate("params P { bool B; }; effect MyEffect { using params P; if (P.B) { mixin A; extend shader Owner { [Unbind(Phases = { A })] Run; } } }"));
    }

    // The words are not reserved
    [Theory]
    [InlineData("float order = 2;")]
    [InlineData("float phase = 2;")]
    [InlineData("float extend = 2;")]
    public void TheWordsCanStillNameAVariable(string line)
    {
        Assert.Contains("= 2", Generate($"effect MyEffect {{ {line} }}"));
    }

    [Theory]
    [InlineData("order Owner.Run.A: X, Y;", "SDSL0251: order Owner.Run.A: X, Y; is no longer part of the language. Write extend shader Owner { [MixinOrder(X, Y)] Run; }.")]
    [InlineData("order Some.Owner.Run.A: X;", "Write extend shader Some.Owner { [MixinOrder(X)] Run; }.")]
    [InlineData("bind Owner.Run.A = X;", "SDSL0251: bind Owner.Run.A = X; is no longer part of the language. Write extend shader X { [Phase(A)] Owner.Run; }.")]
    [InlineData("unbind Owner.Run.A;", "SDSL0251: unbind Owner.Run.A; is no longer part of the language. Write extend shader Owner { [Unbind(Phases = { A })] Run; }.")]
    [InlineData("unbind Owner.Run = X;", "SDSL0251: unbind Owner.Run = X; is no longer part of the language. Write extend shader Owner { [Unbind(Mixins = { X })] Run; }.")]
    [InlineData("phase Owner.Run.Wind after A before B;", "SDSL0251: phase Owner.Run.Wind after A before B; is no longer part of the language. Write [Phase(Vendor.Wind, After = A, Before = B)] on a class that joins it, or extend shader X { [Phase(Vendor.Wind, ...)] Owner.Run; }.")]
    public void ARemovedStatementNamesItsBlock(string line, string message)
    {
        Assert.Contains(message, Errors($"namespace Test {{ effect MyEffect {{ {line} }} }}"));
    }

    [Theory]
    [InlineData("extend shader X { [Drops(Mixins = { Y })] Owner.Run; }", "SDSL0256: extend shader X { ... Owner.Run; }: an effect takes contributors out with [Unbind] on the method")]
    [InlineData("extend shader X { [After(Y)] Owner.Run; }", "SDSL0256: extend shader X { ... Owner.Run; }: write [MixinOrder(After = X)] on the method the class contributes to")]
    [InlineData("extend shader X { [Color] Run; }", "SDSL0256: extend shader X { ... Run; }: a block takes [Phase], [MixinOrder] and [Unbind], and [Color] is none of them")]
    [InlineData("extend shader X { [Unbind(Phases = { A })] Owner.Run; }", "SDSL0256: extend shader X { ... Owner.Run; }: [Unbind] goes on the method itself: extend shader Owner { [Unbind(...)] Run; }")]
    [InlineData("extend shader X { [Phase(A)] Run; }", "SDSL0256: extend shader X { ... Run; }: [Phase] goes on a method the class contributes to, written Owner.Run")]
    [InlineData("extend shader X { [MixinOrder(After = A)] Run; }", "SDSL0135: [MixinOrder] on extend shader X { ... Run; }: on a method of the class, [MixinOrder] lists the classes in their running order")]
    [InlineData("extend shader X { [MixinOrder(A, B)] Owner.Run; }", "SDSL0135: [MixinOrder] on extend shader X { ... Owner.Run; }: on a method the class contributes to, [MixinOrder] takes After = and Before =")]
    [InlineData("extend shader X { [MixinOrder(A)] Run; }", "[MixinOrder] lists at least two classes")]
    [InlineData("extend shader X { [Unbind(A)] Run; }", "SDSL0254: [Unbind] on extend shader X: [Unbind] takes Mixins = { ... } for classes and Phases = { ... } for phases.")]
    [InlineData("extend shader X { [MixinOrder(\"A\", B)] Run; }", "SDSL0255: [MixinOrder] on extend shader X { ... Run; }: \"A\" is a string.")]
    [InlineData("extend shader X { [Unbind(Mixins = { \"A\" })] Run; }", "SDSL0255: [Unbind] on extend shader X { ... Run; }: \"A\" is a string.")]
    [InlineData("extend shader X { [Phase(Lib.Wind, After = \"A\")] Owner.Run; }", "SDSL0255: [Phase] on extend shader X { ... Owner.Run; }: \"A\" is a string.")]
    [InlineData("extend shader X { [Phase(Wind, After = A)] Owner.Run; }", "SDSL0252: phase Wind, added by extend shader X { ... Owner.Run; }, has a plain name.")]
    [InlineData("extend shader X { [Phase(Stride.Wind, After = A)] Owner.Run; }", "SDSL0253: phase Stride.Wind, added by extend shader X { ... Owner.Run; }: a name that starts with Stride. is kept for the engine.")]
    [InlineData("extend shader X<1> { [Phase(A)] Owner.Run; }", "SDSL0256: extend shader X: a block is about every generic version of a class: write the class without its generic values.")]
    [InlineData("extend shader X { Run }", "SDSL0256: extend shader X: a block holds methods with their attributes")]
    public void AWrongBlockIsAnError(string line, string message)
    {
        Assert.Contains(message, Errors($"namespace Test {{ effect MyEffect {{ {line} }} }}"));
    }

    // An engine class may add a Stride. phase, in a block too
    [Fact]
    public void AnEngineClassAddsAStridePhase()
    {
        Assert.Contains(".Phase(\"Stride.Wind\", after: \"A\", before: null);", Generate("effect MyEffect { extend shader Stride.Rendering.X { [Phase(Stride.Wind, After = A)] Owner.Run; } }"));
    }

    [Theory]
    [InlineData("namespace Test { extend shader X { [Phase(A)] Owner.Run; } }")]
    [InlineData("extend shader X { [Phase(A)] Owner.Run; }")]
    public void ABlockOutsideAnEffectIsAnError(string file)
    {
        Assert.Contains("SDSL0256: extend shader ...: a block goes inside an effect, and is about that effect.", Errors(file));
    }

    [Fact]
    public void AnExtendIsRegisteredAsALayer()
    {
        Assert.Contains("ShaderMixinManager.RegisterLayer(\"Test.MyEffect\"", Generate("extend effect MyEffect { mixin A; }"));
        Assert.Contains("ShaderMixinManager.Register(\"Test.MyEffect\"", Generate("effect MyEffect { mixin A; }"));
    }

    // The project of the effect has the key class: a second one would hide it
    [Fact]
    public void AnExtendWritesNoKeyClass()
    {
        Assert.DoesNotContain("class MyEffectKeys", Generate("extend effect MyEffect { mixin A; }"));
        Assert.Contains("class MyEffectKeys", Generate("effect MyEffect { mixin A; }"));
    }

    [Theory]
    [InlineData("partial effect MyEffect { mixin A; }")]
    [InlineData("namespace Test { partial effect MyEffect { mixin A; } }")]
    [InlineData("namespace Test; partial effect MyEffect { mixin A; }")]
    public void APartialEffectIsAnError(string file)
    {
        Assert.Contains(SDSLParser.Parse(file).Errors, x => x.ToString().Contains("SDSL0311"));
    }

    [Fact]
    public void ReadingAFileTellsALayerFromAnEffect()
    {
        Assert.Equal([new("A", "B", true, true), new("A", "C", true, false)], ShaderFileDeclarations.Read("namespace A; extend /* comment */ effect B { }; effect C { };"));
    }

    private sealed class Base : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSource mixin, ShaderMixinContext context) => mixin.Mixins.Add(new ShaderClassSource("Base"));
    }

    private sealed class OtherBase : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSource mixin, ShaderMixinContext context) => mixin.Mixins.Add(new ShaderClassSource("OtherBase"));
    }

    private sealed class Layer : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSource mixin, ShaderMixinContext context) => mixin.Mixins.Add(new ShaderClassSource("Layer"));
    }

    private static string Unique(string name) => $"LinkageTests{Guid.NewGuid():N}.{name}";

    private static string[] Classes(string effect) => ShaderMixinManager.Generate(effect, new ParameterCollection()).Root.Mixins.Select(x => x.ClassName).ToArray();

    [Fact]
    public void ALayerAddsToTheEffect()
    {
        var name = Unique("Effect");
        ShaderMixinManager.Register(name, new Base());
        ShaderMixinManager.RegisterLayer(name, new Layer());

        Assert.Equal(["Base", "Layer"], Classes(name));
    }

    [Fact]
    public void ALayerRegisteredBeforeTheEffectComesAfterIt()
    {
        var name = Unique("Effect");
        ShaderMixinManager.RegisterLayer(name, new Layer());
        ShaderMixinManager.Register(name, new Base());

        Assert.Equal(["Base", "Layer"], Classes(name));
    }

    // The build found the effect, but its assembly didn't load
    [Fact]
    public void ALayerWithNoEffectIsAnErrorWhenItIsAskedFor()
    {
        var name = Unique("Effect");
        ShaderMixinManager.RegisterLayer(name, new Layer());

        var error = Assert.Throws<InvalidOperationException>(() => Classes(name));
        Assert.Equal(string.Format(SDSLErrorMessages.SDSL0310, name), error.Message);
    }

    // The manager can't see the class of the other messages
    [Fact]
    public void TheManagerAndTheGeneratorGiveOneMessage()
    {
        Assert.Equal(SDSLErrorMessages.SDSL0310, ShaderMixinManager.LayerWithoutEffectMessage);
    }

    // An assembly that reloads registers the same type again
    [Fact]
    public void AReloadedLayerReplacesItself()
    {
        var name = Unique("Effect");
        ShaderMixinManager.Register(name, new Base());
        ShaderMixinManager.RegisterLayer(name, new Layer());
        ShaderMixinManager.RegisterLayer(name, new Layer());
        ShaderMixinManager.Register(name, new Base());

        Assert.Equal(["Base", "Layer"], Classes(name));
    }

    [Fact]
    public void TwoEffectsOfOneNameStayAnErrorWithLayers()
    {
        var name = Unique("Effect");
        ShaderMixinManager.Register(name, new Base());
        ShaderMixinManager.RegisterLayer(name, new Layer());

        var error = Assert.Throws<InvalidOperationException>(() => ShaderMixinManager.Register(name, new OtherBase()));
        Assert.Contains("SDSL0302", error.Message);
    }

    // A layer in an assembly of its own, which references the given ones in its metadata without using them when it runs
    private static IShaderMixinBuilder CompileLayer(string name, params (string Name, byte[] Image)[] references)
    {
        var usage = string.Concat(references.Select(x => $"new {x.Name}.Layer(); "));
        var source = $$"""
            namespace {{name}};
            public class Layer : Stride.Shaders.IShaderMixinBuilder
            {
                public void Generate(Stride.Shaders.ShaderMixinSource mixin, Stride.Shaders.ShaderMixinContext context) => mixin.Mixins.Add(new Stride.Shaders.ShaderClassSource("{{name}}"));
                static void Unused() { {{usage}} }
            }
            """;
        var image = CompileImage(name, source, references);
        return (IShaderMixinBuilder)Assembly.Load(image).CreateInstance($"{name}.Layer")!;
    }

    private static byte[] CompileImage(string name, string source, (string Name, byte[] Image)[] references)
    {
        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Where(x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(x => MetadataReference.CreateFromFile(x));
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            framework.Concat<MetadataReference>(references.Select(x => MetadataReference.CreateFromImage(x.Image))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }

    private static (string Name, byte[] Image) Compiled(string name, params (string Name, byte[] Image)[] references)
    {
        var usage = string.Concat(references.Select(x => $"new {x.Name}.Layer(); "));
        return (name, CompileImage(name, $"namespace {name}; public class Layer {{ static void Unused() {{ {usage} }} }}", references));
    }

    // AGame references both plugins, and PluginB references PluginA: the name of AGame would put it first
    [Fact]
    public void AnAssemblyComesAfterTheLayersItReferences()
    {
        var name = Unique("Effect");
        var id = Guid.NewGuid().ToString("N");
        string pluginA = $"PluginA{id}", pluginB = $"PluginB{id}", game = $"AGame{id}";
        var compiledA = Compiled(pluginA);
        var compiledB = Compiled(pluginB, compiledA);

        ShaderMixinManager.Register(name, new Base());
        ShaderMixinManager.RegisterLayer(name, CompileLayer(game, compiledA, compiledB));
        ShaderMixinManager.RegisterLayer(name, CompileLayer(pluginB, compiledA));
        ShaderMixinManager.RegisterLayer(name, CompileLayer(pluginA));

        Assert.Equal(["Base", pluginA, pluginB, game], Classes(name));
    }

    private sealed class EffectFile(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    // A project as a real build gives it: the generator runs on its effect, and it can see another project through a reference assembly
    private static (byte[] Image, byte[] ReferenceAssembly, string Generated) CompileProject(string name, string effect, string code, params (string Name, byte[] Image)[] references)
    {
        var output = RunGenerator(name, [effect], code, references);

        using var image = new MemoryStream();
        using var referenceAssembly = new MemoryStream();
        var result = output.Emit(image, metadataPEStream: referenceAssembly);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return (image.ToArray(), referenceAssembly.ToArray(), output.SyntaxTrees.Last().ToString());
    }

    private static Compilation RunGenerator(string name, string[] effects, string code, params (string Name, byte[] Image)[] references)
    {
        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Where(x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(x => MetadataReference.CreateFromFile(x));
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(code)],
            framework.Concat<MetadataReference>(references.Select(x => MetadataReference.CreateFromImage(x.Image))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        CSharpGeneratorDriver.Create([new EffectGenerator().AsSourceGenerator()], additionalTexts: [.. effects.Select((x, i) => new EffectFile($"{name}{i}.sdfx", x))])
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return output;
    }

    private static string BuildErrors(string[] effects, params (string Name, byte[] Image)[] references)
        => string.Join(Environment.NewLine, RunGenerator($"Project{Guid.NewGuid():N}", effects, "", references).GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error));

    // The project that declares the effect that the layers of a test add to. It registers when it loads.
    private static (string Name, byte[] Image, byte[] ReferenceAssembly) EffectProject(string id, string effects = null)
    {
        var name = $"Effects{id}";
        var compiled = CompileProject(name, $"namespace Layers{id} {{ {effects ?? "effect Layered { mixin Declared; }"} }}", "");
        RuntimeHelpers.RunModuleConstructor(Assembly.Load(compiled.Image).ManifestModule.ModuleHandle);
        return (name, compiled.Image, compiled.ReferenceAssembly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExtendFindsItsEffectInAReference(bool throughReferenceAssembly)
    {
        var id = Guid.NewGuid().ToString("N");
        var effect = EffectProject(id);

        Assert.Empty(BuildErrors([$"namespace Layers{id} {{ extend effect Layered {{ mixin A; }} }}"], (effect.Name, throughReferenceAssembly ? effect.ReferenceAssembly : effect.Image)));
    }

    // A builder written in C# has no generated key class: it can write one
    [Fact]
    public void AnExtendFindsAnEffectWrittenInCSharpThroughItsKeyClass()
    {
        var id = Guid.NewGuid().ToString("N");
        var keys = ($"Keys{id}", CompileImage($"Keys{id}", $"namespace Layers{id}; public class LayeredKeys {{ public const string EffectName = \"Layers{id}.Layered\"; }}", []));

        Assert.Empty(BuildErrors([$"namespace Layers{id} {{ extend effect Layered {{ mixin A; }} }}"], keys));
    }

    // Two effects of one name are SDSL0302, when they load and in the asset build, not an effect that doesn't exist
    [Fact]
    public void AnExtendFindsAnEffectThatTwoReferencesDeclare()
    {
        var id = Guid.NewGuid().ToString("N");
        var first = CompileProject($"First{id}", $"namespace Layers{id} {{ effect Layered {{ mixin A; }} }}", "");
        var second = CompileProject($"Second{id}", $"namespace Layers{id} {{ effect Layered {{ mixin B; }} }}", "");

        Assert.Empty(BuildErrors([$"namespace Layers{id} {{ extend effect Layered {{ mixin C; }} }}"], ($"First{id}", first.Image), ($"Second{id}", second.Image)));
    }

    // One line, as anything after it in the generated file would be compiled
    [Fact]
    public void AnErrorOfTheGeneratorIsOneLineWithItsPlace()
    {
        var id = Guid.NewGuid().ToString("N");
        var errors = BuildErrors([$"namespace Layers{id}\n{{\n    extend effect Misspelled {{ mixin A; }}\n}}"]);

        Assert.Contains($"SDSL0310: extend effect Layers{id}.Misspelled:", errors);
        Assert.Contains("(line 3, column 19)", errors);
        Assert.Single(errors.Split(Environment.NewLine));
    }

    // Its key class is in the default namespace of the generated code, and the full name has no namespace
    [Fact]
    public void AnExtendFindsAnEffectWithNoNamespace()
    {
        var id = Guid.NewGuid().ToString("N");
        var effect = CompileProject($"Effects{id}", $"effect Global{id} {{ mixin A; }}", "");

        Assert.Empty(BuildErrors([$"extend effect Global{id} {{ mixin B; }}"], ($"Effects{id}", effect.Image)));
    }

    // Nothing asks for an effect that doesn't exist, so a misspelled layer would otherwise do nothing, forever
    [Fact]
    public void AnExtendOfAnEffectThatDoesNotExistIsABuildError()
    {
        var id = Guid.NewGuid().ToString("N");
        var effect = EffectProject(id);

        Assert.Contains($"SDSL0310: extend effect Layers{id}.Misspelled:", BuildErrors([$"namespace Layers{id} {{ extend effect Misspelled {{ mixin A; }} }}"], (effect.Name, effect.Image)));
        Assert.Contains($"SDSL0310: extend effect Other{id}.Layered:", BuildErrors([$"namespace Other{id} {{ extend effect Layered {{ mixin A; }} }}"], (effect.Name, effect.Image)));
        Assert.Contains($"SDSL0310: extend effect Layers{id}.Layered:", BuildErrors([$"namespace Layers{id} {{ extend effect Layered {{ mixin A; }} }}"]));
    }

    // Their classes would have one name
    [Fact]
    public void AnExtendInAProjectWithThatEffectOrAnotherExtendIsABuildError()
    {
        var id = Guid.NewGuid().ToString("N");
        var effect = EffectProject(id);
        var layer = $"namespace Layers{id} {{ extend effect Layered {{ mixin A; }} }}";

        Assert.Contains("SDSL0312", BuildErrors([$"namespace Layers{id} {{ effect Own {{ mixin A; }} extend effect Own {{ mixin B; }} }}"]));
        Assert.Contains("SDSL0312", BuildErrors([$"namespace Layers{id} {{ effect Own {{ mixin A; }} }}", $"namespace Layers{id} {{ extend effect Own {{ mixin B; }} }}"]));
        Assert.Contains("SDSL0312", BuildErrors([layer, layer], (effect.Name, effect.Image)));
    }

    // A layer adds to the effect whichever child is asked for: the statements after a mixin child don't run for that child, but a layer does
    [Fact]
    public void ALayerReachesAMixinChild()
    {
        var id = Guid.NewGuid().ToString("N");
        var effect = EffectProject(id, "effect ChildEffect { mixin InChild; } effect Layered { mixin Declared; mixin child ChildEffect; mixin AfterChild; }");
        var plugin = CompileProject($"Plugin{id}", $"namespace Layers{id} {{ extend effect Layered {{ mixin Plugin; }} }}", "", (effect.Name, effect.Image));
        RuntimeHelpers.RunModuleConstructor(Assembly.Load(plugin.Image).ManifestModule.ModuleHandle);

        Assert.Equal(["Declared", "AfterChild", "Plugin"], Classes($"Layers{id}.Layered"));
        Assert.Equal(["Declared", "InChild", "Plugin"], Classes($"Layers{id}.Layered.ChildEffect"));
    }

    // AGame uses no type of ZPlugin, so its metadata has no reference to it, and its name would put it first
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AProjectComesAfterAPluginItUsesNoTypeOf(bool throughReferenceAssembly)
    {
        var id = Guid.NewGuid().ToString("N");
        string plugin = $"ZPlugin{id}", game = $"AGame{id}";
        var effect = EffectProject(id);
        var compiledPlugin = CompileProject(plugin, $"namespace Layers{id} {{ extend effect Layered {{ mixin {plugin}; }} }}", "", (effect.Name, effect.Image));
        var compiledGame = CompileProject(game, $"namespace Layers{id} {{ extend effect Layered {{ mixin {game}; }} }}", "", (effect.Name, effect.Image), (plugin, throughReferenceAssembly ? compiledPlugin.ReferenceAssembly : compiledPlugin.Image));

        var gameAssembly = Assembly.Load(compiledGame.Image);
        Assert.DoesNotContain(gameAssembly.GetReferencedAssemblies(), x => x.Name == plugin);

        // The game loads first
        RuntimeHelpers.RunModuleConstructor(gameAssembly.ManifestModule.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(Assembly.Load(compiledPlugin.Image).ManifestModule.ModuleHandle);

        Assert.Equal(["Declared", plugin, game], Classes($"Layers{id}.Layered"));
    }

    // As a game that only uses Stride.Engine, which has no layer, and gets Stride.Rendering as a transitive reference
    [Fact]
    public void AProjectComesAfterALayerItOnlyReachesThroughAnotherAssembly()
    {
        var id = Guid.NewGuid().ToString("N");
        string rendering = $"ZRendering{id}", engine = $"Engine{id}", game = $"AGame{id}";
        var effect = EffectProject(id);
        var compiledRendering = CompileProject(rendering, $"namespace Layers{id} {{ extend effect Layered {{ mixin {rendering}; }} }}", $"namespace {rendering}; public class Layer {{ }}", (effect.Name, effect.Image));
        var compiledEngine = Compiled(engine, (rendering, compiledRendering.Image));
        var compiledGame = CompileProject(game, $"namespace Layers{id} {{ extend effect Layered {{ mixin {game}; }} }}", $"class Uses {{ static void Unused() {{ new {engine}.Layer(); }} }}", compiledEngine, (effect.Name, effect.Image), (rendering, compiledRendering.Image));

        var gameAssembly = Assembly.Load(compiledGame.Image);
        Assert.Contains(gameAssembly.GetReferencedAssemblies(), x => x.Name == engine);
        Assert.DoesNotContain(gameAssembly.GetReferencedAssemblies(), x => x.Name == rendering);
        Assert.Contains($"\"{rendering}\"", compiledGame.Generated);
        Assert.DoesNotContain(engine, compiledGame.Generated);

        // The game loads first
        RuntimeHelpers.RunModuleConstructor(gameAssembly.ManifestModule.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(Assembly.Load(compiledRendering.Image).ManifestModule.ModuleHandle);

        Assert.Equal(["Declared", rendering, game], Classes($"Layers{id}.Layered"));
    }

    [Fact]
    public void ALayerWritesTheReferencesThatHoldALayerInOrder()
    {
        var id = Guid.NewGuid().ToString("N");
        string pluginA = $"PluginA{id}", pluginB = $"PluginB{id}", plain = $"Plain{id}";
        var effect = EffectProject(id);
        var compiledA = CompileProject(pluginA, $"namespace Layers{id} {{ extend effect Layered {{ mixin {pluginA}; }} }}", "", (effect.Name, effect.Image));
        var compiledB = CompileProject(pluginB, $"namespace Layers{id} {{ extend effect Layered {{ mixin {pluginB}; }} }}", "", (effect.Name, effect.Image));

        var game = CompileProject($"Game{id}", $"namespace Layers{id} {{ extend effect Layered {{ mixin Game; }} effect Whole {{ mixin Game; }} }}", "", (effect.Name, effect.Image), (pluginB, compiledB.Image), (pluginA, compiledA.Image), Compiled(plain));

        // The assemblies of this test run can hold layers too
        var written = game.Generated.Split('\n').Single(x => x.Contains("ShaderMixins.Layered()")).Split("Layered()")[1].Split('"').Where((_, i) => i % 2 == 1).ToList();
        Assert.Contains(pluginA, written);
        Assert.Contains(pluginB, written);
        Assert.Equal(written.Order(StringComparer.Ordinal), written);
        Assert.Contains("ShaderMixins.Whole());", game.Generated);
        Assert.DoesNotContain(plain, game.Generated);
        Assert.DoesNotContain(effect.Name, game.Generated);
    }

    private static ShaderMixinContext Context(EffectSource effect) => new(effect, new ParameterCollection(), []);

    [Fact]
    public void ABlockAtTheTopLevelGoesOnTheEffect()
    {
        var effect = new EffectSource(new ShaderMixinSource());
        var context = Context(effect);

        context.UnbindPhases("Owner", "Run", "A");
        context.PushComposition(effect.Root, "Slot", new ShaderMixinSource());
        context.PopComposition();
        context.ExtendShader("X").Method("Owner.Run").Phase("B");

        Assert.Equal(["extend shader Owner { [Unbind(Phases = { A })] Run; }", "extend shader X { [Phase(B)] Owner.Run; }"], effect.Linkage.Select(x => x.ToStatement()));
    }

    [Theory]
    [InlineData("MixinOrder", "extend shader Owner { [MixinOrder(X, Y)] Run; }")]
    [InlineData("Unbind", "extend shader Owner { [Unbind(Mixins = { X })] Run; }")]
    [InlineData("Phase", "extend shader X { [Phase(Lib.W, After = A)] Owner.Run; }")]
    [InlineData("Neighbours", "extend shader X { [MixinOrder(After = A)] Owner.Run; }")]
    public void ABlockInACompositionIsAnError(string attribute, string statement)
    {
        var effect = new EffectSource(new ShaderMixinSource());
        var context = Context(effect);
        var slot = new ShaderMixinSource();

        context.PushComposition(effect.Root, "Slot", slot);
        context.PushCompositionArray(slot, "Layers", new ShaderMixinSource());
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            switch (attribute)
            {
                case "MixinOrder": context.MixinOrder("Owner", "Run", "X", "Y"); break;
                case "Unbind": context.UnbindMixins("Owner", "Run", "X"); break;
                case "Phase": context.ExtendShader("X").Method("Owner.Run").Phase("Lib.W", after: "A"); break;
                default: context.ExtendShader("X").Method("Owner.Run").MixinOrderAfter("A"); break;
            }
        });

        Assert.StartsWith($"SDSL0151: the block {statement} is inside the composition Slot.Layers[0].", error.Message);
        Assert.Empty(effect.Linkage);
    }

    [Theory]
    [InlineData("Run", "MixinOrder", "lists at least two classes")]
    [InlineData("Run", "Unbind", "[Unbind(Phases = ...)] takes phases")]
    [InlineData("Run", "UnbindMixins", "[Unbind(Mixins = ...)] takes classes")]
    [InlineData("Owner.Run", "MixinOrder", "[MixinOrder(After = ...)] takes the classes it runs after")]
    [InlineData("Owner.Run", "Before", "[MixinOrder(Before = ...)] takes the classes it runs before")]
    [InlineData("Run", "Phase", "[Phase] goes on a method the class contributes to")]
    [InlineData("Run", "Neighbours", "[MixinOrder(After = A)] goes on a method the class contributes to")]
    [InlineData("Owner.Run", "List", "[MixinOrder(A, B)] lists classes on a method of the class")]
    [InlineData("Owner.Run", "Unbind", "[Unbind] goes on a method of the class")]
    public void AWrongCallIsAClearError(string method, string attribute, string message)
    {
        var effect = new EffectSource(new ShaderMixinSource());
        var member = effect.ExtendShader("X").Method(method);
        var error = Assert.Throws<ArgumentException>(() =>
        {
            switch (attribute)
            {
                case "MixinOrder" when method == "Run": member.MixinOrder("A"); break;
                case "MixinOrder": member.MixinOrderAfter(); break;
                case "Before": member.MixinOrderBefore(); break;
                case "List": member.MixinOrder("A", "B"); break;
                case "Unbind": member.UnbindPhases(); break;
                case "UnbindMixins": member.UnbindMixins(); break;
                case "Phase": member.Phase("A"); break;
                default: member.MixinOrderAfter("A"); break;
            }
        });

        Assert.Contains(message, error.Message);
        Assert.Empty(effect.Linkage);
    }

    [Fact]
    public void AMethodNamesItsOwnerAndItsOverload()
    {
        var effect = new EffectSource(new ShaderMixinSource());
        effect.ExtendShader("X").Method("Some.Owner.Run(float3, int)").Phase("A");

        var rule = Assert.Single(effect.Linkage);
        Assert.Equal(("X", "Some.Owner", "Run", "float3,int"), (rule.Shader, rule.Owner, rule.Method, rule.Parameters));
        Assert.Equal("extend shader X { [Phase(A)] Some.Owner.Run(float3,int); }", rule.ToStatement());
    }

    [Fact]
    public void ABlockOfAMixedEffectGoesOnTheEffect()
    {
        var effect = ShaderMixinManager.Generate(TestShader.FullName("EffectLinkageMixedPart"), new ParameterCollection());

        Assert.Equal([ShaderLinkageKind.MixinOrder, ShaderLinkageKind.Unbind], effect.Linkage.Select(x => x.Kind));
        Assert.All(effect.Linkage, x => Assert.Matches(@"^EffectLinkagePlaces\.sdfx, line \d+$", x.Source));
    }

    [Fact]
    public void ABlockOfAComposedEffectIsAnError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ShaderMixinManager.Generate(TestShader.FullName("EffectLinkageComposedPart"), new ParameterCollection()));

        Assert.StartsWith("SDSL0151: the block extend shader Owner { [Unbind(Phases = { Late })] Run; } is inside the composition Layer.", error.Message);
    }

    private sealed class Effect : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSource mixin, ShaderMixinContext context) => context.UnbindPhases("Owner", "Run", "A");
    }

    private sealed class FirstLayer : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSource mixin, ShaderMixinContext context) => context.UnbindPhases("Owner", "Run", "B");
    }

    // The effect is level 0, and each layer one more, in the order layers run
    [Fact]
    public void EachLayerIsOneLevelFurtherOut()
    {
        var name = Unique("Effect");
        ShaderMixinManager.Register(name, new Effect());
        ShaderMixinManager.RegisterLayer(name, new FirstLayer());

        var effect = ShaderMixinManager.Generate(name, new ParameterCollection());

        Assert.Equal([0, 1], effect.Linkage.Select(x => x.Level));
        Assert.EndsWith("(layer 1)", effect.Linkage[1].ToString());
    }

    // The compiler adds the platform macros to a copy of the effect. A block about a phase that doesn't exist only fails if it gets to the mixer.
    [Fact]
    public void TheCompilerGivesTheLinkageToTheMixer()
    {
        using var provider = new FileSystemProvider($"/linkage-{Guid.NewGuid():N}", AppContext.BaseDirectory);
        using var compiler = new Stride.Shaders.Compiler.EffectCompiler(provider);
        compiler.SourceDirectories.Add("assets/SDSL/CompilerTests");
        var effect = new EffectSource(new ShaderClassSource(TestShader.FullName("EffectLinkageOwner")));
        effect.UnbindPhases("EffectLinkageOwner", "Run", "Missing");

        var result = compiler.Compile(effect, Stride.Shaders.Compiler.EffectCompilerParameters.Default, new Stride.Shaders.Compiler.CompilerParameters(), ObjectId.New()).WaitForResult();

        Assert.Contains("SDSL0254", string.Join(Environment.NewLine, result.CompilationLog.Messages.Select(x => x.Text)));
    }

    // A compiled effect is cached by this hash, and the effect log keys an effect with it (EffectCompileCommand): two effects that differ
    // only by a block, or by the level of a block, are two effects. Where a block is written is not part of it.
    [Fact]
    public void TheBlocksArePartOfTheHashOfACompiledEffect()
    {
        var parameters = Stride.Shaders.Compiler.EffectCompilerParameters.Default;
        EffectSource Effect(Action<EffectSource> blocks)
        {
            var effect = new EffectSource(new ShaderClassSource("A"));
            blocks(effect);
            return effect;
        }

        var plain = ShaderMixinObjectId.Compute(Effect(_ => { }), parameters);
        var unbind = ShaderMixinObjectId.Compute(Effect(x => x.UnbindPhases("Owner", "Run", "A")), parameters);
        var otherUnbind = ShaderMixinObjectId.Compute(Effect(x => x.UnbindPhases("Owner", "Run", "B")), parameters);
        var order = ShaderMixinObjectId.Compute(Effect(x => x.MixinOrder("Owner", "Run", "X", "Y")), parameters);
        var otherOrder = ShaderMixinObjectId.Compute(Effect(x => x.MixinOrder("Owner", "Run", "Y", "X")), parameters);
        var outer = ShaderMixinObjectId.Compute(Effect(x => { x.UnbindPhases("Owner", "Run", "A"); x.Linkage[0].Level = 1; }), parameters);
        var elsewhere = ShaderMixinObjectId.Compute(Effect(x => x.ExtendShader("Owner", "Other.sdfx, line 3").Method("Run").UnbindPhases("A")), parameters);

        Assert.Equal(6, new[] { plain, unbind, otherUnbind, order, otherOrder, outer }.Distinct().Count());
        Assert.Equal(unbind, elsewhere);
    }

    // Captured with the tree, and not part of its id: an effect gives the same blocks for the same parameters
    [Fact]
    public void TheCorpusKeepsTheLinkageOfAnEntry()
    {
        var plain = new Stride.Shaders.Compiler.ShaderCorpusEntry("Effect", Stride.Graphics.GraphicsProfile.Level_11_0, new EffectSource(new ShaderClassSource("A")));
        var plainId = plain.Id;

        var withRule = new EffectSource(new ShaderClassSource("A"));
        withRule.UnbindPhases("Owner", "Run", "A");
        withRule.UnbindMixins("Owner", "Run", "X");
        withRule.MixinOrder("Owner", "Run(float3)", "X", "Y");
        withRule.ExtendShader("X").Method("Owner.Run").Phase("Lib.W", after: "A").MixinOrderBefore("Y");
        withRule.Linkage[^1].Level = 2;
        var entry = new Stride.Shaders.Compiler.ShaderCorpusEntry("Effect", Stride.Graphics.GraphicsProfile.Level_11_0, withRule);
        var read = Stride.Shaders.Compiler.ShaderCorpusEntry.FromJson(Stride.Shaders.Compiler.ShaderCorpusEntry.ToJson(entry));

        Assert.Equal(withRule.Linkage, read.Effect.Linkage);
        Assert.Equal(plainId, entry.Id);
    }

    [Fact]
    public void LinkageIsPartOfWhatAnEffectIs()
    {
        var first = new EffectSource(new ShaderMixinSource());
        var second = new EffectSource(new ShaderMixinSource());
        Assert.Equal(first, second);

        first.UnbindPhases("Owner", "Run", "A");
        Assert.NotEqual(first, second);

        var clone = first.Clone();
        Assert.Equal(first, clone);
        Assert.Equal(first.GetHashCode(), clone.GetHashCode());
    }
}
