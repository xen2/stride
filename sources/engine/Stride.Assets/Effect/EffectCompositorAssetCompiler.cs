// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Shaders;
using Stride.Shaders.Parsing;

namespace Stride.Assets.Effect
{
    /// <summary>
    /// Reports an effect declared twice in a session. An <c>extend effect</c> doesn't declare one. The effect itself is C#, built by the generator, so there is nothing else to build.
    /// </summary>
    [AssetCompiler(typeof(EffectCompositorAsset), typeof(AssetCompilationContext))]
    public class EffectCompositorAssetCompiler : AssetCompilerBase
    {
        private static readonly PropertyKey<Dictionary<string, List<string>>> SessionEffectsKey = new("SessionEffectsKey", typeof(EffectCompositorAssetCompiler));

        protected override void Prepare(AssetCompilerContext context, AssetItem assetItem, string targetUrlInStorage, AssetCompilerResult result)
        {
            Dictionary<string, List<string>> sessionEffects;
            lock (context)
                sessionEffects = (Dictionary<string, List<string>>)context.Properties.GetOrAdd(SessionEffectsKey, key => FindDeclaredEffects(assetItem.Package.Session));

            var declaredBy = Describe(assetItem);
            foreach (var name in DeclaredEffects(assetItem))
            {
                var first = sessionEffects[name][0];
                if (first != declaredBy)
                    result.Error(string.Format(ShaderMixinManager.DuplicateEffectMessage, name, first, declaredBy));
            }
        }

        /// <summary>
        /// Who declares each effect full name in a session.
        /// </summary>
        public static Dictionary<string, List<string>> FindDeclaredEffects(PackageSession session)
        {
            var effects = new Dictionary<string, List<string>>();
            foreach (var package in session.Packages)
            {
                foreach (var assetItem in package.Assets)
                {
                    if (assetItem.Asset is not EffectCompositorAsset || !File.Exists(assetItem.FullPath))
                        continue;

                    var declaredBy = Describe(assetItem);
                    foreach (var name in DeclaredEffects(assetItem))
                    {
                        if (!effects.TryGetValue(name, out var files))
                            effects.Add(name, files = []);
                        if (!files.Contains(declaredBy))
                            files.Add(declaredBy);
                    }
                }
            }
            return effects;
        }

        private static IEnumerable<string> DeclaredEffects(AssetItem assetItem)
            => ShaderFileDeclarations.Read(File.ReadAllText(assetItem.FullPath)).Where(x => x.IsEffect && !x.IsLayer).Select(x => x.FullName).Distinct();

        private static string Describe(AssetItem assetItem) => $"{assetItem.FullPath} (package {assetItem.Package.Meta.Name})";
    }
}
