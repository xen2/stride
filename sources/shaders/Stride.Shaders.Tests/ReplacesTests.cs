// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Storage;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders.Compiler;
using Stride.Shaders.Compilers;
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

    private (bool Ok, string Log, string Explanation) Mix(ShaderMixinSource source)
    {
        var loader = new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL");
        foreach (var (original, replacer) in replacers)
            loader.Replacers[original] = replacer;
        var log = new LoggerResult();
        var mixer = new ShaderMixer(loader) { CollectExplanation = true };
        bool ok;
        try
        {
            ok = mixer.MergeSDSL(new EffectSource(source), new ShaderMixer.Options(true), log, out _, out _, out _, out _);
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
        Write("Root", "using Stride.Rendering; namespace Lib; shader Root : SpriteBase, X<3>, X<4> { module override float4 Shading() { return X<3>.Get(); } };");
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
        Write("M", "namespace Lib; module M { pipeline float4 Get() { return 1; } };");
        Write("R", """namespace Game; [Replaces(Lib.M)] module R : Lib.M { override float4 Get() { return base.Get() * 2; } };""");
        Write("Root", "using Stride.Rendering; namespace Lib; shader Root : SpriteBase { module override float4 Shading() { return M.Get(); } };");
        replacers["Lib.M"] = "Game.R";

        var (ok, log, explanation) = Mix(Tree("Lib.Root"));

        Assert.True(ok, log);
        Assert.Contains("(replaces ", explanation);
        Assert.Contains("> R", Chain(explanation, "Get"));
    }

    // A static method is in the override chain like any method
    [Fact]
    public void AStaticMethodIsOverridden()
    {
        Write("X", "namespace Lib; shader X { static float4 F() { return 1; } float4 Get() { return F(); } };");
        Write("R", """namespace Game; [Replaces(Lib.X)] shader R : Lib.X { override static float4 F() { return 2; } };""");
        Write("Root", "using Stride.Rendering; namespace Lib; shader Root : SpriteBase, X { module override float4 Shading() { return Get(); } };");
        replacers["Lib.X"] = "Game.R";

        var (ok, log, explanation) = Mix(Tree("Lib.Root"));

        Assert.True(ok, log);
        Assert.Contains("> R", Chain(explanation, " F("));
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

    private EffectCompiler Compiler(FileSystemProvider provider)
    {
        var compiler = new EffectCompiler(provider) { ImplicitUsings = ShaderImplicitUsings.List };
        compiler.SourceDirectories.Add("assets/Stride/SDSL");
        return compiler;
    }

    private const string RootShader = "using Stride.Rendering; namespace Lib; shader Root : SpriteBase, X { module override float4 Shading() { return Get(); } };";
    private const string OriginalShader = "namespace Lib; shader X { float4 Get() { return 1; } };";
    private const string ReplacerShader = """namespace Game; [Replaces(Lib.X)] shader R : Lib.X { override float4 Get() { return base.Get() * 2; } };""";

    private static (ObjectId EffectId, EffectBytecode Bytecode) CompileRoot(EffectCompiler compiler, GraphicsPlatform platform)
    {
        var results = compiler.Compile(new EffectSource(new ShaderClassSource("Lib.Root")), new CompilerParameters { EffectParameters = EffectCompilerParameters.Default with { Platform = platform } });
        Assert.False(results.HasErrors, string.Join(Environment.NewLine, results.Messages.Select(x => x.Text)));
        var completed = results.Bytecode.WaitForResult();
        Assert.False(completed.CompilationLog.HasErrors, string.Join(Environment.NewLine, completed.CompilationLog.Messages.Select(x => x.Text)));
        return (ShaderMixinObjectId.Compute(new EffectSource(new ShaderClassSource("Lib.Root")), EffectCompilerParameters.Default, compiler.GetReplacementsHash()), completed.Bytecode);
    }

    public static TheoryData<GraphicsPlatform> Platforms()
    {
        var platforms = new TheoryData<GraphicsPlatform> { GraphicsPlatform.Vulkan };
        if (OperatingSystem.IsWindows())
            platforms.Add(GraphicsPlatform.Direct3D11);
        return platforms;
    }

    // Without an effect id that changes, the bytecode of the original would be served: the tree and the source of X don't change
    [Theory]
    [MemberData(nameof(Platforms))]
    public void AReplacerReachesTheBytecodeAndChangesTheEffectId(GraphicsPlatform platform)
    {
        using var provider = new FileSystemProvider($"/replacestest-{Guid.NewGuid():N}", AppContext.BaseDirectory);

        using var plain = Compiler(provider);
        plain.GetFileShaderLoader().SourceManager.AddShaderSource("Lib.X", OriginalShader, "X.sdsl");
        plain.GetFileShaderLoader().SourceManager.AddShaderSource("Lib.Root", RootShader, "Root.sdsl");
        var without = CompileRoot(plain, platform);

        using var replaced = Compiler(provider);
        replaced.GetFileShaderLoader().SourceManager.AddShaderSource("Lib.X", OriginalShader, "X.sdsl");
        replaced.GetFileShaderLoader().SourceManager.AddShaderSource("Lib.Root", RootShader, "Root.sdsl");
        replaced.GetFileShaderLoader().SourceManager.AddShaderSource("Game.R", ReplacerShader, "R.sdsl");
        var with = CompileRoot(replaced, platform);

        Assert.NotEqual(without.EffectId, with.EffectId);
        Assert.Contains("Game.R", with.Bytecode.HashSources.Keys);
        Assert.DoesNotContain("Game.R", without.Bytecode.HashSources.Keys);
    }

    [Fact]
    public void NoReplacementChangesNoEffectId()
    {
        var tree = new ShaderMixinSource { Name = "Effect", Mixins = { new ShaderClassSource("Lib.X") } };

        Assert.Equal(ObjectId.Empty, ShaderMixinObjectId.ComputeReplacements([]));
        Assert.Equal(ShaderMixinObjectId.Compute(new EffectSource(tree), EffectCompilerParameters.Default), ShaderMixinObjectId.Compute(new EffectSource(tree), EffectCompilerParameters.Default, ShaderMixinObjectId.ComputeReplacements([])));
    }

    // The build and the game list them in different orders
    [Fact]
    public void TheHashOfTheReplacementsDoesNotDependOnTheirOrder()
    {
        KeyValuePair<string, string> first = new("Lib.X", "Game.R"), second = new("Lib.Y", "Game.S");

        Assert.Equal(ShaderMixinObjectId.ComputeReplacements([first, second]), ShaderMixinObjectId.ComputeReplacements([second, first]));
        Assert.NotEqual(ShaderMixinObjectId.ComputeReplacements([first]), ShaderMixinObjectId.ComputeReplacements([first, second]));
    }

    // What the editor's database holds, with the source of a replacer that is also a file on disk
    private sealed class StoredShaders(string replacerFile) : VirtualFileProviderBase(null)
    {
        private readonly Dictionary<string, string> files = new()
        {
            ["shaders/Lib.X.sdsl"] = OriginalShader,
            ["shaders/Game.R.sdsl"] = ReplacerShader,
            ["shaders/Game.R.sdsl/path"] = replacerFile,
            ["shaders/Lib.X.sdsl/replacedby"] = "Game.R",
        };

        public override bool FileExists(string url) => files.ContainsKey(url);

        public override string[] ListFiles(string url, string searchPattern, VirtualSearchOption searchOption)
            => [.. files.Keys.Where(x => x.StartsWith(url + "/", StringComparison.Ordinal) && x.EndsWith(searchPattern[1..], StringComparison.Ordinal))];

        public override Stream OpenStream(string url, VirtualFileMode mode, VirtualFileAccess access, VirtualFileShare share = VirtualFileShare.Read, StreamFlags streamFlags = StreamFlags.None)
            => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(files[url]));
    }

    // The watcher reports the file of the replacer: the class it replaced must recompile, with its own id, each way
    [Fact]
    public void AReplacerDeletedWhileRunningStopsReplacingUntilItsFileIsBack()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("The original files of shaders are read on Windows only");

        var file = Path.Combine(sourceDir.FullName, "R.sdsl");
        File.WriteAllText(file, ReplacerShader);
        using var provider = new StoredShaders(file);
        using var compiler = new EffectCompiler(provider);
        compiler.SourceDirectories.Add("shaders");
        var loader = compiler.GetFileShaderLoader();
        var replaced = compiler.GetReplacementsHash();
        Assert.Equal("Game.R", loader.FindReplacer("Lib.X"));

        File.Delete(file);
        HashSet<string> deleted = ["Game.R"];
        compiler.ResetCache(deleted);

        Assert.Contains("Lib.X", deleted);
        Assert.Null(loader.FindReplacer("Lib.X"));
        Assert.NotEqual(replaced, compiler.GetReplacementsHash());

        File.WriteAllText(file, ReplacerShader);
        HashSet<string> added = ["Game.R"];
        compiler.ResetCache(added);

        Assert.Contains("Lib.X", added);
        Assert.Equal("Game.R", loader.FindReplacer("Lib.X"));
        Assert.Equal(replaced, compiler.GetReplacementsHash());
    }

    // A database can name a file of another machine: that isn't a deletion
    [Fact]
    public void AReplacerWhoseFileWasNeverSeenKeepsReplacing()
    {
        using var provider = new StoredShaders(Path.Combine(sourceDir.FullName, "NotHere.sdsl"));
        using var compiler = new EffectCompiler(provider);
        compiler.SourceDirectories.Add("shaders");

        Assert.Equal("Game.R", compiler.GetFileShaderLoader().FindReplacer("Lib.X"));
    }

    // What the editor's database holds as builds go on: empty at first, on top of the engine's shaders
    private sealed class GrowingDatabase(string basePath) : FileSystemProvider($"/replacestest-{Guid.NewGuid():N}", basePath)
    {
        public readonly Dictionary<string, string> Files = [];

        public override bool FileExists(string url) => Files.ContainsKey(url) || base.FileExists(url);

        public override string[] ListFiles(string url, string searchPattern, VirtualSearchOption searchOption)
            => [.. Files.Keys.Where(x => x.StartsWith(url + "/", StringComparison.Ordinal) && x.EndsWith(searchPattern[1..], StringComparison.Ordinal))];

        public override Stream OpenStream(string url, VirtualFileMode mode, VirtualFileAccess access, VirtualFileShare share = VirtualFileShare.Read, StreamFlags streamType = StreamFlags.None)
            => Files.TryGetValue(url, out var text) ? new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)) : base.OpenStream(url, mode, access, share, streamType);

        public void AddOriginal()
        {
            Files["shaders/Lib.X.sdsl"] = OriginalShader;
            Files["shaders/Lib.Root.sdsl"] = RootShader;
        }

        public void AddReplacer()
        {
            Files["shaders/Game.R.sdsl"] = ReplacerShader;
            Files["shaders/Lib.X.sdsl/replacedby"] = "Game.R";
        }
    }

    private static EffectCompiler GrowingCompiler(GrowingDatabase database, List<IReadOnlyCollection<string>> reported)
    {
        var compiler = new EffectCompiler(database) { ImplicitUsings = ShaderImplicitUsings.List };
        compiler.SourceDirectories.Add("shaders");
        compiler.SourceDirectories.Add("assets/Stride/SDSL");
        compiler.ReplacementsChanged += reported.Add;
        return compiler;
    }

    // Nothing else resets the compiler: a new replacer has no file event, because no effect uses it yet
    [Theory]
    [MemberData(nameof(Platforms))]
    public void AReplacerThatAppearsAfterABuildReachesTheNextCompile(GraphicsPlatform platform)
    {
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        List<IReadOnlyCollection<string>> reported = [];
        using var compiler = GrowingCompiler(database, reported);
        var plain = CompileRoot(compiler, platform);
        Assert.Null(compiler.GetFileShaderLoader().FindReplacer("Lib.X"));

        database.AddReplacer();
        Assert.Equal(plain.EffectId, CompileRoot(compiler, platform).EffectId);
        compiler.CheckReplacements();

        Assert.Equal(["Game.R", "Lib.X"], Assert.Single(reported).Order());
        Assert.Equal("Game.R", compiler.GetFileShaderLoader().FindReplacer("Lib.X"));
        var replaced = CompileRoot(compiler, platform);
        Assert.NotEqual(plain.EffectId, replaced.EffectId);
        Assert.Contains("Game.R", replaced.Bytecode.HashSources.Keys);
    }

    // A save runs a build per shader asset, and most change nothing
    [Fact]
    public void ABuildThatChangesNoReplacementReportsNothing()
    {
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        database.AddReplacer();
        List<IReadOnlyCollection<string>> reported = [];
        using var compiler = GrowingCompiler(database, reported);
        var hash = compiler.GetReplacementsHash();

        compiler.CheckReplacements();
        database.Files["shaders/Lib.Other.sdsl"] = "namespace Lib; shader Other { };";
        compiler.CheckReplacements();

        Assert.Empty(reported);
        Assert.Equal(hash, compiler.GetReplacementsHash());
    }

    [Fact]
    public void AChangeIsReportedOnce()
    {
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        List<IReadOnlyCollection<string>> reported = [];
        using var compiler = GrowingCompiler(database, reported);
        compiler.GetReplacementsHash();

        database.AddReplacer();
        compiler.CheckReplacements();
        compiler.CheckReplacements();

        Assert.Single(reported);
    }

    [Fact]
    public void AnotherReplacerReportsBothReplacersAndTheClass()
    {
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        database.AddReplacer();
        List<IReadOnlyCollection<string>> reported = [];
        using var compiler = GrowingCompiler(database, reported);
        compiler.GetReplacementsHash();

        database.Files["shaders/Lib.X.sdsl/replacedby"] = "Game.S";
        compiler.CheckReplacements();

        Assert.Equal(["Game.R", "Game.S", "Lib.X"], Assert.Single(reported).Order());
        Assert.Equal("Game.S", compiler.GetFileShaderLoader().FindReplacer("Lib.X"));
    }

    // Before the first effect, the build that opens the project: the effect id isn't computed yet, so it will see them
    [Fact]
    public void AReplacerThatAppearsBeforeAnyEffectReportsNothing()
    {
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        List<IReadOnlyCollection<string>> reported = [];
        using var compiler = GrowingCompiler(database, reported);
        var none = ShaderMixinObjectId.ComputeReplacements([]);

        database.AddReplacer();
        compiler.CheckReplacements();

        Assert.Empty(reported);
        Assert.NotEqual(none, compiler.GetReplacementsHash());
        Assert.Equal("Game.R", compiler.GetFileShaderLoader().FindReplacer("Lib.X"));
    }

    // The editor's games hold the outer compiler
    [Fact]
    public void TheCacheInFrontOfTheCompilerReportsToo()
    {
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        List<IReadOnlyCollection<string>> inner = [], reported = [];
        using var compiler = GrowingCompiler(database, inner);
        using var effects = new DatabaseFileProvider(ObjectDatabase.CreateDefaultDatabase());
        using var cache = new EffectCompilerCache(compiler, effects);
        cache.ReplacementsChanged += reported.Add;
        var plain = cache.GetReplacementsHash();

        database.AddReplacer();
        cache.CheckReplacements();

        Assert.Equal(["Game.R", "Lib.X"], Assert.Single(reported).Order());
        Assert.NotEqual(plain, cache.GetReplacementsHash());
    }

    // No game may be open, or the replaced class not used, so that no file event comes
    [Fact]
    public void AReplacerDeletedWithNoFileEventIsFoundByABuild()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("The original files of shaders are read on Windows only");

        var file = Path.Combine(sourceDir.FullName, "R.sdsl");
        File.WriteAllText(file, ReplacerShader);
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        database.AddReplacer();
        database.Files["shaders/Game.R.sdsl/path"] = file;
        List<IReadOnlyCollection<string>> reported = [];
        using var compiler = GrowingCompiler(database, reported);
        var replaced = compiler.GetReplacementsHash();

        File.Delete(file);
        compiler.CheckReplacements();

        Assert.Equal(["Game.R", "Lib.X"], Assert.Single(reported).Order());
        Assert.Null(compiler.GetFileShaderLoader().FindReplacer("Lib.X"));

        File.WriteAllText(file, ReplacerShader);
        compiler.CheckReplacements();

        Assert.Equal(2, reported.Count);
        Assert.Equal("Game.R", compiler.GetFileShaderLoader().FindReplacer("Lib.X"));
        Assert.Equal(replaced, compiler.GetReplacementsHash());
    }

    // A shader pasted under the name of a replacer that was deleted: it has its old file, with another class in it
    [Fact]
    public void AReplacerWhoseFileHoldsAnotherClassStopsReplacing()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("The original files of shaders are read on Windows only");

        var file = Path.Combine(sourceDir.FullName, "R.sdsl");
        File.WriteAllText(file, ReplacerShader);
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        database.Files["shaders/Game.R.sdsl"] = ReplacerShader;
        database.Files["shaders/Lib.X.sdsl/replacedby"] = "Game.R";
        database.Files["shaders/Game.R.sdsl/path"] = file;
        List<IReadOnlyCollection<string>> reported = [];
        using var compiler = GrowingCompiler(database, reported);
        compiler.GetReplacementsHash();

        File.WriteAllText(file, "shader Other { };");
        compiler.CheckReplacements();

        Assert.Single(reported);
        Assert.Null(compiler.GetFileShaderLoader().FindReplacer("Lib.X"));

        // A file with a parse error still holds its class, so that its error shows
        File.WriteAllText(file, ReplacerShader.Replace("return", "retur n"));
        compiler.CheckReplacements();

        Assert.Equal(2, reported.Count);
        Assert.Equal("Game.R", compiler.GetFileShaderLoader().FindReplacer("Lib.X"));
    }

    private const string NoAttribute = "namespace Game; shader R : Lib.X { override float4 Get() { return base.Get() * 2; } };";
    private const string Retargeted = """namespace Game; [Replaces(Lib.Y)] shader R : Lib.Y { };""";
    private const string ParseError = "namespace Game; shader R : Lib.X { override float4 Get() { retur n 1; } };";

    private static ShaderReplacement.BuiltFile Built(string text, string fileName = "R", bool succeeded = true) => new(text, fileName, succeeded);

    // What the editor does after the merge of a shader build
    private static void AfterBuild(GrowingDatabase database, EffectCompiler compiler, params ShaderReplacement.BuiltFile[] files)
    {
        foreach (var target in ShaderReplacement.NoLongerDeclared(EffectCompilerBase.ReadReplacements(database), files))
            database.Files.Remove($"{EffectCompilerBase.GetStoragePathFromShaderType(target)}/replacedby");
        compiler.CheckReplacements();
    }

    // The replacer was added earlier in the session: the plain effect comes back, with its id, and so does the attribute
    [Theory]
    [MemberData(nameof(Platforms))]
    public void AReplacerThatLosesItsAttributeStopsReplacingAndCanComeBack(GraphicsPlatform platform)
    {
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        List<IReadOnlyCollection<string>> reported = [];
        using var compiler = GrowingCompiler(database, reported);
        var plain = CompileRoot(compiler, platform);
        database.AddReplacer();
        compiler.CheckReplacements();
        var replaced = CompileRoot(compiler, platform);
        Assert.Contains("Game.R", replaced.Bytecode.HashSources.Keys);
        reported.Clear();

        AfterBuild(database, compiler, Built(NoAttribute));

        Assert.Equal(["Game.R", "Lib.X"], Assert.Single(reported).Order());
        Assert.Null(compiler.GetFileShaderLoader().FindReplacer("Lib.X"));
        var dropped = CompileRoot(compiler, platform);
        Assert.Equal(plain.EffectId, dropped.EffectId);
        Assert.DoesNotContain("Game.R", dropped.Bytecode.HashSources.Keys);

        database.AddReplacer();
        AfterBuild(database, compiler, Built(ReplacerShader));

        Assert.Equal(2, reported.Count);
        Assert.Equal("Game.R", compiler.GetFileShaderLoader().FindReplacer("Lib.X"));
        Assert.Equal(replaced.EffectId, CompileRoot(compiler, platform).EffectId);
    }

    // The record came with the build that opens the project
    [Fact]
    public void AReplacerOfTheProjectOpenLosesItsAttribute()
    {
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        database.AddReplacer();
        List<IReadOnlyCollection<string>> reported = [];
        using var compiler = GrowingCompiler(database, reported);
        var none = ShaderMixinObjectId.ComputeReplacements([]);
        Assert.NotEqual(none, compiler.GetReplacementsHash());

        AfterBuild(database, compiler, Built(NoAttribute));

        Assert.Equal(["Game.R", "Lib.X"], Assert.Single(reported).Order());
        Assert.Equal(none, compiler.GetReplacementsHash());
    }

    [Fact]
    public void ARetargetedReplacerDropsTheOldClassAndTakesTheNewOne()
    {
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        database.AddReplacer();
        database.Files["shaders/Lib.Y.sdsl"] = "namespace Lib; shader Y { };";
        List<IReadOnlyCollection<string>> reported = [];
        using var compiler = GrowingCompiler(database, reported);
        compiler.GetReplacementsHash();

        database.Files["shaders/Lib.Y.sdsl/replacedby"] = "Game.R";
        AfterBuild(database, compiler, Built(Retargeted));

        Assert.Equal(["Game.R", "Lib.X", "Lib.Y"], Assert.Single(reported).Order());
        Assert.Null(compiler.GetFileShaderLoader().FindReplacer("Lib.X"));
        Assert.Equal("Game.R", compiler.GetFileShaderLoader().FindReplacer("Lib.Y"));
        Assert.Equal(ShaderMixinObjectId.ComputeReplacements([new("Lib.Y", "Game.R")]), compiler.GetReplacementsHash());
    }

    // What is in effect stays until the file is fixed
    [Theory]
    [InlineData(NoAttribute)]
    [InlineData(Retargeted)]
    public void AFailedBuildDropsNothing(string text)
    {
        using var database = new GrowingDatabase(AppContext.BaseDirectory);
        database.AddOriginal();
        database.AddReplacer();
        List<IReadOnlyCollection<string>> reported = [];
        using var compiler = GrowingCompiler(database, reported);
        var replaced = compiler.GetReplacementsHash();

        AfterBuild(database, compiler, Built(text, succeeded: false));

        Assert.Empty(reported);
        Assert.Equal("Game.R", compiler.GetFileShaderLoader().FindReplacer("Lib.X"));
        Assert.Equal(replaced, compiler.GetReplacementsHash());

        AfterBuild(database, compiler, Built(text));

        Assert.Single(reported);
        Assert.Null(compiler.GetFileShaderLoader().FindReplacer("Lib.X"));
    }

    private static readonly KeyValuePair<string, string> XByR = new("Lib.X", "Game.R");

    [Theory]
    // It still declares that class
    [InlineData(ReplacerShader, "R", true, false)]
    [InlineData(NoAttribute, "R", true, true)]
    [InlineData(Retargeted, "R", true, true)]
    // The file has an error: it isn't known what it declares
    [InlineData(ParseError, "R", true, false)]
    // Two assets write one class: the one that failed keeps it, whatever the other says
    [InlineData(NoAttribute, "R", false, false)]
    // A class of another namespace, or of another file
    [InlineData("namespace Other; shader R : Lib.X { };", "R", true, false)]
    [InlineData("namespace Game; shader S : Lib.X { };", "S", true, false)]
    // The file of the replacer, whose class has a new name
    [InlineData("namespace Game; shader Renamed : Lib.X { };", "R", true, true)]
    // Another file holds the class
    [InlineData(NoAttribute, "OtherFile", true, true)]
    public void AReplacementIsDroppedOnlyWhenTheFileThatHoldsItSaysSo(string text, string fileName, bool succeeded, bool dropped)
    {
        var result = ShaderReplacement.NoLongerDeclared([XByR], [Built(text, fileName, succeeded)]);

        Assert.Equal(dropped ? ["Lib.X"] : [], result);
    }

    [Fact]
    public void TheTextWithAParseErrorHasOne() => Assert.NotEmpty(SDSLParser.Parse(ParseError).Errors);

    [Fact]
    public void TwoFilesThatHoldAReplacerMustBothHaveBuilt()
    {
        Assert.Empty(ShaderReplacement.NoLongerDeclared([XByR], [Built(NoAttribute), Built(ReplacerShader, "Copy", succeeded: false)]));
        Assert.Empty(ShaderReplacement.NoLongerDeclared([XByR], [Built(NoAttribute), Built(ReplacerShader, "Copy")]));
    }

    [Fact]
    public void TheRecordOfAnotherReplacerStays()
    {
        KeyValuePair<string, string> yByS = new("Lib.Y", "Game.S");
        const string other = """namespace Game; [Replaces(Lib.Y)] shader S : Lib.Y { };""";

        Assert.Equal(["Lib.X"], ShaderReplacement.NoLongerDeclared([XByR, yByS], [Built(NoAttribute), Built(other, "S")]));
        // S isn't in this build: a package built apart, or a file that was deleted
        Assert.Equal(["Lib.X"], ShaderReplacement.NoLongerDeclared([XByR, yByS], [Built(NoAttribute)]));
    }

    // The build wrote the new replacer of X over R, before this runs
    [Fact]
    public void ANewReplacerOfTheClassIsNotDropped()
        => Assert.Empty(ShaderReplacement.NoLongerDeclared([new("Lib.X", "Game.S")], [Built(NoAttribute), Built("""namespace Game; [Replaces(Lib.X)] shader S : Lib.X { };""", "S")]));

    // Its file is gone, so no build goes through it: the deleted state of the compiler holds it (see AReplacerDeletedWithNoFileEventIsFoundByABuild)
    [Fact]
    public void ADeletedReplacerKeepsItsRecordUntilItIsBack()
    {
        Assert.Empty(ShaderReplacement.NoLongerDeclared([XByR], []));
        Assert.Empty(ShaderReplacement.NoLongerDeclared([XByR], [Built(ReplacerShader)]));
        Assert.Equal(["Lib.X"], ShaderReplacement.NoLongerDeclared([XByR], [Built(NoAttribute)]));
    }

    [Fact]
    public void AddShaderSourceReadsTheAttribute()
    {
        using var provider = new FileSystemProvider($"/replacestest-{Guid.NewGuid():N}", AppContext.BaseDirectory);
        var manager = new ShaderSourceManager(provider);
        manager.AddShaderSource("Game.R", ReplacerShader, "R.sdsl");

        Assert.Equal("Game.R", manager.FindReplacer("Lib.X"));
        Assert.Null(manager.FindReplacer("Lib.Other"));
    }

    [Fact]
    public void TwoReplacersOfOneClassAreAnError()
    {
        using var provider = new FileSystemProvider($"/replacestest-{Guid.NewGuid():N}", AppContext.BaseDirectory);
        var manager = new ShaderSourceManager(provider);
        manager.AddShaderSource("Game.R", ReplacerShader, "R.sdsl");

        var error = Assert.Throws<InvalidOperationException>(() => manager.AddShaderSource("Game.S", """namespace Game; [Replaces(Lib.X)] shader S : Lib.X { };""", "S.sdsl"));
        Assert.Contains("SDSL0304: Lib.X has two replacers: Game.R and Game.S", error.Message);
    }

    [Fact]
    public void AddShaderSourceChecksTheReplacer()
    {
        using var provider = new FileSystemProvider($"/replacestest-{Guid.NewGuid():N}", AppContext.BaseDirectory);
        var manager = new ShaderSourceManager(provider);
        manager.AddShaderSource("Lib.X", OriginalShader, "X.sdsl");

        var error = Assert.Throws<InvalidOperationException>(() => manager.AddShaderSource("Game.R", """namespace Game; [Replaces(Lib.X)] shader R { };""", "R.sdsl"));
        Assert.StartsWith("SDSL0305", error.Message);
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