// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Globalization;
using System.Text;
using Stride.Graphics;
using Stride.Shaders.Compiler;
using Stride.Shaders.Parsers.Tests.Corpus;
using Stride.Shaders.Spirv.Tools;

namespace Stride.Shaders.Parsers.Tests.Spike;

public partial class R3SpecializationTests
{
    /// <summary>
    /// The real corpus family <c>GaussianBlurShader&lt;BlurCount, IsVertical&gt;</c> (10 entries): <c>IsVertical</c> as a
    /// spec constant, <c>BlurCount</c> stays a separate compile (it sizes a cbuffer array).
    /// </summary>
    [Fact(Explicit = true)]
    public void GaussianBlurFamily()
    {
        var file = Path.Combine(SpikeDirectory, "GaussianBlurShaderR3.sdsl");
        var loader = ShaderCorpus.CreateLoader(file);
        var profile = ShaderCorpus.ReadEntries().First(e => e.EffectName == "GaussianBlurEffect").Profile;
        var report = new StringBuilder();
        report.AppendLine("## GaussianBlurShader family (corpus: BlurCount 2..6 x IsVertical)");
        report.AppendLine();
        report.AppendLine("| Platform | BlurCount | separate: 2 mixes (ms) | uber: 1 mix + 2 C# freezes (ms) | specialized == separate after -O (declaration order ignored) | FXC PS slots separate / F1 |");
        report.AppendLine("|---|---|---|---|---|---|");

        ShaderMixinSource Tree(GraphicsPlatform platform, int blurCount, string isVertical)
        {
            var mixin = new ShaderMixinSource { Name = "GaussianBlurEffect" };
            mixin.Mixins.Add(new ShaderClassSource("GaussianBlurShaderR3", blurCount.ToString(CultureInfo.InvariantCulture), isVertical));
            EffectCompiler.AddPlatformMacros(mixin, new EffectCompilerParameters { Platform = platform, Profile = profile });
            return mixin;
        }

        foreach (var platform in new[] { GraphicsPlatform.Vulkan, GraphicsPlatform.Direct3D11 })
        {
            for (int blurCount = 2; blurCount <= 6; blurCount++)
            {
                var separateTrees = new[] { Tree(platform, blurCount, "0"), Tree(platform, blurCount, "1") };
                var uberTree = Tree(platform, blurCount, Sentinel.ToString(CultureInfo.InvariantCulture));
                Mixed[] separate = new Mixed[2];
                uint[] spec = [];
                var separateTime = Time(3, 20, () => { separate[0] = Mix(separateTrees[0], platform, loader); separate[1] = Mix(separateTrees[1], platform, loader); });
                var uberTime = Time(3, 20, () => { spec = MakeSpecConstants(Mix(uberTree, platform, loader).Words, 1); Freeze(spec, 0); Freeze(spec, 1); });
                // Same lines as a set: the order of global declarations can differ (same code)
                static string Sorted(string text) => string.Join("\n", text.Split('\n').Order(StringComparer.Ordinal));
                var same = Enumerable.Range(0, 2).All(m => Sorted(OptimizedText(separate[m].Words)) == Sorted(OptimizedText(Freeze(spec, (uint)m))));
                if (!same)
                {
                    Directory.CreateDirectory(OutputDirectory);
                    File.WriteAllText(Path.Combine(OutputDirectory, $"gaussian-{platform}-{blurCount}-separate.spvasm"), OptimizedText(separate[1].Words));
                    File.WriteAllText(Path.Combine(OutputDirectory, $"gaussian-{platform}-{blurCount}-frozen.spvasm"), OptimizedText(Freeze(spec, 1)));
                }
                var fxc = "";
                if (platform == GraphicsPlatform.Direct3D11)
                {
                    var a = Fxc(CrossCompile(SpirvTools.LegalizeForHlsl(separate[1].Words)), "", profile, separate[1].Reflection).First(s => s.Stage == ShaderStage.Pixel);
                    var b = Fxc(CrossCompile(SpirvTools.LegalizeForHlsl(Freeze(spec, 1))), "", profile, separate[1].Reflection).First(s => s.Stage == ShaderStage.Pixel);
                    fxc = $"{a.Instructions} / {b.Instructions}";
                }
                report.AppendLine(CultureInfo.InvariantCulture, $"| {platform} | {blurCount} | {separateTime.Median:F1} | {uberTime.Median:F1} | {same} | {fxc} |");
            }
        }

        Directory.CreateDirectory(OutputDirectory);
        File.WriteAllText(Path.Combine(OutputDirectory, "gaussian.md"), report.ToString());
        Console.WriteLine(report.ToString());
    }
}
