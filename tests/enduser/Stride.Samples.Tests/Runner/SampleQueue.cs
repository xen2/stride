// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;

namespace Stride.SampleScreenshotRunner;

/// <summary>
/// Prepares the samples of a run (generation from the template, then the upgrade) ahead of the tests that use them, while
/// the current test builds and runs its own.
/// </summary>
/// <remarks>
/// One owner at a time for each sample folder: the queue, from the moment it starts a sample until a test takes it, or the
/// test that prepares it itself. A test that passes gives the folder to the next test of the same sample, which takes it as
/// it is.
/// </remarks>
internal static class SampleQueue
{
    // Samples prepared or being prepared that no test took yet
    private const int Ahead = 2;

    private static readonly object Lock = new();
    private static readonly SemaphoreSlim Slots = new(Ahead);
    // The samples to prepare, in the order of their first test, that nobody started yet
    private static readonly List<string> planned = [];
    // The number of tests still to come for each sample
    private static readonly Dictionary<string, int> uses = new(StringComparer.OrdinalIgnoreCase);
    // The prepared folders a test gave back for the next test of the same sample
    private static readonly HashSet<string> kept = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<SampleResult?>> started = new(StringComparer.OrdinalIgnoreCase);
    private static bool running;

    /// <summary>Sets the sample of each coming test, in the order they run.</summary>
    internal static void Plan(IEnumerable<string> sampleNames)
    {
        lock (Lock)
        {
            planned.Clear();
            uses.Clear();
            kept.Clear();
            foreach (var name in sampleNames)
            {
                if (uses.TryGetValue(name, out var count))
                {
                    uses[name] = count + 1;
                }
                else
                {
                    uses[name] = 1;
                    planned.Add(name);
                }
            }
        }
    }

    /// <summary>
    /// Gives the folder of <paramref name="fixture"/>'s sample to the caller, prepared: as the previous test of the same
    /// sample left it, else waits for it when the queue started it, else prepares it now. Returns null on success; a
    /// failure SampleResult on regen error.
    /// </summary>
    internal static SampleResult? Take(DiscoveredFixture fixture, string worktreeRoot)
    {
        var fixtures = ScreenshotRunner.LoadFixtures(worktreeRoot);
        Task<SampleResult?>? ahead = null;
        bool reuse, start;
        lock (Lock)
        {
            if (uses.TryGetValue(fixture.SampleName, out var count))
                uses[fixture.SampleName] = count - 1;
            reuse = kept.Remove(fixture.SampleName);
            if (!reuse && !started.Remove(fixture.SampleName, out ahead))
                planned.RemoveAll(x => string.Equals(x, fixture.SampleName, StringComparison.OrdinalIgnoreCase));
            start = !running && planned.Count > 0;
            running |= start;
        }
        if (start)
            _ = Task.Run(() => PrepareAhead(fixtures, worktreeRoot));

        if (reuse)
        {
            Console.WriteLine($"[{fixture.SampleName}] takes the folder of its previous test");
            return null;
        }

        if (ahead is null)
            return ScreenshotRunner.RegenerateSample(fixture, ScreenshotRunner.SampleDirectory(worktreeRoot, fixture.SampleName));

        var watch = Stopwatch.StartNew();
        try
        {
            var result = ahead.GetAwaiter().GetResult();
            Console.WriteLine($"[{fixture.SampleName}] prepared ahead; waited {watch.Elapsed.TotalSeconds:F0} s for it");
            return result;
        }
        finally
        {
            Slots.Release();
        }
    }

    /// <summary>
    /// Gives back the folder of a test that passed. Returns false when a later test of the run uses the same sample: that
    /// test takes the folder; true when the caller may delete it.
    /// </summary>
    internal static bool Release(string sampleName)
    {
        lock (Lock)
        {
            if (uses.GetValueOrDefault(sampleName) <= 0)
                return true;
            kept.Add(sampleName);
            return false;
        }
    }

    private static async Task PrepareAhead(IReadOnlyList<DiscoveredFixture> fixtures, string worktreeRoot)
    {
        while (true)
        {
            await Slots.WaitAsync();
            lock (Lock)
            {
                var fixture = NextPlanned(fixtures);
                if (fixture is null)
                {
                    Slots.Release();
                    running = false;
                    return;
                }
                started[fixture.SampleName] = Task.Run(() => ScreenshotRunner.RegenerateSample(fixture, ScreenshotRunner.SampleDirectory(worktreeRoot, fixture.SampleName)));
            }
        }
    }

    // Under the lock
    private static DiscoveredFixture? NextPlanned(IReadOnlyList<DiscoveredFixture> fixtures)
    {
        while (planned.Count > 0)
        {
            var name = planned[0];
            planned.RemoveAt(0);
            var fixture = fixtures.FirstOrDefault(x => string.Equals(x.SampleName, name, StringComparison.OrdinalIgnoreCase));
            if (fixture is not null)
                return fixture;
        }
        return null;
    }
}
