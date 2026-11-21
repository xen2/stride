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
/// test that prepares it itself.
/// </remarks>
internal static class SampleQueue
{
    // Samples prepared or being prepared that no test took yet
    private const int Ahead = 2;

    private static readonly object Lock = new();
    private static readonly SemaphoreSlim Slots = new(Ahead);
    // The samples to prepare, in the order their tests run, that nobody started yet
    private static readonly List<string> planned = [];
    private static readonly Dictionary<string, Task<SampleResult?>> started = new(StringComparer.OrdinalIgnoreCase);
    private static bool running;

    /// <summary>Sets the samples the coming tests use, in the order they run.</summary>
    internal static void Plan(IEnumerable<string> sampleNames)
    {
        lock (Lock)
        {
            planned.Clear();
            planned.AddRange(sampleNames);
        }
    }

    /// <summary>
    /// Gives the folder of <paramref name="fixture"/>'s sample to the caller, prepared: waits for it when the queue started
    /// it, else prepares it now. Returns null on success; a failure SampleResult on regen error.
    /// </summary>
    internal static SampleResult? Take(DiscoveredFixture fixture, string worktreeRoot)
    {
        var fixtures = ScreenshotRunner.LoadFixtures(worktreeRoot);
        Task<SampleResult?>? ahead;
        bool start;
        lock (Lock)
        {
            if (!started.Remove(fixture.SampleName, out ahead))
                planned.RemoveAll(x => string.Equals(x, fixture.SampleName, StringComparison.OrdinalIgnoreCase));
            start = !running && planned.Count > 0;
            running |= start;
        }
        if (start)
            _ = Task.Run(() => PrepareAhead(fixtures, worktreeRoot));

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
