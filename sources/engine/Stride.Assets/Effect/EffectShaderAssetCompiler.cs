// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core;
using Stride.Core.IO;
using Stride.Rendering;
using Stride.Shaders.Compiler;
using Stride.Shaders.Parsing;

namespace Stride.Assets.Effect
{
    /// <summary>
    /// Entry point to compile an <see cref="EffectShaderAsset"/>
    /// </summary>
    [AssetCompiler(typeof(EffectShaderAsset), typeof(AssetCompilationContext))]
    public class EffectShaderAssetCompiler : AssetCompilerBase
    {
        public static readonly PropertyKey<ConcurrentDictionary<string, string>> ShaderLocationsKey = new PropertyKey<ConcurrentDictionary<string, string>>("ShaderPathsKey", typeof(EffectShaderAssetCompiler));

        /// <summary>
        /// Who declares each shader full name, in shaders built apart from this session (the editor builds system and user shaders apart).
        /// </summary>
        public static readonly PropertyKey<Dictionary<string, List<string>>> ImportedShadersKey = new PropertyKey<Dictionary<string, List<string>>>("ImportedShadersKey", typeof(EffectShaderAssetCompiler));

        private static readonly PropertyKey<Dictionary<string, List<string>>> SessionShadersKey = new PropertyKey<Dictionary<string, List<string>>>("SessionShadersKey", typeof(EffectShaderAssetCompiler));

        protected override void Prepare(AssetCompilerContext context, AssetItem assetItem, string targetUrlInStorage, AssetCompilerResult result)
        {
            var originalSourcePath = assetItem.FullPath;
            var code = File.ReadAllText(originalSourcePath);

            var sessionShaders = GetSessionShaders(context, assetItem.Package.Session);
            var declaredBy = Describe(assetItem);
            foreach (var name in DeclaredNames(code, originalSourcePath))
            {
                var first = sessionShaders[name][0];
                if (first != declaredBy)
                    result.Error(string.Format(SDSLErrorMessages.SDSL0301, name, first, declaredBy));
            }

            var url = EffectCompilerBase.GetStoragePathFromShaderType(ShaderFileDeclarations.StorageName(code, originalSourcePath.GetFileNameWithoutExtension()));

            result.BuildSteps = new AssetBuildStep(assetItem);
            result.BuildSteps.Add(new ImportStreamCommand { SourcePath = originalSourcePath, Location = url, SaveSourcePath = true });
            var shaderLocations = (ConcurrentDictionary<string, string>)context.Properties.GetOrAdd(ShaderLocationsKey, key => new ConcurrentDictionary<string, string>());

            // Store directly this into the context TODO this this temporary
            shaderLocations[url] = originalSourcePath;
        }

        /// <summary>
        /// Who declares each shader full name in a session.
        /// </summary>
        public static Dictionary<string, List<string>> FindDeclaredShaders(PackageSession session, Dictionary<string, List<string>> imported = null)
        {
            var shaders = imported?.ToDictionary(x => x.Key, x => new List<string>(x.Value)) ?? [];
            foreach (var package in session.Packages)
            {
                foreach (var assetItem in package.Assets)
                {
                    if (assetItem.Asset is not EffectShaderAsset || !File.Exists(assetItem.FullPath))
                        continue;

                    var declaredBy = Describe(assetItem);
                    foreach (var name in DeclaredNames(File.ReadAllText(assetItem.FullPath), assetItem.FullPath))
                    {
                        if (!shaders.TryGetValue(name, out var files))
                            shaders.Add(name, files = []);
                        if (!files.Contains(declaredBy))
                            files.Add(declaredBy);
                    }
                }
            }
            return shaders;
        }

        private static Dictionary<string, List<string>> GetSessionShaders(AssetCompilerContext context, PackageSession session)
        {
            lock (context)
            {
                return (Dictionary<string, List<string>>)context.Properties.GetOrAdd(SessionShadersKey, key => FindDeclaredShaders(session, context.Properties.Get(ImportedShadersKey)));
            }
        }

        private static IEnumerable<string> DeclaredNames(string code, UFile path)
        {
            return ShaderFileDeclarations.Read(code).Where(x => !x.IsEffect).Select(x => x.FullName)
                .Append(ShaderFileDeclarations.StorageName(code, path.GetFileNameWithoutExtension()))
                .Distinct();
        }

        private static string Describe(AssetItem assetItem) => $"{assetItem.FullPath} (package {assetItem.Package.Meta.Name})";
    }
}
