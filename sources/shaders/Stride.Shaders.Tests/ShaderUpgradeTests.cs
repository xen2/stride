// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Assets;
using Stride.Core.Yaml;
using Stride.Rendering;
using Stride.Shaders.Parsers.Tests.Corpus;
using Stride.Shaders.Compilers.SDSL;
using Stride.Shaders.Parsing.Upgrade;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// The shader source upgrade (<see cref="ShaderUpgrader"/>): the engine and samples are upgraded by it, and it gives the same
/// result as the steps done by hand in the past.
/// </summary>
public class ShaderUpgradeTests
{
    static string RepositoryRoot => ShaderCorpus.RepositoryRoot;

    // The engine shaders are one project; each sample is a project using them
    static readonly string[] EngineRoots = ["sources/engine", "sources/plugins", "sources/editor"];

    static string EngineShaderClassesFile => Path.Combine(RepositoryRoot, "sources", "shaders", "Stride.Shaders.Parsers", "Upgrade", "EngineShaders.txt");

    // Written back with their byte order mark
    static readonly HashSet<string> FilesWithBom = [];

    static Dictionary<string, string> ReadShaders(params string[] roots)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var rootPath = Path.Combine(RepositoryRoot, root);
            if (!Directory.Exists(rootPath))
                continue;
            foreach (var file in Directory.EnumerateFiles(rootPath, "*.sdsl", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(rootPath, "*.sdfx", SearchOption.AllDirectories)))
            {
                var relative = Path.GetRelativePath(RepositoryRoot, file).Replace('\\', '/');
                if (relative.Contains("/bin/") || relative.Contains("/obj/"))
                    continue;
                if (File.ReadAllBytes(file).AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
                    FilesWithBom.Add(relative);
                files[relative] = File.ReadAllText(file);
            }
        }
        return files;
    }

    // samples/<category>/<sample>
    static IEnumerable<string> AllSampleRoots() => Directory.EnumerateDirectories(Path.Combine(RepositoryRoot, "samples"))
        .SelectMany(Directory.EnumerateDirectories)
        .Select(x => Path.GetRelativePath(RepositoryRoot, x).Replace('\\', '/'))
        .Order(StringComparer.Ordinal);

    static IEnumerable<string> SampleRoots() => AllSampleRoots().Where(x => ReadShaders(x).Count > 0);

    static void Write(IReadOnlyDictionary<string, string> before, ShaderUpgradeResult result)
    {
        foreach (var (path, text) in result.Files)
        {
            if (before[path] != text)
            {
                File.WriteAllText(Path.Combine(RepositoryRoot, path), text, new System.Text.UTF8Encoding(FilesWithBom.Contains(path)));
                Console.WriteLine($"Upgraded {path}");
            }
        }
        foreach (var note in result.Notes)
            Console.WriteLine($"Note: {note}");
    }

    /// <summary>
    /// Upgrades the engine, editor and sample shaders in place, and rewrites <c>EngineShaders.txt</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public void UpgradeRepository()
    {
        var engine = ReadShaders(EngineRoots);
        var upgraded = ShaderUpgrader.Upgrade(engine, engine: null);
        Write(engine, upgraded);

        var engineClasses = EngineShaderClasses.Write(upgraded.Files);
        File.WriteAllText(EngineShaderClassesFile, engineClasses);

        foreach (var sampleRoot in SampleRoots())
        {
            var sample = ReadShaders(sampleRoot);
            Write(sample, ShaderUpgrader.Upgrade(sample, EngineShaderClasses.Parse(engineClasses), implicitUsings: ShaderImplicitUsings.List));
        }
    }

    /// <summary>
    /// Writes the full names of shaders and effects in the effect logs, compositors and materials of the repository.
    /// </summary>
    [Fact(Explicit = true)]
    public void UpgradeRepositoryData()
    {
        foreach (var (file, text, upgraded) in RepositoryData())
        {
            if (upgraded != text)
                File.WriteAllText(file, upgraded, new System.Text.UTF8Encoding(File.ReadAllBytes(file).AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])));
        }
    }

    // Each data file of the samples, sources and tests, and its text after the upgrade
    static IEnumerable<(string File, string Text, string Upgraded)> RepositoryData()
    {
        var engine = EngineShaderClasses.Parse(File.ReadAllText(EngineShaderClassesFile));
        foreach (var root in AllSampleRoots().Concat(["sources", "tests"]))
        {
            var rootPath = Path.Combine(RepositoryRoot, root);
            // Test.sdsl is a test shader: EffectName: Test in the compositors is a placeholder
            var classes = engine.WithProject(ReadShaders(root).Where(x => Path.GetFileName(x.Key) != "Test.sdsl").Select(x => x.Value));
            foreach (var pattern in new[] { "*.sdeffectlog", "*.sdgfxcomp", "*.sdmat", "*.sdscene", "*.sdprefab" })
            {
                foreach (var file in Directory.EnumerateFiles(rootPath, pattern, SearchOption.AllDirectories))
                {
                    var relative = file.Replace('\\', '/');
                    if (relative.Contains("/bin/") || relative.Contains("/obj/"))
                        continue;
                    var text = File.ReadAllText(file);
                    yield return (file, text, ShaderDataUpgrader.Upgrade(file, text, classes));
                }
            }
        }
    }

    // A template is stamped with the engine that packs it, so no upgrade runs on a new project: the data goes in as committed.
    // A build doesn't catch a compositor: its archetype doesn't change it, and the game fails when it first draws.
    [Fact]
    public void RepositoryDataHasFullNames()
    {
        var stale = RepositoryData().Where(x => x.Upgraded != x.Text).Select(x => Path.GetRelativePath(RepositoryRoot, x.File)).ToList();

        Assert.True(stale.Count == 0, $"Short shader or effect names (run the explicit test {nameof(UpgradeRepositoryData)}):{Environment.NewLine}{string.Join(Environment.NewLine, stale)}");
    }

    private const string DataEngine =
        "Texturing\t0\tmodule\t-\t-\t\t\tStride.Rendering.Texturing\tTexture0\n" +
        "ComputeColor\t0\tshader\t-\t-\t\t\tStride.Rendering.Materials.ComputeColor\t\n" +
        "MyEffect\t0\teffect\t-\t-\t\t\tStride.Rendering.MyEffect\t\n";

    [Fact]
    public void EffectLogsTakeFullNames()
    {
        var log = """
            !EffectCompileRequest
            EffectName: MyEffect.Child
            UsedParameters:
                Material.PixelStageSurfaceShaders: !ShaderMixinSource
                    Mixins:
                        - !ShaderClassSource
                            ClassName: ComputeColor
                            GenericArguments:
                                - Texturing.Texture0
                                - Material.Sampler.i0
                                - TEXCOORD0
                        - !ShaderClassSource
                            ClassName: MyOwnShader
                ComputeEffectShader.ComputeShaderName: MyEffect
                Texturing.Texture0.first: 3
                Other: !ShaderMixinGeneratorSource
                    Name: MyEffect
            """;
        var engine = EngineShaderClasses.Parse(DataEngine);

        var upgraded = ShaderDataUpgrader.Upgrade("Log.sdeffectlog", log, engine);

        Assert.Equal(log
            .Replace("EffectName: MyEffect.Child", "EffectName: Stride.Rendering.MyEffect.Child")
            .Replace("ClassName: ComputeColor", "ClassName: Stride.Rendering.Materials.ComputeColor")
            .Replace("- Texturing.Texture0", "- Stride.Rendering.Texturing.Texture0")
            .Replace("ComputeShaderName: MyEffect", "ComputeShaderName: Stride.Rendering.MyEffect")
            .Replace("Texturing.Texture0.first:", "Stride.Rendering.Texturing.Texture0.first:")
            .Replace("    Name: MyEffect", "    Name: Stride.Rendering.MyEffect"), upgraded);
        Assert.Equal(upgraded, ShaderDataUpgrader.Upgrade("Log.sdeffectlog", upgraded, engine));
    }

    // A placeholder and a stage name aren't effects
    [Fact]
    public void CompositorsOnlyChangeEffectNames()
    {
        var compositor = "    Name: MyEffect\n    EffectName: MyEffect.ShadowMapCaster\n    EffectName: Test\n    EffectName: null\n    EffectSlotName: MyEffect\n";

        var upgraded = ShaderDataUpgrader.Upgrade("Compositor.sdgfxcomp", compositor, EngineShaderClasses.Parse(DataEngine));

        Assert.Equal("    Name: MyEffect\n    EffectName: Stride.Rendering.MyEffect.ShadowMapCaster\n    EffectName: Test\n    EffectName: null\n    EffectSlotName: MyEffect\n", upgraded);
    }

    // In an asset with an archetype, a member it overrides has a mark after its name
    [Fact]
    public void OverriddenNamesAreUpgraded()
    {
        var classes = EngineShaderClasses.Parse(DataEngine).WithProject(["namespace Game; shader Own { };"]);

        Assert.Equal("    EffectName*: Stride.Rendering.MyEffect\n    EffectName!: Game.Own.Child\n    EffectName*: Test\n",
            ShaderDataUpgrader.Upgrade("Compositor.sdgfxcomp", "    EffectName*: MyEffect\n    EffectName!: Own.Child\n    EffectName*: Test\n", classes));
        Assert.Equal("    MixinReference*: Game.Own\n",
            ShaderDataUpgrader.Upgrade("Material.sdmat", "    MixinReference*: Own\n", classes));
    }

    // A class of the project wins over an engine class of the same name, and one with no namespace keeps its name
    [Theory]
    [InlineData("Material.sdmat")]
    [InlineData("Scene.sdscene")]
    [InlineData("Prefab.sdprefab")]
    public void MixinReferencesNameTheClassOfTheProject(string file)
    {
        var classes = EngineShaderClasses.Parse(DataEngine).WithProject(["shader ComputeColor { };", "namespace Game; shader Own { };"]);

        var upgraded = ShaderDataUpgrader.Upgrade(file, "    MixinReference: ComputeColor\n    MixinReference: Own\n    MixinReference: Texturing\n", classes);

        Assert.Equal("    MixinReference: ComputeColor\n    MixinReference: Game.Own\n    MixinReference: Stride.Rendering.Texturing\n", upgraded);
    }

    [Fact]
    public void MaterialsNameTheShaderOfTheProject()
    {
        var material = "    MixinReference: MyShader\n    MixinReference: Global\n    MixinReference: 00000000-0000-0000-0000-000000000000:MyShader\n";
        var classes = EngineShaderClasses.Parse(DataEngine).WithProject(["namespace Game.Effects; shader MyShader { };", "shader Global { };"]);

        var upgraded = ShaderDataUpgrader.Upgrade("Material.sdmat", material, classes);

        Assert.Equal("    MixinReference: Game.Effects.MyShader\n    MixinReference: Global\n    MixinReference: 00000000-0000-0000-0000-000000000000:MyShader\n", upgraded);
    }

    // The key of a member of a namespaced class has the full name of the class. A global class keeps its keys, and a project class hides the engine class of its name.
    [Fact]
    public void EffectLogsNameTheKeysOfTheProject()
    {
        var log = "UsedParameters:\n    Mine.Value: 1\n    Mine.Value.first: 2\n    Mine.NotAKey: 3\n    Global.Value: 4\n    Texturing.Texture0: 5\n    Game.Mine.Value: 6\n    A: !ShaderClassSource\n        GenericArguments:\n            - Mine.Value\n";
        var classes = EngineShaderClasses.Parse(DataEngine).WithProject(["namespace Game; shader Mine { float Value; };", "shader Global { float Value; };", "shader Texturing { };"]);

        var upgraded = ShaderDataUpgrader.Upgrade("Log.sdeffectlog", log, classes);

        Assert.Equal("UsedParameters:\n    Game.Mine.Value: 1\n    Game.Mine.Value.first: 2\n    Mine.NotAKey: 3\n    Global.Value: 4\n    Texturing.Texture0: 5\n    Game.Mine.Value: 6\n    A: !ShaderClassSource\n        GenericArguments:\n            - Game.Mine.Value\n", upgraded);
    }

    private static readonly EngineShaderClasses DataClasses = EngineShaderClasses.Parse(DataEngine).WithProject(["namespace Game; shader Mine { float Value; };"]);

    private static string UpgradeLog(string text) => ShaderDataUpgrader.Upgrade("Log.sdeffectlog", text, DataClasses);

    // The generic arguments are written in both forms, and they are keys only there
    [Fact]
    public void GenericArgumentsAreKeysInEitherForm()
    {
        Assert.Equal("GenericArguments: [Game.Mine.Value, TEXCOORD0, 'Game.Mine.Value']\n", UpgradeLog("GenericArguments: [Mine.Value, TEXCOORD0, 'Mine.Value']\n"));
        Assert.Equal("Other:\n    - Mine.Value\n", UpgradeLog("Other:\n    - Mine.Value\n"));
    }

    // A dotted key is the key of a shader member only as a direct key of the used parameters
    [Fact]
    public void AKeyOutsideTheUsedParametersStays()
    {
        const string log = "Mine.Value: 1\nOther:\n    Mine.Value: 2\nUsedParameters:\n    A:\n        Mine.Value: 3\n";

        Assert.Equal(log, UpgradeLog(log));
    }

    // Macros have a Name too
    [Fact]
    public void ANameIsAnEffectOnlyInAGeneratorSource()
    {
        const string text = "A: !ShaderMixinGeneratorSource\n    Other: 1\n    Name: MyEffect\nMacros:\n    - Name: MyEffect\n      Definition: 1\nB: !OtherSource\n    Name: MyEffect\n";

        Assert.Equal(text.Replace("    Name: MyEffect\nMacros", "    Name: Stride.Rendering.MyEffect\nMacros"), UpgradeLog(text));
    }

    // The upgrade edits the places it found: comments, spacing, line endings and characters before them stay
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void EveryOtherCharacterOfTheFileStays(string newLine)
    {
        const string emoji = "\U0001F3A8";
        var lines = new[]
        {
            "%YAML 1.1",
            $"# {emoji} EffectName: MyEffect",
            "--- !EffectCompileRequest",
            "",
            $"Before{emoji}: \"{emoji}\\U0001F3A8\\n\"   # {emoji}",
            "EffectName:    MyEffect   # EffectName: MyEffect   ",
            "   ",
            "UsedParameters: {  }",
            $"Other: '{emoji} EffectName: MyEffect'",
            "After:   1  ",
            "ClassName: ComputeColor",
        };
        var text = string.Join(newLine, lines);

        var upgraded = UpgradeLog(text);

        Assert.Equal(text.Replace("EffectName:    MyEffect", "EffectName:    Stride.Rendering.MyEffect").Replace("ClassName: ComputeColor", "ClassName: Stride.Rendering.Materials.ComputeColor"), upgraded);
    }

    // Other line breaks of YAML, and an edit after each of them
    [Theory]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void UnicodeLineBreaksKeepTheOffsets(string lineBreak)
    {
        var text = $"# comment{lineBreak}Before: 'a{lineBreak}b'{lineBreak}EffectName: MyEffect{lineBreak}";

        Assert.Equal(text.Replace("MyEffect", "Stride.Rendering.MyEffect"), UpgradeLog(text));
    }

    // A value that is quoted stays quoted
    [Theory]
    [InlineData("EffectName: \"MyEffect\"\n", "EffectName: \"Stride.Rendering.MyEffect\"\n")]
    [InlineData("EffectName: 'MyEffect.Child'\n", "EffectName: 'Stride.Rendering.MyEffect.Child'\n")]
    [InlineData("UsedParameters:\n    \"Mine.Value\": 1\n", "UsedParameters:\n    \"Game.Mine.Value\": 1\n")]
    [InlineData("EffectName: \"My\\x45ffect\"\n", "EffectName: \"Stride.Rendering.MyEffect\"\n")]
    public void QuotedNamesAreUpgraded(string text, string expected)
    {
        Assert.Equal(expected, UpgradeLog(text));
    }

    [Theory]
    // A name on the line after its key is one line of text
    [InlineData("EffectName:\n    MyEffect\n", "EffectName:\n    Stride.Rendering.MyEffect\n")]
    [InlineData("EffectName: \"My\\\n    Effect\"\n", "EffectName: \"Stride.Rendering.MyEffect\"\n")]
    // A line break in a value reads as a space, so these are not names
    [InlineData("EffectName: MyEffect.\n    Child\n", null)]
    [InlineData("EffectName: \"MyEffect.\n    Child\"\n", null)]
    [InlineData("EffectName: MyEffect Child\n", null)]
    public void AValueOnSeveralLines(string text, string? expected)
    {
        Assert.Equal(expected ?? text, UpgradeLog(text));
    }

    // A block holds any text: the code of a shader, or the YAML of something else
    [Theory]
    [InlineData("Material.sdmat", "Code: |\n    MixinReference: MyEffect\n    - Mine.Value\nMixinReference: MyEffect\n", "Code: |\n    MixinReference: MyEffect\n    - Mine.Value\nMixinReference: Stride.Rendering.MyEffect\n")]
    [InlineData("Scene.sdscene", "Code: >-\n    MixinReference: MyEffect\n", null)]
    [InlineData("Log.sdeffectlog", "ShaderSourceCode: |\n    ClassName: ComputeColor\n    EffectName: MyEffect\n    Mine.Value: 1\n", null)]
    [InlineData("Compositor.sdgfxcomp", "Comment: \"EffectName: MyEffect\"\n", null)]
    // The name itself in a block is left as written
    [InlineData("Compositor.sdgfxcomp", "EffectName: |-\n    MyEffect\n", null)]
    public void TheTextOfABlockStays(string file, string text, string? expected)
    {
        Assert.Equal(expected ?? text, ShaderDataUpgrader.Upgrade(file, text, DataClasses));
    }

    [Fact]
    public void EveryDocumentOfAFileIsUpgraded()
    {
        const string log = "---\nEffectName: MyEffect\n...\n---\n!EffectCompileRequest\nEffectName: MyEffect.Child\n---\nEffectName: Unknown\n";

        Assert.Equal("---\nEffectName: Stride.Rendering.MyEffect\n...\n---\n!EffectCompileRequest\nEffectName: Stride.Rendering.MyEffect.Child\n---\nEffectName: Unknown\n", UpgradeLog(log));
    }

    // Only a value alone is a name: an anchor and the aliases that read it stay the same
    [Theory]
    [InlineData("EffectName: &name MyEffect\nOther: *name\nEffectName: *name\n")]
    [InlineData("EffectName: !!str MyEffect\n")]
    [InlineData("EffectName: &name !!str MyEffect\n")]
    public void AnchorsAliasesAndTagsStay(string text)
    {
        Assert.Equal(text, UpgradeLog(text));
    }

    [Theory]
    [InlineData("EffectName: null\n")]
    [InlineData("EffectName: ~\n")]
    [InlineData("EffectName:\n")]
    [InlineData("EffectName: !!null\n")]
    [InlineData("")]
    [InlineData("# only a comment\n")]
    [InlineData("MyEffect\n")]
    public void ThereIsNoNameToUpgrade(string text)
    {
        Assert.Equal(text, UpgradeLog(text));
    }

    [Theory]
    [InlineData("    EffectName: MyEffect\n    - Mine.Value\n")]
    [InlineData("EffectName: 'MyEffect\n")]
    [InlineData("\tEffectName: MyEffect\n")]
    public void TextThatIsNotYamlIsAnError(string text)
    {
        Assert.ThrowsAny<YamlException>(() => UpgradeLog(text));
    }

    // Only the types of data the upgrade knows are read
    [Fact]
    public void AnotherTypeOfFileIsNotRead()
    {
        const string text = "    EffectName: MyEffect\n    - Mine.Value\n";

        Assert.Equal(text, ShaderDataUpgrader.Upgrade("File.sdtex", text, DataClasses));
    }

    /// <summary>
    /// Rewrites <c>EngineShaders.txt</c> from the engine shaders.
    /// </summary>
    [Fact(Explicit = true)]
    public void UpdateEngineShaderClasses()
    {
        File.WriteAllText(EngineShaderClassesFile, EngineShaderClasses.Write(ReadShaders(EngineRoots)));
    }

    // The list used to upgrade projects describes today's engine
    [Fact]
    public void EngineShaderClassesAreCurrent()
    {
        var expected = EngineShaderClasses.Write(ReadShaders(EngineRoots));
        Assert.True(File.ReadAllText(EngineShaderClassesFile).ReplaceLineEndings("\n") == expected,
            $"{EngineShaderClassesFile} doesn't match the engine shaders: run the explicit test {nameof(UpdateEngineShaderClasses)}");
    }

    // A shader listed as removed isn't in today's engine: its message would be wrong
    [Fact]
    public void RemovedEngineShadersAreGone()
    {
        var engine = EngineShaderClasses.Write(ReadShaders(EngineRoots));
        Assert.Empty(EngineShaderClasses.Removed.Where(x => engine.Contains($"\t{x}\t", StringComparison.Ordinal)));
    }

    static string FixturesDirectory => Path.Combine(RepositoryRoot, "tests", "shader-upgrade");

    // tests/shader-upgrade/<step>/<case>: user-style shaders in the old syntax (before/) and their upgrade (after/), with the
    // sources of a library they use (dependencies/, already upgraded) when there is one
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var directory in Directory.EnumerateDirectories(FixturesDirectory).SelectMany(Directory.EnumerateDirectories).Order(StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(FixturesDirectory, directory).Replace('\\', '/'));
        return data;
    }

    static Dictionary<string, string> ReadFixture(string fixture, string part)
    {
        var directory = Path.Combine(FixturesDirectory, fixture, part);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.sd*").ToDictionary(Path.GetFileName, File.ReadAllText, StringComparer.Ordinal)!
            : [];
    }

    /// <summary>
    /// Writes the after/ folder of every fixture from its before/ folder (review the result before committing it).
    /// </summary>
    [Fact(Explicit = true)]
    public void UpdateFixtures()
    {
        foreach (var fixture in Fixtures().Select(x => (string)x.Data))
        {
            var result = ShaderUpgrader.Upgrade(ReadFixture(fixture, "before"), EngineShaderClasses.Current, implicitUsings: ShaderImplicitUsings.List, dependencies: ReadFixture(fixture, "dependencies"));
            var directory = Path.Combine(FixturesDirectory, fixture, "after");
            Directory.CreateDirectory(directory);
            foreach (var (name, text) in result.Files)
                File.WriteAllText(Path.Combine(directory, name), text);
            foreach (var note in result.Notes)
                Console.WriteLine($"{fixture}: {note}");
        }
    }

    // The chain gives the expected sources, changes nothing when run again, and the result compiles
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FixtureUpgrade(string fixture)
    {
        var expected = ReadFixture(fixture, "after");
        var dependencies = ReadFixture(fixture, "dependencies");
        var result = ShaderUpgrader.Upgrade(ReadFixture(fixture, "before"), EngineShaderClasses.Current, implicitUsings: ShaderImplicitUsings.List, dependencies: dependencies);
        Assert.Equal(expected.Keys.Order(), result.Files.Keys.Order());
        foreach (var (name, text) in result.Files)
            Assert.Equal(expected[name].ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"));
        Assert.Empty(result.Notes.Where(x => x.Contains("dependency written for an older Stride version")));

        var again = ShaderUpgrader.Upgrade(expected, EngineShaderClasses.Current, implicitUsings: ShaderImplicitUsings.List, dependencies: dependencies);
        Assert.Empty(again.Files.Where(x => expected[x.Key] != x.Value).Select(x => x.Key));

        var loader = new ShaderLoader(Path.Combine(FixturesDirectory, fixture, "after"), Path.Combine(FixturesDirectory, fixture, "dependencies"), "./assets/Stride/SDSL");
        foreach (var name in expected.Keys.Where(x => !ShaderUpgrader.IsEffectFile(x)))
            Assert.True(loader.LoadExternalBuffer(Stride.Shaders.Parsing.ShaderFileDeclarations.StorageName(expected[name], Path.GetFileNameWithoutExtension(name)), [], out _, out _, out _), name);
    }

    // A dependency is read, never changed; one written for an older version is read as if upgraded, with a note
    [Fact]
    public void DependencyIsReadNotChanged()
    {
        var dependencies = new Dictionary<string, string> { ["MyLibSettings.sdsl"] = "shader MyLibSettings { stage float Scale; };" };
        var files = new Dictionary<string, string> { ["MyUser.sdsl"] = "shader MyUser : MyLibSettings { float Get() { return Scale; } };" };
        var result = ShaderUpgrader.Upgrade(files, EngineShaderClasses.Current, dependencies: dependencies);
        Assert.Equal(["MyUser.sdsl"], result.Files.Keys);
        Assert.Contains(result.Notes, x => x.StartsWith("MyLibSettings.sdsl: dependency written for an older Stride version", StringComparison.Ordinal));
        // Not a module: only its own upgrade decides that
        Assert.Contains(": MyLibSettings", result.Files["MyUser.sdsl"]);
    }

    // The keys of a shader written with an older spelling count too: it can be read before its own upgrade
    [Fact]
    public void WithProjectReadsAnOlderSpellingAndKeepsTheNamesOfAFileThatDoesNotParse()
    {
        var classes = EngineShaderClasses.Parse("").WithProject(["namespace Game; shader Old { stage float Value; float Other; };", "namespace Game; shader Broken { float + ; };"]);

        Assert.Equal("Game.Old.Other", classes.ToFullKeyName("Old.Other"));
        Assert.Equal("Game.Broken", classes.FullName("Broken"));
        Assert.Equal("Broken.Value", classes.ToFullKeyName("Broken.Value"));
    }

    [Theory]
    [InlineData("""[Link("Mine.Value")] float A;""", """[Link("Game.Mine.Value")] float A;""")]
    [InlineData("Param<Mine.Value> B;", "Param<Game.Mine.Value> B;")]
    [InlineData("""[Link("Lib.Value")] float A;""", """[Link("Library.Lib.Value")] float A;""")]
    [InlineData("""[Link("Global.Value")] float A;""", """[Link("Global.Value")] float A;""")]
    [InlineData("""[Link("Mine.NotAKey")] float A;""", """[Link("Mine.NotAKey")] float A;""")]
    public void SourcesNameTheKeysOfTheProject(string member, string expected)
    {
        var files = new Dictionary<string, string>
        {
            ["Mine.sdsl"] = "namespace Game; shader Mine { float Value; };\n",
            ["Global.sdsl"] = "shader Global { float Value; };\n",
            ["User.sdsl"] = $"shader User {{ {member} }};\n",
        };
        var dependencies = new Dictionary<string, string> { ["Lib.sdsl"] = "namespace Library; shader Lib { float Value; };\n" };

        var upgraded = ShaderUpgrader.Upgrade(files, EngineShaderClasses.Parse(""), dependencies: dependencies).Files["User.sdsl"];

        Assert.Contains(expected, upgraded);
    }

    // The upgraded feature works in a composition: its helpers are module code like the code calling them
    [Fact]
    public void UpgradedFeatureHelpersMixInComposition()
    {
        var loader = new ShaderLoader(Path.Combine(FixturesDirectory, "module-helpers", "feature-helpers", "after"), "./assets/Stride/SDSL");
        var source = new ShaderMixinSource
        {
            Mixins = { new ShaderClassSource(TestShader.FullName("MyDisplacementRoot")) },
            Compositions = { ["Surface"] = new ShaderMixinSource { Mixins = { new ShaderClassSource(TestShader.FullName("MyStormDisplacement")) } } },
        };
        var log = new Stride.Core.Diagnostics.LoggerResult();
        Assert.True(new ShaderMixer(loader).MergeSDSL(source, new ShaderMixer.Options(true), log, out _, out _, out _, out _),
            string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)));
    }

    // The upgraded node reads the compute thread streams in a composition (ComputeShaderBase is [Root], SDSL0119 there)
    [Fact]
    public void UpgradedComputeStreamsMixInComposition()
    {
        var loader = new ShaderLoader(Path.Combine(FixturesDirectory, "module-units", "compute-streams", "after"), "./assets/Stride/SDSL");
        var source = new ShaderMixinSource
        {
            Mixins = { new ShaderClassSource(TestShader.FullName("MyComputeRoot")) },
            Compositions = { ["Color"] = new ShaderMixinSource { Mixins = { new ShaderClassSource(TestShader.FullName("MyThreadColor")) } } },
        };
        var log = new Stride.Core.Diagnostics.LoggerResult();
        Assert.True(new ShaderMixer(loader).MergeSDSL(source, new ShaderMixer.Options(true), log, out _, out _, out _, out _),
            string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)));
    }

    // The upgraded node calls its parent's helper without `base.` (a base call to a module method fails in a composition, SDSL0208)
    [Fact]
    public void UpgradedBaseHelperCallMixesInComposition()
    {
        var loader = new ShaderLoader(Path.Combine(FixturesDirectory, "module-units", "base-helper", "after"), "./assets/Stride/SDSL");
        var source = new ShaderMixinSource
        {
            Mixins = { new ShaderClassSource(TestShader.FullName("MyBrightRoot")) },
            Compositions = { ["Color"] = new ShaderMixinSource { Mixins = { new ShaderClassSource(TestShader.FullName("MyBrightColor")) } } },
        };
        var log = new Stride.Core.Diagnostics.LoggerResult();
        Assert.True(new ShaderMixer(loader).MergeSDSL(source, new ShaderMixer.Options(true), log, out _, out _, out _, out _),
            string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)));
    }

    // Master before the `module` work (A13a): engine shaders still written with `stage`
    const string EngineBeforeModules = "195392b6d9";

    // Edits made by hand since then, not by the upgrade: file → the text the chain gives, and the text today
    static readonly Dictionary<string, (string Upgraded, string Today)[]> ManualEdits = new()
    {
        // The upgrade makes the light group a parent (it was used by name); the comment and the shorter access were written by hand
        ["sources/engine/Stride.Rendering/Rendering/Shadows/ShadowMapReceiverPointCubeMap.sdsl"] =
        [
            ("    /// Selects the shadow map and computes the shadow factor.\n", "    /// Selects the shadow map and computes the shadow factor.\n    /// Mixed into the point light group, whose light positions it reads.\n"),
            ("LightPointGroup<TLightCount>.Lights[lightIndex]", "Lights[lightIndex]"),
        ],
        // The thread streams moved to ComputeShaderStreams, which shaders used in compositions import ([Root] is checked there)
        ["sources/engine/Stride.Rendering/Rendering/ComputeEffect/ComputeShaderBase.sdsl"] =
        [
            ("/// ThreadNumberZ: Macro - number of threads on the Z axis.\n", "/// ThreadNumberZ: Macro - number of threads on the Z axis.\n/// A shader used in a composition reads the thread streams by importing <see cref=\"ComputeShaderStreams\"/>.\n"),
            ("{\n    stream uint3 GroupId : SV_GroupID;\n    stream uint3 DispatchThreadId : SV_DispatchThreadID;\n    stream uint3 GroupThreadId : SV_GroupThreadID;\n"
                + "    stream uint  GroupIndex : SV_GroupIndex;\n\n    stream uint3 ThreadGroupCount;\n    stream uint  ThreadCountPerGroup;\n"
                + "    stream uint  ThreadGroupIndex;\n    \n    stream int ThreadCountX;\n    stream int ThreadCountY;\n    stream int ThreadCountZ;\n\n",
                "{\n    import ComputeShaderStreams;\n\n"),
            ("    }\n\n    bool IsFirstThreadOfGroup()\n    {\n        return streams.GroupThreadId.x == 0 && streams.GroupThreadId.y == 0 && streams.GroupThreadId.z == 0;\n    }\n};", "    }\n};"),
            ("[Link(\"ComputeShaderBase.", "[Link(\"Stride.Rendering.ComputeShaderBase."),
        ],
        // The namespace of the effect is the namespace of the file
        ["sources/engine/Stride.Graphics/Shaders/SpriteAlphaCutoff.sdsl"] =
        [
            ("namespace Stride.Rendering\n{\n    effect SpriteAlphaCutoffEffect\n    {\n        using params SpriteBaseKeys;\n        mixin SpriteAlphaCutoff<SpriteBaseKeys.ColorIsSRgb>;\n    };\n}\n",
                "effect SpriteAlphaCutoffEffect\n{\n    using params SpriteBaseKeys;\n    mixin SpriteAlphaCutoff<SpriteBaseKeys.ColorIsSRgb>;\n};\n"),
        ],
        // Key names are full names
        ["sources/plugins/Voxels/Stride.Voxels/Voxels/Voxelization/Modifiers/Appliers/Anisotropic/VoxelModifierApplierOpacifyAnisotropic.sdsl"] =
        [
            ("[Link(\"VoxelModifierApplierOpacifyIsotropic.", "[Link(\"Stride.Rendering.VoxelModifierApplierOpacifyIsotropic."),
        ],
        ["sources/plugins/Voxels/Stride.Voxels/Voxels/Voxelization/Modifiers/Appliers/AnisotropicPaired/VoxelModifierApplierOpacifyAnisotropicPaired.sdsl"] =
        [
            ("[Link(\"VoxelModifierApplierOpacifyIsotropic.", "[Link(\"Stride.Rendering.VoxelModifierApplierOpacifyIsotropic."),
        ],
        ["sources/plugins/Voxels/Stride.Voxels/Voxels/Voxelization/VoxelStorage/Processing/BufferToTextureColumns.sdsl"] =
        [
            ("[Link(\"BufferToTexture.", "[Link(\"Stride.Rendering.Voxels.BufferToTexture."),
        ],
    };

    // In an effect file and in a shader file, which both can declare effects
    [Fact]
    public void PartialEffectBecomesEffect()
    {
        const string effect = "// partial effect in a comment\nnamespace N\n{\n    partial  effect E { mixin A; };\n    extend effect F { mixin A; };\n}\n";
        const string shader = "partial effect G { mixin A; };\nshader S { };\n";

        var files = ShaderUpgrader.Upgrade(new Dictionary<string, string> { ["E.sdfx"] = effect, ["S.sdsl"] = shader }, engine: null).Files;

        Assert.Equal("// partial effect in a comment\nnamespace N\n{\n    effect E { mixin A; };\n    extend effect F { mixin A; };\n}\n", files["E.sdfx"]);
        Assert.Equal("effect G { mixin A; };\nshader S { };\n", files["S.sdsl"]);
        Assert.Equal(files["E.sdfx"], ShaderUpgrader.Upgrade(new Dictionary<string, string> { ["E.sdfx"] = files["E.sdfx"] }, engine: null).Files["E.sdfx"]);
    }

    // The engine shaders of that commit, upgraded, are today's engine shaders apart from the edits made by hand
    [Fact]
    public void EngineBeforeModulesUpgradesToToday()
    {
        var old = ReadShadersAt(EngineBeforeModules, EngineRoots);
        if (old == null)
            Assert.Skip($"Commit {EngineBeforeModules} is not available (shallow clone?)");

        var today = ReadShaders(EngineRoots);
        var upgraded = ShaderUpgrader.Upgrade(old, engine: null).Files;
        var differences = new List<string>();
        foreach (var path in today.Keys.Intersect(upgraded.Keys).Order(StringComparer.Ordinal))
        {
            var text = upgraded[path].ReplaceLineEndings("\n");
            foreach (var (upgradedText, todayText) in ManualEdits.GetValueOrDefault(path) ?? [])
                text = text.Replace(upgradedText, todayText);
            // The kinds the upgrade writes are not the ones the engine chose by hand
            text = WithoutCallSyntax(WithoutEngineNamespaceLines(WithoutKinds(text)));
            // [Root], [Pure] and the kinds, phases and orders of extension points are written by hand (the upgrade takes the engine's [Root] markers from EngineShaders.txt)
            var expected = WithoutCallSyntax(WithoutEngineNamespaceLines(WithoutKinds(System.Text.RegularExpressions.Regex.Replace(today[path].ReplaceLineEndings("\n"), @"^[ \t]*\[(Root|Pure|(Phases|Phase|MixinOrder|Drops)(\([^)\n]*\))?)\]\n", "", System.Text.RegularExpressions.RegexOptions.Multiline))));
            if (text != expected)
                differences.Add(path);
        }
        Assert.True(differences.Count == 0, $"Upgraded from {EngineBeforeModules}, differs from today: {string.Join(", ", differences)}");
    }

    static string WithoutKinds(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"^([ \t]*(?:module[ \t]+)?)(?:pipeline|replace|around)[ \t]+", "$1", System.Text.RegularExpressions.RegexOptions.Multiline);

    // The engine moved to extend after that commit: what is left of a method is its body, whichever way the override is written
    static string WithoutCallSyntax(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[ \t]*extend [^\n]*\n", "override\n", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[ \t]*(?:(?:module|override)[ \t]+)+[^\n(]*\w+[ \t]*\([^\n]*\)[ \t]*\n", "override\n", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[ \t]*(?:base\.\w+|next)\([^\n;]*\);[ \t]*\n", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\bbase\.\w+\(", "next(");
        return System.Text.RegularExpressions.Regex.Replace(text, @"\n[ \t]*\n", "\n");
    }

    static string AddKind(string owner, string overrider, IReadOnlyDictionary<string, string>? dependencies = null, List<string>? notes = null)
    {
        var result = ShaderUpgrader.Upgrade(new Dictionary<string, string> { ["Owner.sdsl"] = owner, ["Overrider.sdsl"] = overrider }, engine: null, lastStep: "add-kinds", dependencies: dependencies);
        notes?.AddRange(result.Notes);
        return result.Files["Owner.sdsl"].ReplaceLineEndings("\n");
    }

    [Theory]
    [InlineData("override void F() { }", "replace")]
    [InlineData("override void F() { base.F(); }", "pipeline")]
    // base from another method reaches the same chain
    [InlineData("override void F() { } void Helper() { base.F(); }", "pipeline")]
    public void TheKindComesFromTheOverrides(string member, string kind)
    {
        var owner = "module Owner\n{\n    void F() { }\n}\n";

        Assert.Equal($"module Owner\n{{\n    {kind} void F() {{ }}\n}}\n", AddKind(owner, $"module Other {{ import Owner; {member} }}"));
    }

    // The upgrader decides with all the overrides: one that calls base is enough, whatever the others do
    [Fact]
    public void TheKindSeesEveryOverride()
    {
        var files = new Dictionary<string, string>
        {
            ["Owner.sdsl"] = "module Owner\n{\n    void F() { }\n}\n",
            ["A.sdsl"] = "module A { import Owner; override void F() { } }",
            ["B.sdsl"] = "module B { import A; override void F() { base.F(); } }",
            ["C.sdsl"] = "module C { import Owner; override void F() { } }",
        };

        Assert.Contains("pipeline void F()", ShaderUpgrader.Upgrade(files, engine: null, lastStep: "add-kinds").Files["Owner.sdsl"].ReplaceLineEndings("\n"));
    }

    [Fact]
    public void AKindGoesOnAModuleMethodOfAShader()
    {
        var owner = "shader Owner\n{\n    module void F() { }\n    void G() { }\n}\n";
        var result = AddKind(owner, "shader Other : Owner { module override void F() { } override void G() { } }");

        Assert.Equal("shader Owner\n{\n    module replace void F() { }\n    void G() { }\n}\n", result);
    }

    [Theory]
    // Already declared
    [InlineData("module Owner\n{\n    around float F() { return 1; }\n}\n", "override float F() { return 2; }")]
    // Implementing an abstract method is not a contribution
    [InlineData("module Owner\n{\n    abstract void F();\n}\n", "override void F() { }")]
    // No override
    [InlineData("module Owner\n{\n    void F() { }\n}\n", "void G() { }")]
    public void NoKindIsNeeded(string owner, string member)
    {
        Assert.Equal(owner, AddKind(owner, $"module Other {{ import Owner; {member} }}"));
    }

    // VL's ShaderUtils.r2d: only the overload that has overrides
    [Fact]
    public void AKindGoesOnTheOverloadThatIsOverridden()
    {
        var owner = "module Owner\n{\n    float2 F(float2 a, float2 b, float c) { return a; }\n\n    float2 F(float2 a, inout float c) { return a; }\n}\n";
        var result = AddKind(owner, "module Other { import Owner; override float2 F(float2 x, inout float y) { return x; } }");

        Assert.Equal("module Owner\n{\n    float2 F(float2 a, float2 b, float c) { return a; }\n\n    replace float2 F(float2 a, inout float c) { return a; }\n}\n", result);
    }

    [Fact]
    public void AddingAKindAgainChangesNothing()
    {
        var once = AddKind("module Owner\n{\n    void F() { }\n}\n", "module Other { import Owner; override void F() { } }");

        Assert.Equal(once, AddKind(once, "module Other { import Owner; override void F() { } }"));
    }

    // The sources of a library are read, never changed
    [Fact]
    public void AMethodOfADependencyGetsANote()
    {
        var notes = new List<string>();
        var dependencies = new Dictionary<string, string> { ["Library.sdsl"] = "module Library\n{\n    void F() { }\n}\n" };
        AddKind("module Owner { }", "module Other { import Library; override void F() { } }", dependencies, notes);

        Assert.Contains(notes, x => x.Contains("error SDSL0246. Write patch Library.F instead of override, or ask the author of Library to write", StringComparison.Ordinal));
    }

    // The file-scoped namespace and its usings were added by hand after that commit
    static string WithoutEngineNamespaceLines(string text)
        => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(text.TrimStart('﻿'), @"^(namespace Stride\.Rendering;|using Stride\.Rendering\.\w+;)\n", "", System.Text.RegularExpressions.RegexOptions.Multiline),
            @"\n{2,}", "\n").TrimStart('\n');

    // The .sdsl files under the roots at a commit (null if git or the commit is not available)
    static Dictionary<string, string>? ReadShadersAt(string commit, string[] roots)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git", ["archive", "--format=zip", commit, "--", .. roots])
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            var archive = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(archive);
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                return null;

            archive.Position = 0;
            using var zip = new System.IO.Compression.ZipArchive(archive);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".sdsl") || entry.FullName.Contains("/bin/") || entry.FullName.Contains("/obj/"))
                    continue;
                using var reader = new StreamReader(entry.Open());
                files[entry.FullName] = reader.ReadToEnd();
            }
            return files;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // Shaders that need the macros of their effect to compile
    static readonly Dictionary<string, Stride.Shaders.ShaderMacro[]> ShaderMacros = new()
    {
        // Override methods taking 12 control points (InputControlPointCount defaults to 3)
        ["TessellationAE2"] = [new("InputControlPointCount", "12")],
        ["TessellationAE3"] = [new("InputControlPointCount", "12")],
        ["TessellationAE4"] = [new("InputControlPointCount", "12")],
    };

    // Shaders that don't compile alone, for a reason not related to the upgrade
    static readonly HashSet<string> KnownFailures =
    [
        // Casts a float4 to a struct, which the compiler doesn't support
        "LightTiling",
    ];

    // Every engine and sample shader compiles on its own, also those no known effect uses (the corpus doesn't see them), with no
    // warning (the corpus loader reports them as errors)
    [Fact]
    public void EveryShaderCompiles()
    {
        var loader = ShaderCorpus.CreateLoader();
        var failures = new List<string>();
        foreach (var (path, text) in ReadShaders([.. EngineRoots, "samples"]).Where(x => !ShaderUpgrader.IsEffectFile(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (KnownFailures.Contains(name))
                continue;
            try
            {
                if (!loader.LoadExternalBuffer(Stride.Shaders.Parsing.ShaderFileDeclarations.StorageName(text, name), ShaderMacros.GetValueOrDefault(name) ?? [], out _, out _, out _))
                    failures.Add(path);
            }
            catch (Exception e)
            {
                failures.Add($"{path}: {e.Message.Split('\n')[0]}");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // The engine and samples are upgraded already: the upgrade changes nothing
    [Fact]
    public void RepositoryIsUpgraded()
    {
        var engine = ReadShaders(EngineRoots);
        var result = ShaderUpgrader.Upgrade(engine, engine: null);
        Assert.Empty(result.Files.Where(x => engine[x.Key] != x.Value).Select(x => x.Key));

        foreach (var sampleRoot in SampleRoots())
        {
            var sample = ReadShaders(sampleRoot);
            var sampleResult = ShaderUpgrader.Upgrade(sample, EngineShaderClasses.Current, implicitUsings: ShaderImplicitUsings.List);
            Assert.Empty(sampleResult.Files.Where(x => sample[x.Key] != x.Value).Select(x => x.Key));
        }
    }
}
