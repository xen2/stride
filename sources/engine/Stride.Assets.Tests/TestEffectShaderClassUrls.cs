// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.Assets.Effect;
using Stride.Assets.Materials;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Storage;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders.Compiler;
using Stride.Shaders.Compilers;
using Xunit;

namespace Stride.Assets.Tests
{
    /// <summary>
    /// The build stores a shader file under the URL of each class it declares.
    /// </summary>
    public class TestEffectShaderClassUrls : IDisposable
    {
        private const string TwoClasses = "namespace Lib; shader Foo { }; shader Bar { };";
        private const string IndexName = "index.test";

        private readonly string directory = Directory.CreateTempSubdirectory("stride-shader-urls-").FullName;
        private readonly AssetCompilerContext context = new() { CompilationContext = typeof(AssetCompilationContext) };
        private readonly string applicationData = ((FileSystemProvider)VirtualFileSystem.ApplicationData).RootPath;

        public void Dispose()
        {
            context.Dispose();
            Builder.CloseObjectDatabase();
            ((FileSystemProvider)VirtualFileSystem.ApplicationData).ChangeBasePath(applicationData);
            Directory.Delete(directory, recursive: true);
            GC.SuppressFinalize(this);
        }

        [Theory]
        [InlineData("namespace Lib; shader Foo { }; shader Bar { };", "Stuff", new[] { "shaders/Lib.Foo.sdsl", "shaders/Lib.Bar.sdsl" })]
        [InlineData("namespace A { shader Foo { }; } namespace B { shader Bar { }; }", "Stuff", new[] { "shaders/A.Foo.sdsl", "shaders/B.Bar.sdsl" })]
        [InlineData("namespace Lib; shader Bar { };", "Stuff", new[] { "shaders/Lib.Bar.sdsl" })]
        [InlineData("shader Stuff { };", "Stuff", new[] { "shaders/Stuff.sdsl" })]
        // Each side of an #if is read, and one class is one URL
        [InlineData("#ifdef X\nshader Foo { };\n#else\nshader Foo { float x; };\n#endif", "Stuff", new[] { "shaders/Foo.sdsl" })]
        // An effect is C#, and a file with no class still has one URL: its file name
        [InlineData("namespace Lib; shader Foo { }; effect FooEffect { mixin Foo; };", "Stuff", new[] { "shaders/Lib.Foo.sdsl" })]
        [InlineData("namespace Lib; effect FooEffect { };", "Stuff", new[] { "shaders/Lib.Stuff.sdsl" })]
        public void AFileGetsOneUrlPerClass(string code, string fileName, string[] urls)
        {
            var file = AddShader(NewPackage("Game"), fileName, code);
            InOneSession(file.Package);

            var result = Prepare(file);

            Assert.False(result.HasErrors);
            Assert.Equal(urls.Order(), Imports(result).Select(x => x.Location.FullPath).Order());
            Assert.All(Imports(result), x => Assert.Equal(file.FullPath, x.SourcePath));
        }

        [Fact]
        public void TwoUrlsOfAFileAreStoredOnce()
        {
            var file = AddShader(NewPackage("Game"), "Stuff", TwoClasses);
            InOneSession(file.Package);

            var index = Build(file);

            Assert.Equal(index["shaders/Lib.Foo.sdsl"], index["shaders/Lib.Bar.sdsl"]);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(directory, "build", "db"), "*", SearchOption.AllDirectories), x => ObjectFileId(x) == index["shaders/Lib.Foo.sdsl"]);
            Assert.Equal(index["shaders/Lib.Foo.sdsl/path"], index["shaders/Lib.Bar.sdsl/path"]);
        }

        [Fact]
        public void AClassRenamedInAFileLeavesTheIndexOnTheNextBuild()
        {
            var file = AddShader(NewPackage("Game"), "Stuff", TwoClasses);
            InOneSession(file.Package);
            Assert.Contains("shaders/Lib.Bar.sdsl", Build(file).Keys);

            File.WriteAllText(file.FullPath, "namespace Lib; shader Foo { }; shader Baz { };");
            var index = Build(file);

            Assert.Contains("shaders/Lib.Baz.sdsl", index.Keys);
            Assert.Contains("shaders/Lib.Foo.sdsl", index.Keys);
            Assert.DoesNotContain("shaders/Lib.Bar.sdsl", index.Keys);
            Assert.DoesNotContain("shaders/Lib.Bar.sdsl/path", index.Keys);
        }

        // A build, then what a game does with its database
        [Fact]
        public void ABuiltDatabaseGivesAnyClassOfAFile()
        {
            var file = AddShader(NewPackage("Game"), "Stuff", "namespace Lib; shader Foo { float FooValue() { return 1; } }; shader Bar<int N> { float BarValue() { return N; } };");
            InOneSession(file.Package);
            Build(file);

            Builder.OpenObjectDatabase(Path.Combine(directory, "build"), IndexName);
            var loader = new FileShaderLoader(new DatabaseFileProvider(Builder.ObjectDatabase));
            loader.SourceManager.LookupDirectoryList.Add(EffectCompilerBase.DefaultSourceShaderFolder);

            Assert.True(loader.LoadExternalBuffer("Lib.Bar", [], out var bar, out var barHash, out _));
            Assert.Contains("BarValue", bar.Context.Names.Values);
            // A game without a compiler compares this with what an effect recorded
            Assert.Equal(barHash, new NullEffectCompiler(null, new DatabaseFileProvider(Builder.ObjectDatabase)).GetShaderSourceHash("Lib.Bar"));
            Assert.True(loader.LoadExternalBuffer("Lib.Foo", [], out var foo, out _, out _));
            Assert.Contains("FooValue", foo.Context.Names.Values);
            Assert.False(loader.Exists("Lib.Stuff"));
            Assert.Equal(["Lib.Bar"], loader.FindFullNames("Bar"));
        }

        [Fact]
        public void ClassesNoLongerDeclaredAreThoseOfAFileThatABuildWentThrough()
        {
            var first = ObjectId.New();
            var second = ObjectId.New();
            var content = ObjectId.New();

            var stored = new Dictionary<string, ObjectId>
            {
                ["shaders/Lib.Foo.sdsl"] = content,
                ["shaders/Lib.Foo.sdsl/path"] = first,
                ["shaders/Lib.Bar.sdsl"] = content,
                ["shaders/Lib.Bar.sdsl/path"] = first,
                // A file that wasn't built, or was built apart
                ["shaders/Other.Baz.sdsl"] = content,
                ["shaders/Other.Baz.sdsl/path"] = second,
                ["textures/Lib.Bar.sdsl/path"] = first,
            };
            var written = new Dictionary<string, ObjectId>
            {
                ["shaders/Lib.Foo.sdsl"] = content,
                ["shaders/Lib.Foo.sdsl/path"] = first,
            };

            Assert.Equal(["Lib.Bar"], EffectShaderAssetCompiler.FindClassesNoLongerDeclared(stored, written));
            Assert.Empty(EffectShaderAssetCompiler.FindClassesNoLongerDeclared(stored, []));
        }

        public enum Reference
        {
            Project,
            NuGet,
        }

        [Theory]
        [InlineData(Reference.Project)]
        [InlineData(Reference.NuGet)]
        public void TwoPackagesThatDeclareOneClassAreAnError(Reference reference)
        {
            var library = AddShader(NewPackage("Library"), "Stuff", TwoClasses);
            var game = AddShader(NewPackage("Game"), "Other", "namespace Lib; shader Bar { };");
            var session = new PackageSession(game.Package);
            session.Projects.Add(reference == Reference.Project
                ? new SolutionProject(library.Package, Guid.NewGuid(), Path.Combine(directory, "Library", "Library.csproj"))
                : new StandalonePackage(library.Package) { IsDependencyPackage = true });

            var messages = new[] { library, game }.SelectMany(x => Prepare(x).Messages).Where(x => x.Type == LogMessageType.Error).ToList();

            var error = Assert.Single(messages);
            Assert.Contains("SDSL0301: Lib.Bar is declared twice", error.Text);
            Assert.Contains(library.FullPath, error.Text);
            Assert.Contains(game.FullPath, error.Text);
        }

        // The two files of one package, built, then read as a source folder
        [Theory]
        [InlineData("Stuff")]
        [InlineData("Bar")]
        public void ASourceFolderAgreesWithABuildOnAClassOfTwoFiles(string secondFile)
        {
            var package = NewPackage("Game");
            var first = AddShader(package, "First", TwoClasses);
            var second = AddShader(package, secondFile, "namespace Lib; shader Bar { };");
            InOneSession(package);

            var built = new[] { first, second }.SelectMany(x => Prepare(x).Messages).Where(x => x.Type == LogMessageType.Error).ToList();
            var error = Assert.Single(built);
            Assert.Contains("SDSL0301: Lib.Bar is declared twice", error.Text);
            Assert.Contains(first.FullPath, error.Text);
            Assert.Contains(second.FullPath, error.Text);

            using var folder = new FileSystemProvider($"/shaderurls-{Guid.NewGuid():N}", Path.GetDirectoryName(first.FullPath.ToOSPath()));
            var loader = new FileShaderLoader(folder);
            loader.SourceManager.LookupDirectoryList.Add("");

            var lookup = Assert.Throws<InvalidOperationException>(() => loader.Exists("Lib.Bar"));
            Assert.Contains("SDSL0301: Lib.Bar is declared twice", lookup.Message);
            Assert.Contains("First.sdsl", lookup.Message);
            Assert.Contains($"{secondFile}.sdsl", lookup.Message);
            Assert.True(loader.Exists("Lib.Foo"));
        }

        // Stuff.sdsl has no class Lib.Stuff, so the other package's class stays the only one
        [Fact]
        public void AFileNameIsNotADeclaration()
        {
            var first = AddShader(NewPackage("First"), "Stuff", "namespace Lib; shader Bar { };");
            var second = AddShader(NewPackage("Second"), "Other", "namespace Lib; shader Stuff { };");
            InOneSession(first.Package, second.Package);

            Assert.False(Prepare(first).HasErrors);
            Assert.False(Prepare(second).HasErrors);
        }

        [Theory]
        [InlineData("Lib.Bar")]
        [InlineData("Lib.Foo")]
        public void TheMaterialEditorReadsTheClassItReferences(string mixinReference)
        {
            var node = new ComputeShaderClassColor { MixinReference = mixinReference };

            var shader = node.ParseReferencedShader(new Dictionary<string, string> { [mixinReference] = "namespace Lib; shader Foo<int First> { }; shader Bar<int Second> { };" });

            Assert.NotNull(shader);
            Assert.Equal(mixinReference.Split('.')[1], shader.Name.Name);
        }

        private static IEnumerable<ImportStreamCommand> Imports(AssetCompilerResult result)
            => result.BuildSteps.EnumerateRecursively().OfType<CommandBuildStep>().Select(x => x.Command).OfType<ImportStreamCommand>();

        private static string ObjectFileId(string path) => Path.GetFileName(Path.GetDirectoryName(path)) + Path.GetFileName(path);

        // A build like the command line's: its index is what this build wrote
        private Dictionary<string, string> Build(params AssetItem[] items)
        {
            var logger = new LoggerResult();
            var buildPath = Path.Combine(directory, "build");
            var builder = new Builder(logger, buildPath, IndexName) { BuilderName = "TestEffectShaderClassUrls", ThreadCount = 2 };
            using var buildContext = new AssetCompilerContext { CompilationContext = typeof(AssetCompilationContext) };
            foreach (var item in items)
            {
                var prepared = new EffectShaderAssetCompiler().Prepare(buildContext, item);
                Assert.False(prepared.HasErrors, string.Join("\n", prepared.Messages.Select(x => x.Text)));
                builder.Root.Add(prepared.BuildSteps);
            }

            Assert.Equal(BuildResultCode.Successful, builder.Run(Builder.Mode.Build));
            builder.WriteIndexFile(false);

            return File.ReadAllLines(Path.Combine(buildPath, "db", IndexName)).Select(x => x.Split(' ')).ToDictionary(x => x[0], x => x[1]);
        }

        private static void InOneSession(Package first, params Package[] others)
        {
            var session = new PackageSession(first);
            foreach (var other in others)
                session.Projects.Add(new StandalonePackage(other));
        }

        private AssetCompilerResult Prepare(AssetItem assetItem) => new EffectShaderAssetCompiler().Prepare(context, assetItem);

        private Package NewPackage(string name) => new() { Meta = { Name = name }, FullPath = Path.Combine(directory, name, $"{name}.sdpkg") };

        private static AssetItem AddShader(Package package, string name, string code)
        {
            var assetItem = new AssetItem(name, new EffectShaderAsset());
            package.Assets.Add(assetItem);

            var path = assetItem.FullPath.ToOSPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, code);
            return assetItem;
        }
    }
}
