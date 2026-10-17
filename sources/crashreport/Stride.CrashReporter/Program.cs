// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Globalization;
using Avalonia;
using Stride.CrashReport;

namespace Stride.CrashReporter;

/// <summary>
/// The out-of-process crash reporter. A headless tool that crashed (or its native fault handler) spawns this,
/// pointing it at the run directory the crashes were written to; it shows the report window and sends what the
/// user approves. Two forms:
/// <list type="bullet">
/// <item><c>Stride.CrashReporter &lt;run-directory&gt; [--owner-hwnd &lt;hwnd&gt;] [--dsn &lt;url&gt;]</c> — show a run written
/// by a live host (a GameStudio-routed build crash); with an owner HWND the window is owned by the host's main
/// window (above it, not above other apps, minimized with it, not modal).</item>
/// <item><c>Stride.CrashReporter &lt;run-directory&gt; --host-pid &lt;pid&gt; [--dsn &lt;url&gt;]</c> — a managed crash of the
/// host itself, which stays alive, blocked, until this window closes; its pid lets the window write a full memory
/// dump of it on demand.</item>
/// <item><c>Stride.CrashReporter --capture &lt;pid&gt; &lt;tid&gt; &lt;exception-pointers&gt; --event &lt;name&gt; --dump-dir &lt;dir&gt; [--dsn &lt;url&gt;]</c>
/// — capture a crashing host's dump from the outside (its native trigger is frozen waiting on the event), then
/// show it. See <see cref="NativeCapture"/>.</item>
/// <item><c>Stride.CrashReporter --dump-process &lt;pid&gt; &lt;path&gt; [--full]</c> — write a dump of a live host that
/// asked for one of itself and waits on us (<see cref="NativeCrashReporting.TryDumpThisProcess"/>), then exit, no
/// window: a scrubbed triage dump, or with <c>--full</c> an unscrubbed full-memory one.</item>
/// </list>
/// </summary>
internal static class Program
{
    /// <summary>The loaded run, handed to the app once the arguments are validated (Avalonia owns Main's flow).</summary>
    internal static CrashSession? Session { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--dump-process") >= 0)
            return DumpProcess(args) ? 0 : 1;

        string? runDirectory;
        if (Array.IndexOf(args, "--capture") >= 0)
        {
            // The crashing host is frozen waiting on the event; capture its dump before doing anything slower.
            runDirectory = GetOption(args, "--dump-dir");
            if (string.IsNullOrEmpty(runDirectory))
            {
                Console.Error.WriteLine("Usage: Stride.CrashReporter --capture <pid> <tid> <exception-pointers> --event <name> --dump-dir <dir>");
                return 1;
            }
            NativeCapture.Capture(args, runDirectory);

            // Unattended (save/send/CI): the dump and report are saved to the run; don't pop a window.
            if (CrashPolicy.ResolveAction() != CrashAction.Report)
                return 0;
        }
        else
        {
            runDirectory = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
            if (string.IsNullOrEmpty(runDirectory) || !Directory.Exists(runDirectory))
            {
                Console.Error.WriteLine("Usage: Stride.CrashReporter <run-directory> [--dsn <url>]");
                return 1;
            }
        }

        // Only a plain run-directory launch is a live GameStudio session (the compiler-routing path); a --capture or
        // --host-pid launch is a host that itself crashed and is exiting, so a send there suppresses nothing session-scoped.
        var hostProcessId = int.TryParse(GetOption(args, "--host-pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : (int?)null;
        var sessionScoped = Array.IndexOf(args, "--capture") < 0 && hostProcessId is null;
        var ownerWindow = long.TryParse(GetOption(args, "--owner-hwnd"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hwnd) ? (IntPtr)hwnd : IntPtr.Zero;
        var session = CrashSession.Load(runDirectory, GetOption(args, "--dsn"), sessionScoped, hostProcessId, ownerWindow);
        // Nothing to ask about (empty run, or every signature already suppressed): exit quietly, no window.
        if (session.Groups.Count == 0)
            return 0;

        Session = session;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // The --dump-process entry: the host can't safely dump itself, so it waits while we do it from here.
    private static bool DumpProcess(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var index = Array.IndexOf(args, "--dump-process");
        if (index + 2 >= args.Length || !int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId))
        {
            Console.Error.WriteLine("Usage: Stride.CrashReporter --dump-process <pid> <path> [--full]");
            return false;
        }
        var path = args[index + 2];
        var fullMemory = Array.IndexOf(args, "--full") >= 0;
        try
        {
            // A managed crash has no native exception record: a plain snapshot of the live host.
            if (!MinidumpWriter.TryWriteTargetProcess(processId, 0, IntPtr.Zero, path, fullMemory))
                return false;
            if (fullMemory)
                return true;
            try
            {
                MinidumpWriter.ScrubFile(path);
                return true;
            }
            catch (Exception)
            {
                File.Delete(path); // a triage dump may be sent: never leave one unscrubbed
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }
}
