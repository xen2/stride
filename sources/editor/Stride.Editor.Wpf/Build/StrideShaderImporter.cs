// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.BuildEngine;
using Stride.Core.Diagnostics;
using Stride.Assets.Effect;

namespace Stride.Editor.Build
{
    public class StrideShaderImporter
    {
        /// <summary>
        /// The current session being processed
        /// </summary>
        private readonly HashSet<string> systemProjectsLoaded = new HashSet<string>();

        // Who declares each shader of a read-only package: a user shader of the same full name is an error
        private Dictionary<string, List<string>> systemShaders;

        private readonly ILogger log;

        /// <param name="log">Where the errors of the shaders go.</param>
        public StrideShaderImporter(ILogger log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        private class UpdateImportShaderCacheBuildStep : BuildStep
        {
            private readonly HashSet<string> cachedProject;

            private readonly List<string> importedProjectIds;

            public UpdateImportShaderCacheBuildStep(HashSet<string> cachedProject, List<string> importedProjectIds)
            {
                this.cachedProject = cachedProject;
                this.importedProjectIds = importedProjectIds;
            }

            public override string Title
            {
                get { return "UpdateImportShaderCacheBuildStep"; }
            }

            public override Task<ResultStatus> Execute(IExecuteContext executeContext, BuilderContext builderContext)
            {
                // check the status of the import build steps
                if (((ListBuildStep)Parent).Steps.Any(s => s.Failed))
                    return Task.FromResult(ResultStatus.Successful);

                // Mark System projects as loaded
                foreach (var projectId in importedProjectIds)
                    cachedProject.Add(projectId);

                return Task.FromResult(ResultStatus.Successful);
            }

            public override string ToString()
            {
                return Title;
            }
        }

        /// <summary>
        /// Creates a build step that will build all shaders from system packages.
        /// </summary>
        /// <param name="session">The session used to retrieve currently used system packages.</param>
        /// <returns>A <see cref="ListBuildStep"/> containing the steps to build all shaders from system packages.</returns>
        public ListBuildStep CreateSystemShaderBuildSteps(SessionViewModel session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            // Preload shaders from every not-yet-loaded package once at startup.
            // TODO: scope this to the project owning the currently previewed/built asset instead of loading all.
            var systemPackages = session.AllPackages.Where(project => !systemProjectsLoaded.Contains(project.Package.Meta.Name)).ToList();
            if (systemPackages.Count == 0)
                return null;

            var context = new AssetCompilerContext { CompilationContext = typeof(AssetCompilationContext) };
            var buildSteps = Prepare(context, systemPackages);
            context.Dispose();
            systemShaders = EffectShaderAssetCompiler.FindDeclaredShaders(systemPackages.Where(x => x.Package.IsReadOnly).Select(x => x.Package));

            buildSteps?.Add(new UpdateImportShaderCacheBuildStep(systemProjectsLoaded, systemPackages.Select(x => x.Package.Meta.Name).ToList()));

            return buildSteps;
        }

        public ListBuildStep CreateUserShaderBuildSteps(ISessionViewModel session)
        {
            var packages = session.AllPackages.Where(project => !project.Package.IsReadOnly).ToList();
            if (packages.Count == 0)
                return null;

            var userContext = new AssetCompilerContext { CompilationContext = typeof(AssetCompilationContext) };
            userContext.Properties.Set(EffectShaderAssetCompiler.ImportedShadersKey, systemShaders);
            var buildSteps = Prepare(userContext, packages);
            buildSteps?.Add(new UpdateImportShaderCacheBuildStep(new HashSet<string>(), packages.Select(x => x.Package.Meta.Name).ToList()));

            return buildSteps;
        }

        // Compiles a fake project (creates the build steps). The asset log shows only what is selected, so the errors go to the log too.
        private ListBuildStep Prepare(AssetCompilerContext context, List<PackageViewModel> packages)
        {
            var root = EffectShaderAssetCompiler.CreateImportSession(packages.Select(x => x.Package));
            var result = new PackageCompiler(new PackageAssetEnumerator(root)).Prepare(context);
            result.CopyTo(log);

            foreach (var step in result.BuildSteps.Steps.OfType<AssetBuildStep>())
            {
                step.StepProcessed += (_, _) =>
                {
                    foreach (var message in step.Logger.Messages.Where(x => x.IsAtLeast(LogMessageType.Error)))
                        log.Log(message);
                };
            }
            return result.BuildSteps;
        }
    }
}
