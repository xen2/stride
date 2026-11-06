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
    /// An effect log goes through <see cref="EffectCompileCommand"/> in a build of its package: the only place that compiles effects as an asset build.
    /// </summary>
    public class TestEffectLogBuild : IDisposable
    {
        private const string Entry = """
            module Entry
            {
                stream float4 ColorTarget : SV_Target0;

                pipeline void PSMain() { streams.ColorTarget = 0; }

                replace float Pick() { return 1; }
            };
            """;

        private const string Step = """
            module Step
            {
                import Entry;

                override void PSMain() { base.PSMain(); streams.ColorTarget += 1; }

                extend Entry.Pick() { return 2; }
            };
            """;

        private readonly string directory = Directory.CreateTempSubdirectory("stride-effect-log-").FullName;

        public void Dispose()
        {
            Builder.CloseObjectDatabase();
            Directory.Delete(directory, recursive: true);
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void AnExtendThatDropsAnotherFailsTheBuild()
        {
            var (result, log) = Build("module Dropper { import Step; extend Entry.Pick() { return 3; } };");

            Assert.Equal(BuildResultCode.BuildError, result);
            Assert.Contains(log.Messages, x => x.Type == LogMessageType.Error && x.Text.Contains("SDSL0136: Dropper.Pick doesn't call next, so Step never run in this effect. Write [Drops(Mixins = { Step })] on it, or call next."));
        }

        [Fact]
        public void AnOverrideThatDropsAnotherFailsTheBuild()
        {
            var (result, log) = Build("module Dropper { import Step; override void PSMain() { streams.ColorTarget = 2; } };");

            Assert.Equal(BuildResultCode.BuildError, result);
            Assert.Contains(log.Messages, x => x.Type == LogMessageType.Error && x.Text.Contains("SDSL0216: Dropper.PSMain doesn't call base, so Step never run in this effect. Write [Drops(Mixins = { Step })] on it, or call base."));
            Assert.Contains(log.Messages, x => x.Type == LogMessageType.Error && x.Text.Contains("This entry of an effect log failed. If the error above is about your shaders, fix it. If the log is older than the engine, delete it and record it again."));
        }

        [Theory]
        [InlineData("module Dropper { import Step; [Drops(Mixins = { Step })] override void PSMain() { streams.ColorTarget = 2; } };")]
        [InlineData("module Dropper { import Step; override void PSMain() { base.PSMain(); streams.ColorTarget *= 2; } };")]
        public void AnOverrideThatStatesItOrCallsBaseBuilds(string code)
        {
            var (result, log) = Build(code);

            Assert.True(result == BuildResultCode.Successful, string.Join(Environment.NewLine, log.Messages.Select(x => x.Text)));
        }

        // The effect log comes after the shaders, so it reads them from what they output
        private (BuildResultCode Result, LoggerResult Log) Build(string dropper)
        {
            var package = new Package { Meta = { Name = "Game" }, FullPath = Path.Combine(directory, "Game", "Game.sdpkg") };
            var shaders = new[] { AddShader(package, "Entry", Entry), AddShader(package, "Step", Step), AddShader(package, "Dropper", dropper) };
            var effectLog = new AssetItem("EffectCompileLog", new EffectLogAsset { Text = "---\n!EffectCompileRequest\nEffectName: Dropper\nUsedParameters: {}\n" });
            package.Assets.Add(effectLog);
            _ = new PackageSession(package);

            using var context = new AssetCompilerContext { CompilationContext = typeof(AssetCompilationContext), Platform = Core.PlatformType.Windows, Package = package };
            context.SetGameSettingsAsset(GameSettingsFactory.Create());

            var log = new LoggerResult();
            using var builder = new Builder(log, Path.Combine(directory, "Build"), "index");
            var effectSteps = new EffectLogAssetCompiler().Prepare(context, effectLog).BuildSteps;
            foreach (var shader in shaders)
            {
                var shaderSteps = new EffectShaderAssetCompiler().Prepare(context, shader).BuildSteps;
                builder.Root.Add(shaderSteps);
                BuildStep.LinkBuildSteps(shaderSteps, effectSteps);
            }
            builder.Root.Add(effectSteps);
            return (builder.Run(Builder.Mode.Build), log);
        }

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
