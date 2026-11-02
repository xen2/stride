// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Shaders.Parsing;
using Stride.Shaders.Parsing.SDSL.AST;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// A composition written with a value (<c>compose ComputeFloat X = 0.1f;</c>) parses, and the value is kept in the tree for tools, but it is error SDSL0236.
/// </summary>
public class ComposeWithValueTests
{
    [Fact]
    public void ValueIsKeptInTheTree()
    {
        var result = SDSLParser.Parse("shader S { compose ComputeFloat Distance = 0.1f; };");
        Assert.True(result.Errors.Count == 0, string.Join("\n", result.Errors.Select(x => x.ToString())));

        var shader = Assert.IsType<ShaderClass>(Assert.Single(((ShaderFile)result.AST!).RootDeclarations));
        var member = shader.Elements.OfType<ShaderMember>().Single();
        Assert.True(member.IsCompose);
        Assert.Equal("0.1f", member.Value?.Info.Text.ToString());
    }

    [Fact]
    public void ValueIsAnError()
    {
        var log = new LoggerResult();
        var loader = new ShaderLoader("./assets/SDSL/CompilerTests", "./assets/Stride/SDSL") { Log = log };
        var error = Assert.ThrowsAny<InvalidOperationException>(() => loader.LoadExternalBuffer(TestShader.FullName("ComposeWithValue"), [], out _, out _, out _));
        foreach (var (name, value) in new[] { ("Color", "float4(1.0, 0.0, 0.0, 1.0)"), ("Scale", "0.5f"), ("Count", "1") })
            Assert.Contains($"SDSL0236: the value of composition {name} is ignored: a composition gets its shader from the effect, not from a value. Remove '= {value}'.", error.Message);
    }
}
