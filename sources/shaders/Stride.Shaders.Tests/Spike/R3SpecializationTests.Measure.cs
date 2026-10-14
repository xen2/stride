// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Stride.Core.Storage;
using Stride.Graphics;
using Stride.Shaders.Compiler;
using Stride.Shaders.Parsers.Tests.Corpus;
using Stride.Shaders.Spirv.Tools;

namespace Stride.Shaders.Parsers.Tests.Spike;

public partial class R3SpecializationTests
{
    static readonly GraphicsPlatform[] MixPlatforms = [GraphicsPlatform.Vulkan, GraphicsPlatform.Direct3D12, GraphicsPlatform.Direct3D11];

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>
    /// CPU time of the calling thread (kernel + user), in ms: not inflated when other processes load the machine.
    /// </summary>
    static double ThreadCpuMilliseconds()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user);
        return (kernel + user) / 10000.0;
    }

    /// <summary>
    /// Per-variant time of one step: the step runs over the whole variant set, <paramref name="runs"/> times after
    /// <paramref name="warmup"/>; result = median over runs of (set time / variant count), wall clock and thread CPU,
    /// and the slowest variant (each variant's best run).
    /// </summary>
    static (double PerVariant, double MaxVariant, double Set, double CpuPerVariant) TimeSet(int warmup, int runs, int count, Action<int> step)
    {
        var perRun = new double[runs];
        var cpuPerRun = new double[runs];
        var bestPerVariant = Enumerable.Repeat(double.MaxValue, count).ToArray();
        for (int r = -warmup; r < runs; r++)
        {
            var total = Stopwatch.StartNew();
            var cpuStart = ThreadCpuMilliseconds();
            for (int i = 0; i < count; i++)
            {
                var one = Stopwatch.StartNew();
                step(i);
                if (r >= 0)
                    bestPerVariant[i] = Math.Min(bestPerVariant[i], one.Elapsed.TotalMilliseconds);
            }
            if (r >= 0)
            {
                perRun[r] = total.Elapsed.TotalMilliseconds;
                cpuPerRun[r] = ThreadCpuMilliseconds() - cpuStart;
            }
        }
        Array.Sort(perRun);
        Array.Sort(cpuPerRun);
        var set = perRun[runs / 2];
        return (set / count, bestPerVariant.Max(), set, cpuPerRun[runs / 2] / count);
    }

    static double TimeParallel(int count, Action<int> step)
    {
        var stopwatch = Stopwatch.StartNew();
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, step);
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    static void SaveBytecode(string path, EffectReflection reflection, IEnumerable<ShaderBytecode> stages)
    {
        var bytecode = new EffectBytecode { Reflection = reflection, Stages = stages.ToArray(), HashSources = new() };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        bytecode.WriteTo(stream);
    }

    static IEnumerable<ShaderBytecode> SpirvStages(uint[] words, List<Spirv.Processing.Interfaces.InterfaceProcessor.EntryPointInfo> entryPoints)
    {
        var data = MemoryMarshal.AsBytes(words.AsSpan()).ToArray();
        var id = ObjectId.FromBytes(data);
        foreach (var entryPoint in entryPoints)
        {
            var name = new byte[Encoding.UTF8.GetByteCount(entryPoint.Name) + 1];
            Encoding.UTF8.GetBytes(entryPoint.Name, name);
            yield return new ShaderBytecode { Stage = entryPoint.Stage, Data = data, Id = id, EntryPoint = name };
        }
    }

    [Theory(Explicit = true)]
    [InlineData(5)]
    [InlineData(7)]
    public void Measure(int flagCount)
    {
        var count = 1 << flagCount;
        // Set-level repeats: the 7-flag set is 4x bigger
        var (setWarmup, setRuns) = flagCount <= 5 ? (1, 5) : (1, 3);
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        var output = Path.Combine(OutputDirectory, $"flags{flagCount}");
        Directory.CreateDirectory(output);
        var report = new StringBuilder();
        void Line(string text = "")
        {
            report.AppendLine(text);
            Console.WriteLine(text);
        }

        var entry = BaseEntry.Value;
        var profile = entry.Profile;
        Line($"# R3 measurement, {flagCount} flags ({string.Join(", ", LobeNames.Take(flagCount))}), {count} variants");
        Line();
        Line($"Base: corpus entry `{entry.Id}` ({profile}) + `{UberClass}` as an extra surface. {Environment.ProcessorCount} threads, {RuntimeInformation.FrameworkDescription}, " +
#if DEBUG
            "Debug build.");
#else
            "Release build.");
#endif
        Line($"Set timings: {setWarmup} warm-up + {setRuns} runs over all variants, median; single timings: 3 warm-up + 20 runs.");
        Line();

        var loader = NewLoader();
        var masks = Enumerable.Range(0, count).Select(x => (uint)x).ToArray();

        // ---------------------------------------------------------------------------------------------------------
        // Front end
        var separate = new Dictionary<GraphicsPlatform, Mixed[]>();
        var uber = new Dictionary<GraphicsPlatform, Mixed>();
        var spec = new Dictionary<GraphicsPlatform, uint[]>();
        var frontEnd = new List<(string Name, string Value)>();
        foreach (var platform in MixPlatforms)
        {
            var trees = masks.Select(m => ForwardTree(platform, SeparateGenerics(m))).ToArray();
            var mixed = new Mixed[count];
            var mixWarm = TimeSet(setWarmup, setRuns, count, i => mixed[i] = Mix(trees[i], platform, loader));
            separate[platform] = mixed;
            var mixParallel = TimeParallel(count, i => Mix(trees[i], platform, loader));

            var uberTree = ForwardTree(platform, UberGenerics(flagCount));
            var uberMix = Time(3, 20, () => uber[platform] = Mix(uberTree, platform, loader));
            var makeSpec = Time(3, 20, () => spec[platform] = MakeSpecConstants(uber[platform].Words, flagCount));
            var freezeCs = TimeSet(setWarmup, setRuns, count, i => Freeze(spec[platform], masks[i]));
            var freezeOpt = TimeSet(setWarmup, setRuns, count, i => FreezeWithSpirvOpt(spec[platform], masks[i], flagCount));

            frontEnd.Add(($"{platform}: mix one variant, warm class cache", $"{mixWarm.PerVariant:F1} ms wall, {mixWarm.CpuPerVariant:F1} ms CPU (slowest variant {mixWarm.MaxVariant:F1})"));
            frontEnd.Add(($"{platform}: separate, whole set, 1 thread", $"{mixWarm.Set:F0} ms"));
            frontEnd.Add(($"{platform}: separate, whole set, {Environment.ProcessorCount} threads", $"{mixParallel:F0} ms"));
            frontEnd.Add(($"{platform}: uber mix once", $"{uberMix.Median:F1} ms (p90 {uberMix.P90:F1})"));
            frontEnd.Add(($"{platform}: sentinels -> spec constants (once)", $"{makeSpec.Median:F2} ms"));
            frontEnd.Add(($"{platform}: freeze per variant, C#", $"{freezeCs.PerVariant:F3} ms"));
            frontEnd.Add(($"{platform}: freeze per variant, spirv-opt", $"{freezeOpt.PerVariant:F2} ms"));
            frontEnd.Add(($"{platform}: uber, whole set (mix + C# freeze of every variant)", $"{uberMix.Median + makeSpec.Median + freezeCs.Set:F0} ms"));
        }
        {
            // Cold: a new loader (empty class cache) per mix, a few variants
            var platform = GraphicsPlatform.Vulkan;
            var tree = ForwardTree(platform, SeparateGenerics((uint)count - 1));
            var cold = Time(1, 5, () => Mix(tree, platform, NewLoader()));
            frontEnd.Add(($"{platform}: mix one variant, cold class cache (new loader)", $"{cold.Median:F0} ms (p90 {cold.P90:F0})"));
        }
        Line("## Front end");
        Line();
        Line("| Step | Time |");
        Line("|---|---|");
        foreach (var (name, value) in frontEnd)
            Line($"| {name} | {value} |");
        Line();

        // ---------------------------------------------------------------------------------------------------------
        // Back ends: intermediates first, then each step timed over the set
        var frozen = MixPlatforms.ToDictionary(p => p, p => masks.Select(m => Freeze(spec[p], m)).ToArray());
        var dx11 = GraphicsPlatform.Direct3D11;
        var dx12 = GraphicsPlatform.Direct3D12;
        var vk = GraphicsPlatform.Vulkan;

        var legalSeparate = separate[dx11].Select(m => SpirvTools.LegalizeForHlsl(m.Words)).ToArray();
        var legalFrozen = frozen[dx11].Select(w => SpirvTools.LegalizeForHlsl(w)).ToArray();
        var crossSeparate = legalSeparate.Select(CrossCompile).ToArray();
        var crossFrozen = legalFrozen.Select(CrossCompile).ToArray();
        var legalSpec = SpirvTools.LegalizeForHlsl(spec[dx11]);
        var crossSpec = CrossCompile(legalSpec);
        File.WriteAllText(Path.Combine(output, "dx11-spec-ps.hlsl"), crossSpec.First(x => x.Stage == ShaderStage.Pixel).Code);

        var backEnd = new List<(string Backend, string Path, string Step, double PerVariant, double Max, double Cpu)>();
        void Step(string backend, string path, string step, Action<int> action)
        {
            var t = TimeSet(setWarmup, setRuns, count, action);
            backEnd.Add((backend, path, step, t.PerVariant, t.MaxVariant, t.CpuPerVariant));
            Console.WriteLine($"  {backend} / {path} / {step}: {t.PerVariant:F2} ms");
        }

        // Vulkan: today the mixer SPIR-V goes to the driver as is
        Step("Vulkan", "separate", "(none: mixer SPIR-V to the driver)", _ => { });
        Step("Vulkan", "V1 spec info", "(none: values at pipeline creation)", _ => { });
        Step("Vulkan", "V2 pre-specialized", "C# freeze", i => Freeze(spec[vk], masks[i]));
        Step("Vulkan", "V2 pre-specialized", "spirv-opt -O", i => SpirvTools.OptimizeForPerformance(frozen[vk][i]));
        Step("Vulkan", "separate, optimized", "spirv-opt -O", i => SpirvTools.OptimizeForPerformance(separate[vk][i].Words));

        // Direct3D 12
        Step("D3D12", "separate", "spirv_to_dxil (linked pipeline)", i => DxilPipeline(separate[dx12][i].Words, separate[dx12][i].EntryPoints));
        Step("D3D12", "D2 frozen", "C# freeze", i => Freeze(spec[dx12], masks[i]));
        Step("D3D12", "D2 frozen", "spirv_to_dxil (linked pipeline)", i => DxilPipeline(frozen[dx12][i], uber[dx12].EntryPoints));
        Step("D3D12", "D1 spec values", "spirv_to_dxil per stage + specializations", i => DxilSingleStage(spec[dx12], uber[dx12].EntryPoints, masks[i], flagCount));
        Step("D3D12", "control", "spirv_to_dxil per stage, separate module", i => DxilSingleStage(separate[dx12][i].Words, separate[dx12][i].EntryPoints, 0, 0));

        // Direct3D 11
        Step("D3D11", "separate", "legalize", i => SpirvTools.LegalizeForHlsl(separate[dx11][i].Words));
        Step("D3D11", "separate", "SPIRV-Cross", i => CrossCompile(legalSeparate[i]));
        Step("D3D11", "separate", "FXC", i => Fxc(crossSeparate[i], "", profile, separate[dx11][i].Reflection));
        Step("D3D11", "F1 frozen", "C# freeze", i => Freeze(spec[dx11], masks[i]));
        Step("D3D11", "F1 frozen", "legalize", i => SpirvTools.LegalizeForHlsl(frozen[dx11][i]));
        Step("D3D11", "F1 frozen", "SPIRV-Cross", i => CrossCompile(legalFrozen[i]));
        Step("D3D11", "F1 frozen", "FXC", i => Fxc(crossFrozen[i], "", profile, uber[dx11].Reflection));
        Step("D3D11", "F2 macros", "FXC with SPIRV_CROSS_CONSTANT_ID defines", i => Fxc(crossSpec, SpecDefines(masks[i], flagCount), profile, uber[dx11].Reflection));
        var f2Once = Time(3, 20, () => CrossCompile(SpirvTools.LegalizeForHlsl(spec[dx11])));

        // Tier 0: legalization only vs legalization + full optimization (on the frozen modules)
        Step("Tier 0", "frozen", "legalize only", i => SpirvTools.LegalizeForHlsl(frozen[vk][i]));
        Step("Tier 1", "frozen", "legalize + spirv-opt -O", i => SpirvTools.OptimizeForPerformance(SpirvTools.LegalizeForHlsl(frozen[vk][i])));

        Line("## Back ends (ms per variant)");
        Line();
        Line("| Backend | Path | Step | Per variant wall | Per variant CPU | Slowest variant |");
        Line("|---|---|---|---|---|---|");
        foreach (var (backend, path, step, perVariant, max, cpu) in backEnd)
            Line($"| {backend} | {path} | {step} | {perVariant:F2} | {cpu:F2} | {max:F2} |");
        Line();
        Line($"D3D11 F2 once per family (legalize + SPIRV-Cross of the spec module): {f2Once.Median:F1} ms.");
        Line();

        // Whole set, full path, 1 thread vs all threads
        Line("## Whole set, full CPU path per backend (mix included)");
        Line();
        Line("| Path | 1 thread (ms) | all threads (ms) |");
        Line("|---|---|---|");
        void Whole(string name, Action<int> perVariant, Action? once = null)
        {
            var single = Stopwatch.StartNew();
            once?.Invoke();
            for (int i = 0; i < count; i++)
                perVariant(i);
            var t1 = single.Elapsed.TotalMilliseconds;
            var parallel = Stopwatch.StartNew();
            once?.Invoke();
            Parallel.For(0, count, perVariant);
            var tn = parallel.Elapsed.TotalMilliseconds;
            Line($"| {name} | {t1:F0} | {tn:F0} |");
        }
        var treesDx11 = masks.Select(m => ForwardTree(dx11, SeparateGenerics(m))).ToArray();
        var treesDx12 = masks.Select(m => ForwardTree(dx12, SeparateGenerics(m))).ToArray();
        var uberTreeDx11 = ForwardTree(dx11, UberGenerics(flagCount));
        var uberTreeDx12 = ForwardTree(dx12, UberGenerics(flagCount));
        uint[]? specOnce11 = null, specOnce12 = null;
        List<(ShaderStage Stage, string Code)>? crossOnce = null;
        EffectReflection? reflectionOnce = null;
        Whole("D3D11 separate (mix, legalize, Cross, FXC)", i =>
        {
            var m = Mix(treesDx11[i], dx11, loader);
            Fxc(CrossCompile(SpirvTools.LegalizeForHlsl(m.Words)), "", profile, m.Reflection);
        });
        Whole("D3D11 F1 (mix once; freeze, legalize, Cross, FXC)", i =>
        {
            Fxc(CrossCompile(SpirvTools.LegalizeForHlsl(Freeze(specOnce11!, masks[i]))), "", profile, reflectionOnce!);
        }, () => { var m = Mix(uberTreeDx11, dx11, loader); specOnce11 = MakeSpecConstants(m.Words, flagCount); reflectionOnce = m.Reflection; });
        Whole("D3D11 F2 (mix, legalize, Cross once; FXC)", i =>
        {
            Fxc(crossOnce!, SpecDefines(masks[i], flagCount), profile, reflectionOnce!);
        }, () => { var m = Mix(uberTreeDx11, dx11, loader); crossOnce = CrossCompile(SpirvTools.LegalizeForHlsl(MakeSpecConstants(m.Words, flagCount))); reflectionOnce = m.Reflection; });
        Whole("D3D12 separate (mix, spirv_to_dxil)", i =>
        {
            var m = Mix(treesDx12[i], dx12, loader);
            DxilPipeline(m.Words, m.EntryPoints);
        });
        Whole("D3D12 D2 (mix once; freeze, spirv_to_dxil)", i =>
        {
            DxilPipeline(Freeze(specOnce12!, masks[i]), uber[dx12].EntryPoints);
        }, () => specOnce12 = MakeSpecConstants(Mix(uberTreeDx12, dx12, loader).Words, flagCount));
        Line();

        // ---------------------------------------------------------------------------------------------------------
        // Quality
        var runtime = MixPlatforms.ToDictionary(p => p, p => Mix(ForwardTree(p, RuntimeGenerics()), p, loader));
        var runtimeOptimized = InstructionCount(OptimizedText(runtime[vk].Words));
        var runtimeFxc = Fxc(CrossCompile(SpirvTools.LegalizeForHlsl(runtime[dx11].Words)), "", profile, runtime[dx11].Reflection);
        var runtimeDxil = DxilPipeline(runtime[dx12].Words, runtime[dx12].EntryPoints);

        var quality = new ConcurrentBag<string>();
        var identical = 0;
        var legalIdentical = 0;
        var rows = new string[count];
        Parallel.For(0, count, i =>
        {
            var mask = masks[i];
            var a = OptimizedText(separate[vk][i].Words);
            var b = OptimizedText(frozen[vk][i]);
            if (a == b)
                Interlocked.Increment(ref identical);
            else
            {
                File.WriteAllText(Path.Combine(output, $"diff-{mask}-separate.spvasm"), a);
                File.WriteAllText(Path.Combine(output, $"diff-{mask}-frozen.spvasm"), b);
            }
            if (SpirvNormalizer.Normalize(MemoryMarshal.Cast<uint, int>(legalSeparate[i].AsSpan()), keepNames: false) == SpirvNormalizer.Normalize(MemoryMarshal.Cast<uint, int>(legalFrozen[i].AsSpan()), keepNames: false))
                Interlocked.Increment(ref legalIdentical);

            var fxcSeparate = Fxc(crossSeparate[i], "", profile, separate[dx11][i].Reflection).First(s => s.Stage == ShaderStage.Pixel);
            var fxcF1 = Fxc(crossFrozen[i], "", profile, uber[dx11].Reflection).First(s => s.Stage == ShaderStage.Pixel);
            var fxcF2 = Fxc(crossSpec, SpecDefines(mask, flagCount), profile, uber[dx11].Reflection).First(s => s.Stage == ShaderStage.Pixel);
            var dxilSeparate = DxilPipeline(separate[dx12][i].Words, separate[dx12][i].EntryPoints);
            var dxilD2 = DxilPipeline(frozen[dx12][i], uber[dx12].EntryPoints);
            var dxilD1 = DxilSingleStage(spec[dx12], uber[dx12].EntryPoints, mask, flagCount);
            int PixelSize(List<ShaderBytecode> s) => s.First(x => x.Stage == ShaderStage.Pixel).Data.Length;
            var pixelIndex = uber[dx12].EntryPoints.FindIndex(e => e.Stage == ShaderStage.Pixel);

            rows[i] = $"| {Convert.ToString(mask, 2).PadLeft(flagCount, '0')} | {InstructionCount(a)} | {(a == b ? "same" : InstructionCount(b).ToString(CultureInfo.InvariantCulture))} " +
                $"| {fxcSeparate.Instructions} / {fxcSeparate.Temps} | {fxcF1.Instructions} / {fxcF1.Temps} | {fxcF2.Instructions} / {fxcF2.Temps} " +
                $"| {PixelSize(dxilSeparate)} | {PixelSize(dxilD2)} | {dxilD1[pixelIndex]} |";

            // Inputs of the pipeline-creation test
            SaveBytecode(Path.Combine(output, "vulkan", $"separate-{mask}.sdfxbytecode"), separate[vk][i].Reflection, SpirvStages(separate[vk][i].Words, separate[vk][i].EntryPoints));
            SaveBytecode(Path.Combine(output, "vulkan", $"optimized-{mask}.sdfxbytecode"), uber[vk].Reflection, SpirvStages(SpirvTools.OptimizeForPerformance(frozen[vk][i]), uber[vk].EntryPoints));
            SaveBytecode(Path.Combine(output, "d3d12", $"separate-{mask}.sdfxbytecode"), separate[dx12][i].Reflection, dxilSeparate);
            SaveBytecode(Path.Combine(output, "d3d12", $"frozen-{mask}.sdfxbytecode"), uber[dx12].Reflection, dxilD2);
        });
        SaveBytecode(Path.Combine(output, "vulkan", "spec.sdfxbytecode"), uber[vk].Reflection, SpirvStages(spec[vk], uber[vk].EntryPoints));
        SaveBytecode(Path.Combine(output, "vulkan", "runtime.sdfxbytecode"), runtime[vk].Reflection, SpirvStages(runtime[vk].Words, runtime[vk].EntryPoints));
        SaveBytecode(Path.Combine(output, "d3d12", "runtime.sdfxbytecode"), runtime[dx12].Reflection, runtimeDxil);
        foreach (var m in masks.Where(m => m == 0 || m == count - 1 || m == 0b101))
        {
            var i = (int)m;
            File.WriteAllBytes(Path.Combine(output, $"dxil-separate-{m}-ps.dxil"), DxilPipeline(separate[dx12][i].Words, separate[dx12][i].EntryPoints).First(x => x.Stage == ShaderStage.Pixel).Data);
            File.WriteAllBytes(Path.Combine(output, $"dxil-frozen-{m}-ps.dxil"), DxilPipeline(frozen[dx12][i], uber[dx12].EntryPoints).First(x => x.Stage == ShaderStage.Pixel).Data);
        }
        File.WriteAllBytes(Path.Combine(output, "dxil-runtime-ps.dxil"), runtimeDxil.First(x => x.Stage == ShaderStage.Pixel).Data);

        Line("## Quality");
        Line();
        Line($"SPIR-V after legalize + `spirv-opt -O`: specialized == separate for **{identical} / {count}** variants (normalized, names stripped). " +
             $"After legalization only: {legalIdentical} / {count}.");
        Line();
        var runtimePs = runtimeFxc.First(s => s.Stage == ShaderStage.Pixel);
        Line($"Runtime-flag reference (one shader, flags from a cbuffer): {runtimeOptimized} SPIR-V instructions after -O, FXC PS {runtimePs.Instructions} slots / {runtimePs.Temps} temps, DXIL PS {runtimeDxil.First(x => x.Stage == ShaderStage.Pixel).Data.Length} bytes.");
        Line();
        Line("| Mask | SPIR-V instr. separate | specialized | FXC PS slots / temps separate | F1 | F2 | DXIL PS bytes separate | D2 | D1 |");
        Line("|---|---|---|---|---|---|---|---|---|");
        foreach (var row in rows)
            Line(row);

        File.WriteAllText(Path.Combine(output, "report.md"), report.ToString());
        Console.WriteLine($"Report: {Path.Combine(output, "report.md")}");
    }
}
