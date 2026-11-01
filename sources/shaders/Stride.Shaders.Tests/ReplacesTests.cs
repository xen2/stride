// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Shaders.Compilers.SDSL;
using Stride.Shaders.Parsing;
using Stride.Shaders.Parsing.SDSL.AST;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// <c>[Replaces(Full.Name)]</c>: the replacer inherits the original, and joins every mix right after it.
/// </summary>
public class ReplacesTests : IDisposable
{
    private readonly DirectoryInfo sourceDir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"replaces-src-{Guid.NewGuid():N}"));
    private readonly Dictionary<string, string> replacers = [];

    public void Dispose()
    {
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string name, string code) => File.WriteAllText(Path.Combine(sourceDir.FullName, $"{name}.sdsl"), code);

    private (bool Ok, string Log, string Explanation) Mix(ShaderSource source)
    {
        var loader = new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL");
        foreach (var (original, replacer) in replacers)
            loader.Replacers[original] = replacer;
        var log = new LoggerResult();
        var mixer = new ShaderMixer(loader) { CollectExplanation = true };
        bool ok;
        try
        {
            ok = mixer.MergeSDSL(source, new ShaderMixer.Options(true), log, out _, out _, out _, out _);
        }
        catch (InvalidOperationException e)
        {
            ok = false;
            log.Error(e.Message);
        }
        return (ok, string.Join(Environment.NewLine, log.Messages.Select(x => $"{x.Type}: {x.Text}")), mixer.Explanation ?? "");
    }

    private static ShaderMixinSource Tree(params string[] classes)
    {
        var tree = new ShaderMixinSource();
        foreach (var name in classes)
            tree.Mixins.Add(new ShaderClassSource(name));
        return tree;
    }

    private static List<string> MixinLines(string explanation) => [.. explanation.Split('\n').Select(x => x.Trim()).Where(x => x.StartsWith("mixin ", StringComparison.Ordinal) || x.StartsWith("module ", StringComparison.Ordinal))];

    // The line of a method in the explanation: its chain of overrides
    private static string Chain(string explanation, string method) => explanation.Split('\n').Select(x => x.Trim()).First(x => x.Contains(method, StringComparison.Ordinal) && x.Contains('>'));

    private void WriteOriginalAndReplacer()
    {
        Write("X", "namespace Lib; shader X { float4 Get() { return 1; } };");
        Write("R", """namespace Game; [Replaces(Lib.X)] shader R : Lib.X { override float4 Get() { return base.Get() * 2; } };""");
    }

    [Fact]
    public void TheReplacerComesRightAfterTheOriginalAndBeforeWhatDerivesFromIt()
    {
        WriteOriginalAndReplacer();
        Write("Bar", "namespace Lib; shader Bar : X { override float4 Get() { return base.Get() + 1; } };");
        Write("Root", "using Stride.Rendering; namespace Lib; shader Root : SpriteBase, Bar { module override float4 Shading() { return Get(); } };");
        replacers["Lib.X"] = "Game.R";

        var (ok, log, explanation) = Mix(Tree("Lib.Root"));

        Assert.True(ok, log);
        var lines = MixinLines(explanation);
        Assert.Equal(lines.FindIndex(x => x.Contains("Lib.X") || x == "mixin X") + 1, lines.FindIndex(x => x.Contains("replaces")));
        Assert.Equal(lines.FindIndex(x => x.Contains("replaces")) + 1, lines.FindIndex(x => x.Contains("Bar")));
        Assert.Contains("(replaces ", string.Join("\n", lines));
        Assert.Contains("X > ", Chain(explanation, "Get"));
        Assert.Contains("R > ", Chain(explanation, "Get"));
        Assert.Contains("Bar", Chain(explanation, "Get"));
        Assert.True(Chain(explanation, "Get").IndexOf("R > ", StringComparison.Ordinal) < Chain(explanation, "Get").IndexOf("Bar", StringComparison.Ordinal), Chain(explanation, "Get"));
    }

    [Fact]
    public void WithoutAReplacerTheClassIsAlone()
    {
        WriteOriginalAndReplacer();

        Write("Root", "using Stride.Rendering; namespace Lib; shader Root : SpriteBase, X { module override float4 Shading() { return Get(); } };");

        var (ok, log, explanation) = Mix(Tree("Lib.Root"));

        Assert.True(ok, log);
        Assert.DoesNotContain("replaces", explanation);
        Assert.DoesNotContain("Game.R", explanation);
    }

    // X<3> gets R<3> and X<4> gets R<4>, each after its own original
    [Fact]
    public void TwoGenericInstancesEachGetTheirReplacer()
    {
        Write("X", "namespace Lib; shader X<int N> { float4 Get() { return N; } };");
        Write("R", """namespace Game; [Replaces(Lib.X)] shader R<int N> : Lib.X<N> { override float4 Get() { return base.Get() * 2; } };""");
        Write("Root", "using Stride.Rendering; namespace Lib; shader Root : SpriteBase, X<3>, X<4> { module override float4 Shading() { return Get(); } };");
        replacers["Lib.X"] = "Game.R";

        var (ok, log, explanation) = Mix(Tree("Lib.Root"));

        Assert.True(ok, log);
        var lines = MixinLines(explanation);
        Assert.True(lines.Exists(x => x.Contains("R<3>")), string.Join(", ", lines));
        Assert.Equal(lines.FindIndex(x => x.Contains("X<3>")) + 1, lines.FindIndex(x => x.Contains("R<3>")));
        Assert.Equal(lines.FindIndex(x => x.Contains("X<4>")) + 1, lines.FindIndex(x => x.Contains("R<4>")));
    }

    [Fact]
    public void ACompositionDefaultIsReplacedToo()
    {
        WriteOriginalAndReplacer();
        Write("Root", "using Stride.Rendering; namespace Lib; shader Root : SpriteBase { compose X slot; module override float4 Shading() { return slot.Get(); } };");
        replacers["Lib.X"] = "Game.R";

        var (ok, log, explanation) = Mix(Tree("Lib.Root"));

        Assert.True(ok, log);
        Assert.Contains("(replaces ", explanation);
    }

    [Fact]
    public void AValueGivenToACompositionIsReplacedToo()
    {
        WriteOriginalAndReplacer();
        Write("Root", "using Stride.Rendering; namespace Lib; shader Root : SpriteBase { compose X slot; module override float4 Shading() { return slot.Get(); } };");
        replacers["Lib.X"] = "Game.R";
        var tree = Tree("Lib.Root");
        tree.Compositions["slot"] = Tree("Lib.X");

        var (ok, log, explanation) = Mix(tree);

        Assert.True(ok, log);
        Assert.Contains("(replaces ", explanation);
    }

    // A call to the module reaches the override: that is what replaces it for every class that uses it
    [Fact]
    public void AModuleIsReplacedForTheClassesThatCallIt()
    {
        Write("M", "namespace Lib; module M { float4 Get() { return 1; } };");
        Write("R", """namespace Game; [Replaces(Lib.M)] module R : Lib.M { override float4 Get() { return base.Get() * 2; } };""");
        Write("Root", "using Stride.Rendering; namespace Lib; shader Root : SpriteBase { module override float4 Shading() { return M.Get(); } };");
        replacers["Lib.M"] = "Game.R";

        var (ok, log, explanation) = Mix(Tree("Lib.Root"));

        Assert.True(ok, log);
        Assert.Contains("(replaces ", explanation);
        Assert.Contains("> R", Chain(explanation, "Get"));
    }

    // Only the compile knows which class a parent is. A short target, or a parent that is another class of that short name.
    [Theory]
    [InlineData("""namespace Game { [Replaces(X)] shader R : Lib.X { }; }""")]
    [InlineData("""namespace Game { [Replaces(Lib.X)] shader R : X { }; }""")]
    public void AReplacerThatDoesNotInheritItsTargetDoesNotCompile(string replacer)
    {
        Write("X", "namespace Lib { shader X { }; } namespace Game { shader X { }; }");
        Write("R", replacer);

        var loader = new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL");
        var error = Assert.Throws<InvalidOperationException>(() => loader.LoadExternalBuffer("Game.R", [], out _, out _, out _));
        Assert.Contains("SDSL0305: Game.R can't replace", error.Message);
    }

    [Fact]
    public void AReplacerCanBeReplaced()
    {
        WriteOriginalAndReplacer();
        Write("G", """namespace Top; [Replaces(Game.R)] shader G : Game.R { override float4 Get() { return base.Get() * 3; } };""");
        Write("Root", "using Stride.Rendering; namespace Lib; shader Root : SpriteBase, X { module override float4 Shading() { return Get(); } };");
        replacers["Lib.X"] = "Game.R";
        replacers["Game.R"] = "Top.G";

        var (ok, log, explanation) = Mix(Tree("Lib.Root"));

        Assert.True(ok, log);
        var lines = MixinLines(explanation);
        Assert.True(lines.FindIndex(x => x.Contains('R')) < lines.FindIndex(x => x.Contains('G')), string.Join(", ", lines));
    }

    private static ShaderClass Parse(string code)
    {
        var parsed = SDSLParser.Parse(code);
        Assert.Empty(parsed.Errors);
        var file = (ShaderFile)parsed.AST!;
        return file.Namespaces.SelectMany(x => x.Declarations).Concat(file.RootDeclarations).OfType<ShaderClass>().Single();
    }

    [Fact]
    public void TheAttributeNamesTheTarget()
    {
        Assert.Equal("Lib.X", ShaderReplacement.Target(Parse("""namespace Game; [Replaces(Lib.X)] shader R : Lib.X { };""")));
        Assert.Null(ShaderReplacement.Target(Parse("namespace Game; shader R { };")));
    }

    [Theory]
    [InlineData("shader R : Lib.X { }", "shader X { }", null)]
    [InlineData("shader R : X { }", "shader X { }", null)]
    [InlineData("shader R<int N, MemberName M> : Lib.X<N, M> { }", "shader X<int A, MemberName B> { }", null)]
    [InlineData("shader R { }", "shader X { }", "SDSL0305")]
    [InlineData("module R : Lib.X { }", "shader X { }", "SDSL0305")]
    [InlineData("[Root] shader R : Lib.X { }", "shader X { }", "SDSL0305")]
    [InlineData("shader R : Lib.X<3> { }", "shader X<int N> { }", "SDSL0306")]
    [InlineData("shader R<int N> : Lib.X { }", "shader X { }", "SDSL0306")]
    [InlineData("shader R<float N> : Lib.X<N> { }", "shader X<int N> { }", "SDSL0306")]
    [InlineData("shader R<int A, int B> : Lib.X<B, A> { }", "shader X<int A, int B> { }", "SDSL0306")]
    public void TheChecks(string replacer, string target, string? code)
    {
        var message = ShaderReplacement.Check("Game.R", Parse($"namespace Game; {replacer};"), "Lib.X", Parse($"namespace Lib; {target};"));

        if (code == null)
            Assert.Null(message);
        else
            Assert.StartsWith(code, message);
    }
}