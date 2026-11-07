// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Diagnostics;
using Stride.Shaders.Core;
using Stride.Shaders.Parsing;
using Stride.Shaders.Spirv.Building;
using Stride.Shaders.Spirv.Core;
using Stride.Shaders.Spirv.Core.Buffers;
using static Stride.Shaders.Spirv.Specification;

namespace Stride.Shaders.Compilers.SDSL;

public partial class ShaderMixer
{
    /// <summary>
    /// A <c>[CSharp]</c> struct in a cbuffer has the offsets of its C# twin, the tight layout of a structured buffer, so that C# fills
    /// the cbuffer right (SDSL0291). The class marks such a struct (CSharpLayoutSDSL), unless <c>SkipLayoutCheck = true</c>.
    /// </summary>
    private static bool CheckCSharpStructsInCBuffers(SpirvContext context, SpirvBuffer buffer, ILogger log)
    {
        var twins = new HashSet<int>();
        foreach (var i in context)
        {
            if (i.Op == Op.OpDecorate && (OpDecorate)i is { Decoration: Decoration.CSharpLayoutSDSL } decorate)
                twins.Add(decorate.Target);
        }
        if (twins.Count == 0)
            return true;

        var ok = true;
        foreach (var i in buffer)
        {
            if (i.Op != Op.OpVariableSDSL || context.ReverseTypes[i.Data.IdResultType!.Value] is not PointerType { StorageClass: StorageClass.Uniform, BaseType: ConstantBufferSymbol cbuffer })
                continue;
            foreach (var member in cbuffer.Members)
                ok &= Check(cbuffer.Name, member.Name, member.Type);
        }
        return ok;

        bool Check(string cbufferName, string path, SymbolType type)
        {
            switch (type)
            {
                case ArrayType array:
                    return Check(cbufferName, $"{path}[]", array.BaseType);
                case StructType structType:
                    if (twins.Contains(context.Types[structType]))
                    {
                        var cbufferOffsets = MemberOffsets(structType, SpirvBuilder.AlignmentRules.CBuffer);
                        var csharpOffsets = MemberOffsets(structType, SpirvBuilder.AlignmentRules.StructuredBufferTight);
                        for (var index = 0; index < structType.Members.Count; index++)
                        {
                            if (cbufferOffsets[index] == csharpOffsets[index])
                                continue;
                            var field = structType.Members[index].Name;
                            var padding = (cbufferOffsets[index] - csharpOffsets[index]) switch
                            {
                                4 => ", such as float pad0;",
                                8 => ", such as float2 pad0;",
                                12 => ", such as float3 pad0;",
                                var bytes => $" of {bytes} bytes",
                            };
                            log.Error(string.Format(SDSLErrorMessages.SDSL0291, path, Parsing.SDSL.AST.ShaderClass.GetCBufferRealName(cbufferName), structType.Name, field, cbufferOffsets[index], csharpOffsets[index], padding));
                            return false;
                        }
                    }
                    var ok = true;
                    foreach (var member in structType.Members)
                        ok &= Check(cbufferName, $"{path}.{member.Name}", member.Type);
                    return ok;
                default:
                    return true;
            }
        }
    }

    // The offsets of the members of a struct, as EmitStructDecorations writes them
    private static int[] MemberOffsets(StructType structType, SpirvBuilder.AlignmentRules alignmentRules)
    {
        var offset = 0;
        var offsets = new int[structType.Members.Count];
        for (var index = 0; index < structType.Members.Count; index++)
        {
            var member = structType.Members[index];
            var size = SpirvBuilder.ComputeBufferOffset(member.Type, member.TypeModifier, ref offset, alignmentRules).Size;
            offsets[index] = offset;
            offset += size;
            SpirvBuilder.PadOffsetAfterArray(member.Type, member.TypeModifier, offsets[index], ref offset, alignmentRules);
        }
        return offsets;
    }
}
