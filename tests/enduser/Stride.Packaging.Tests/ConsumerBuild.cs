// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Xunit;

namespace Stride.Packaging.Tests;

/// <summary>An extra local package feed plus the Stride.* glob patterns it should serve.</summary>
internal sealed record ExtraFeed(string Key, string Path, params string[] Patterns);

internal static class NuGetConsumerFeed
{
    /// <summary>
    /// nuget.config resolving Stride.* only from the workspace bin/packages feed — no public
    /// fallback for first-party packages, so a missing-from-pack package fails as NU1101
    /// instead of silently resolving a public version. Mirrors the workspace's own
    /// packageSourceMapping (Stride.Dependencies.* etc. still flow from nuget.org). Extra
    /// feeds (e.g. a freshly packed plugin) are mapped to their own patterns. The packages go to the case's own
    /// folder, the engine's come from the shared one (<see cref="TestEnvironment.SharedPackagesFolder"/>).
    /// </summary>
    public static void WriteStrictNuGetConfig(string consumerDir, IReadOnlyList<ExtraFeed>? extraFeeds = null)
        => WriteStrictNuGetConfig(consumerDir, TestEnvironment.CasePackagesFolder(consumerDir), TestEnvironment.SharedPackagesFolder, extraFeeds);

    /// <summary>The same, with the packages in <paramref name="packagesFolder"/> and no other fallback folder than <paramref name="fallbackFolder"/>.</summary>
    /// <remarks>The companion packages a session restores go to that folder too: the session reads this nuget.config.</remarks>
    public static void WriteStrictNuGetConfig(string consumerDir, string packagesFolder, string? fallbackFolder, IReadOnlyList<ExtraFeed>? extraFeeds = null)
    {
        var binPackages = TestEnvironment.BinPackages();
        var fallback = fallbackFolder is not null ? $"""<add key="shared" value="{fallbackFolder}" />""" : "";

        var sources = new StringBuilder();
        var mappings = new StringBuilder();
        if (extraFeeds is not null)
        {
            foreach (var feed in extraFeeds)
            {
                sources.AppendLine($"""    <add key="{feed.Key}" value="{feed.Path}" />""");
                mappings.AppendLine($"""    <packageSource key="{feed.Key}">""");
                foreach (var pattern in feed.Patterns)
                    mappings.AppendLine($"""      <package pattern="{pattern}" />""");
                mappings.AppendLine("""    </packageSource>""");
            }
        }

        File.WriteAllText(Path.Combine(consumerDir, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{packagesFolder}" />
              </config>
              <fallbackPackageFolders>
                <clear />
                {fallback}
              </fallbackPackageFolders>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="stride-local" value="{binPackages}" />
            {sources.ToString().TrimEnd()}
              </packageSources>
              <packageSourceMapping>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                  <package pattern="Stride.GNU.*" />
                  <package pattern="Stride.Mono.*" />
                  <package pattern="Stride.Dependencies.*" />
                </packageSource>
                <packageSource key="stride-local">
                  <package pattern="Stride" />
                  <package pattern="Stride.*" />
                </packageSource>
            {mappings.ToString().TrimEnd()}
              </packageSourceMapping>
            </configuration>
            """);
    }
}

/// <summary>Exit code plus the captured combined stdout/stderr of a dotnet invocation.</summary>
internal sealed record ExecResult(int ExitCode, string Output);

internal static class Dotnet
{
    // Diagnostics: when set, every build, pack and restore writes a binlog to this folder
    private static readonly string? BinlogDir = Environment.GetEnvironmentVariable("STRIDE_PACKAGING_BINLOG_DIR");
    private static int binlogCount;

    /// <summary>
    /// Run a <c>dotnet</c> command in a case's temp tree, streaming output to the test log; returns exit code + captured output.
    /// It runs on the SDK the repo's global.json selects, and the asset compilers it starts hash files into the case's own
    /// file version cache, not the machine's.
    /// </summary>
    public static ExecResult Exec(IEnumerable<string> args, string workingDir, ITestOutputHelper output, int timeoutMin)
    {
        var caseDir = TestEnvironment.CaseDirectory(workingDir);
        TestEnvironment.WriteGlobalJson(caseDir);
        var argList = args.ToList();
        if (!string.IsNullOrEmpty(BinlogDir) && argList.Count > 0 && argList[0] is "build" or "pack" or "restore")
        {
            Directory.CreateDirectory(BinlogDir);
            var test = TestContext.Current.TestMethod?.MethodName ?? "none";
            var binlog = Path.Combine(BinlogDir, $"{Environment.ProcessId}-{Interlocked.Increment(ref binlogCount):D3}-{test}-{argList[0]}.binlog");
            argList.Add($"-bl:{binlog}");
            output.WriteLine($"Binlog: {binlog}");
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = workingDir,
        };
        // The test process holds the paths of the SDK that runs it (from `dotnet test`, and from FindAndSetMSBuildVersion):
        // a child build would load parts of that SDK into the one it runs on
        psi.Environment.Remove("MSBUILD_EXE_PATH");
        psi.Environment.Remove("MSBuildExtensionsPath");
        psi.Environment.Remove("MSBuildSDKsPath");
        psi.Environment["STRIDE_FILE_VERSION_CACHE"] = Path.Combine(caseDir, "FileVersionTracker.cache");
        foreach (var arg in argList)
            psi.ArgumentList.Add(arg);

        var captured = new List<string>();
        var detached = false;
        void Sink(string tag, string? data)
        {
            if (data is null) return;
            lock (captured)
            {
                // The test may be over by then, and its output helper throws
                if (detached) return;
                captured.Add(data);
                output.WriteLine($"[{tag}] {data}");
            }
        }

        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => Sink("stdout", e.Data);
        proc.ErrorDataReceived += (_, e) => Sink("stderr", e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        // A cancelled run stops the process with its children, and the test
        using var stop = TestContext.Current.CancellationToken.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

        if (!proc.WaitForExit(timeoutMin * 60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"`dotnet {string.Join(' ', argList)}` timed out after {timeoutMin}min");
        }

        // The output ends once every process that inherited its pipes is gone, the children of the command included
        var outputWait = Stopwatch.StartNew();
        using (var outputEnd = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            try
            {
                proc.WaitForExitAsync(outputEnd.Token).GetAwaiter().GetResult();
                if (outputWait.Elapsed > TimeSpan.FromSeconds(2))
                    output.WriteLine($"`dotnet {argList[0]}` output ended {outputWait.Elapsed.TotalSeconds:F0} s after its exit");
            }
            catch (OperationCanceledException)
            {
                lock (captured) detached = true;
                output.WriteLine($"`dotnet {argList[0]}` exited with {proc.ExitCode}, but its output is still open {outputWait.Elapsed.TotalSeconds:F0} s later. "
                    + $"It was pid {proc.Id}. Processes that may hold the output:");
                output.WriteLine(ListBuildProcesses());
            }
        }
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        string text;
        lock (captured) text = string.Join('\n', captured);
        return new ExecResult(proc.ExitCode, text);
    }

    /// <summary>The dotnet, MSBuild, compiler server and Stride processes of the machine, with their parents and command lines.</summary>
    private static string ListBuildProcesses()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell", ["-NoProfile", "-NonInteractive", "-Command",
                @"Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(dotnet|MSBuild|VBCSCompiler|Stride.*)\.exe$' } | Sort-Object CreationDate | Format-List ProcessId,ParentProcessId,Name,CreationDate,CommandLine | Out-String -Width 8192"])
            : new ProcessStartInfo("ps", ["-eo", "pid,ppid,lstart,args"]);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        try
        {
            using var proc = Process.Start(psi)!;
            var text = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return text;
        }
        catch (Exception e)
        {
            return $"Could not list the processes: {e.Message}";
        }
    }
}

internal static class ConsumerBuild
{
    /// <summary>Run <c>dotnet build &lt;project&gt;</c> as an external consumer; streams output, returns exit code + output.</summary>
    public static ExecResult Run(string project, string workingDir, ITestOutputHelper output, int timeoutMin, params string[] extraArgs)
    {
        var args = new List<string> { "build", project, "-c", "Debug", "-nr:false", "-v:m" };
        args.AddRange(extraArgs);
        return Dotnet.Exec(args, workingDir, output, timeoutMin);
    }
}
