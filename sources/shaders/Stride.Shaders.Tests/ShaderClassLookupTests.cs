// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Text;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Rendering;
using Stride.Shaders.Compiler;
using Stride.Shaders.Compilers;
using Stride.Shaders.Compilers.SDSL;
using Stride.Shaders.Parsing;
using Stride.Shaders.Spirv.Core.Buffers;
using Stride.Shaders.Spirv.Tools;
using Spv = Stride.Shaders.Spirv.Tools.Spv;

namespace Stride.Shaders.Parsers.Tests;

/// <summary>
/// A class is found by its own name, whichever file declares it: under its own URL in a database, by reading a source folder once otherwise.
/// Two files that declare one class are SDSL0301 in a source folder as they are in a build.
/// </summary>
public class ShaderClassLookupTests : IDisposable
{
    public enum Lookup
    {
        Database,
        Folder,
        TestLoader,
    }

    private const string Namespace = "Lookup.Test";

    // The SPIR-V cache lives in the application cache, which outlives the test run: fresh names per run.
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly DirectoryInfo sourceDir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"class-lookup-{Guid.NewGuid():N}"));
    private readonly FileSystemProvider folder;

    public ShaderClassLookupTests()
    {
        folder = new FileSystemProvider($"/classlookup-{Guid.NewGuid():N}", AppContext.BaseDirectory);
    }

    public void Dispose()
    {
        folder.Dispose();
        sourceDir.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Bar => $"Bar{id}";

    private string Foo => $"Foo{id}";

    private string Stuff => $"Stuff{id}";

    private string Full(string name) => $"{Namespace}.{name}";

    private string Write(string fileName, string code)
    {
        var path = Path.Combine(sourceDir.FullName, $"{fileName}.sdsl");
        File.WriteAllText(path, code);
        return path;
    }

    private ShaderLoaderBase NewLoader(Lookup lookup)
    {
        switch (lookup)
        {
            case Lookup.Database:
                var database = new FileShaderLoader(BuiltDatabase.Build(sourceDir));
                database.SourceManager.LookupDirectoryList.Add("shaders");
                database.ImplicitUsings = ShaderImplicitUsings.List;
                return database;
            case Lookup.Folder:
                var loose = new FileShaderLoader(folder);
                loose.SourceManager.LookupDirectoryList.Add(sourceDir.Name);
                loose.SourceManager.LookupDirectoryList.Add("assets/Stride/SDSL");
                loose.ImplicitUsings = ShaderImplicitUsings.List;
                return loose;
            default:
                return new ShaderLoader(sourceDir.FullName, "./assets/Stride/SDSL");
        }
    }

    private static string Load(ShaderLoaderBase loader, string name, params ShaderMacro[] macros)
    {
        Assert.True(loader.LoadExternalBuffer(name, macros, out var buffer, out _, out _), $"could not load shader {name}");
        return Spv.Dis(buffer, DisassemblerFlags.Name);
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AskingForAnyClassOfAFileCompilesItsFileAndReturnsThatClass(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { int FooValue() { return 1; } };
            shader {{Bar}} { int BarValue() { return 2; } };
            """);

        var foo = Load(NewLoader(lookup), Full(Foo));
        Assert.Contains("FooValue", foo);
        Assert.DoesNotContain("BarValue", foo);

        var bar = Load(NewLoader(lookup), Full(Bar));
        Assert.Contains("BarValue", bar);
        Assert.DoesNotContain("FooValue", bar);
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AClassInASecondNamespaceOfAFileIsFound(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}} { shader {{Foo}} { int FooValue() { return 1; } }; }
            namespace Other.Test { shader {{Bar}} { int BarValue() { return 2; } }; }
            """);

        Assert.Contains("BarValue", Load(NewLoader(lookup), $"Other.Test.{Bar}"));
    }

    // The file's name is no class, so nothing is found under it
    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AFileCanBeNamedLikeNoneOfItsClasses(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Bar}} { int BarValue() { return 2; } };
            """);

        var loader = NewLoader(lookup);
        loader.WarningsAsErrors = true;
        Assert.Contains("BarValue", Load(loader, Full(Bar)));
        Assert.False(loader.Exists(Full(Stuff)));
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AClassCanInheritAClassDeclaredLaterInItsFile(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} : {{Bar}} { int FooValue() { return BarValue(); } };
            shader {{Bar}} { int BarValue() { return 2; } };
            """);

        Assert.Contains("FooValue", Load(NewLoader(lookup), Full(Foo)));
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AClassCanInheritAClassDeclaredEarlierInItsFile(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Bar}} { int BarValue() { return 2; } };
            shader {{Foo}} : {{Bar}} { int FooValue() { return BarValue(); } };
            """);

        Assert.Contains("FooValue", Load(NewLoader(lookup), Full(Foo)));
    }

    [Theory]
    [InlineData(Lookup.Database, true)]
    [InlineData(Lookup.Folder, true)]
    [InlineData(Lookup.TestLoader, true)]
    [InlineData(Lookup.Database, false)]
    [InlineData(Lookup.Folder, false)]
    [InlineData(Lookup.TestLoader, false)]
    public void AClassCanImportAModuleOfItsFile(Lookup lookup, bool moduleFirst)
    {
        var module = $$"""module {{Bar}} { int BarValue() { return 2; } }""";
        var shader = $$"""shader {{Foo}} { import {{Bar}}; int FooValue() { return BarValue(); } };""";
        Write(Stuff, $$"""
            namespace {{Namespace}};
            {{(moduleFirst ? module : shader)}}
            {{(moduleFirst ? shader : module)}}
            """);

        Assert.Contains("FooValue", Load(NewLoader(lookup), Full(Foo)));
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AModuleCanImportAModuleDeclaredEarlierInItsFile(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            module {{Bar}} { int BarValue() { return 2; } }
            module {{Foo}} { import {{Bar}}; int FooValue() { return BarValue(); } }
            """);

        Assert.Contains("FooValue", Load(NewLoader(lookup), Full(Foo)));
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AClassCanInheritAModuleDeclaredEarlierInItsFile(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            module {{Bar}} { int BarValue() { return 2; } }
            shader {{Foo}} : {{Bar}} { int FooValue() { return BarValue(); } };
            """);

        Assert.Contains("FooValue", Load(NewLoader(lookup), Full(Foo)));
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    public void AMixUsesClassesAndModulesDeclaredEarlierInItsFile(Lookup lookup)
    {
        var module = $"Module{id}";
        var root = $"Root{id}";
        Write(Stuff, $$"""
            namespace {{Namespace}};
            module {{module}} { int ModuleValue() { return 3; } }
            shader {{Foo}} { import {{module}}; int FooValue() { return ModuleValue(); } };
            shader {{root}} : ComputeShaderBase, {{Foo}}
            {
                RWBuffer<int> Out;
                override void Compute() { Out[0] = FooValue(); }
            };
            """);

        var loader = (FileShaderLoader)NewLoader(lookup);
        Mix(loader, Full(root), out var sources);

        Assert.True(sources.ContainsKey(Full(Foo)));
        Assert.True(sources.ContainsKey(Full(module)));
    }

    // A's file holds X, and C (in another file) inherits X while A inherits C: no class depends on itself
    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void TwoFilesCanUseEachOthersClasses(Lookup lookup)
    {
        var a = $"A{id}";
        var c = $"C{id}";
        var x = $"X{id}";
        Write(a, $$"""
            namespace {{Namespace}};
            shader {{a}} : {{c}} { int AValue() { return CValue(); } };
            shader {{x}} { int XValue() { return 2; } };
            """);
        Write(c, $$"""
            namespace {{Namespace}};
            shader {{c}} : {{x}} { int CValue() { return XValue(); } };
            """);

        Assert.Contains("AValue", Load(NewLoader(lookup), Full(a)));
    }

    // Never compile everything: a class that doesn't compile only fails when asked for
    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AskingForAClassCompilesOnlyThatClass(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} : NoSuchParent{{id}} { };
            shader {{Bar}} { int BarValue() { return 2; } };
            """);

        Assert.Contains("BarValue", Load(NewLoader(lookup), Full(Bar)));
        Assert.Throws<InvalidOperationException>(() => NewLoader(lookup).LoadExternalBuffer(Full(Foo), [], out _, out _, out _));
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AGenericSecondClassIsInstantiated(Lookup lookup)
    {
        var root = $"Root{id}";
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { int FooValue() { return 1; } };
            shader {{Bar}}<int TValue> { int BarValue() { return TValue; } };
            """);
        Write(root, $$"""
            namespace {{Namespace}};
            shader {{root}} : ComputeShaderBase, {{Bar}}<3>
            {
                RWBuffer<int> Out;
                override void Compute() { Out[0] = BarValue(); }
            }
            """);

        Assert.Contains("BarValue", Mix(NewLoader(lookup), Full(root), out _));
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AMemberNameSecondClassIsInstantiated(Lookup lookup)
    {
        var root = $"Root{id}";
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { int FooValue() { return 1; } };
            shader {{Bar}}<MemberName TGroup>
            {
                cbuffer TGroup { float GroupValue; }
                float BarValue() { return GroupValue; }
            };
            shader {{Foo}}Last { int LastValue() { return 2; } };
            """);
        Write(root, $$"""
            namespace {{Namespace}};
            shader {{root}} : ComputeShaderBase, {{Bar}}<PerFrameGroup>
            {
                RWBuffer<float> Out;
                override void Compute() { Out[0] = BarValue(); }
            }
            """);

        var mixed = Mix(NewLoader(lookup), Full(root), out _);
        Assert.Contains("PerFrameGroup", mixed);
        Assert.DoesNotContain("LastValue", mixed);
    }

    // The effect caches compare what a mix recorded with the current hash of each class: a difference recompiles on every run
    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    public void AMixRecordsASecondClassWithTheHashOfItsFile(Lookup lookup)
    {
        var root = $"Root{id}";
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { int FooValue() { return 1; } };
            shader {{root}} : ComputeShaderBase, {{Foo}}
            {
                RWBuffer<int> Out;
                override void Compute() { Out[0] = FooValue(); }
            };
            """);

        var loader = (FileShaderLoader)NewLoader(lookup);
        Mix(loader, Full(root), out var sources);

        Assert.Equal(loader.SourceManager.GetShaderSourceHash(Full(root)), sources[Full(root)]);
        Assert.Equal(loader.SourceManager.GetShaderSourceHash(Full(Foo)), sources[Full(Foo)]);
    }

    // ShaderFileDeclarations.Read sees both sides of an #if: what it lists is only where to look
    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AClassInsideAnOffIfdefIsAClearError(Lookup lookup)
    {
        var path = Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { int FooValue() { return 1; } };
            #ifdef MACRO_THAT_IS_OFF
            shader {{Bar}} { int BarValue() { return 2; } };
            #endif

            """);

        var error = Assert.Throws<InvalidOperationException>(() => NewLoader(lookup).LoadExternalBuffer(Full(Bar), [], out _, out _, out _));
        Assert.Contains(string.Format(SDSLErrorMessages.SDSL0308, Full(Bar), $"{Stuff}.sdsl"), error.Message);

        Assert.Contains("BarValue", Load(NewLoader(lookup), Full(Bar), new ShaderMacro("MACRO_THAT_IS_OFF", 1)));
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AFileThatIsAllInsideAnOffIfdefIsAClearError(Lookup lookup)
    {
        Write(Stuff, $$"""
            #ifdef MACRO_THAT_IS_OFF
            namespace {{Namespace}} { shader {{Bar}} { int BarValue() { return 2; } }; }
            #endif

            """);

        var error = Assert.Throws<InvalidOperationException>(() => NewLoader(lookup).LoadExternalBuffer(Full(Bar), [], out _, out _, out _));
        Assert.Contains(string.Format(SDSLErrorMessages.SDSL0308, Full(Bar), $"{Stuff}.sdsl"), error.Message);
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void OneClassOnBothSidesOfAnIfdefFollowsTheMacros(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            #ifdef USE_ONE
            shader {{Bar}} { int One() { return 1; } };
            #else
            shader {{Bar}} { int Two() { return 2; } };
            #endif

            """);

        Assert.Contains("Two", Load(NewLoader(lookup), Full(Bar)));
        Assert.Contains("One", Load(NewLoader(lookup), Full(Bar), new ShaderMacro("USE_ONE", 1)));
    }

    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void EachSideOfAnIfdefCanOpenAClass(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            #ifdef USE_FOO
            shader {{Foo}}
            {
            #else
            shader {{Bar}}
            {
            #endif
                int Value() { return 1; }
            };

            """);

        Assert.Contains("Value", Load(NewLoader(lookup), Full(Bar)));
        Assert.Contains("Value", Load(NewLoader(lookup), Full(Foo), new ShaderMacro("USE_FOO", 1)));

        var error = Assert.Throws<InvalidOperationException>(() => NewLoader(lookup).LoadExternalBuffer(Full(Foo), [], out _, out _, out _));
        Assert.Contains("SDSL0308", error.Message);
    }

    // The watcher only names the classes an effect used
    [Theory]
    [InlineData(Lookup.Database)]
    [InlineData(Lookup.Folder)]
    public void AnEditOfAFileReloadsAllItsClasses(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { int FooValue() { return 1; } };
            shader {{Bar}} { int BarValue() { return 2; } };
            """);
        var loader = (FileShaderLoader)NewLoader(lookup);
        Assert.Contains("BarValue", Load(loader, Full(Bar)));
        var hash = loader.SourceManager.GetShaderSourceHash(Full(Bar));

        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { int FooValue() { return 1; } };
            shader {{Bar}} { int BarEdited() { return 3; } };
            """);
        loader.SourceManager.DeleteObsoleteCache([Full(Foo)]);

        Assert.NotEqual(hash, loader.SourceManager.GetShaderSourceHash(Full(Bar)));
        Assert.Contains("BarEdited", Load(loader, Full(Bar)));
    }

    [Theory]
    [InlineData(Lookup.Database)]
    public void AClassRemovedFromAFileIsAClearError(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { int FooValue() { return 1; } };
            shader {{Bar}} { int BarValue() { return 2; } };
            """);
        var loader = (FileShaderLoader)NewLoader(lookup);
        Assert.Contains("BarValue", Load(loader, Full(Bar)));

        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { int FooValue() { return 1; } };
            """);
        loader.SourceManager.DeleteObsoleteCache([Full(Foo), Full(Bar)]);

        Assert.Throws<InvalidOperationException>(() => loader.LoadExternalBuffer(Full(Bar), [], out _, out _, out _));
    }

    [Theory]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AShortNameFindsASecondClass(Lookup lookup)
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { };
            shader {{Bar}} { };
            """);

        Assert.Contains(Full(Bar), NewLoader(lookup).FindFullNames(Bar));
    }

    // A database can't see this, it has one URL per class: the build reports it
    [Theory]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AClassOfTwoFilesIsAnErrorThatNamesBoth(Lookup lookup)
    {
        var first = $"First{id}";
        var second = $"Second{id}";
        Write(first, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { int FooValue() { return 1; } };
            shader {{Bar}} { int BarValue() { return 2; } };
            """);
        Write(second, $$"""
            namespace {{Namespace}};
            shader {{Bar}} { int BarValue() { return 3; } };
            """);

        var error = Assert.Throws<InvalidOperationException>(() => NewLoader(lookup).LoadExternalBuffer(Full(Bar), [], out _, out _, out _));
        Assert.Contains($"SDSL0301: {Full(Bar)} is declared twice", error.Message);
        Assert.Contains($"{first}.sdsl", error.Message);
        Assert.Contains($"{second}.sdsl", error.Message);

        Assert.Contains("FooValue", Load(NewLoader(lookup), Full(Foo)));
    }

    // Found by its name alone, the second file would go unseen
    [Theory]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AClassOfTwoFilesIsAnErrorWhenOneIsNamedAfterIt(Lookup lookup)
    {
        Write(Bar, $$"""
            namespace {{Namespace}};
            shader {{Bar}} { int BarValue() { return 2; } };
            """);
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Bar}} { int BarValue() { return 3; } };
            """);

        var loader = NewLoader(lookup);
        var error = Assert.Throws<InvalidOperationException>(() => loader.Exists(Full(Bar)));
        Assert.Contains($"SDSL0301: {Full(Bar)} is declared twice", error.Message);
    }

    [Theory]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AParentOfTwoFilesIsAnError(Lookup lookup)
    {
        Write(Foo, $$"""
            namespace {{Namespace}};
            shader {{Foo}} : {{Bar}} { };
            """);
        Write(Bar, $$"""
            namespace {{Namespace}};
            shader {{Bar}} { };
            """);
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Bar}} { };
            """);

        var error = Assert.Throws<InvalidOperationException>(() => NewLoader(lookup).LoadExternalBuffer(Full(Foo), [], out _, out _, out _));
        Assert.Contains($"SDSL0301: {Full(Bar)} is declared twice", error.Message);
    }

    // The build reads both sides of an #if too, so this doesn't depend on the macros
    [Theory]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void AClassOfTwoFilesIsAnErrorOnEitherSideOfAnIfdef(Lookup lookup)
    {
        Write(Bar, $$"""
            namespace {{Namespace}};
            #ifdef USE_ONE
            shader {{Bar}} { int One() { return 1; } };
            #endif

            """);
        Write(Stuff, $$"""
            namespace {{Namespace}};
            #ifndef USE_ONE
            shader {{Bar}} { int Two() { return 2; } };
            #endif

            """);

        var error = Assert.Throws<InvalidOperationException>(() => NewLoader(lookup).LoadExternalBuffer(Full(Bar), [], out _, out _, out _));
        Assert.Contains("SDSL0301", error.Message);
    }

    [Theory]
    [InlineData(Lookup.Folder)]
    [InlineData(Lookup.TestLoader)]
    public void OneShortNameInTwoNamespacesOfTwoFilesIsNoError(Lookup lookup)
    {
        Write(Bar, $$"""
            namespace {{Namespace}};
            shader {{Bar}} { int BarValue() { return 2; } };
            """);
        Write(Stuff, $$"""
            namespace Other.Test;
            shader {{Bar}} { int OtherValue() { return 3; } };
            """);

        Assert.Contains("BarValue", Load(NewLoader(lookup), Full(Bar)));
        Assert.Contains("OtherValue", Load(NewLoader(lookup), $"Other.Test.{Bar}"));
    }

    // Two folders of one lookup are what two packages of one build are
    [Theory]
    [InlineData(Lookup.Folder, "Same")]
    [InlineData(Lookup.Folder, "Different")]
    [InlineData(Lookup.TestLoader, "Different")]
    public void AClassOfTwoFoldersIsAnErrorThatNamesBoth(Lookup lookup, string fileNames)
    {
        Write(Bar, $$"""
            namespace {{Namespace}};
            shader {{Bar}} { };
            """);
        var secondFolder = sourceDir.CreateSubdirectory("second");
        var secondFile = fileNames == "Same" ? Bar : Stuff;
        File.WriteAllText(Path.Combine(secondFolder.FullName, $"{secondFile}.sdsl"), $$"""
            namespace {{Namespace}};
            shader {{Bar}} { };
            """);

        ShaderLoaderBase loader;
        if (lookup == Lookup.Folder)
        {
            var loose = new FileShaderLoader(folder);
            loose.SourceManager.LookupDirectoryList.Add(sourceDir.Name);
            loose.SourceManager.LookupDirectoryList.Add($"{sourceDir.Name}/second");
            loader = loose;
        }
        else
        {
            loader = new ShaderLoader(sourceDir.FullName, secondFolder.FullName);
        }

        var error = Assert.Throws<InvalidOperationException>(() => loader.Exists(Full(Bar)));
        Assert.Contains($"SDSL0301: {Full(Bar)} is declared twice", error.Message);
        Assert.Contains($"{Bar}.sdsl", error.Message);
        Assert.Contains($"{secondFile}.sdsl", error.Message);
    }

    // The file watcher's reload is how the mistake gets fixed: it must not stop on it
    [Fact]
    public void AClassOfTwoFilesWorksOnceAReloadSeesItFixed()
    {
        Write(Bar, $$"""
            namespace {{Namespace}};
            shader {{Bar}} { int BarValue() { return 2; } };
            """);
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Bar}} { int BarValue() { return 3; } };
            """);
        var loader = (FileShaderLoader)NewLoader(Lookup.Folder);
        Assert.Throws<InvalidOperationException>(() => loader.Exists(Full(Bar)));

        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { };
            """);
        loader.SourceManager.DeleteObsoleteCache([Full(Bar)]);

        Assert.Contains("BarValue", Load(loader, Full(Bar)));
    }

    // Each miss would be a read of every shader, and a game asks for names that don't exist through the implicit usings
    [Fact]
    public void ADatabaseIsNeverListedOrReadForAMissingName()
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { };
            shader {{Bar}} { };
            """);
        var database = BuiltDatabase.Build(sourceDir);
        var loader = new FileShaderLoader(database);
        loader.SourceManager.LookupDirectoryList.Add("shaders");

        Assert.False(loader.Exists(Full($"Missing{id}")));
        Assert.False(loader.Exists(Full($"Missing2{id}")));

        Assert.Equal(0, database.Reads);
    }

    [Fact]
    public void AFolderIsReadOnceForMissingNames()
    {
        Write(Stuff, $$"""
            namespace {{Namespace}};
            shader {{Foo}} { };
            shader {{Bar}} { };
            """);
        var counting = new CountingFolder(AppContext.BaseDirectory);
        var loader = new FileShaderLoader(counting);
        loader.SourceManager.LookupDirectoryList.Add(sourceDir.Name);

        Assert.False(loader.Exists(Full($"Missing{id}")));
        var reads = counting.Lists;
        Assert.False(loader.Exists(Full($"Missing2{id}")));

        Assert.Equal(1, reads);
        Assert.Equal(reads, counting.Lists);
        counting.Dispose();
    }

    private static string Mix(ShaderLoaderBase loader, string root, out HashSourceCollection sources)
    {
        var log = new LoggerResult();
        var ok = new ShaderMixer(loader).MergeSDSL(new ShaderMixinSource { Mixins = { new ShaderClassSource(root) } }, new ShaderMixer.Options(true), log, out var bytecode, out _, out sources, out _);
        Assert.True(ok, string.Join(Environment.NewLine, log.Messages.Select(m => m.Text)));
        return Spv.Dis(SpirvBytecode.CreateFromSpan(bytecode), DisassemblerFlags.Name);
    }

    /// <summary>
    /// What the build leaves: <c>shaders/Full.Name.sdsl</c> and <c>/path</c> for each class of each file. It can't be listed, like the remote compiler's.
    /// </summary>
    private sealed class BuiltDatabase(Dictionary<string, string> files) : VirtualFileProviderBase(null)
    {
        public int Reads { get; private set; }

        public static BuiltDatabase Build(DirectoryInfo source)
        {
            var files = new Dictionary<string, string>();
            foreach (var file in source.EnumerateFiles("*.sdsl"))
            {
                var text = File.ReadAllText(file.FullName);
                foreach (var name in ShaderFileDeclarations.StorageNames(text, Path.GetFileNameWithoutExtension(file.Name)))
                {
                    files[$"shaders/{name}.sdsl"] = text;
                    files[$"shaders/{name}.sdsl/path"] = file.FullName;
                }
            }
            foreach (var file in new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "assets/Stride/SDSL")).EnumerateFiles("*.sdsl"))
            {
                var text = File.ReadAllText(file.FullName);
                foreach (var name in ShaderFileDeclarations.StorageNames(text, Path.GetFileNameWithoutExtension(file.Name)))
                    files[$"shaders/{name}.sdsl"] = text;
            }
            return new BuiltDatabase(files);
        }

        public override bool FileExists(string url) => files.ContainsKey(url);

        public override Stream OpenStream(string url, VirtualFileMode mode, VirtualFileAccess access, VirtualFileShare share = VirtualFileShare.Read, StreamFlags streamFlags = StreamFlags.None)
        {
            Reads++;
            return new MemoryStream(Encoding.UTF8.GetBytes(files[url]));
        }
    }

    private sealed class CountingFolder(string root) : FileSystemProvider($"/classlookupcount-{Guid.NewGuid():N}", root)
    {
        public int Lists { get; private set; }

        public override string[] ListFiles(string url, string searchPattern, VirtualSearchOption searchOption)
        {
            if (searchPattern == "*.sdsl")
                Lists++;
            return base.ListFiles(url, searchPattern, searchOption);
        }
    }
}
