// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using Stride.Assets.Effect;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.Diagnostics;
using Xunit;

namespace Stride.Assets.Tests
{
    /// <summary>
    /// A full effect name can only be declared once in a session, in one package or across two.
    /// </summary>
    public class TestEffectCompositorDuplicates : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("stride-effect-duplicates-").FullName;

        public void Dispose()
        {
            Directory.Delete(directory, recursive: true);
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void TheSecondDeclarationIsAnErrorThatNamesBoth()
        {
            var first = AddEffect("First", "namespace Game; effect MyEffect { };");
            var second = AddEffect("Second", "namespace Game; effect MyEffect { };");
            InOneSession(first, second);
            var context = new AssetCompilerContext { CompilationContext = typeof(AssetCompilationContext) };

            Assert.False(Prepare(context, first).HasErrors);

            var result = Prepare(context, second);
            var error = Assert.Single(result.Messages, x => x.Type == LogMessageType.Error);
            Assert.Contains("SDSL0302: effect Game.MyEffect is declared twice", error.Text);
            Assert.Contains($"{first.FullPath} (package First)", error.Text);
            Assert.Contains($"{second.FullPath} (package Second)", error.Text);
        }

        [Fact]
        public void OneShortNameInTwoNamespacesIsFine()
        {
            var first = AddEffect("First", "namespace Game; effect MyEffect { };");
            var second = AddEffect("Second", "namespace Plugin; effect MyEffect { };");
            InOneSession(first, second);
            var context = new AssetCompilerContext { CompilationContext = typeof(AssetCompilationContext) };

            Assert.False(Prepare(context, first).HasErrors);
            Assert.False(Prepare(context, second).HasErrors);
        }

        // A params class can have parts in several files
        [Fact]
        public void TwoFilesCanWriteOneParams()
        {
            var first = AddEffect("First", "namespace Game; params Shared { bool A; }; effect MyEffect { };");
            var second = AddEffect("Second", "namespace Game; params Shared { bool B; };");
            InOneSession(first, second);
            var context = new AssetCompilerContext { CompilationContext = typeof(AssetCompilationContext) };

            Assert.False(Prepare(context, first).HasErrors);
            Assert.False(Prepare(context, second).HasErrors);
        }

        private static void InOneSession(AssetItem first, AssetItem second)
            => new PackageSession(first.Package).Projects.Add(new StandalonePackage(second.Package));

        private static AssetCompilerResult Prepare(AssetCompilerContext context, AssetItem assetItem)
            => new EffectCompositorAssetCompiler().Prepare(context, assetItem);

        private AssetItem AddEffect(string packageName, string code)
        {
            var package = new Package { Meta = { Name = packageName }, FullPath = Path.Combine(directory, packageName, $"{packageName}.sdpkg") };
            var assetItem = new AssetItem("MyEffect", new EffectCompositorAsset());
            package.Assets.Add(assetItem);

            var path = assetItem.FullPath.ToOSPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, code);
            return assetItem;
        }
    }
}
