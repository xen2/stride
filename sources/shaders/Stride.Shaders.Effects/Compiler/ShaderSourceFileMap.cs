// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.IO;

namespace Stride.Shaders.Compiler
{
    /// <summary>
    /// Which shaders a source file holds, for hot reload: a watcher reports a path, and the effects know shaders by full name.
    /// </summary>
    public sealed class ShaderSourceFileMap
    {
        private readonly Dictionary<string, HashSet<string>> shadersByPath = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string path, string shaderFullName)
        {
            lock (shadersByPath)
            {
                path = Path.GetFullPath(path);
                if (!shadersByPath.TryGetValue(path, out var shaders))
                    shadersByPath.Add(path, shaders = []);
                shaders.Add(shaderFullName);
            }
        }

        /// <summary>
        /// Adds the shaders of a file to <paramref name="shaderFullNames"/>; a file no effect uses holds none.
        /// </summary>
        public void AddShadersOf(string path, HashSet<string> shaderFullNames)
        {
            lock (shadersByPath)
            {
                if (shadersByPath.TryGetValue(Path.GetFullPath(path), out var shaders))
                    shaderFullNames.UnionWith(shaders);
            }
        }
    }
}
