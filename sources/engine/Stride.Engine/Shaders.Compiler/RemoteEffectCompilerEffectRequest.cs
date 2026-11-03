// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Collections.Generic;
using Stride.Engine.Network;

namespace Stride.Shaders.Compiler
{
    // TODO: Make that private as soon as we stop signing assemblies (so that EffectCompilerServer can use it)
    public class RemoteEffectCompilerEffectRequest : SocketMessage
    {
        public ShaderMixinSource MixinTree { get; set; }

        /// <summary>The macros of the effect.</summary>
        public List<ShaderMacro> Macros { get; set; }

        /// <summary>The linkage rules of the effect.</summary>
        public List<ShaderLinkageRule> Linkage { get; set; }
        
        public EffectCompilerParameters EffectParameters { get; set; }
    }
}
