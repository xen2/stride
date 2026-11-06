// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Shaders.Core;
using Stride.Shaders.Parsing.Analysis;

namespace Stride.Shaders.Parsing.SDSL.AST;

/// <summary>
/// The rules of resource arrays: an array of resources is a fixed-size, one-dimension array of read-only textures, declared in an rgroup.
/// </summary>
internal static class ResourceArrays
{
    /// <summary>
    /// Reports a variable or member of a resource array type that breaks the rules (SDSL0259 to SDSL0264).
    /// </summary>
    public static void CheckDeclaration(SymbolTable table, TextLocation location, string name, SymbolType type, bool inRGroup)
    {
        if (type is not ArrayType array || CheckElement(table, location, name, array) is not { } element)
            return;
        if (!inRGroup)
            throw Error(location, string.Format(SDSLErrorMessages.SDSL0260, name, TypeName(element), array.Size == -1 ? "N" : array.Size));
        throw Error(location, string.Format(SDSLErrorMessages.SDSL0259, name));
    }

    /// <summary>
    /// Reports a method parameter of a resource array type (SDSL0266).
    /// </summary>
    public static void CheckParameter(SymbolTable table, TextLocation location, string name, string method, SymbolType type)
    {
        if (type is ArrayType array && CheckElement(table, location, name, array) is { } element)
            throw Error(location, string.Format(SDSLErrorMessages.SDSL0266, name, method, TypeName(element)));
    }

    // The texture type of an array of resources whose element and size are allowed, null for an array of values
    private static TextureType? CheckElement(SymbolTable table, TextLocation location, string name, ArrayType array)
    {
        var element = array.BaseType;
        var dimensions = 1;
        var size = array.Size;
        while (element is ArrayType inner)
        {
            element = inner.BaseType;
            dimensions++;
            size *= inner.Size;
        }
        if (!IsResource(element))
            return null;
        if (element is not TextureType { Sampled: not 2, Multisampled: false } texture)
            throw Error(location, string.Format(SDSLErrorMessages.SDSL0261, name, TypeName(element)));
        if (dimensions > 1)
            throw Error(location, string.Format(SDSLErrorMessages.SDSL0262, name, TypeName(texture), size > 0 ? size : "N"));

        // The keys generator doesn't resolve sizes
        if (table.ResolveArraySizes && array.Size == -1)
            throw Error(location, string.Format(array.SizeExpression == null ? SDSLErrorMessages.SDSL0263 : SDSLErrorMessages.SDSL0264, name));
        return texture;
    }

    private static bool IsResource(SymbolType type)
        => type is TextureType or SamplerType or BufferType or StructuredBufferType or ByteAddressBufferType or AppendStructuredBufferType or ConsumeStructuredBufferType;

    // The name a shader writes: Texture2D, not Texture2D<float4>
    private static string TypeName(SymbolType type) => type.ToString()!.Split('<')[0];

    private static StatementCompileException Error(TextLocation location, string message) => new(new(location, message));
}
