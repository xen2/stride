// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Stride.CrashReport;

/// <summary>
/// Writes a minidump of another process: thread stacks and module list (triage), or full memory. Windows-only
/// (dbghelp). Never of the calling process: dbghelp suspends the target's other threads while it writes, and in the
/// calling process one of them can hold a heap or loader lock dbghelp then waits on, freezing it for good. A process
/// that needs a dump of itself asks the reporter (<see cref="NativeCrashReporting.TryDumpThisProcess"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class MinidumpWriter
{
    private const int MiniDumpNormal = 0x0;
    private const int MiniDumpWithFullMemory = 0x2;
    private const int MiniDumpWithHandleData = 0x4;
    private const int MiniDumpWithUnloadedModules = 0x20;
    private const int MiniDumpWithFullMemoryInfo = 0x800;
    private const int MiniDumpWithThreadInfo = 0x1000;

    // Thread stacks + module list, no process memory.
    private const int TriageFlags = MiniDumpNormal | MiniDumpWithUnloadedModules | MiniDumpWithThreadInfo;

    /// <summary>
    /// Writes a dump of <em>another</em> process from this (healthy) one, carrying that process's crash
    /// exception record so the dump has an exception stream (the faulting thread and fault are recorded). The
    /// out-of-process reporter uses this to capture a dying host: the host's native trigger freezes the crashing
    /// thread and hands us its pid, thread id, and the address of its <c>EXCEPTION_POINTERS</c>, which dbghelp
    /// reads across the process boundary (<c>ClientPointers</c>). With <paramref name="exceptionPointers"/> zero
    /// (a managed crash dumped on demand: the host is alive and waiting, not faulting) no exception stream is
    /// written, just the process snapshot. A full-memory dump can be several GB and is not scrubbed; it never
    /// leaves the machine unless the user shares it themselves. Returns false if the target can't be opened or
    /// the dump can't be written, and for the calling process (see the class remarks).
    /// </summary>
    public static bool TryWriteTargetProcess(int processId, uint threadId, IntPtr exceptionPointers, string path, bool fullMemory)
    {
        const uint ProcessQueryInformation = 0x0400;
        const uint ProcessVmRead = 0x0010;

        if (processId == Environment.ProcessId)
            return false;

        var target = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)processId);
        if (target == IntPtr.Zero)
            return false;
        try
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var flags = fullMemory
                ? MiniDumpWithFullMemory | MiniDumpWithFullMemoryInfo | MiniDumpWithHandleData | MiniDumpWithUnloadedModules | MiniDumpWithThreadInfo
                : TriageFlags;
            if (exceptionPointers == IntPtr.Zero)
                return MiniDumpWriteDump(target, (uint)processId, file.SafeFileHandle, flags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            var information = new MinidumpExceptionInformation
            {
                ThreadId = threadId,
                ExceptionPointers = exceptionPointers, // an address in the target; ClientPointers reads it there
                ClientPointers = 1,
            };
            var pinned = GCHandle.Alloc(information, GCHandleType.Pinned);
            try
            {
                return MiniDumpWriteDump(target, (uint)processId, file.SafeFileHandle, flags,
                    pinned.AddrOfPinnedObject(), IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                pinned.Free();
            }
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            CloseHandle(target);
        }
    }

    // Pack=4 required: dbghelp.h structs are 4-byte packed; natural x64 layout makes dbghelp read a garbage
    // pointer and fail with ERROR_NOACCESS.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MinidumpExceptionInformation
    {
        public uint ThreadId;
        public IntPtr ExceptionPointers;
        public int ClientPointers; // BOOL
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MiniDumpWriteDump(IntPtr hProcess, uint processId, SafeFileHandle hFile, int dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
