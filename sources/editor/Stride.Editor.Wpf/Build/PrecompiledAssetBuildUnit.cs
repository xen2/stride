// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.Assets.Effect;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.BuildEngine;
using Stride.Core.Serialization.Contents;
using Stride.Shaders.Compiler;
using Stride.Shaders.Parsing;

namespace Stride.Editor.Build
{
    public class PrecompiledAssetBuildUnit : AssetBuildUnit
    {
        private readonly ListBuildStep buildStep;

        private readonly bool mergeInCommonDatabase;

        public PrecompiledAssetBuildUnit(AssetBuildUnitIdentifier identifier, ListBuildStep buildStep, bool mergeInCommonDatabase = false)
            : base(identifier)
        {
            this.buildStep = buildStep;
            this.mergeInCommonDatabase = mergeInCommonDatabase;
        }

        protected override ListBuildStep Prepare()
        {
            return buildStep;
        }

        /// <summary>
        /// Raised on the builder's thread once the outputs are in the common database.
        /// </summary>
        public event Action Merged;

        protected override void PostBuild()
        {
            base.PostBuild();
            if (mergeInCommonDatabase)
            {
                MicrothreadLocalDatabases.AddToSharedGroup(OutputsOfAssetsThatBuilt());
                MicrothreadLocalDatabases.RemoveFromSharedGroup(ReplacementsNoLongerDeclared());
                Merged?.Invoke();
            }
        }

        // After the merge: a new replacer of the same class has written over the old one
        private List<ObjectUrl> ReplacementsNoLongerDeclared()
        {
            var replacements = EffectCompilerBase.ReadReplacements(MicrothreadLocalDatabases.GetSharedDatabase()).ToList();
            if (replacements.Count == 0)
                return [];

            var files = new List<ShaderReplacement.BuiltFile>();
            foreach (var asset in buildStep.EnumerateRecursively().OfType<AssetBuildStep>().Where(x => x.AssetItem.Asset is EffectShaderAsset))
            {
                try
                {
                    files.Add(new(File.ReadAllText(asset.AssetItem.FullPath), asset.AssetItem.FullPath.GetFileNameWithoutExtension(), asset.Succeeded));
                }
                catch (IOException)
                {
                    // Being written or deleted: the next build reads it
                }
            }
            return ShaderReplacement.NoLongerDeclared(replacements, files)
                .Select(target => new ObjectUrl(UrlType.Content, $"{EffectCompilerBase.GetStoragePathFromShaderType(target)}/replacedby"))
                .ToList();
        }

        // What an asset that failed wrote must not change what is in effect. Two assets can write one url, when they declare the same class.
        private Dictionary<ObjectUrl, OutputObject> OutputsOfAssetsThatBuilt()
        {
            var outputs = new Dictionary<ObjectUrl, OutputObject>(buildStep.OutputObjects);
            var assets = buildStep.EnumerateRecursively().OfType<AssetBuildStep>().ToLookup(x => x.Failed);
            foreach (var url in assets[true].SelectMany(x => x.OutputObjects.Keys))
                outputs.Remove(url);
            foreach (var (url, output) in assets[false].SelectMany(x => x.OutputObjects))
                outputs[url] = output;
            return outputs;
        }
    }
}
