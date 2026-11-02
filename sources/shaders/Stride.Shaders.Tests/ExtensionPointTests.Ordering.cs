// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Rendering;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// The ordering model: the running order of the contributors of a phase, as Explain lists it.
/// </summary>
public partial class ExtensionPointTests
{
    // Before the default body each step calls base last, so the outermost one runs first: Explain lists the running order
    [Fact]
    public void ExplainListsAPhaseBeforeTheDefaultBodyInRunningOrder()
    {
        WriteOwner();
        WriteModule("First", "[Phase(Early)] override void Run() { streams.Steps = 2; base.Run(); }");
        WriteModule("Second", "[Phase(Early)] override void Run() { streams.Steps = 3; base.Run(); }");

        var (ok, log, explanation) = Mix(isAssetBuild: true, "Test.Second", "Test.First");

        Assert.True(ok, log);
        var block = Block(explanation, "Run");
        Assert.Equal("module method Run() -> void = Owner > Second > First", block[0]);
        Assert.Contains("Early: First > Second", block);
    }
}
