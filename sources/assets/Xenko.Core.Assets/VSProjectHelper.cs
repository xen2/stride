// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Xenko.Core;
using Xenko.Core.Diagnostics;
using ILogger = Xenko.Core.Diagnostics.ILogger;

namespace Xenko.Core.Assets
{
    public interface ICancellableAsyncBuild
    {
        string AssemblyPath { get; }

        Task<BuildResult> BuildTask { get; }

        bool IsCanceled { get; }

        void Cancel();
    }

    public static class VSProjectHelper
    {
        private const string XenkoProjectType = "XenkoProjectType";
        private const string XenkoPlatform = "XenkoPlatform";

        private static BuildManager mainBuildManager = new BuildManager();

        public static Guid GetProjectGuid(Microsoft.Build.Evaluation.Project project)
        {
            if (project == null) throw new ArgumentNullException("project");
            return Guid.Parse(project.GetPropertyValue("ProjectGuid"));
        }

        public static PlatformType? GetPlatformTypeFromProject(Microsoft.Build.Evaluation.Project project)
        {
            return GetEnumFromProperty<PlatformType>(project, XenkoPlatform);
        }

        public static ProjectType? GetProjectTypeFromProject(Microsoft.Build.Evaluation.Project project)
        {
            return GetEnumFromProperty<ProjectType>(project, XenkoProjectType);
        }

        private static T? GetEnumFromProperty<T>(Microsoft.Build.Evaluation.Project project, string propertyName) where T : struct
        {
            if (project == null) throw new ArgumentNullException("project");
            if (propertyName == null) throw new ArgumentNullException("propertyName");
            var value = project.GetPropertyValue(propertyName);
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            return (T)Enum.Parse(typeof(T), value);
        }

        public static string GetOrCompileProjectAssembly(string solutionFullPath, string fullProjectLocation, ILogger logger, string targets, bool autoCompileProject, string configuration, string platform = "AnyCPU", Dictionary<string, string> extraProperties = null, bool onlyErrors = false, BuildRequestDataFlags flags = BuildRequestDataFlags.None)
        {
            if (fullProjectLocation == null) throw new ArgumentNullException("fullProjectLocation");
            if (logger == null) throw new ArgumentNullException("logger");

            var project = LoadProject(fullProjectLocation, configuration, platform, extraProperties);
            var assemblyPath = project.GetPropertyValue("TargetPath");
            try
            {
                if (!string.IsNullOrWhiteSpace(assemblyPath))
                {
                    if (autoCompileProject)
                    {
                        var asyncBuild = new CancellableAsyncBuild(project, assemblyPath);
                        asyncBuild.Build(project, targets, flags, new LoggerRedirect(logger, onlyErrors));
                        var buildResult = asyncBuild.BuildTask.Result;
                    }
                }
            }
            finally
            {
                project.ProjectCollection.UnloadAllProjects();
                project.ProjectCollection.Dispose();
            }

            return assemblyPath;
        }

        public static ICancellableAsyncBuild CompileProjectAssemblyAsync(string solutionFullPath, string fullProjectLocation, ILogger logger, string targets = "Build", string configuration = "Debug", string platform = "AnyCPU", Dictionary<string, string> extraProperties = null, BuildRequestDataFlags flags = BuildRequestDataFlags.None)
        {
            if (fullProjectLocation == null) throw new ArgumentNullException("fullProjectLocation");
            if (logger == null) throw new ArgumentNullException("logger");

            var project = LoadProject(fullProjectLocation, configuration, platform, extraProperties);
            var assemblyPath = project.GetPropertyValue("TargetPath");
            try
            {
                if (!string.IsNullOrWhiteSpace(assemblyPath))
                {
                    var asyncBuild = new CancellableAsyncBuild(project, assemblyPath);
                    asyncBuild.Build(project, targets, flags, new LoggerRedirect(logger));
                    return asyncBuild;
                }
            }
            finally
            {
                project.ProjectCollection.UnloadAllProjects();
                project.ProjectCollection.Dispose();
            }

            return null;
        }

        public static async Task RestoreNugetPackages(ILogger logger, string projectPath)
        {
            if (Path.GetExtension(projectPath)?.ToLowerInvariant() == ".csproj")
            {
                var project = LoadProject(projectPath);

                try
                {
                    var addedProjs = new HashSet<string>(); //to avoid worst case circular dependencies.
                    var allProjs = Core.Utilities.IterateTree(project, project1 =>
                    {
                        var projs = new List<Microsoft.Build.Evaluation.Project>();
                        foreach (var item in project1.AllEvaluatedItems.Where(x => x.ItemType == "ProjectReference"))
                        {
                            var path = Path.Combine(project.DirectoryPath, item.EvaluatedInclude);
                            if (!File.Exists(path)) continue;

                            if (addedProjs.Add(path))
                            {
                                projs.Add(project.ProjectCollection.LoadProject(path));
                            }
                        }

                        return projs;
                    });

                    await RestoreNugetPackagesNonRecursive(logger, allProjs.Select(x => x.FullPath));
                }
                finally
                {
                    project.ProjectCollection.UnloadAllProjects();
                    project.ProjectCollection.Dispose();
                }
            }
            else
            {
                // Solution or unknown project file
                await RestoreNugetPackagesNonRecursive(logger, projectPath);
            }
        }

        public static async Task RestoreNugetPackagesNonRecursive(ILogger logger, string projectPath)
        {
            await Task.Run(() =>
            {
                var pc = new Microsoft.Build.Evaluation.ProjectCollection();

                try
                {
                    var parameters = new BuildParameters(pc)
                    {
                        Loggers = new[] { new LoggerRedirect(logger, true) } //Instance of ILogger instantiated earlier
                    };

                    // Run a MSBuild /t:Restore <projectfile>
                    var request = new BuildRequestData(projectPath, new Dictionary<string, string>(), null, new[] { "Restore" }, null, BuildRequestDataFlags.None);

                    mainBuildManager.Build(parameters, request);
                }
                finally
                {
                    pc.UnloadAllProjects();
                    pc.Dispose();
                }
            });
        }

        public static async Task RestoreNugetPackagesNonRecursive(ILogger logger, IEnumerable<string> projectPaths)
        {
            foreach (var projectPath in projectPaths)
            {
                await RestoreNugetPackagesNonRecursive(logger, projectPath);
            }
        }

        public static Microsoft.Build.Evaluation.Project LoadProject(string fullProjectLocation, string configuration = "Debug", string platform = "AnyCPU", Dictionary<string, string> extraProperties = null)
        {
            configuration = configuration ?? "Debug";
            platform = platform ?? "AnyCPU";

            var globalProperties = new Dictionary<string, string>();
            globalProperties["Configuration"] = configuration;
            globalProperties["Platform"] = platform;

            if (extraProperties != null)
            {
                foreach (var extraProperty in extraProperties)
                {
                    globalProperties[extraProperty.Key] = extraProperty.Value;
                }
            }

            var projectCollection = new Microsoft.Build.Evaluation.ProjectCollection(globalProperties);
            projectCollection.LoadProject(fullProjectLocation);
            var project = projectCollection.LoadedProjects.First();

            // Support for cross-targeting (TargetFrameworks)
            var assemblyPath = project.GetPropertyValue("TargetPath");
            var targetFramework = project.GetPropertyValue("TargetFramework");
            var targetFrameworks = project.GetPropertyValue("TargetFrameworks");
            if (string.IsNullOrWhiteSpace(assemblyPath) && string.IsNullOrWhiteSpace(targetFramework) && !string.IsNullOrWhiteSpace(targetFrameworks))
            {
                // We might be in a cross-targeting scenario
                // Reload project with first target framework
                project.ProjectCollection.UnloadAllProjects();
                project.ProjectCollection.Dispose();

                globalProperties.Add("TargetFramework", targetFrameworks.Split(';').First());
                projectCollection = new Microsoft.Build.Evaluation.ProjectCollection(globalProperties);
                projectCollection.LoadProject(fullProjectLocation);
                project = projectCollection.LoadedProjects.First();
            }

            return project;
        }

        private class LoggerRedirect : Microsoft.Build.Utilities.Logger
        {
            private readonly ILogger logger;
            private readonly bool onlyErrors;

            public LoggerRedirect(ILogger logger, bool onlyErrors = false)
            {
                if (logger == null) throw new ArgumentNullException("logger");
                this.logger = logger;
                this.onlyErrors = onlyErrors;
            }

            public override void Initialize(Microsoft.Build.Framework.IEventSource eventSource)
            {
                if (eventSource == null) throw new ArgumentNullException("eventSource");
                if (!onlyErrors)
                {
                    eventSource.MessageRaised += MessageRaised;
                    eventSource.WarningRaised += WarningRaised;
                }
                eventSource.ErrorRaised += ErrorRaised;
            }

            void MessageRaised(object sender, BuildMessageEventArgs e)
            {
                var loggerResult = logger as LoggerResult;
                if (loggerResult != null)
                {
                    loggerResult.Module = $"{e.File}({e.LineNumber},{e.ColumnNumber})";
                }

                // Redirect task execution messages to verbose output
                var importance = e is TaskCommandLineEventArgs ? MessageImportance.Normal : e.Importance;

                switch (importance)
                {
                    case MessageImportance.High:
                        logger.Info(e.Message);
                        break;
                    case MessageImportance.Normal:
                        logger.Verbose(e.Message);
                        break;
                    case MessageImportance.Low:
                        logger.Debug(e.Message);
                        break;
                }
            }

            void WarningRaised(object sender, BuildWarningEventArgs e)
            {
                var loggerResult = logger as LoggerResult;
                if (loggerResult != null)
                {
                    loggerResult.Module = string.Format("{0}({1},{2})", e.File, e.LineNumber, e.ColumnNumber);
                }
                logger.Warning(e.Message);
            }

            void ErrorRaised(object sender, Microsoft.Build.Framework.BuildErrorEventArgs e)
            {
                var loggerResult = logger as LoggerResult;
                if (loggerResult != null)
                {
                    loggerResult.Module = string.Format("{0}({1},{2})", e.File, e.LineNumber, e.ColumnNumber);
                }
                logger.Error(e.Message);
            }
        }

        public static void Reset()
        {
            mainBuildManager.ResetCaches();
        }

        private class CancellableAsyncBuild : ICancellableAsyncBuild
        {
            public CancellableAsyncBuild(Microsoft.Build.Evaluation.Project project, string assemblyPath)
            {
                Project = project;
                AssemblyPath = assemblyPath;
            }

            public string AssemblyPath { get; private set; }

            public Microsoft.Build.Evaluation.Project Project { get; private set; }

            public Task<BuildResult> BuildTask { get; private set; }

            public bool IsCanceled { get; private set; }

            internal void Build(Microsoft.Build.Evaluation.Project project, string targets, BuildRequestDataFlags flags, Microsoft.Build.Utilities.Logger logger)
            {
                if (project == null) throw new ArgumentNullException("project");
                if (logger == null) throw new ArgumentNullException("logger");

                // Make sure that we are using the project collection from the loaded project, otherwise we are getting
                // weird cache behavior with the msbuild system
                var projectInstance = new ProjectInstance(project.Xml, project.ProjectCollection.GlobalProperties, project.ToolsVersion, project.ProjectCollection);

                BuildTask = Task.Run(() =>
                {
                    var buildResult = mainBuildManager.Build(
                        new BuildParameters(project.ProjectCollection)
                        {
                            Loggers = new[] { logger }
                        },
                        new BuildRequestData(projectInstance, targets.Split(';'), null, flags));

                    return buildResult;
                });
            }

            public void Cancel()
            {
                var localManager = mainBuildManager;
                if (localManager != null)
                {
                    localManager.CancelAllSubmissions();
                    IsCanceled = true;
                }
            }
        }
    }
}
