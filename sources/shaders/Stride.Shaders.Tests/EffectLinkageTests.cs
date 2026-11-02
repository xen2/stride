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
/// Linkage rules of an effect: what the generator writes for them, and the layers of a <c>partial effect</c>.
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
    [InlineData("float feature = 2;")]
    [InlineData("float variant = 2;")]
    public void TheWordsCanStillNameAVariable(string line)
    {
        Assert.Contains("= 2", Generate($"effect MyEffect {{ {line} }}"));
    }

    [Fact]
    public void AFeatureIsAnIfAndAnAxis()
    {
        var code = Generate("""
            effect MyEffect
            {
                using params MyKeys;
                feature MyKeys.Flag { mixin A; } else { mixin B; }
                optional MyKeys.Slot { mixin C; }
            }
            """);

        Assert.Contains("[global::Stride.Shaders.EffectAxes(\"MyKeys.Flag\", \"MyKeys.Slot\")]", code);
        Assert.Contains("if (context.GetParam(MyKeys.Flag))", code);
        Assert.Contains("else", code);
        Assert.Contains("if (context.GetParam(MyKeys.Slot) != null)", code);
    }

    // Written first, it still has the statement after it
    [Fact]
    public void AVariantHasAllTheStatementsOfTheEffect()
    {
        var code = Generate("""
            effect MyEffect
            {
                variant Depth { mixin OnlyInDepth; }
                mixin Always;
            }
            """);

        Assert.True(code.IndexOf("\"Always\"", StringComparison.Ordinal) < code.IndexOf("context.ChildEffectName == \"Depth\"", StringComparison.Ordinal), code);
        Assert.Contains("return;", code);
    }

    [Fact]
    public void AVariantGoesAtTheTopOfAnEffect()
    {
        var parsed = SDSLParser.Parse("namespace Test { effect MyEffect { if (true) { variant Depth { mixin A; } } mixin B; } }");
        Assert.Empty(parsed.Errors);

        Assert.False(new EffectCodeWriter().Run(parsed.AST!));
    }

    [Fact]
    public void APartialEffectIsRegisteredAsALayer()
    {
        Assert.Contains("ShaderMixinManager.RegisterPartial(\"Test.MyEffect\"", Generate("partial effect MyEffect { mixin A; }"));
        Assert.Contains("ShaderMixinManager.Register(\"Test.MyEffect\"", Generate("effect MyEffect { mixin A; }"));
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
        ShaderMixinManager.RegisterPartial(name, new Layer());

        Assert.Equal(["Base", "Layer"], Classes(name));
    }

    [Fact]
    public void ALayerRegisteredBeforeTheEffectComesAfterIt()
    {
        var name = Unique("Effect");
        ShaderMixinManager.RegisterPartial(name, new Layer());
        ShaderMixinManager.Register(name, new Base());

        Assert.Equal(["Base", "Layer"], Classes(name));
    }

    [Fact]
    public void APartialEffectAloneIsTheEffect()
    {
        var name = Unique("Effect");
        ShaderMixinManager.RegisterPartial(name, new Layer());

        Assert.Equal(["Layer"], Classes(name));
    }

    // An assembly that reloads registers the same type again
    [Fact]
    public void AReloadedLayerReplacesItself()
    {
        var name = Unique("Effect");
        ShaderMixinManager.Register(name, new Base());
        ShaderMixinManager.RegisterPartial(name, new Layer());
        ShaderMixinManager.RegisterPartial(name, new Layer());
        ShaderMixinManager.RegisterPartial(name, new Base());

        Assert.Equal(["Base", "Layer"], Classes(name));
    }

    [Fact]
    public void TwoEffectsOfOneNameStayAnErrorWithLayers()
    {
        var name = Unique("Effect");
        ShaderMixinManager.Register(name, new Base());
        ShaderMixinManager.RegisterPartial(name, new Layer());

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

        ShaderMixinManager.RegisterPartial(name, CompileLayer(game, compiledA, compiledB));
        ShaderMixinManager.RegisterPartial(name, CompileLayer(pluginB, compiledA));
        ShaderMixinManager.RegisterPartial(name, CompileLayer(pluginA));

        Assert.Equal([pluginA, pluginB, game], Classes(name));
    }

    private sealed class EffectFile(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    // A project as a real build gives it: the generator runs on its effect, and it can see another project through a reference assembly
    private static (byte[] Image, byte[] ReferenceAssembly, string Generated) CompileProject(string name, string effect, string code, params (string Name, byte[] Image)[] references)
    {
        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Where(x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(x => MetadataReference.CreateFromFile(x));
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(code)],
            framework.Concat<MetadataReference>(references.Select(x => MetadataReference.CreateFromImage(x.Image))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        CSharpGeneratorDriver.Create([new EffectGenerator().AsSourceGenerator()], additionalTexts: [new EffectFile($"{name}.sdfx", effect)])
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        using var image = new MemoryStream();
        using var referenceAssembly = new MemoryStream();
        var result = output.Emit(image, metadataPEStream: referenceAssembly);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return (image.ToArray(), referenceAssembly.ToArray(), output.SyntaxTrees.Last().ToString());
    }

    // AGame uses no type of ZPlugin, so its metadata has no reference to it, and its name would put it first
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AProjectComesAfterAPluginItUsesNoTypeOf(bool throughReferenceAssembly)
    {
        var id = Guid.NewGuid().ToString("N");
        string plugin = $"ZPlugin{id}", game = $"AGame{id}";
        var compiledPlugin = CompileProject(plugin, $"namespace Layers{id} {{ partial effect Layered {{ mixin {plugin}; }} }}", "");
        var compiledGame = CompileProject(game, $"namespace Layers{id} {{ partial effect Layered {{ mixin {game}; }} }}", "", (plugin, throughReferenceAssembly ? compiledPlugin.ReferenceAssembly : compiledPlugin.Image));

        var gameAssembly = Assembly.Load(compiledGame.Image);
        Assert.DoesNotContain(gameAssembly.GetReferencedAssemblies(), x => x.Name == plugin);

        // The game loads first
        RuntimeHelpers.RunModuleConstructor(gameAssembly.ManifestModule.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(Assembly.Load(compiledPlugin.Image).ManifestModule.ModuleHandle);

        Assert.Equal([plugin, game], Classes($"Layers{id}.Layered"));
    }

    // As a game that only uses Stride.Engine, which has no layer, and gets Stride.Rendering as a transitive reference
    [Fact]
    public void AProjectComesAfterALayerItOnlyReachesThroughAnotherAssembly()
    {
        var id = Guid.NewGuid().ToString("N");
        string rendering = $"ZRendering{id}", engine = $"Engine{id}", game = $"AGame{id}";
        var compiledRendering = CompileProject(rendering, $"namespace Layers{id} {{ partial effect Layered {{ mixin {rendering}; }} }}", $"namespace {rendering}; public class Layer {{ }}");
        var compiledEngine = Compiled(engine, (rendering, compiledRendering.Image));
        var compiledGame = CompileProject(game, $"namespace Layers{id} {{ partial effect Layered {{ mixin {game}; }} }}", $"class Uses {{ static void Unused() {{ new {engine}.Layer(); }} }}", compiledEngine, (rendering, compiledRendering.Image));

        var gameAssembly = Assembly.Load(compiledGame.Image);
        Assert.Contains(gameAssembly.GetReferencedAssemblies(), x => x.Name == engine);
        Assert.DoesNotContain(gameAssembly.GetReferencedAssemblies(), x => x.Name == rendering);
        Assert.Contains($"\"{rendering}\"", compiledGame.Generated);
        Assert.DoesNotContain(engine, compiledGame.Generated);

        // The game loads first
        RuntimeHelpers.RunModuleConstructor(gameAssembly.ManifestModule.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(Assembly.Load(compiledRendering.Image).ManifestModule.ModuleHandle);

        Assert.Equal([rendering, game], Classes($"Layers{id}.Layered"));
    }

    [Fact]
    public void ALayerWritesTheReferencesThatHoldALayerInOrder()
    {
        var id = Guid.NewGuid().ToString("N");
        string pluginA = $"PluginA{id}", pluginB = $"PluginB{id}", plain = $"Plain{id}";
        var compiledA = CompileProject(pluginA, $"namespace Layers{id} {{ partial effect Layered {{ mixin {pluginA}; }} }}", "");
        var compiledB = CompileProject(pluginB, $"namespace Layers{id} {{ partial effect Layered {{ mixin {pluginB}; }} }}", "");

        var game = CompileProject($"Game{id}", $"namespace Layers{id} {{ partial effect Layered {{ mixin Game; }} effect Whole {{ mixin Game; }} }}", "", (pluginB, compiledB.Image), (pluginA, compiledA.Image), Compiled(plain));

        // The assemblies of this test run can hold layers too
        var written = game.Generated.Split('\n').Single(x => x.Contains("ShaderMixins.Layered()")).Split("Layered()")[1].Split('"').Where((_, i) => i % 2 == 1).ToList();
        Assert.Contains(pluginA, written);
        Assert.Contains(pluginB, written);
        Assert.Equal(written.Order(StringComparer.Ordinal), written);
        Assert.Contains("ShaderMixins.Whole());", game.Generated);
        Assert.DoesNotContain(plain, game.Generated);
    }

    [EffectAxes("Keys.First")]
    private sealed class WithAxes : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSource mixin, ShaderMixinContext context) { }
    }

    [EffectAxes("Keys.Second", "Keys.First")]
    private sealed class LayerWithAxes : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSource mixin, ShaderMixinContext context) { }
    }

    [Fact]
    public void TheAxesOfAnEffectIncludeItsLayers()
    {
        var name = Unique("Effect");
        ShaderMixinManager.Register(name, new WithAxes());
        ShaderMixinManager.RegisterPartial(name, new LayerWithAxes());

        Assert.Equal(["Keys.First", "Keys.Second"], ShaderMixinManager.GetStructuralAxes(name));
        Assert.Empty(ShaderMixinManager.GetStructuralAxes(Unique("Missing")));
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
