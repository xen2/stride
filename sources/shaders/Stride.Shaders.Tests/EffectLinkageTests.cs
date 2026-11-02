// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Stride.Core.Serialization;
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

    [Theory]
    [InlineData("order Owner.Run.A: X, Y;", "AddLinkageRule(global::Stride.Shaders.ShaderLinkageKind.Order, \"Owner\", \"Run\", \"A\", \"X\", \"Y\");")]
    [InlineData("order Some.Space.Owner.Run.A: Some.X;", "ShaderLinkageKind.Order, \"Some.Space.Owner\", \"Run\", \"A\", \"Some.X\");")]
    [InlineData("bind Owner.Run.A = X;", "ShaderLinkageKind.Bind, \"Owner\", \"Run\", \"A\", \"X\");")]
    [InlineData("unbind Owner.Run.A;", "ShaderLinkageKind.Unbind, \"Owner\", \"Run\", \"A\");")]
    [InlineData("unbind Owner.Run = X;", "ShaderLinkageKind.Unbind, \"Owner\", \"Run\", null, \"X\");")]
    [InlineData("phase Owner.Run.Wind after A before B;", "ShaderLinkageKind.Phase, \"Owner\", \"Run\", \"Wind\", \"A\", \"B\");")]
    [InlineData("phase Owner.Run.Wind after A;", "ShaderLinkageKind.Phase, \"Owner\", \"Run\", \"Wind\", \"A\", \"\");")]
    [InlineData("phase Owner.Run.Wind before B;", "ShaderLinkageKind.Phase, \"Owner\", \"Run\", \"Wind\", \"\", \"B\");")]
    public void ALinkageRuleIsWritten(string line, string expected)
    {
        Assert.Contains(expected, Generate($"effect MyEffect {{ {line} }}"));
    }

    // The words are not reserved
    [Theory]
    [InlineData("float order = 2;")]
    [InlineData("float phase = 2;")]
    public void TheWordsCanStillNameAVariable(string line)
    {
        Assert.Contains("= 2", Generate($"effect MyEffect {{ {line} }}"));
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

    private static string[] Classes(string effect) => ShaderMixinManager.Generate(effect, new ParameterCollection()).Mixins.Select(x => x.ClassName).ToArray();

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

    // Compiled assets hold mixins in binary form, and their build cache doesn't see a change of that form
    [Fact]
    public void LinkageIsNotInTheBinaryFormOfAMixin()
    {
        var plain = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        var withRule = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        withRule.AddLinkageRule(ShaderLinkageKind.Unbind, "Owner", "Run", "A");

        static byte[] Write(ShaderMixinSource mixin)
        {
            using var bytes = new MemoryStream();
            var writer = new BinarySerializationWriter(bytes);
            writer.Write(mixin);
            return bytes.ToArray();
        }
        Assert.Equal(Write(plain), Write(withRule));
    }

    // A compiled effect is cached by this hash
    [Fact]
    public void LinkageIsPartOfTheHashOfACompiledEffect()
    {
        var parameters = Stride.Shaders.Compiler.EffectCompilerParameters.Default;
        var plain = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        var withRule = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        withRule.AddLinkageRule(ShaderLinkageKind.Unbind, "Owner", "Run", "A");

        Assert.NotEqual(ShaderMixinObjectId.Compute(plain, parameters), ShaderMixinObjectId.Compute(withRule, parameters));
    }

    // Captured with the tree, and not part of its id: an effect gives the same linkage rules for the same parameters
    [Fact]
    public void TheCorpusKeepsTheLinkageOfAnEntry()
    {
        var mixin = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        var plain = new Stride.Shaders.Compiler.ShaderCorpusEntry("Effect", Stride.Graphics.GraphicsProfile.Level_11_0, mixin);
        var plainId = plain.Id;

        var withRule = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        withRule.AddLinkageRule(ShaderLinkageKind.Unbind, "Owner", "Run", "A");
        withRule.AddLinkageRule(ShaderLinkageKind.Unbind, "Owner", "Run", null, "X");
        var entry = new Stride.Shaders.Compiler.ShaderCorpusEntry("Effect", Stride.Graphics.GraphicsProfile.Level_11_0, withRule);
        var read = Stride.Shaders.Compiler.ShaderCorpusEntry.FromJson(Stride.Shaders.Compiler.ShaderCorpusEntry.ToJson(entry));

        Assert.Equal(withRule.Linkage, read.Mixin.Linkage);
        Assert.Equal(plainId, entry.Id);
    }

    // The cache of compiled effects has to see them
    [Fact]
    public void LinkageIsPartOfWhatAMixinIs()
    {
        var first = new ShaderMixinSource();
        var second = new ShaderMixinSource();
        Assert.Equal(first, second);

        first.AddLinkageRule(ShaderLinkageKind.Unbind, "Owner", "Run", "A");
        Assert.NotEqual(first, second);
        Assert.Equal(first, (ShaderMixinSource)first.Clone());

        second.DeepCloneFrom(first);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }
}
