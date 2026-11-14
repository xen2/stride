// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// TEMPORARY diagnostic for the iOS flake of TestCapturePhase.ReadingMaskedStateDoesNotAllocate. Not for master.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Xunit;

namespace Stride.Input.Tests;

public class DiagMaskedStateAlloc
{
    private const int Rounds = 40;
    private const int Warmup = 64;
    private const int Iterations = 256;
    private const int Attempts = 3;
    private const int MaxRecords = 8192;

    // Preallocated so recording never allocates inside a measured window
    private static readonly int[] recRound = new int[MaxRecords];
    private static readonly int[] recProbe = new int[MaxRecords];
    private static readonly int[] recAttempt = new int[MaxRecords];
    private static readonly int[] recIter = new int[MaxRecords];
    private static readonly long[] recDelta = new long[MaxRecords];
    private static int recCount;
    private static int recDropped;

    private static readonly string[] probeNames =
    {
        "empty", "gamePad.State", "gamePad.DownButtons.Count", "Keyboard.DownKeys.Count",
        "Mouse.DownButtons.Count", "Mouse.ReleasedButtons.Count", "all5", "raw-counter", "fresh-3000",
    };

    private static int freshSink;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int FreshMethod(int x) => x + 1;

    private static void Record(int round, int probe, int attempt, int iter, long delta)
    {
        if (recCount < MaxRecords)
        {
            recRound[recCount] = round;
            recProbe[recCount] = probe;
            recAttempt[recCount] = attempt;
            recIter[recCount] = iter;
            recDelta[recCount] = delta;
            recCount++;
        }
        else
        {
            recDropped++;
        }
    }

    private static void MeasureProbe(int round, int probe, Action action, int warmup, int iterations, int attempts)
    {
        for (int i = 0; i < warmup; i++)
            action();

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long sum = 0;
            long attemptStart = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                action();
                long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                if (delta != 0)
                {
                    sum += delta;
                    Record(round, probe, attempt, i, delta);
                }
            }
            long total = GC.GetAllocatedBytesForCurrentThread() - attemptStart;

            // Bytes allocated between iterations, by the loop itself rather than by the probe
            if (total != sum)
                Record(round, probe, attempt, -1, total - sum);
            int gcs = (GC.CollectionCount(0) - g0) * 10000 + (GC.CollectionCount(1) - g1) * 100 + (GC.CollectionCount(2) - g2);
            if (gcs != 0)
                Record(round, probe, attempt, -2, gcs);
        }
    }

    private static void MeasureRawCounter(int round, int attempts, int iterations)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            for (int i = 0; i < iterations; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                if (delta != 0)
                    Record(round, 7, attempt, i, delta);
            }
        }
    }

    [Fact]
    public void Diagnose()
    {
        var thread = Thread.CurrentThread;
        var header = new StringBuilder();
        header.Append($"runtime={RuntimeInformation.FrameworkDescription} dynCode={RuntimeFeature.IsDynamicCodeSupported}/{RuntimeFeature.IsDynamicCodeCompiled} ");
        header.Append($"thread={thread.ManagedThreadId} pool={thread.IsThreadPoolThread} sync={SynchronizationContext.Current?.GetType().Name ?? "null"} cpus={Environment.ProcessorCount} ");
        header.Append($"gcAtStart={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} heap={GC.GetTotalMemory(false):N0}\n");

        int gcMeasureFailures = 0;
        var gcMeasureResults = new StringBuilder();
        Action fresh = () => freshSink = FreshMethod(freshSink);

        for (int round = 0; round < Rounds; round++)
        {
            using var headless = new HeadlessInput();
            var source = new InputSourceSimulated();
            headless.Input.Sources.Add(source);
            var gamePad = source.AddGamePad();
            var owner = new object();
            headless.Input.TryCapture(gamePad, owner);
            gamePad.SetButton(GamePadButton.A, true);
            headless.Keyboard.SimulateDown(Keys.W);
            headless.Mouse.SimulateMouseDown(MouseButton.Left);
            headless.Update();
            headless.Input.Release(gamePad, owner);

            // The same measurement as the test, first, as the test sees it
            var measurement = GCMeasure.Run(() =>
            {
                _ = gamePad.State;
                _ = gamePad.DownButtons.Count;
                _ = headless.Keyboard.DownKeys.Count;
                _ = headless.Mouse.DownButtons.Count;
                _ = headless.Mouse.ReleasedButtons.Count;
            });
            if (measurement.AllocatedBytes != 0)
            {
                gcMeasureFailures++;
                gcMeasureResults.Append($"  round {round}: {measurement}\n");
            }

            MeasureProbe(round, 0, () => { }, Warmup, Iterations, Attempts);
            MeasureProbe(round, 1, () => _ = gamePad.State, Warmup, Iterations, Attempts);
            MeasureProbe(round, 2, () => _ = gamePad.DownButtons.Count, Warmup, Iterations, Attempts);
            MeasureProbe(round, 3, () => _ = headless.Keyboard.DownKeys.Count, Warmup, Iterations, Attempts);
            MeasureProbe(round, 4, () => _ = headless.Mouse.DownButtons.Count, Warmup, Iterations, Attempts);
            MeasureProbe(round, 5, () => _ = headless.Mouse.ReleasedButtons.Count, Warmup, Iterations, Attempts);
            MeasureProbe(round, 6, () =>
            {
                _ = gamePad.State;
                _ = gamePad.DownButtons.Count;
                _ = headless.Keyboard.DownKeys.Count;
                _ = headless.Mouse.DownButtons.Count;
                _ = headless.Mouse.ReleasedButtons.Count;
            }, Warmup, Iterations, Attempts);
            MeasureRawCounter(round, Attempts, Iterations);
            MeasureProbe(round, 8, fresh, 0, 3000, 1);
        }

        var report = new StringBuilder(header.ToString());
        report.Append($"GCMeasure.Run failures: {gcMeasureFailures}/{Rounds}\n{gcMeasureResults}");
        report.Append($"records={recCount} dropped={recDropped} (iter -1 = between iterations, -2 = GC counts g0*10000+g1*100+g2)\n");

        // Per probe: how many attempts allocated, and the distinct (iter, delta) pairs
        for (int probe = 0; probe < probeNames.Length; probe++)
        {
            int n = 0;
            var lines = new StringBuilder();
            int shown = 0;
            for (int r = 0; r < recCount; r++)
            {
                if (recProbe[r] != probe)
                    continue;
                n++;
                if (shown++ < 40)
                    lines.Append($" r{recRound[r]}a{recAttempt[r]}i{recIter[r]}:{recDelta[r]}");
            }
            report.Append($"[{probe} {probeNames[probe]}] records={n}{lines}\n");
        }

        Assert.Fail(report.ToString());
    }
}
