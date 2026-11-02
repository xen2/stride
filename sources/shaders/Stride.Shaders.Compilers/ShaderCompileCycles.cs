// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Shaders.Parsing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stride.Shaders.Compilers;

/// <summary>
/// Finds the classes that need each other while they compile (SDSL0146). On one thread the <see cref="Lazy{T}"/> of the loader would
/// give a .NET message that names no class; on two threads, each would wait for the other forever.
/// </summary>
internal sealed class ShaderCompileCycles
{
    private readonly Dictionary<(string Name, int MacrosHash), int> compilers = [];
    private readonly Dictionary<int, List<(string Name, int MacrosHash)>> compiling = [];
    private readonly Dictionary<int, (string Name, int MacrosHash)> waiting = [];

    /// <summary>
    /// Before a thread compiles a class, or waits for the thread that compiles it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The class is already being compiled by this thread, or by a thread that waits for this one.</exception>
    public void BeforeLoad((string Name, int MacrosHash) key)
    {
        var thread = Environment.CurrentManagedThreadId;
        lock (compilers)
        {
            var cycle = new List<string>();
            var next = key;
            while (compilers.TryGetValue(next, out var compiler))
            {
                var classes = compiling[compiler];
                cycle.AddRange(classes.Skip(classes.IndexOf(next)).Select(x => x.Name));
                if (compiler == thread)
                    throw new InvalidOperationException(string.Format(SDSLErrorMessages.SDSL0146, string.Join(" -> ", cycle.Append(key.Name))));
                if (!waiting.TryGetValue(compiler, out next))
                    break;
            }
            waiting[thread] = key;
        }
    }

    public void AfterLoad()
    {
        lock (compilers)
            waiting.Remove(Environment.CurrentManagedThreadId);
    }

    public void CompileStarted((string Name, int MacrosHash) key)
    {
        var thread = Environment.CurrentManagedThreadId;
        lock (compilers)
        {
            waiting.Remove(thread);
            compilers[key] = thread;
            if (!compiling.TryGetValue(thread, out var classes))
                compiling[thread] = classes = [];
            classes.Add(key);
        }
    }

    public void CompileFinished((string Name, int MacrosHash) key)
    {
        var thread = Environment.CurrentManagedThreadId;
        lock (compilers)
        {
            compilers.Remove(key);
            var classes = compiling[thread];
            classes.Remove(key);
            if (classes.Count == 0)
                compiling.Remove(thread);
        }
    }
}
