// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders.Compiler;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// Macros belong to the whole effect: the root of a tree has them, a composition can't.
/// </summary>
public class EffectSourceTests
{
    private static ShaderMixinContext Context(EffectSource effect) => new(effect, new ParameterCollection(), []);

    [Fact]
    public void AMacroAtTheTopLevelGoesOnTheEffect()
    {
        var effect = new EffectSource(new ShaderMixinSource());
        var context = Context(effect);

        context.AddMacro("A", 1);
        context.PushComposition(effect.Root, "Slot", new ShaderMixinSource());
        context.PopComposition();
        context.AddMacro("B", 2);

        Assert.Equal([new ShaderMacro("A", 1), new ShaderMacro("B", 2)], effect.Macros);
    }

    [Fact]
    public void AMacroInACompositionIsAnError()
    {
        var effect = new EffectSource(new ShaderMixinSource());
        var context = Context(effect);
        var slot = new ShaderMixinSource();

        context.PushComposition(effect.Root, "Slot", slot);
        context.PushCompositionArray(slot, "Layers", new ShaderMixinSource());
        var error = Assert.Throws<InvalidOperationException>(() => context.AddMacro("A", 1));

        Assert.StartsWith("SDSL0149: macro A is set inside the composition Slot.Layers[0].", error.Message);
        Assert.Empty(effect.Macros);
    }

    [Fact]
    public void AMacroOfAMixedEffectGoesOnTheEffect()
    {
        var effect = ShaderMixinManager.Generate(TestShader.FullName("EffectMacroMixedPart"), new ParameterCollection());

        Assert.Equal([new ShaderMacro("TOP_MACRO", 2), new ShaderMacro("PART_MACRO", 1)], effect.Macros);
    }

    [Fact]
    public void AMacroOfAComposedEffectIsAnError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ShaderMixinManager.Generate(TestShader.FullName("EffectMacroComposedPart"), new ParameterCollection()));

        Assert.StartsWith("SDSL0149: macro PART_MACRO is set inside the composition Layer.", error.Message);
    }

    [Fact]
    public void TheMacrosOfAnEffectChangeItsId()
    {
        var plain = new EffectSource(new ShaderClassSource("A"));
        var withMacro = new EffectSource(new ShaderClassSource("A")) { Macros = { new ShaderMacro("M", 1) } };

        Assert.NotEqual(ShaderMixinObjectId.Compute(plain, EffectCompilerParameters.Default), ShaderMixinObjectId.Compute(withMacro, EffectCompilerParameters.Default));
        Assert.NotEqual(plain, withMacro);
        Assert.Equal(withMacro, withMacro.Clone());
    }

    [Fact]
    public void AClassIsAnEffectNamedAfterIt()
    {
        var effect = new EffectSource(new ShaderClassSource("Lib.A"));

        Assert.Equal("Lib.A", effect.Name);
        Assert.Equal(["Lib.A"], effect.Root.Mixins.Select(x => x.ClassName));
    }

    [Fact]
    public void TheCodeOfAnEffectHasItsMacros()
    {
        var effect = new EffectSource(new ShaderClassSource("A")) { Macros = { new ShaderMacro("M", 1) } };

        Assert.StartsWith("new EffectSource(new ShaderMixinSource", effect.ToCode());
        Assert.Contains("Macros = { new ShaderMacro(\"M\", \"1\"),", effect.ToCode());
    }

    [Fact]
    public void TheCodeOfAnEffectHasItsLinkageRules()
    {
        var effect = new EffectSource(new ShaderClassSource("A"))
        {
            Macros = { new ShaderMacro("M", "1"), },
            Linkage =
            {
                new ShaderLinkageRule { Kind = ShaderLinkageKind.MixinOrder, Shader = "O", Method = "M", Names = { "X", "Y" } },
                new ShaderLinkageRule { Kind = ShaderLinkageKind.Unbind, Shader = "O", Method = "M", Parameters = "float3", Phases = { "P" }, Mixins = { "X" } },
                new ShaderLinkageRule { Kind = ShaderLinkageKind.Phase, Shader = "X", Owner = "O", Method = "M", Phase = "P", After = { "A" }, Level = 1 },
                new ShaderLinkageRule { Kind = ShaderLinkageKind.MixinOrder, Shader = "X", Owner = "O", Method = "M", Before = { "Y" } },
            },
        };

        var code = effect.ToCode();

        Assert.Contains("new ShaderMacro(\"M\", \"1\"),", code);
        Assert.Contains("new ShaderLinkageRule { Kind = ShaderLinkageKind.MixinOrder, Shader = \"O\", Method = \"M\", Names = { \"X\", \"Y\" } },", code);
        Assert.Contains("new ShaderLinkageRule { Kind = ShaderLinkageKind.Unbind, Shader = \"O\", Method = \"M\", Parameters = \"float3\", Phases = { \"P\" }, Mixins = { \"X\" } },", code);
        Assert.Contains("new ShaderLinkageRule { Kind = ShaderLinkageKind.Phase, Shader = \"X\", Owner = \"O\", Method = \"M\", Phase = \"P\", After = { \"A\" }, Level = 1 },", code);
        Assert.Contains("new ShaderLinkageRule { Kind = ShaderLinkageKind.MixinOrder, Shader = \"X\", Owner = \"O\", Method = \"M\", Before = { \"Y\" } },", code);
        Assert.Contains(" },\nLinkage =\n{\n", code);
    }

    [Fact]
    public void AnEffectWithOnlyLinkageRulesGetsOneInitializer()
    {
        var effect = new EffectSource(new ShaderClassSource("A"))
        {
            Linkage = { new ShaderLinkageRule { Kind = ShaderLinkageKind.Unbind, Shader = "O", Method = "M", Phases = { "P" } }, },
        };

        Assert.EndsWith(")\n{\nLinkage =\n{\nnew ShaderLinkageRule { Kind = ShaderLinkageKind.Unbind, Shader = \"O\", Method = \"M\", Phases = { \"P\" } },\n},\n}", effect.ToCode());
    }

    [Fact]
    public void TheCorpusKeepsTheMacrosOfTheEffect()
    {
        var effect = new EffectSource(new ShaderClassSource("A")) { Macros = { new ShaderMacro("M", 1) } };
        var entry = new ShaderCorpusEntry("A", GraphicsProfile.Level_11_0, effect);

        var read = ShaderCorpusEntry.FromJson(ShaderCorpusEntry.ToJson(entry));

        Assert.Equal(effect.Macros, read.Effect.Macros);
        Assert.Equal(entry.Id, read.Id);
    }

    [Fact]
    public void TheCorpusRefusesAMacroInAComposition()
    {
        const string Json = """
            { "effect": "A", "profile": "Level_11_0", "mixin": { "mixins": [ "A" ], "compositions": { "Slot": { "mixins": [ "B" ], "macros": [ [ "M", "1" ] ] } } } }
            """;

        Assert.Throws<InvalidDataException>(() => ShaderCorpusEntry.FromJson(Json));
    }
}
