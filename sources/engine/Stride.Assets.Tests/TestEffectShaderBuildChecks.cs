// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using Stride.Assets.Effect;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.Diagnostics;
using Xunit;

namespace Stride.Assets.Tests
{
    /// <summary>
    /// What the asset build reports about the shaders of a session, before anything compiles.
    /// </summary>
    public class TestEffectShaderBuildChecks : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("stride-shader-checks-").FullName;
        private readonly AssetCompilerContext context = new() { CompilationContext = typeof(AssetCompilationContext) };

        public void Dispose()
        {
            context.Dispose();
            Directory.Delete(directory, recursive: true);
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void TheSecondDeclarationOfAFullNameIsAnErrorThatNamesBoth()
        {
            var first = AddShader(NewPackage("First"), "X", "namespace Lib; shader X { };");
            var second = AddShader(NewPackage("Second"), "X", "namespace Lib; shader X { };");
            InOneSession(first.Package, second.Package);

            Assert.False(Prepare(first).HasErrors);

            var error = Assert.Single(Prepare(second).Messages, x => x.Type == LogMessageType.Error);
            Assert.Contains("SDSL0301: Lib.X is declared twice", error.Text);
            Assert.Contains($"{first.FullPath} (package First)", error.Text);
            Assert.Contains($"{second.FullPath} (package Second)", error.Text);
        }

        [Fact]
        public void OneShortNameInTwoNamespacesIsFine()
        {
            var first = AddShader(NewPackage("First"), "X", "namespace Lib; shader X { };");
            var second = AddShader(NewPackage("Second"), "X", "namespace Game; shader X { };");
            InOneSession(first.Package, second.Package);

            Assert.False(Prepare(first).HasErrors);
            Assert.False(Prepare(second).HasErrors);
        }

        [Fact]
        public void AReplacementIsReported()
        {
            var package = NewPackage("Game");
            AddShader(package, "X", "namespace Lib; shader X { };");
            var replacer = AddShader(package, "R", """namespace Game; [Replaces(Lib.X)] shader R : Lib.X { };""");
            InOneSession(package);

            var result = Prepare(replacer);

            Assert.False(result.HasErrors);
            Assert.Contains(result.Messages, x => x.Text == "Game.R replaces Lib.X");
        }

        // The other package isn't a dependency
        [Theory]
        [InlineData("""namespace Game; [Replaces(Lib.X)] shader R : Lib.X { };""")]
        [InlineData("""namespace Game; [Replaces(X)] shader R : Lib.X { };""")]
        public void ATargetThatIsNotVisibleIsAnError(string code)
        {
            var other = AddShader(NewPackage("Other"), "X", "namespace Lib; shader X { };");
            var replacer = AddShader(NewPackage("Game"), "R", code);
            InOneSession(replacer.Package, other.Package);

            var error = Assert.Single(Prepare(replacer).Messages, x => x.Type == LogMessageType.Error);
            Assert.Contains("SDSL0303: Game.R replaces", error.Text);
            Assert.Contains("(Game)", error.Text);
        }

        [Fact]
        public void TwoReplacersOfOneClassAreAnError()
        {
            var package = NewPackage("Game");
            AddShader(package, "X", "namespace Lib; shader X { };");
            var first = AddShader(package, "R", """namespace Game; [Replaces(Lib.X)] shader R : Lib.X { };""");
            AddShader(package, "S", """namespace Game; [Replaces(Lib.X)] shader S : Lib.X { };""");
            InOneSession(package);

            var error = Assert.Single(Prepare(first).Messages, x => x.Type == LogMessageType.Error);
            Assert.Contains("SDSL0304: Lib.X has two replacers: Game.R and Game.S", error.Text);
        }

        [Fact]
        public void AReplacerThatCanNotStandInIsAnError()
        {
            var package = NewPackage("Game");
            AddShader(package, "X", "namespace Lib; shader X<int N> { };");
            var replacer = AddShader(package, "R", """namespace Game; [Replaces(Lib.X)] shader R : Lib.X<3> { };""");
            InOneSession(package);

            var error = Assert.Single(Prepare(replacer).Messages, x => x.Type == LogMessageType.Error);
            Assert.Contains("SDSL0306: Game.R can't replace Lib.X", error.Text);
        }

        // The editor builds shaders in a session of its own, where a package sees what it sees in the project
        [Fact]
        public void AnImportSessionKeepsTheVisibilityOfATarget()
        {
            var engine = NewPackage("Engine");
            AddShader(engine, "X", "namespace Lib; shader X { };");
            var game = NewPackage("Game");
            AddShader(game, "R", """namespace Game; [Replaces(Lib.X)] shader R : Lib.X { };""");
            _ = new StandalonePackage(engine);
            _ = new StandalonePackage(game);

            var root = EffectShaderAssetCompiler.CreateImportSession([engine, game]);

            var error = Assert.Single(Prepare(root.Session.Packages.SelectMany(x => x.Assets).Single(x => x.Location == "R")).Messages, x => x.Type == LogMessageType.Error);
            Assert.Contains("SDSL0303: Game.R replaces Lib.X", error.Text);
            Assert.Contains("(Game)", error.Text);
        }

        [Fact]
        public void AnImportSessionFindsTheTargetOfADependency()
        {
            var engine = NewPackage("Engine");
            AddShader(engine, "X", "namespace Lib; shader X { };");
            var game = NewPackage("Game");
            AddShader(game, "R", """namespace Game; [Replaces(Lib.X)] shader R : Lib.X { };""");
            _ = new StandalonePackage(engine);
            new StandalonePackage(game).FlattenedDependencies.Add(new Dependency(engine));

            var root = EffectShaderAssetCompiler.CreateImportSession([engine, game]);

            var result = Prepare(root.Session.Packages.SelectMany(x => x.Assets).Single(x => x.Location == "R"));
            Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(x => x.Text)));
            Assert.Contains(result.BuildSteps.EnumerateRecursively().OfType<CommandBuildStep>(), x => x.Command is SaveReplacerCommand);
        }

        // Saving a shader builds the user packages alone
        [Fact]
        public void TheTargetOfAReplacerCanBeBuiltApart()
        {
            var game = NewPackage("Game");
            var replacer = AddShader(game, "R", """namespace Game; [Replaces(Lib.X)] shader R : Lib.X { };""");
            InOneSession(game);
            context.Properties.Set(EffectShaderAssetCompiler.ImportedShadersKey, new() { ["Lib.X"] = ["Engine"] });

            Assert.False(Prepare(replacer).HasErrors);
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
