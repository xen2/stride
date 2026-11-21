// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Stride.SampleScreenshotRunner;

/// <summary>
/// Prepares the samples of a run (generation from the template, then the upgrade) before its first test, several at a
/// time, then gives each test the folder of its sample.
/// </summary>
/// <remarks>
/// The upgrades run before the builds and the game runs rather than next to them: on a runner with few cores they would
/// slow them down, and a game that waits in game time can then capture another frame. A test that passes gives the folder
/// to the next test of the same sample, which takes it as it is. The tests run one at a time, so the state has no lock.
/// </remarks>
internal static class PreparedSamples
{
    // An upgrade barely uses more than one core: 4 at once finish about twice as soon on a 4-core runner
    private const int AtOnce = 4;

    // The samples of the run, in the order of their first test
    private static readonly List<string> planned = [];
    // The number of tests still to come for each sample
    private static readonly Dictionary<string, int> uses = new(StringComparer.OrdinalIgnoreCase);
    // The prepared folders a test gave back for the next test of the same sample
    private static readonly HashSet<string> kept = new(StringComparer.OrdinalIgnoreCase);
    // The result of each preparation no test took yet: null on success
    private static ConcurrentDictionary<string, SampleResult?>? prepared;

    /// <summary>Sets the sample of each coming test, in the order they run.</summary>
    internal static void Plan(IEnumerable<string> sampleNames)
    {
        planned.Clear();
        uses.Clear();
        kept.Clear();
        prepared = null;
        foreach (var name in sampleNames)
        {
            var count = uses.GetValueOrDefault(name) + 1;
            uses[name] = count;
            if (count == 1)
                planned.Add(name);
        }
    }

    /// <summary>
    /// Gives the folder of <paramref name="fixture"/>'s sample to the caller, prepared: as the previous test of the same
    /// sample left it, else as prepared before the first test, else prepared now. The first call prepares the samples of
    /// the run. Returns null on success; a failure SampleResult on regen error.
    /// </summary>
    internal static SampleResult? Take(DiscoveredFixture fixture, string worktreeRoot)
    {
        prepared ??= PrepareAll(worktreeRoot);
        if (uses.TryGetValue(fixture.SampleName, out var count))
            uses[fixture.SampleName] = count - 1;

        if (kept.Remove(fixture.SampleName))
        {
            Console.WriteLine($"[{fixture.SampleName}] takes the folder of its previous test");
            return null;
        }

        if (prepared.TryRemove(fixture.SampleName, out var result))
            return result;

        return ScreenshotRunner.RegenerateSample(fixture, ScreenshotRunner.SampleDirectory(worktreeRoot, fixture.SampleName));
    }

    /// <summary>
    /// Gives back the folder of a test that passed. Returns false when a later test of the run uses the same sample: that
    /// test takes the folder; true when the caller may delete it.
    /// </summary>
    internal static bool Release(string sampleName)
    {
        if (uses.GetValueOrDefault(sampleName) <= 0)
            return true;
        kept.Add(sampleName);
        return false;
    }

    private static ConcurrentDictionary<string, SampleResult?> PrepareAll(string worktreeRoot)
    {
        var fixtures = ScreenshotRunner.LoadFixtures(worktreeRoot);
        var toPrepare = planned
            .Select(name => fixtures.FirstOrDefault(x => string.Equals(x.SampleName, name, StringComparison.OrdinalIgnoreCase)))
            .OfType<DiscoveredFixture>()
            .ToList();

        var watch = Stopwatch.StartNew();
        var results = new ConcurrentDictionary<string, SampleResult?>(StringComparer.OrdinalIgnoreCase);
        // One sample at a time per worker, so that a long upgrade doesn't hold back the samples after it
        Parallel.ForEach(
            Partitioner.Create(toPrepare, EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = AtOnce },
            fixture => results[fixture.SampleName] = ScreenshotRunner.RegenerateSample(fixture, ScreenshotRunner.SampleDirectory(worktreeRoot, fixture.SampleName)));
        Console.WriteLine($"Prepared {results.Count} samples in {watch.Elapsed.TotalSeconds:F0} s, {AtOnce} at a time");
        return results;
    }
}
