// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.CrashReport;

/// <summary>
/// Masks the user name and profile path in a minidump that may be sent, as the reporter window promises: module paths,
/// PDB paths and the captured memory (stacks, and for a createdump dump the managed memory it carries). Plain bytes, so
/// any platform: the asset compiler adopts createdump dumps everywhere. A full-memory dump is local-only, never sent,
/// and not scrubbed.
/// </summary>
public static class MinidumpScrubber
{
    private const int ModuleListStream = 4;
    private const int MemoryListStream = 5;
    private const int Memory64ListStream = 9;
    private const int UnloadedModuleListStream = 14;

    /// <summary>
    /// Scrubs a dump file in place. When that fails, deletes it: a dump that may be sent must never stay unscrubbed.
    /// False when the dump was deleted. A file that is not a minidump is left as is (nothing it knows how to scrub).
    /// </summary>
    public static bool ScrubOrDelete(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            Scrub(bytes);
            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch (Exception)
        {
            try { File.Delete(path); } catch (Exception) { /* best effort */ }
            return false;
        }
    }

    /// <summary>
    /// Masks the user name and profile path where they can appear: module path strings and the captured memory
    /// ranges. Walks the stream directory so headers are never touched. Throws on a malformed minidump.
    /// </summary>
    private static void Scrub(byte[] dump)
    {
        if (dump.Length < 16 || BitConverter.ToUInt32(dump, 0) != 0x504D444D) // 'MDMP'
            return;

        var streamCount = BitConverter.ToInt32(dump, 8);
        var directory = BitConverter.ToInt32(dump, 12);
        for (var i = 0; i < streamCount; i++)
        {
            var streamType = BitConverter.ToInt32(dump, directory + i * 12);
            var rva = BitConverter.ToInt32(dump, directory + i * 12 + 8);
            switch (streamType)
            {
                case ModuleListStream:
                {
                    // MINIDUMP_MODULE is 108 bytes: name RVA at offset 20, CodeView record (PDB path) at 76
                    var count = BitConverter.ToInt32(dump, rva);
                    for (var m = 0; m < count; m++)
                    {
                        var module = rva + 4 + m * 108;
                        ScrubString(dump, BitConverter.ToInt32(dump, module + 20));
                        var cvSize = BitConverter.ToInt32(dump, module + 76);
                        var cvRva = BitConverter.ToInt32(dump, module + 80);
                        CrashReportAnonymizer.Scrub(dump, cvRva, cvSize);
                    }
                    break;
                }
                case UnloadedModuleListStream:
                {
                    // Sized header, then entries with the name RVA at offset 20
                    var headerSize = BitConverter.ToInt32(dump, rva);
                    var entrySize = BitConverter.ToInt32(dump, rva + 4);
                    var count = BitConverter.ToInt32(dump, rva + 8);
                    for (var m = 0; m < count; m++)
                        ScrubString(dump, BitConverter.ToInt32(dump, rva + headerSize + m * entrySize + 20));
                    break;
                }
                case MemoryListStream:
                {
                    // MINIDUMP_MEMORY_DESCRIPTOR: address (8), data size (4), data RVA (4)
                    var count = BitConverter.ToInt32(dump, rva);
                    for (var m = 0; m < count; m++)
                    {
                        var size = BitConverter.ToInt32(dump, rva + 4 + m * 16 + 8);
                        var dataRva = BitConverter.ToInt32(dump, rva + 4 + m * 16 + 12);
                        CrashReportAnonymizer.Scrub(dump, dataRva, size);
                    }
                    break;
                }
                case Memory64ListStream:
                {
                    // All ranges are stored contiguously starting at the base RVA
                    var count = BitConverter.ToInt64(dump, rva);
                    var baseRva = BitConverter.ToInt64(dump, rva + 8);
                    long total = 0;
                    for (var m = 0; m < count; m++)
                        total += BitConverter.ToInt64(dump, rva + 16 + m * 16 + 8);
                    CrashReportAnonymizer.Scrub(dump, (int)baseRva, (int)Math.Min(total, dump.Length - baseRva));
                    break;
                }
            }
        }
    }

    /// <summary>MINIDUMP_STRING: byte length prefix, then UTF-16 characters.</summary>
    private static void ScrubString(byte[] dump, int rva)
    {
        if (rva <= 0 || rva + 4 > dump.Length)
            return;
        var byteLength = BitConverter.ToInt32(dump, rva);
        CrashReportAnonymizer.Scrub(dump, rva + 4, byteLength);
    }
}
