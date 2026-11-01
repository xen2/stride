// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.Core;
using Stride.Core.IO;
using Stride.Core.Storage;

namespace Stride.Shaders.Compiler
{
    /// <summary>
    /// Base class for implementations of <see cref="IEffectCompiler"/>, providing some helper functions.
    /// </summary>
    public abstract class EffectCompilerBase : DisposeBase, IEffectCompiler
    {
        private readonly object replacementsLock = new();
        private ObjectId? replacementsHash;
        // What the last effect id was computed with: the effects in use were compiled with these
        private List<KeyValuePair<string, string>> replacementsInUse;

        protected EffectCompilerBase()
        {
        }

        /// <summary>
        /// Raised by <see cref="CheckReplacements"/> with the replaced classes and the replacers that changed. An effect that uses one must recompile.
        /// </summary>
        public event Action<IReadOnlyCollection<string>> ReplacementsChanged;

        /// <summary>
        /// Gets or sets the database file provider, to use for loading effects and shader sources.
        /// </summary>
        /// <value>
        /// The database file provider.
        /// </value>
        public abstract IVirtualFileProvider FileProvider { get; set; }

        public abstract ObjectId GetShaderSourceHash(string type);

        /// <summary>
        /// Remove cached files for modified shaders
        /// </summary>
        /// <param name="modifiedShaders">The modified shaders. A compiler adds the classes whose effects change with them.</param>
        public virtual void ResetCache(HashSet<string> modifiedShaders)
        {
            lock (replacementsLock)
                replacementsHash = null;
        }

        /// <summary>
        /// The hash of the shader replacements of this project, for the effect ids (see <see cref="ShaderMixinObjectId.ComputeReplacements"/>).
        /// </summary>
        public virtual ObjectId GetReplacementsHash()
        {
            lock (replacementsLock)
            {
                if (replacementsHash == null)
                {
                    replacementsInUse = [.. GetReplacements()];
                    replacementsHash = ShaderMixinObjectId.ComputeReplacements(replacementsInUse);
                }
                return replacementsHash.Value;
            }
        }

        /// <summary>
        /// Compares the replacements with those the effects were compiled with, after new shaders reached <see cref="FileProvider"/>.
        /// If they differ, the next effects use the new ones, and <see cref="ReplacementsChanged"/> tells who holds the old effects.
        /// </summary>
        public virtual void CheckReplacements()
        {
            HashSet<string> changed;
            lock (replacementsLock)
            {
                // No effect id yet: nothing was compiled with the old ones
                if (replacementsInUse == null)
                    return;

                List<KeyValuePair<string, string>> replacements = [.. GetReplacements()];
                changed = [.. replacementsInUse.Except(replacements).Concat(replacements.Except(replacementsInUse)).SelectMany(x => new[] { x.Key, x.Value })];
                if (changed.Count == 0)
                    return;
                replacementsInUse = replacements;
            }

            ResetCache(changed);
            OnReplacementsChanged(changed);
        }

        protected void OnReplacementsChanged(IReadOnlyCollection<string> changed) => ReplacementsChanged?.Invoke(changed);

        /// <summary>
        /// The shader replacements of this project: the replaced class and its replacer, by full name.
        /// </summary>
        protected virtual IEnumerable<KeyValuePair<string, string>> GetReplacements() => ReadReplacements(FileProvider);

        /// <summary>
        /// The shader replacements a database holds: the replaced class and its replacer, by full name.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> ReadReplacements(IVirtualFileProvider database)
        {
            const string suffix = ".sdsl/replacedby";
            string[] files;
            try
            {
                files = database.ListFiles(DefaultSourceShaderFolder, $"*{suffix}", VirtualSearchOption.TopDirectoryOnly);
            }
            catch (Exception e) when (e is NotImplementedException or DirectoryNotFoundException)
            {
                // The provider of the remote compiler can't list: the host compiles with its own replacements
                yield break;
            }

            foreach (var file in files)
            {
                using var stream = database.OpenStream(file, VirtualFileMode.Open, VirtualFileAccess.Read, VirtualFileShare.Read);
                using var reader = new StreamReader(stream);
                var name = file[(file.LastIndexOf('/', file.Length - suffix.Length) + 1)..^suffix.Length];
                yield return new KeyValuePair<string, string>(name, reader.ReadToEnd());
            }
        }

        public CompilerResults Compile(ShaderSource shaderSource, CompilerParameters compilerParameters)
        {
            ShaderMixinSource mixinToCompile;
            var shaderMixinGeneratorSource = shaderSource as ShaderMixinGeneratorSource;

            if (shaderMixinGeneratorSource != null)
            {
                mixinToCompile = ShaderMixinManager.Generate(shaderMixinGeneratorSource.Name, compilerParameters);
            }
            else
            {
                mixinToCompile = shaderSource as ShaderMixinSource;
                var shaderClassSource = shaderSource as ShaderClassCode;

                if (shaderClassSource != null)
                {
                    mixinToCompile = new ShaderMixinSource { Name = ToFullShaderName(shaderClassSource.ClassName) };
                    mixinToCompile.Mixins.Add(shaderClassSource);
                }

                if (mixinToCompile == null)
                {
                    throw new ArgumentException("Unsupported ShaderSource type [{0}]. Supporting only ShaderMixinSource/sdfx, ShaderClassSource", "shaderSource");
                }
                if (string.IsNullOrEmpty(mixinToCompile.Name))
                {
                    throw new ArgumentException("ShaderMixinSource must have a name", "shaderSource");
                }
            }

            ToFullShaderNames(mixinToCompile);
            ShaderCorpusCapture.Record(mixinToCompile, compilerParameters.EffectParameters);

            // Compile the whole mixin tree
            var compilerResults = new CompilerResults { Module = $"EffectCompile [{mixinToCompile.Name}]" };
            var effectInputHash = ShaderMixinObjectId.Compute(mixinToCompile, compilerParameters.EffectParameters, GetReplacementsHash());
            var bytecode = Compile(mixinToCompile, effectParameters: compilerParameters.EffectParameters, compilerParameters, effectInputHash);

            // Since bytecode.Result is a struct, we check if any of its member has been set to know if it's valid
            if (bytecode.Result.CompilationLog is not null || bytecode.Task is not null)
            {
                bytecode.Result.CompilationLog?.CopyTo(compilerResults);
                compilerResults.Bytecode = bytecode;
                compilerResults.SourceParameters = new CompilerParameters(compilerParameters);
            }
            return compilerResults;
        }

        /// <summary>
        /// Compiles the ShaderMixinSource into a platform bytecode.
        /// </summary>
        /// <param name="mixinTree">The mixin tree.</param>
        /// <param name="effectParameters"></param>
        /// <param name="compilerParameters"></param>
        /// <param name="effectInputHash">The hash of the effect inputs.</param>
        /// <returns>The platform-dependent bytecode.</returns>
        public abstract TaskOrResult<EffectBytecodeCompilerResult> Compile(
            ShaderMixinSource mixinTree,
            EffectCompilerParameters effectParameters,
            CompilerParameters compilerParameters,
            ObjectId effectInputHash);

        /// <summary>
        /// TODO(A15): remove with <c>ShaderLoaderBase.ToFullName</c>. The full name of a class named by its short name, so that it has one effect id.
        /// </summary>
        public virtual string ToFullShaderName(string name) => name;

        private void ToFullShaderNames(ShaderSource source)
        {
            switch (source)
            {
                case ShaderClassCode classCode:
                    classCode.ClassName = ToFullShaderName(classCode.ClassName);
                    break;
                case ShaderArraySource array:
                    foreach (var value in array.Values)
                        ToFullShaderNames(value);
                    break;
                case ShaderMixinSource mixin:
                    foreach (var classCode in mixin.Mixins)
                        ToFullShaderNames(classCode);
                    foreach (var composition in mixin.Compositions.Values)
                        ToFullShaderNames(composition);
                    break;
            }
        }

        public static readonly string DefaultSourceShaderFolder = "shaders";

        public static string GetStoragePathFromShaderType(string type)
        {
            if (type == null) throw new ArgumentNullException("type");
            // TODO: harcoded values, bad bad bad
            return DefaultSourceShaderFolder + "/" + type + ".sdsl";
        }
    }
}
