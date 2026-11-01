// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Rendering;

namespace Stride.Shaders.Parsers.Tests;

public class ShaderMixinManagerNameTests
{
    private sealed class FirstBuilder : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSource mixin, ShaderMixinContext context) => mixin.Mixins.Add(new ShaderClassSource(string.IsNullOrEmpty(context.ChildEffectName) ? "First" : context.ChildEffectName));
    }

    private sealed class SecondBuilder : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSource mixin, ShaderMixinContext context) => mixin.Mixins.Add(new ShaderClassSource("Second"));
    }

    private static string Unique(string name) => $"NameTests{Guid.NewGuid():N}.{name}";

    [Fact]
    public void TheLongestRegisteredNameIsTheRootAndTheRestIsTheChild()
    {
        var root = Unique("Effect");
        ShaderMixinManager.Register(root, new FirstBuilder());

        Assert.True(ShaderMixinManager.Contains(root));
        Assert.True(ShaderMixinManager.Contains($"{root}.Child"));
        var mixin = ShaderMixinManager.Generate($"{root}.Child", new ParameterCollection());
        Assert.Equal($"{root}.Child", mixin.Name);
        Assert.Equal("Child", ((ShaderClassSource)mixin.Mixins[0]).ClassName);

        // A registered name wins over the name it would be a child of
        ShaderMixinManager.Register($"{root}.Child", new SecondBuilder());
        Assert.Equal("Second", ((ShaderClassSource)ShaderMixinManager.Generate($"{root}.Child", new ParameterCollection()).Mixins[0]).ClassName);
    }

    [Fact]
    public void TwoEffectsOfOneFullNameAreAnError()
    {
        var name = Unique("Twice");
        ShaderMixinManager.Register(name, new FirstBuilder());

        // An assembly that reloads registers the same type again
        ShaderMixinManager.Register(name, new FirstBuilder());

        var error = Assert.Throws<InvalidOperationException>(() => ShaderMixinManager.Register(name, new SecondBuilder()));
        Assert.Contains($"SDSL0302: effect {name} is declared twice", error.Message);
        Assert.Contains(nameof(FirstBuilder), error.Message);
        Assert.Contains(nameof(SecondBuilder), error.Message);
    }

    // A short name is an error that names the effect it can be
    [Fact]
    public void AShortNameFindsNoEffectAndSaysWhichOneItCanBe()
    {
        var shortName = $"Short{Guid.NewGuid():N}";
        var full = $"Some.Namespace.{shortName}";
        ShaderMixinManager.Register(full, new FirstBuilder());

        Assert.False(ShaderMixinManager.Contains(shortName));
        var error = Assert.Throws<ArgumentException>(() => ShaderMixinManager.Generate(shortName, new ParameterCollection()));
        Assert.Contains($"Did you mean {full}?", error.Message);
        Assert.Equal([full], ShaderMixinManager.FindFullNames(shortName));

        // With a child, as a compositor names it
        error = Assert.Throws<ArgumentException>(() => ShaderMixinManager.Generate($"{shortName}.Child", new ParameterCollection()));
        Assert.Contains($"Did you mean {full}.Child?", error.Message);
        Assert.Equal([$"{full}.Child"], ShaderMixinManager.FindFullNames($"{shortName}.Child"));
    }
}
