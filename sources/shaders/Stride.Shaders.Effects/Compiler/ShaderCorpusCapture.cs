// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace Stride.Shaders.Compiler
{
    /// <summary>
    /// Records every mixin tree given to an effect compiler as a <see cref="ShaderCorpusEntry"/> file, when the
    /// <c>STRIDE_SHADER_CORPUS_CAPTURE</c> environment variable names a folder. <c>STRIDE_SHADER_CORPUS_TAG</c> is stored as the
    /// entry source (the process name if unset). Used to refresh the shader corpus from a running game or an asset build.
    /// </summary>
    public static class ShaderCorpusCapture
    {
        private static readonly string? CaptureDirectory = Environment.GetEnvironmentVariable("STRIDE_SHADER_CORPUS_CAPTURE");

        public static void Record(ShaderMixinSource mixinTree, EffectCompilerParameters effectParameters)
        {
            if (string.IsNullOrEmpty(CaptureDirectory) || string.IsNullOrEmpty(mixinTree.Name))
                return;

            try
            {
                var entry = new ShaderCorpusEntry(mixinTree.Name, effectParameters.Profile, mixinTree);
                entry.Sources.Add(Environment.GetEnvironmentVariable("STRIDE_SHADER_CORPUS_TAG") ?? Process.GetCurrentProcess().ProcessName);

                Directory.CreateDirectory(CaptureDirectory);
                var path = Path.Combine(CaptureDirectory, $"{entry.Id}.json");
                if (File.Exists(path))
                    return;

                // Write then move, so a reader (or another compiler thread) never sees a partial file
                var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tempPath, ShaderCorpusEntry.ToJson(entry));
                try
                {
                    File.Move(tempPath, path);
                }
                catch (IOException)
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception e)
            {
                // Capture is a debug aid, it must never fail a compile
                Debug.WriteLine($"Shader corpus capture failed for {mixinTree.Name}: {e.Message}");
            }
        }
    }
}
