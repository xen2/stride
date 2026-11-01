// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core;
using Stride.Core.IO;
using Stride.Core.Storage;
using Stride.Rendering;
using Stride.Shaders;
using Stride.Shaders.Compiler;
using Stride.Shaders.Parsing;
using Stride.Shaders.Parsing.SDSL.AST;

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

        private static readonly PropertyKey<List<ShaderReplacementDeclaration>> SessionReplacementsKey = new PropertyKey<List<ShaderReplacementDeclaration>>("SessionReplacementsKey", typeof(EffectShaderAssetCompiler));

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
            PrepareReplacers(context, assetItem, code, result);
            var shaderLocations = (ConcurrentDictionary<string, string>)context.Properties.GetOrAdd(ShaderLocationsKey, key => new ConcurrentDictionary<string, string>());

            // Store directly this into the context TODO this this temporary
            shaderLocations[url] = originalSourcePath;
        }

        private static void PrepareReplacers(AssetCompilerContext context, AssetItem assetItem, string code, AssetCompilerResult result)
        {
            if (!code.Contains("[Replaces", StringComparison.Ordinal))
                return;

            var replacements = GetSessionReplacements(context, assetItem.Package.Session);
            foreach (var replacement in replacements.Where(x => x.Item.FullPath == assetItem.FullPath))
            {
                var error = CheckReplacement(context, replacement, replacements);
                if (error != null)
                {
                    result.Error(error);
                    continue;
                }

                result.Info($"{replacement.Replacer} replaces {replacement.Target}");
                result.BuildSteps.Add(new SaveReplacerCommand($"{EffectCompilerBase.GetStoragePathFromShaderType(replacement.Target)}/replacedby", replacement.Replacer));
            }
        }

        private static string CheckReplacement(AssetCompilerContext context, ShaderReplacementDeclaration replacement, List<ShaderReplacementDeclaration> replacements)
        {
            var other = replacements.FirstOrDefault(x => x.Target == replacement.Target && x.Replacer != replacement.Replacer);
            if (other != null)
                return string.Format(SDSLErrorMessages.SDSL0304, replacement.Target, replacement.Replacer, other.Replacer);

            // The editor builds the system shaders apart: a class of theirs is visible, and it isn't in this session
            if (context.Properties.TryGet(ImportedShadersKey, out var imported) && imported.ContainsKey(replacement.Target))
                return null;

            var package = replacement.Item.Package;
            var visible = new List<Package> { package };
            visible.AddRange(package.Container.FlattenedDependencies.Select(x => x.Package).Where(x => x != null));
            foreach (var targetItem in visible.SelectMany(x => x.Assets).Where(x => x.Asset is EffectShaderAsset && File.Exists(x.FullPath)))
            {
                var targetCode = File.ReadAllText(targetItem.FullPath);
                if (!ShaderFileDeclarations.Read(targetCode).Any(x => !x.IsEffect && x.FullName == replacement.Target))
                    continue;

                var targetClass = SdslClasses(targetCode).FirstOrDefault(x => ShaderNames.Combine(x.Namespace, x.Class.Name.Name) == replacement.Target).Class;
                return targetClass == null ? null : ShaderReplacement.Check(replacement.Replacer, replacement.Class, replacement.Target, targetClass);
            }
            return string.Format(SDSLErrorMessages.SDSL0303, replacement.Replacer, replacement.Target, package.Meta.Name);
        }

        private static IEnumerable<(string Namespace, ShaderClass Class)> SdslClasses(string code)
            => SDSLParser.Parse(code).AST is ShaderFile file ? ShaderReplacement.Classes(file) : [];

        /// <summary>
        /// The hash of the replacements of a session, for the id of what an effect log compiles.
        /// </summary>
        public static ObjectId GetReplacementsHash(AssetCompilerContext context, PackageSession session)
            => ShaderMixinObjectId.ComputeReplacements(GetSessionReplacements(context, session).Select(x => new KeyValuePair<string, string>(x.Target, x.Replacer)));

        private static List<ShaderReplacementDeclaration> GetSessionReplacements(AssetCompilerContext context, PackageSession session)
        {
            lock (context)
            {
                return (List<ShaderReplacementDeclaration>)context.Properties.GetOrAdd(SessionReplacementsKey, key => FindReplacements(session));
            }
        }

        private static List<ShaderReplacementDeclaration> FindReplacements(PackageSession session)
        {
            var result = new List<ShaderReplacementDeclaration>();
            foreach (var assetItem in session.Packages.SelectMany(x => x.Assets).Where(x => x.Asset is EffectShaderAsset && File.Exists(x.FullPath)))
            {
                var code = File.ReadAllText(assetItem.FullPath);
                if (!code.Contains("[Replaces", StringComparison.Ordinal))
                    continue;

                foreach (var (@namespace, shaderClass) in SdslClasses(code))
                {
                    if (ShaderReplacement.Target(shaderClass) is { } target)
                        result.Add(new ShaderReplacementDeclaration(ShaderNames.Combine(@namespace, shaderClass.Name.Name), target, shaderClass, assetItem));
                }
            }
            return result;
        }

        private sealed record ShaderReplacementDeclaration(string Replacer, string Target, ShaderClass Class, AssetItem Item);

        /// <summary>
        /// A session of its own for building the shaders of some packages: copies that hold their shaders and the dependencies between them.
        /// </summary>
        /// <returns>The package whose dependencies are the copies.</returns>
        public static Package CreateImportSession(IEnumerable<Package> packages)
        {
            var root = new StandalonePackage(new Package());
            var session = new PackageSession();
            session.Projects.Add(root);

            var copies = new Dictionary<Package, StandalonePackage>();
            foreach (var package in packages)
            {
                // Carry the resolved namespace so the wrapped StandalonePackage resolves rooted shader locations
                var copy = new Package { Meta = { Name = package.Meta.Name }, FullPath = package.FullPath, AssetNamespace = package.Container?.AssetNamespace };
                foreach (var asset in package.Assets.Where(x => x.Asset is EffectShaderAsset))
                    copy.Assets.Add(new AssetItem(asset.Location, asset.Asset) { SourceFolder = asset.SourceFolder, AlternativePath = asset.AlternativePath });

                copies.Add(package, new StandalonePackage(copy));
                session.Projects.Add(copies[package]);
                root.FlattenedDependencies.Add(new Dependency(copy));
            }

            foreach (var (package, copy) in copies)
            {
                foreach (var dependency in package.Container?.FlattenedDependencies ?? [])
                {
                    if (dependency.Package != null && copies.TryGetValue(dependency.Package, out var target))
                        copy.FlattenedDependencies.Add(new Dependency(target.Package));
                }
            }
            return root.Package;
        }

        /// <summary>
        /// Who declares each shader full name in some packages.
        /// </summary>
        public static Dictionary<string, List<string>> FindDeclaredShaders(IEnumerable<Package> packages, Dictionary<string, List<string>> imported = null)
        {
            var shaders = imported?.ToDictionary(x => x.Key, x => new List<string>(x.Value)) ?? [];
            foreach (var package in packages)
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
                return (Dictionary<string, List<string>>)context.Properties.GetOrAdd(SessionShadersKey, key => FindDeclaredShaders(session.Packages, context.Properties.Get(ImportedShadersKey)));
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
