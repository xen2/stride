// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Serialization;
using Stride.Rendering;
using Stride.Shaders.Parsing;
using Stride.Shaders.Parsing.SDFX;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Wiring statements of an effect: what the generator writes for them, and the layers of a <c>partial effect</c>.
/// </summary>
public class EffectWiringTests
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
    [InlineData("order Owner.Run.A: X, Y;", "AddWiring(global::Stride.Shaders.ShaderWiringKind.Order, \"Owner\", \"Run\", \"A\", \"X\", \"Y\");")]
    [InlineData("order Some.Space.Owner.Run.A: Some.X;", "ShaderWiringKind.Order, \"Some.Space.Owner\", \"Run\", \"A\", \"Some.X\");")]
    [InlineData("bind Owner.Run.A = X;", "ShaderWiringKind.Bind, \"Owner\", \"Run\", \"A\", \"X\");")]
    [InlineData("unbind Owner.Run.A;", "ShaderWiringKind.Unbind, \"Owner\", \"Run\", \"A\");")]
    [InlineData("unbind Owner.Run = X;", "ShaderWiringKind.Unbind, \"Owner\", \"Run\", null, \"X\");")]
    [InlineData("phase Owner.Run.Wind after A before B;", "ShaderWiringKind.Phase, \"Owner\", \"Run\", \"Wind\", \"A\", \"B\");")]
    [InlineData("phase Owner.Run.Wind after A;", "ShaderWiringKind.Phase, \"Owner\", \"Run\", \"Wind\", \"A\", \"\");")]
    [InlineData("phase Owner.Run.Wind before B;", "ShaderWiringKind.Phase, \"Owner\", \"Run\", \"Wind\", \"\", \"B\");")]
    public void AWiringLineIsWritten(string line, string expected)
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

    private static string Unique(string name) => $"WiringTests{Guid.NewGuid():N}.{name}";

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
    public void WiringIsNotInTheBinaryFormOfAMixin()
    {
        var plain = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        var wired = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        wired.AddWiring(ShaderWiringKind.Unbind, "Owner", "Run", "A");

        static byte[] Write(ShaderMixinSource mixin)
        {
            using var bytes = new MemoryStream();
            var writer = new BinarySerializationWriter(bytes);
            writer.Write(mixin);
            return bytes.ToArray();
        }
        Assert.Equal(Write(plain), Write(wired));
    }

    // A compiled effect is cached by this hash
    [Fact]
    public void WiringIsPartOfTheHashOfACompiledEffect()
    {
        var parameters = Stride.Shaders.Compiler.EffectCompilerParameters.Default;
        var plain = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        var wired = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        wired.AddWiring(ShaderWiringKind.Unbind, "Owner", "Run", "A");

        Assert.NotEqual(ShaderMixinObjectId.Compute(plain, parameters), ShaderMixinObjectId.Compute(wired, parameters));
    }

    // Captured with the tree, and not part of its id: an effect gives the same lines for the same parameters
    [Fact]
    public void TheCorpusKeepsTheWiringOfAnEntry()
    {
        var mixin = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        var plain = new Stride.Shaders.Compiler.ShaderCorpusEntry("Effect", Stride.Graphics.GraphicsProfile.Level_11_0, mixin);
        var plainId = plain.Id;

        var wired = new ShaderMixinSource { Mixins = { new ShaderClassSource("A") } };
        wired.AddWiring(ShaderWiringKind.Unbind, "Owner", "Run", "A");
        wired.AddWiring(ShaderWiringKind.Unbind, "Owner", "Run", null, "X");
        var entry = new Stride.Shaders.Compiler.ShaderCorpusEntry("Effect", Stride.Graphics.GraphicsProfile.Level_11_0, wired);
        var read = Stride.Shaders.Compiler.ShaderCorpusEntry.FromJson(Stride.Shaders.Compiler.ShaderCorpusEntry.ToJson(entry));

        Assert.Equal(wired.Wiring, read.Mixin.Wiring);
        Assert.Equal(plainId, entry.Id);
    }

    // The cache of compiled effects has to see them
    [Fact]
    public void WiringIsPartOfWhatAMixinIs()
    {
        var first = new ShaderMixinSource();
        var second = new ShaderMixinSource();
        Assert.Equal(first, second);

        first.AddWiring(ShaderWiringKind.Unbind, "Owner", "Run", "A");
        Assert.NotEqual(first, second);
        Assert.Equal(first, (ShaderMixinSource)first.Clone());

        second.DeepCloneFrom(first);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }
}
