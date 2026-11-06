// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Stride.Core.Diagnostics;
using Stride.Shaders.Parsing;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Compiler.Direct3D;

/// <summary>
/// Direct3D 11 indexes a texture array with a constant only (FXC error X3512).
/// </summary>
internal static class TextureArrayIndices
{
    /// <summary>
    /// Reports each texture array index of the legalized module that is not a constant (SDSL0268).
    /// </summary>
    /// <returns>Whether every index is a constant.</returns>
    public static bool CheckConstant(uint[] words, ILogger log)
    {
        var imageTypes = new HashSet<uint>();
        var imageArrayTypes = new HashSet<uint>();
        var imageArrayPointerTypes = new HashSet<uint>();
        var imageArrays = new HashSet<uint>();
        var constants = new HashSet<uint>();
        var sourceFiles = new Dictionary<uint, string>();
        var location = string.Empty;
        var ok = true;

        for (var w = 5; w < words.Length; w += (int)(words[w] >> 16))
        {
            var wordCount = (int)(words[w] >> 16);
            switch ((Op)(words[w] & 0xFFFF))
            {
                case Op.OpString:
                    var text = MemoryMarshal.AsBytes(words.AsSpan(w + 2, wordCount - 2));
                    sourceFiles[words[w + 1]] = Encoding.UTF8.GetString(text[..text.IndexOf((byte)0)]);
                    break;
                case Op.OpLine:
                    location = sourceFiles.TryGetValue(words[w + 1], out var file) ? $"{file}({words[w + 2]},{words[w + 3]}): " : string.Empty;
                    break;
                case Op.OpNoLine or Op.OpFunction:
                    location = string.Empty;
                    break;
                case Op.OpTypeImage:
                    imageTypes.Add(words[w + 1]);
                    break;
                case Op.OpTypeArray when imageTypes.Contains(words[w + 2]):
                    imageArrayTypes.Add(words[w + 1]);
                    break;
                case Op.OpTypePointer when imageArrayTypes.Contains(words[w + 3]):
                    imageArrayPointerTypes.Add(words[w + 1]);
                    break;
                case Op.OpVariable when imageArrayPointerTypes.Contains(words[w + 1]):
                    imageArrays.Add(words[w + 2]);
                    break;
                case Op.OpConstant or Op.OpSpecConstant:
                    constants.Add(words[w + 2]);
                    break;
                case Op.OpAccessChain or Op.OpInBoundsAccessChain when imageArrays.Contains(words[w + 3]) && !constants.Contains(words[w + 4]):
                    log.Error($"{location}{SDSLErrorMessages.SDSL0268}");
                    ok = false;
                    break;
            }
        }

        return ok;
    }
}
