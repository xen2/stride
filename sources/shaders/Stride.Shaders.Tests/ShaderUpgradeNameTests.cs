// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Shaders.Parsing.Upgrade;
using Xunit;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// The names the upgrade writes in the sources of a project.
/// </summary>
public class ShaderUpgradeNameTests
{
    private static readonly EngineShaderClasses Engine = EngineShaderClasses.Parse(
        "Vox\t0\tshader\t-\t-\t\t\tStride.Rendering.Voxels.Vox\t\n" +
        "Other\t0\tshader\t-\t-\t\t\tStride.Rendering.Other\t\n" +
        "Third\t0\tshader\t-\t-\t\t\tStride.Rendering.Third\t\n");

    private static string Upgrade(string shader)
        => ShaderUpgrader.Upgrade(new Dictionary<string, string> { ["R.sdsl"] = shader }, Engine, implicitUsings: ["Stride.Rendering"]).Files["R.sdsl"];

    // A parent after a comma and a name in a comment aren't declarations
    [Theory]
    [InlineData("shader R : Other, Vox, Third { };\n")]
    [InlineData("// float4 Vox;\nshader R : Vox { };\n")]
    public void ANameOutOfScopeGetsItsUsing(string shader)
    {
        Assert.Contains("using Stride.Rendering.Voxels;", Upgrade(shader));
    }

    [Theory]
    [InlineData("shader R { float4 Vox; };\n")]
    [InlineData("shader R { Map<A, B> Vox; };\n")]
    public void AMemberOfThatNameNeedsNoUsing(string shader)
    {
        Assert.Equal(shader, Upgrade(shader));
    }
}
