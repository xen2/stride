// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Core.Diagnostics;

/// <summary>
///   Temporary trace for the DWM crash experiment: appends timestamped lines (UTC) to
///   <c>dwm-trace-&lt;pid&gt;.log</c> in the directory named by <c>STRIDE_DWM_TRACE_DIR</c>.
///   Does nothing when the variable is not set.
/// </summary>
public static class ExperimentTrace
{
    private static readonly string? path = GetPath();
    private static readonly object writeLock = new();

    public static bool IsEnabled => path is not null;

    public static void Write(string message)
    {
        if (path is null)
            return;

        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} tid={Environment.CurrentManagedThreadId} {message}{Environment.NewLine}";
        lock (writeLock)
        {
            try
            {
                File.AppendAllText(path, line);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string? GetPath()
    {
        var directory = Environment.GetEnvironmentVariable("STRIDE_DWM_TRACE_DIR");
        if (string.IsNullOrEmpty(directory))
            return null;

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"dwm-trace-{Environment.ProcessId}.log");
    }
}
