// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stride.Rendering;

namespace Stride.Shaders.Compiler
{
    /// <summary>
    /// Main interface used to compile a shader.
    /// </summary>
    public interface IEffectCompiler : IDisposable
    {
        /// <summary>
        /// Compiles the specified effect.
        /// </summary>
        /// <param name="effect">The effect.</param>
        /// <param name="compilerParameters">The compiler parameters.</param>
        /// <returns>Result of the compilation.</returns>
        CompilerResults Compile(EffectSource effect, CompilerParameters compilerParameters);

        /// <summary>
        /// Generates the specified effect from the compiler parameters, then compiles it.
        /// </summary>
        /// <param name="effectGenerator">The name of the effect to generate.</param>
        /// <param name="compilerParameters">The compiler parameters.</param>
        /// <returns>Result of the compilation.</returns>
        CompilerResults Compile(ShaderMixinGeneratorSource effectGenerator, CompilerParameters compilerParameters);
    }
}
