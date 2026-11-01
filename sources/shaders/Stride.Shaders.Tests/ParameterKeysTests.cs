// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Rendering;

namespace Stride.Shaders.Parsers.Tests;

public class ParameterKeysTests
{
    [EffectKeys(KeyNamePrefix = "ParameterKeysTests")]
    private static class ShortKeys
    {
        public static readonly ValueParameterKey<float> Value = ParameterKeys.NewValue<float>();
    }

    private static readonly ValueParameterKey<float> FullKey = (ValueParameterKey<float>)ParameterKeys.Merge(ParameterKeys.NewValue<float>(), typeof(ParameterKeysTests), "A.B.ParameterKeysTests.Value");

    [Theory]
    [InlineData("A.B.ParameterKeysTests.Value.first")]
    [InlineData("A.B.ParameterKeysTests.Value.first.second[0]")]
    public void FindByNameFindsTheKeyOfAFullNameWithACompositionPath(string name)
    {
        _ = FullKey;

        var key = ParameterKeys.FindByName(name);

        Assert.IsType<ValueParameterKey<float>>(key);
        Assert.Equal(name, key.Name);
        Assert.Same(key, ParameterKeys.TryFindByName(name));
    }

    [Fact]
    public void FindByNameFindsAShortNameWithACompositionPath()
    {
        var key = ParameterKeys.FindByName($"{ShortKeys.Value.Name}.first.second");

        Assert.IsType<ValueParameterKey<float>>(key);
        Assert.Equal("ParameterKeysTests.Value.first.second", key.Name);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("A.B")]
    [InlineData("A.B.ParameterKeysTests")]
    [InlineData("A.B.ParameterKeysTests.Other.first")]
    public void FindByNameFindsNoOtherKey(string name)
    {
        _ = FullKey;

        Assert.Null(ParameterKeys.FindByName(name));
    }

    [Fact]
    public void ComposeWithKeepsTheFullName()
    {
        Assert.Equal("A.B.ParameterKeysTests.Value.slot", FullKey.ComposeWith("slot").Name);
    }
}
