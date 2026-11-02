// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Serialization;
using Stride.Core.Storage;
using Stride.Rendering;
using Stride.Rendering.Data;
using Stride.Shaders.Compiler;

namespace Stride.Shaders
{
    /// <summary>
    /// A helper class to compute a unique object id for a <see cref="ShaderMixinSource"/>.
    /// </summary>
    [DataSerializerGlobal(typeof(ParameterKeyHashSerializer), Profile = "Hash")]
    [DataSerializerGlobal(typeof(ParameterCollectionHashSerializer), Profile = "Hash")]
    public class ShaderMixinObjectId
    {
        private static object generatorLock = new object();
        private static ShaderMixinObjectId generator;

        private readonly DigestStream digestStream;
        private readonly HashSerializationWriter writer;

        private ShaderMixinObjectId()
        {
            digestStream = new DigestStream(Stream.Null);
            writer = new HashSerializationWriter(digestStream);
            writer.Context.SerializerSelector = new SerializerSelector("Default", "Hash");
        }

        /// <summary>
        /// Computes a hash <see cref="ObjectId"/> for the specified mixin.
        /// </summary>
        /// <param name="mixin">The mixin.</param>
        /// <param name="effectCompilerParameters">The effect compiler parameters.</param>
        /// <returns>EffectObjectIds.</returns>
        public static ObjectId Compute(ShaderMixinSource mixin, EffectCompilerParameters effectCompilerParameters)
            => Compute(mixin, effectCompilerParameters, ObjectId.Empty);

        /// <summary>
        /// Computes a hash <see cref="ObjectId"/> for the specified mixin, in a project with these replacements.
        /// </summary>
        /// <param name="mixin">The mixin.</param>
        /// <param name="effectCompilerParameters">The compiler parameters.</param>
        /// <param name="replacements">The result of <see cref="ComputeReplacements"/>. A replacer changes the code of an effect but not its tree.</param>
        public static ObjectId Compute(ShaderMixinSource mixin, EffectCompilerParameters effectCompilerParameters, ObjectId replacements)
        {
            lock (generatorLock)
            {
                generator ??= new ShaderMixinObjectId();
                return generator.ComputeInternal(mixin, effectCompilerParameters, replacements);
            }
        }

        /// <summary>
        /// One hash of the shader replacements of a project, the same at build and at run time. Empty when there is none, which changes no id.
        /// </summary>
        /// <param name="replacements">The replaced class and its replacer, by full name.</param>
        public static ObjectId ComputeReplacements(IEnumerable<KeyValuePair<string, string>> replacements)
        {
            var builder = new ObjectIdBuilder();
            var any = false;
            foreach (var (original, replacer) in replacements.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                builder.Write($"{original}\0{replacer}\0");
                any = true;
            }
            return any ? builder.ComputeHash() : ObjectId.Empty;
        }

        /// <summary>
        /// Computes a hash <see cref="ObjectId"/> for the specified effect and compiler parameters.
        /// </summary>
        /// <param name="effectName">Name of the effect.</param>
        /// <param name="compilerParameters">The compiler parameters.</param>
        /// <returns>
        /// EffectObjectIds.
        /// </returns>
        public static ObjectId Compute(string effectName, CompilerParameters compilerParameters)
        {
            lock (generatorLock)
            {
                generator ??= new ShaderMixinObjectId();
                return generator.ComputeInternal(effectName, compilerParameters);
            }
        }

        private ObjectId ComputeInternal(ShaderMixinSource mixin, EffectCompilerParameters effectCompilerParameters, ObjectId replacements)
        {
            digestStream.Reset();
            writer.Write(EffectBytecode.MagicHeader); // Write the effect bytecode magic header
            writer.Write(ShaderCompilerVersion.Shader);
            writer.Write(ShaderCompilerVersion.Effect);
            writer.Write(mixin);
            foreach (var rule in mixin.Linkage)
                writer.Write(rule.ToString());

            writer.Write(effectCompilerParameters.Platform);
            writer.Write(effectCompilerParameters.Profile);
            writer.Write(effectCompilerParameters.Debug);
            writer.Write(effectCompilerParameters.OptimizationLevel);
            if (replacements != ObjectId.Empty)
                writer.Write(replacements);

            return digestStream.CurrentHash;
        }

        private ObjectId ComputeInternal(string effectName, CompilerParameters compilerParameters)
        {
            digestStream.Reset();
            writer.Write(EffectBytecode.MagicHeader); // Write the effect bytecode magic header
            writer.Write(ShaderCompilerVersion.Shader);
            writer.Write(ShaderCompilerVersion.Effect);
            writer.Write(effectName);

            writer.Write(compilerParameters.EffectParameters.Platform);
            writer.Write(compilerParameters.EffectParameters.Profile);
            writer.Write(compilerParameters.EffectParameters.Debug);
            writer.Write(compilerParameters.EffectParameters.OptimizationLevel);

            writer.Write(compilerParameters);

            return digestStream.CurrentHash;
        }
    }
}
