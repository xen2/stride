// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Evaluation;
using NuGet.ProjectModel;
using Xenko.Core.Annotations;
using Xenko.Core.Assets.Analysis;
using Xenko.Core.Assets.Diagnostics;
using Xenko.Core.Assets.Tracking;
using Xenko.Core.Diagnostics;
using Xenko.Core.Extensions;
using Xenko.Core.IO;
using Xenko.Core.Packages;
using Xenko.Core.Reflection;
using Xenko.Core.Serialization;
using ILogger = Xenko.Core.Diagnostics.ILogger;

namespace Xenko.Core.Assets
{
    public interface IPackageContainer
    {
        Package Package { get; }
    }

    public sealed class Project2 : IPackageContainer
    {
        private PackageSession session;
        private Package package;

        public Project2([NotNull] PackageSession session, Guid guid, string fullPath)
        {
            this.Session = session;
            VSProject = new VisualStudio.Project(session.VSSolution, guid, VisualStudio.KnownProjectTypeGuid.CSharp, Path.GetFileNameWithoutExtension(fullPath), fullPath, Guid.Empty,
                Enumerable.Empty<VisualStudio.Section>(),
                Enumerable.Empty<VisualStudio.PropertyItem>(),
                Enumerable.Empty<VisualStudio.PropertyItem>());
        }

        public Project2([NotNull] PackageSession session, VisualStudio.Project vsProject)
        {
            this.Session = session;
            this.VSProject = vsProject;
        }

        public List<LockFileLibrary> Dependencies { get; } = new List<LockFileLibrary>();

        public PackageLoadedAssembly LoadedAssembly { get; private set; }

        [CanBeNull]
        public Package Package
        {
            get => package;
            set
            {
                if (package != null)
                {
                    session.Packages.Remove(package);
                }
                package = value;
                if (package != null)
                {
                    session.Packages.Add(package);
                }
            }
        }

        public UFile FullPath => VSProject.FullPath;

        /// <summary>
        /// Gets the session.
        /// </summary>
        /// <value>The session.</value>
        /// <exception cref="System.InvalidOperationException">Cannot attach a package to more than one session</exception>
        [DataMemberIgnore]
        public PackageSession Session
        {
            get => session;
            internal set
            {
                if (value != null && session != null && !ReferenceEquals(session, value))
                {
                    throw new InvalidOperationException("Cannot attach a package to more than one session");
                }
                session = value;
            }
        }

        internal VisualStudio.Project VSProject;

        private void LoadAssemblyReference(ILogger log, PackageLoadParameters loadParameters)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));
            if (loadParameters == null) throw new ArgumentNullException(nameof(loadParameters));
            var assemblyContainer = loadParameters.AssemblyContainer ?? AssemblyContainer.Default;

            // Check if already loaded
            // TODO: More advanced cases: unload removed references, etc...
            if (LoadedAssembly != null)
                return;

            string assemblyPath = null;

            try
            {
                var forwardingLogger = new ForwardingLoggerResult(log);
                assemblyPath = VSProjectHelper.GetOrCompileProjectAssembly(Session?.SolutionPath, VSProject.FullPath, forwardingLogger, "Build", loadParameters.AutoCompileProjects, loadParameters.BuildConfiguration, extraProperties: loadParameters.ExtraCompileProperties, onlyErrors: true);
                if (String.IsNullOrWhiteSpace(assemblyPath))
                {
                    log.Error($"Unable to locate assembly reference for project [{VSProject.FullPath}]");
                    return;
                }

                LoadedAssembly = new PackageLoadedAssembly(assemblyPath);

                if (!File.Exists(assemblyPath) || forwardingLogger.HasErrors)
                {
                    log.Error($"Unable to build assembly reference [{assemblyPath}]");
                    return;
                }

                var assembly = assemblyContainer.LoadAssemblyFromPath(assemblyPath, log);
                if (assembly == null)
                {
                    log.Error($"Unable to load assembly reference [{assemblyPath}]");
                }

                LoadedAssembly.Assembly = assembly;

                if (assembly != null)
                {
                    // Register assembly in the registry
                    AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Assets);
                }
            }
            catch (Exception ex)
            {
                log.Error($"Unexpected error while loading project [{VSProject.FullPath}] or assembly reference [{assemblyPath}]", ex);
            }
        }
    }

    public sealed class ProjectCollection : ObservableCollection<Project2>
    {
    }

    /// <summary>
    /// A session for editing a package.
    /// </summary>
    public sealed class PackageSession : IDisposable, IAssetFinder
    {
        /// <summary>
        /// The visual studio version property used for newly created project solution files
        /// </summary>
        public static readonly Version DefaultVisualStudioVersion = new Version("14.0.23107.0");

        private readonly ConstraintProvider constraintProvider = new ConstraintProvider();
        private readonly PackageCollection packagesCopy;
        private readonly object dependenciesLock = new object();
        private Project2 currentProject;
        private AssetDependencyManager dependencies;
        private AssetSourceTracker sourceTracker;
        private bool? packageUpgradeAllowed;
        public event DirtyFlagChangedDelegate<AssetItem> AssetDirtyChanged;
        private TaskCompletionSource<int> saveCompletion;

        /// <summary>
        /// Initializes a new instance of the <see cref="PackageSession"/> class.
        /// </summary>
        public PackageSession()
        {
            VSSolution = new VisualStudio.Solution();
            VSSolution.Headers.Add(PackageSessionHelper.SolutionHeader);

            constraintProvider.AddConstraint(PackageStore.Instance.DefaultPackageName, new PackageVersionRange(PackageStore.Instance.DefaultPackageVersion));

            Projects = new ProjectCollection();
            Projects.CollectionChanged += ProjectsCollectionChanged;

            Packages = new PackageCollection();
            packagesCopy = new PackageCollection();
            AssemblyContainer = new AssemblyContainer();
            Packages.CollectionChanged += PackagesCollectionChanged;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PackageSession"/> class.
        /// </summary>
        public PackageSession(Package package) : this()
        {
            if (package != null)
            {
                Packages.Add(package);
            }
        }

        internal VisualStudio.Solution VSSolution;

        public bool IsDirty { get; set; }

        /// <summary>
        /// The projects referenced by the solution.
        /// </summary>
        public ProjectCollection Projects { get; }

        /// <summary>
        /// Gets the packages referenced by the solution.
        /// </summary>
        /// <value>The packages.</value>
        public PackageCollection Packages { get; }

        /// <summary>
        /// Gets the user packages (excluding system packages).
        /// </summary>
        /// <value>The user packages.</value>
        public IEnumerable<Package> LocalPackages => Packages.Where(package => !package.IsSystem);

        /// <summary>
        /// Gets a task that completes when the session is finished saving.
        /// </summary>
        [NotNull]
        public Task SaveCompletion => saveCompletion?.Task ?? Task.CompletedTask;

        /// <summary>
        /// Gets or sets the solution path (sln) in case the session was loaded from a solution.
        /// </summary>
        /// <value>The solution path.</value>
        public UFile SolutionPath
        {
            get => VSSolution.FullPath;
            set => VSSolution.FullPath = value;
        }

        public AssemblyContainer AssemblyContainer { get; }

        /// <summary>
        /// The targeted visual studio version (if specified by the loaded package)
        /// </summary>
        public Version VisualStudioVersion { get; set; }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            dependencies?.Dispose();
            sourceTracker?.Dispose();

            var loadedAssemblies = Packages.SelectMany(x => x.LoadedAssemblies).ToList();
            for (int index = loadedAssemblies.Count - 1; index >= 0; index--)
            {
                var loadedAssembly = loadedAssemblies[index];
                if (loadedAssembly == null)
                    continue;

                // Unregisters assemblies that have been registered in Package.Load => Package.LoadAssemblyReferencesForPackage
                AssemblyRegistry.Unregister(loadedAssembly.Assembly);

                // Unload binary serialization
                DataSerializerFactory.UnregisterSerializationAssembly(loadedAssembly.Assembly);

                // Unload assembly
                AssemblyContainer.UnloadAssembly(loadedAssembly.Assembly);
            }
        }

        /// <summary>
        /// Gets a value indicating whether this instance has dependency manager.
        /// </summary>
        /// <value><c>true</c> if this instance has dependency manager; otherwise, <c>false</c>.</value>
        public bool HasDependencyManager
        {
            get
            {
                lock (dependenciesLock)
                {
                    return dependencies != null;
                }
            }
        }

        /// <summary>
        /// Gets or sets the selected current package.
        /// </summary>
        /// <value>The selected current package.</value>
        /// <exception cref="System.InvalidOperationException">Expecting a package that is already registered in this session</exception>
        public Project2 CurrentProject
        {
            get
            {
                return currentProject;
            }
            set
            {
                if (value != null)
                {
                    if (!Projects.Contains(value))
                    {
                        throw new InvalidOperationException("Expecting a project that is already registered in this session");
                    }
                }
                currentProject = value;
            }
        }

        /// <summary>
        /// Gets the packages referenced by the current package.
        /// </summary>
        /// <returns>IEnumerable&lt;Package&gt;.</returns>
        public IEnumerable<Package> GetPackagesFromCurrent()
        {
            if (CurrentProject == null)
            {
                yield break;
            }

            if (CurrentProject.Package != null)
                yield return CurrentProject.Package;

            foreach (var packageDependency in CurrentProject.Dependencies)
            {
                var loadedPackage = Packages.Find(packageDependency);
                // In case the package is not found (when working with session not fully loaded/resolved with all deps)
                if (loadedPackage == null)
                {
                    yield return loadedPackage;
                }
            }
        }

        /// <summary>
        /// Gets the dependency manager.
        /// </summary>
        /// <value>AssetDependencyManager.</value>
        public AssetDependencyManager DependencyManager
        {
            get
            {
                lock (dependenciesLock)
                {
                    return dependencies ?? (dependencies = new AssetDependencyManager(this));
                }
            }
        }

        public AssetSourceTracker SourceTracker
        {
            get
            {
                lock (dependenciesLock)
                {
                    return sourceTracker ?? (sourceTracker = new AssetSourceTracker(this));
                }
            }
        }

        /// <inheritdoc />
        /// <remarks>Looks for the asset amongst all the packages of this session.</remarks>
        public AssetItem FindAsset(AssetId assetId)
        {
            return Packages.Select(p => p.Assets.Find(assetId)).NotNull().FirstOrDefault();
        }

        /// <inheritdoc />
        /// <remarks>Looks for the asset amongst all the packages of this session.</remarks>
        public AssetItem FindAsset(UFile location)
        {
            return Packages.Select(p => p.Assets.Find(location)).NotNull().FirstOrDefault();
        }

        /// <inheritdoc />
        /// <remarks>Looks for the asset amongst all the packages of this session.</remarks>
        public AssetItem FindAssetFromProxyObject(object proxyObject)
        {
            var reference = AttachedReferenceManager.GetAttachedReference(proxyObject);
            return reference != null ? (FindAsset(reference.Id) ?? FindAsset(reference.Url)) : null;
        }

        /// <summary>
        /// Loads a package from specified file path.
        /// </summary>
        /// <param name="filePath">The file path to a package file.</param>
        /// <param name="sessionResult">The session result.</param>
        /// <param name="loadParameters">The load parameters.</param>
        /// <exception cref="System.ArgumentNullException">filePath</exception>
        /// <exception cref="System.ArgumentException">File [{0}] must exist.ToFormat(filePath);filePath</exception>
        public static void Load(string filePath, PackageSessionResult sessionResult, PackageLoadParameters loadParameters = null)
        {
            if (filePath == null) throw new ArgumentNullException(nameof(filePath));
            if (sessionResult == null) throw new ArgumentNullException(nameof(sessionResult));

            // Make sure with have valid parameters
            loadParameters = loadParameters ?? PackageLoadParameters.Default();

            // Make sure to use a full path.
            filePath = FileUtility.GetAbsolutePath(filePath);

            if (!File.Exists(filePath)) throw new ArgumentException($@"File [{filePath}] must exist", nameof(filePath));

            try
            {
                // Enable reference analysis caching during loading
                AssetReferenceAnalysis.EnableCaching = true;

                using (var profile = Profiler.Begin(PackageSessionProfilingKeys.Loading))
                {
                    sessionResult.Clear();
                    sessionResult.Progress("Loading..", 0, 1);

                    var session = new PackageSession();

                    // If we have a solution, load all packages
                    if (PackageSessionHelper.IsSolutionFile(filePath))
                    {
                        session.VSSolution = VisualStudio.Solution.FromFile(filePath);

                        PackageSessionHelper.LoadSolution(session, filePath, sessionResult);
                    }
                    else
                    {
                        sessionResult.Error($"Unsupported file extension (only .sln is supported)");
                        return;
                    }

                    var cancelToken = loadParameters.CancelToken;

                    // Load all packages
                    foreach (var project in session.Projects)
                    {
                        if (PackageSessionHelper.IsPackage(project, out var packageFilePath))
                        {
                            session.PreLoadPackage(sessionResult, packageFilePath, false, loadParameters);
                        }

                        // Output the session only if there is no cancellation
                        if (cancelToken.HasValue && cancelToken.Value.IsCancellationRequested)
                        {
                            return;
                        }
                    }

                    if (loadParameters.AutoCompileProjects && loadParameters.ForceNugetRestore && PackageSessionHelper.IsPackageFile(filePath))
                    {
                        // Restore nuget packages
                        if (PackageSessionHelper.IsSolutionFile(filePath))
                        {
                            VSProjectHelper.RestoreNugetPackages(sessionResult, filePath).Wait();
                        }
                        else
                        {
                            // No .sln, run NuGet restore for each project
                            foreach (var package in session.Packages)
                                package.RestoreNugetPackages(sessionResult);
                        }
                    }

                    // Load all missing references/dependencies
                    session.LoadMissingReferences(sessionResult, loadParameters);

                    // Fix relative references
                    var analysis = new PackageSessionAnalysis(session, GetPackageAnalysisParametersForLoad());
                    var analysisResults = analysis.Run();
                    analysisResults.CopyTo(sessionResult);

                    // Run custom package session analysis
                    foreach (var type in AssetRegistry.GetPackageSessionAnalysisTypes())
                    {
                        var pkgAnalysis = (PackageSessionAnalysisBase)Activator.CreateInstance(type);
                        pkgAnalysis.Session = session;
                        var results = pkgAnalysis.Run();
                        results.CopyTo(sessionResult);
                    }

                    // Output the session only if there is no cancellation
                    if (!cancelToken.HasValue || !cancelToken.Value.IsCancellationRequested)
                    {
                        sessionResult.Session = session;

                        // Defer the initialization of the dependency manager
                        //session.DependencyManager.InitializeDeferred();
                    }

                    // Setup the current package when loading it
                    if (session.Projects.Count > 0)
                    {
                        session.CurrentProject = session.Projects[0];
                    }

                    // The session is not dirty when loading it
                    session.IsDirty = false;
                }
            }
            finally
            {
                // Disable reference analysis caching after loading
                AssetReferenceAnalysis.EnableCaching = false;
            }
        }

        /// <summary>
        /// Loads a package from specified file path.
        /// </summary>
        /// <param name="filePath">The file path to a package file.</param>
        /// <param name="loadParameters">The load parameters.</param>
        /// <returns>A package.</returns>
        /// <exception cref="System.ArgumentNullException">filePath</exception>
        public static PackageSessionResult Load(string filePath, PackageLoadParameters loadParameters = null)
        {
            var result = new PackageSessionResult();
            Load(filePath, result, loadParameters);
            return result;
        }

        /// <summary>
        /// Make sure packages have their dependencies and assets loaded.
        /// </summary>
        /// <param name="log">The log.</param>
        /// <param name="loadParameters">The load parameters.</param>
        public void LoadMissingReferences(ILogger log, PackageLoadParameters loadParameters = null)
        {
            LoadMissingDependencies(log, loadParameters);
            LoadMissingAssets(log, Packages.ToList(), loadParameters);
        }

        /// <summary>
        /// Make sure packages have their dependencies loaded.
        /// </summary>
        /// <param name="log">The log.</param>
        /// <param name="loadParametersArg">The load parameters argument.</param>
        public void LoadMissingDependencies(ILogger log, PackageLoadParameters loadParametersArg = null)
        {
            var loadParameters = loadParametersArg ?? PackageLoadParameters.Default();

            var cancelToken = loadParameters.CancelToken;

            var packagesLoaded = new PackageCollection();

            // Make a copy of Packages as it can be modified by PreLoadProjectDependencies
            foreach (var project in Projects)
            {
                // Output the session only if there is no cancellation
                if (cancelToken.HasValue && cancelToken.Value.IsCancellationRequested)
                {
                    return;
                }

                PreLoadProjectDependencies(log, project, packagesLoaded, loadParameters);
            }
        }

        /// <summary>
        /// Make sure packages have their assets loaded.
        /// </summary>
        /// <param name="log">The log.</param>
        /// <param name="packages">The packages to try to load missing assets from.</param>
        /// <param name="loadParametersArg">The load parameters argument.</param>
        public void LoadMissingAssets(ILogger log, IEnumerable<Package> packages, PackageLoadParameters loadParametersArg = null)
        {
            var loadParameters = loadParametersArg ?? PackageLoadParameters.Default();

            var cancelToken = loadParameters.CancelToken;

            // Make a copy of Packages as it can be modified by PreLoadProjectDependencies
            foreach (var package in packages)
            {
                // Output the session only if there is no cancellation
                if (cancelToken.HasValue && cancelToken.Value.IsCancellationRequested)
                {
                    return;
                }

                TryLoadAssets(this, log, package, loadParameters);
            }
        }

        /// <summary>
        /// Saves all packages and assets.
        /// </summary>
        /// <param name="log">The <see cref="LoggerResult"/> in which to report result.</param>
        /// <param name="saveParameters">The parameters for the save operation.</param>
        public void Save(ILogger log, PackageSaveParameters saveParameters = null)
        {
            //var clock = Stopwatch.StartNew();
            var loggerResult = new ForwardingLoggerResult(log);
            using (var profile = Profiler.Begin(PackageSessionProfilingKeys.Saving))
            {
                var packagesSaved = false;
                var packagesDirty = false;
                try
                {
                    saveCompletion = new TaskCompletionSource<int>();

                    saveParameters = saveParameters ?? PackageSaveParameters.Default();
                    var assetsOrPackagesToRemove = BuildAssetsOrPackagesToRemove();

                    // Compute packages that have been renamed
                    // TODO: Disable for now, as not sure if we want to delete a previous package
                    //foreach (var package in packagesCopy)
                    //{
                    //    var newPackage = packages.Find(package.Id);
                    //    if (newPackage != null && package.PackagePath != null && newPackage.PackagePath != package.PackagePath)
                    //    {
                    //        assetsOrPackagesToRemove[package.PackagePath] = package;
                    //    }
                    //}

                    // If package are not modified, return immediately
                    if (!CheckModifiedPackages() && assetsOrPackagesToRemove.Count == 0)
                    {
                        return;
                    }

                    // Suspend tracking when saving as we don't want to receive
                    // all notification events
                    dependencies?.BeginSavingSession();
                    sourceTracker?.BeginSavingSession();

                    // Return immediately if there is any error
                    if (loggerResult.HasErrors)
                        return;
       
                    //batch projects
                    var vsProjs = new Dictionary<string, Project>();

                    // Delete previous files
                    foreach (var fileIt in assetsOrPackagesToRemove)
                    {
                        var assetPath = fileIt.Key;
                        var assetItemOrPackage = fileIt.Value;

                        var assetItem = assetItemOrPackage as AssetItem;
                        try
                        {
                            //If we are within a csproj we need to remove the file from there as well
                            if (assetItem?.SourceProject != null)
                            {
                                var projectAsset = assetItem.Asset as IProjectAsset;
                                if (projectAsset != null)
                                {
                                    var projectInclude = assetItem.GetProjectInclude();

                                    if (!vsProjs.TryGetValue(assetItem.SourceProject, out var project))
                                    {
                                        project = VSProjectHelper.LoadProject(assetItem.SourceProject);
                                        vsProjs.Add(assetItem.SourceProject, project);
                                    }
                                    var projectItem = project.Items.FirstOrDefault(x => (x.ItemType == "Compile" || x.ItemType == "None") && x.EvaluatedInclude == projectInclude);
                                    if (projectItem != null && !projectItem.IsImported)
                                    {
                                        project.RemoveItem(projectItem);
                                    }

                                    //delete any generated file as well
                                    var generatorAsset = assetItem.Asset as IProjectFileGeneratorAsset;
                                    if (generatorAsset != null)
                                    {
                                        var generatedAbsolutePath = assetItem.GetGeneratedAbsolutePath().ToWindowsPath();

                                        File.Delete(generatedAbsolutePath);

                                        var generatedInclude = assetItem.GetGeneratedInclude();
                                        var generatedItem = project.Items.FirstOrDefault(x => (x.ItemType == "Compile" || x.ItemType == "None") && x.EvaluatedInclude == generatedInclude);
                                        if (generatedItem != null)
                                        {
                                            project.RemoveItem(generatedItem);
                                        }
                                    }
                                }
                            }

                            File.Delete(assetPath);
                        }
                        catch (Exception ex)
                        {
                            if (assetItem != null)
                            {
                                loggerResult.Error(assetItem.Package, assetItem.ToReference(), AssetMessageCode.AssetCannotDelete, ex, assetPath);
                            }
                            else
                            {
                                var package = assetItemOrPackage as Package;
                                if (package != null)
                                {
                                    loggerResult.Error(package, null, AssetMessageCode.AssetCannotDelete, ex, assetPath);
                                }
                            }
                        }
                    }

                    foreach (var project in vsProjs.Values)
                    {
                        project.Save();
                        project.ProjectCollection.UnloadAllProjects();
                        project.ProjectCollection.Dispose();
                    }

                    // Save all dirty assets
                    packagesCopy.Clear();
                    foreach (var package in LocalPackages)
                    {
                        // Save the package to disk and all its assets
                        package.Save(loggerResult, saveParameters);

                        // Check if everything was saved (might not be the case if things are filtered out)
                        if (package.IsDirty || package.Assets.IsDirty)
                            packagesDirty = true;

                        // Clone the package (but not all assets inside, just the structure)
                        var packageClone = package.Clone();
                        packagesCopy.Add(packageClone);
                    }

                    packagesSaved = true;
                }
                finally
                {
                    sourceTracker?.EndSavingSession();
                    dependencies?.EndSavingSession();

                    // Once all packages and assets have been saved, we can save the solution (as we need to have fullpath to
                    // be setup for the packages)
                    if (packagesSaved)
                    {
                        VSSolution.FullPath = UPath.Combine(Environment.CurrentDirectory, SolutionPath);
                        VSSolution.Save();
                    }
                    saveCompletion?.SetResult(0);
                    saveCompletion = null;
                }

                //System.Diagnostics.Trace.WriteLine("Elapsed saved: " + clock.ElapsedMilliseconds);
                IsDirty = packagesDirty;
            }
        }

        private Dictionary<UFile, object> BuildAssetsOrPackagesToRemove()
        {
            // Grab all previous assets
            var previousAssets = new Dictionary<AssetId, AssetItem>();
            foreach (var assetItem in packagesCopy.SelectMany(package => package.Assets))
            {
                previousAssets[assetItem.Id] = assetItem;
            }

            // Grab all new assets
            var newAssets = new Dictionary<AssetId, AssetItem>();
            foreach (var assetItem in LocalPackages.SelectMany(package => package.Assets))
            {
                newAssets[assetItem.Id] = assetItem;
            }

            // Compute all assets that were removed
            var assetsOrPackagesToRemove = new Dictionary<UFile, object>();
            foreach (var assetIt in previousAssets)
            {
                var asset = assetIt.Value;

                AssetItem newAsset;
                if (!newAssets.TryGetValue(assetIt.Key, out newAsset) || newAsset.Location != asset.Location)
                {
                    assetsOrPackagesToRemove[asset.FullPath] = asset;
                }
            }
            return assetsOrPackagesToRemove;
        }

        private bool CheckModifiedPackages()
        {
            if (IsDirty)
            {
                return true;
            }

            foreach (var package in LocalPackages)
            {
                if (package.IsDirty || package.Assets.IsDirty)
                {
                    return true;
                }
                if (package.Assets.Any(item => item.IsDirty))
                {
                    return true;
                }
            }
            return false;
        }

        private void ProjectsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    RegisterProject((Project2)e.NewItems[0]);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    UnRegisterProject((Project2)e.OldItems[0]);
                    break;

                case NotifyCollectionChangedAction.Replace:
                    packagesCopy.Clear();

                    foreach (var oldProject in e.OldItems.OfType<Project2>())
                    {
                        UnRegisterProject(oldProject);
                    }

                    foreach (var projectToCopy in Projects)
                    {
                        RegisterProject(projectToCopy);
                    }
                    break;
            }            
        }

        private void RegisterProject(Project2 project)
        {
            VSSolution.Projects.Add(project.VSProject);
            if (project.Package != null)
                Packages.Add(project.Package);
        }

        private void UnRegisterProject(Project2 project)
        {
            if (project.Package != null)
                Packages.Remove(project.Package);
            VSSolution.Projects.Remove(project.VSProject);
        }

        private void PackagesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    RegisterPackage((Package)e.NewItems[0]);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    UnRegisterPackage((Package)e.OldItems[0]);
                    break;

                case NotifyCollectionChangedAction.Replace:
                    packagesCopy.Clear();

                    foreach (var oldPackage in e.OldItems.OfType<Package>())
                    {
                        UnRegisterPackage(oldPackage);
                    }

                    foreach (var packageToCopy in Packages)
                    {
                        RegisterPackage(packageToCopy);
                    }
                    break;
            }
        }

        private void RegisterPackage(Package package)
        {
            package.Session = this;
            if (package.IsSystem)
                return;
            package.AssetDirtyChanged += OnAssetDirtyChanged;

            // If the package doesn't have any temporary assets, we can freeze it
            if (package.TemporaryAssets.Count == 0)
            {
                FreezePackage(package);
            }

            IsDirty = true;
        }

        /// <summary>
        /// Freeze a package once it is loaded with all its assets
        /// </summary>
        /// <param name="package">The package to freeze.</param>
        private void FreezePackage(Package package)
        {
            if (package.IsSystem)
                return;

            // Freeze only when assets are loaded
            if (package.State < PackageState.AssetsReady)
                return;

            packagesCopy.Add(package.Clone());
        }

        private void UnRegisterPackage(Package package)
        {
            package.Session = null;
            if (package.IsSystem)
                return;
            package.AssetDirtyChanged -= OnAssetDirtyChanged;

            packagesCopy.RemoveById(package.Id);

            IsDirty = true;
        }

        private void OnAssetDirtyChanged(AssetItem asset, bool oldValue, bool newValue)
        {
            AssetDirtyChanged?.Invoke(asset, oldValue, newValue);
        }

        private Package PreLoadPackage(ILogger log, string filePath, bool isSystemPackage, PackageLoadParameters loadParameters)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));
            if (filePath == null) throw new ArgumentNullException(nameof(filePath));
            if (loadParameters == null) throw new ArgumentNullException(nameof(loadParameters));

            try
            {
                var packageId = Package.GetPackageIdFromFile(filePath);

                // Check that the package was not already loaded, otherwise return the same instance
                if (Packages.ContainsById(packageId))
                {
                    return Packages.Find(packageId);
                }

                // Load the package without loading any assets
                var package = Package.LoadRaw(log, filePath);
                package.IsSystem = isSystemPackage;

                // Convert UPath to absolute (Package only)
                // Removed for now because it is called again in PackageSession.LoadAssembliesAndAssets (and running it twice result in dirty package)
                // If we remove it from here (and call it only in the other method), templates are not loaded (Because they are loaded via the package store that do not use PreLoadPackage)
                //if (loadParameters.ConvertUPathToAbsolute)
                //{
                //    var analysis = new PackageAnalysis(package, new PackageAnalysisParameters()
                //    {
                //        ConvertUPathTo = UPathType.Absolute,
                //        SetDirtyFlagOnAssetWhenFixingAbsoluteUFile = true,
                //        IsProcessingUPaths = true,
                //    });
                //    analysis.Run(log);
                //}
                // If the package doesn't have a meta name, fix it here (This is supposed to be done in the above disabled analysis - but we still need to do it!)
                if (string.IsNullOrWhiteSpace(package.Meta.Name) && package.FullPath != null)
                {
                    package.Meta.Name = package.FullPath.GetFileNameWithoutExtension();
                    package.IsDirty = true;
                }

                // Package has been loaded, register it in constraints so that we force each subsequent loads to use this one (or fails if version doesn't match)
                if (package.Meta.Version != null)
                {
                    constraintProvider.AddConstraint(package.Meta.Name, new PackageVersionRange(package.Meta.Version));
                }

                // Load package dependencies
                // This will perform necessary asset upgrades
                // TODO: We should probably split package loading in two recursive top-level passes (right now those two passes are mixed, making it more difficult to make proper checks)
                //   - First, load raw packages with their dependencies recursively, then resolve dependencies and constraints (and print errors/warnings)
                //   - Then, if everything is OK, load the actual references and assets for each packages
                //PreLoadProjectDependencies(log, package, loadedPackages, loadParameters);

                // Add the package to the session but don't freeze it yet
                Packages.Add(package);

                return package;
            }
            catch (Exception ex)
            {
                log.Error($"Error while pre-loading package [{filePath}]", ex);
            }

            return null;
        }

        private bool TryLoadAssets(PackageSession session, ILogger log, Package package, PackageLoadParameters loadParameters)
        {
            // Already loaded
            if (package.State >= PackageState.AssetsReady)
                return true;

            // Dependencies could not properly be loaded
            if (package.State < PackageState.DependenciesReady)
                return false;

            // A package upgrade has previously been tried and denied, so let's keep the package in this state
            if (package.State == PackageState.UpgradeFailed)
                return false;

            try
            {
                // First, check that dependencies have their assets loaded
                bool dependencyError = false;
                foreach (var dependency in package.FindDependencies(false, false))
                {
                    if (!TryLoadAssets(session, log, dependency, loadParameters))
                        dependencyError = true;
                }

                if (dependencyError)
                    return false;

                var pendingPackageUpgrades = new List<PendingPackageUpgrade>();

                // Note: Default state is upgrade failed (for early exit on error/exceptions)
                // We will update to success as soon as loading is finished.
                package.State = PackageState.UpgradeFailed;

                // Process store dependencies for upgraders
                foreach (var packageDependency in package.Meta.Dependencies)
                {
                    var dependencyPackage = session.Packages.Find(packageDependency);
                    if (dependencyPackage == null)
                    {
                        continue;
                    }

                    // Check for upgraders
                    var packageUpgrader = CheckPackageUpgrade(log, package, packageDependency, dependencyPackage);
                    if (packageUpgrader != null)
                    {
                        pendingPackageUpgrades.Add(new PendingPackageUpgrade(packageUpgrader, packageDependency, dependencyPackage));
                    }
                }

                // Prepare asset loading
                var newLoadParameters = loadParameters.Clone();
                newLoadParameters.AssemblyContainer = session.AssemblyContainer;

                // Default package version override
                newLoadParameters.ExtraCompileProperties = new Dictionary<string, string>();
                var defaultPackageOverride = NugetStore.GetPackageVersionVariable(PackageStore.Instance.DefaultPackageName) + "Override";
                var defaultPackageVersion = PackageStore.Instance.DefaultPackageVersion.Version;
                newLoadParameters.ExtraCompileProperties.Add(defaultPackageOverride, new Version(defaultPackageVersion.Major, defaultPackageVersion.Minor).ToString());
                if (loadParameters.ExtraCompileProperties != null)
                {
                    foreach (var property in loadParameters.ExtraCompileProperties)
                    {
                        newLoadParameters.ExtraCompileProperties[property.Key] = property.Value;
                    }
                }

                if (pendingPackageUpgrades.Count > 0)
                {
                    var upgradeAllowed = packageUpgradeAllowed != false ? PackageUpgradeRequestedAnswer.Upgrade : PackageUpgradeRequestedAnswer.DoNotUpgrade;

                    // Need upgrades, let's ask user confirmation
                    if (loadParameters.PackageUpgradeRequested != null && !packageUpgradeAllowed.HasValue)
                    {
                        upgradeAllowed = loadParameters.PackageUpgradeRequested(package, pendingPackageUpgrades);
                        if (upgradeAllowed == PackageUpgradeRequestedAnswer.UpgradeAll)
                            packageUpgradeAllowed = true;
                        if (upgradeAllowed == PackageUpgradeRequestedAnswer.DoNotUpgradeAny)
                            packageUpgradeAllowed = false;
                    }

                    if (!PackageLoadParameters.ShouldUpgrade(upgradeAllowed))
                    {
                        log.Error($"Necessary package migration for [{package.Meta.Name}] has not been allowed");
                        return false;
                    }

                    // Perform pre assembly load upgrade
                    foreach (var pendingPackageUpgrade in pendingPackageUpgrades)
                    {
                        var packageUpgrader = pendingPackageUpgrade.PackageUpgrader;
                        var dependencyPackage = pendingPackageUpgrade.DependencyPackage;
                        if (!packageUpgrader.UpgradeBeforeAssembliesLoaded(loadParameters, session, log, package, pendingPackageUpgrade.Dependency, dependencyPackage))
                        {
                            log.Error($"Error while upgrading package [{package.Meta.Name}] for [{dependencyPackage.Meta.Name}] from version [{pendingPackageUpgrade.Dependency.Version}] to [{dependencyPackage.Meta.Version}]");
                            return false;
                        }
                    }
                }

                // Load assemblies. Set the package filename to the path on disk, in case of renaming.
                // TODO: Could referenced projects be associated to other packages than this one?
                newLoadParameters.ExtraCompileProperties.Add("XenkoCurrentPackagePath", package.FullPath);
                package.LoadAssemblies(log, newLoadParameters);

                // Load list of assets
                newLoadParameters.AssetFiles = Package.ListAssetFiles(log, package, true, loadParameters.CancelToken);
                // Sort them by size (to improve concurrency during load)
                newLoadParameters.AssetFiles.Sort(PackageLoadingAssetFile.FileSizeComparer.Default);

                if (pendingPackageUpgrades.Count > 0)
                {
                    // Perform upgrades
                    foreach (var pendingPackageUpgrade in pendingPackageUpgrades)
                    {
                        var packageUpgrader = pendingPackageUpgrade.PackageUpgrader;
                        var dependencyPackage = pendingPackageUpgrade.DependencyPackage;
                        if (!packageUpgrader.Upgrade(loadParameters, session, log, package, pendingPackageUpgrade.Dependency, dependencyPackage, newLoadParameters.AssetFiles))
                        {
                            log.Error($"Error while upgrading package [{package.Meta.Name}] for [{dependencyPackage.Meta.Name}] from version [{pendingPackageUpgrade.Dependency.Version}] to [{dependencyPackage.Meta.Version}]");
                            return false;
                        }

                        // Update dependency to reflect new requirement
                        pendingPackageUpgrade.Dependency.Version = pendingPackageUpgrade.PackageUpgrader.Attribute.UpdatedVersionRange;
                    }

                    // Mark package as dirty
                    package.IsDirty = true;
                }

                // Load assets
                package.LoadAssets(log, newLoadParameters);

                // Validate assets from package
                package.ValidateAssets(newLoadParameters.GenerateNewAssetIds, newLoadParameters.RemoveUnloadableObjects, log);

                if (pendingPackageUpgrades.Count > 0)
                {
                    // Perform post asset load upgrade
                    foreach (var pendingPackageUpgrade in pendingPackageUpgrades)
                    {
                        var packageUpgrader = pendingPackageUpgrade.PackageUpgrader;
                        var dependencyPackage = pendingPackageUpgrade.DependencyPackage;
                        if (!packageUpgrader.UpgradeAfterAssetsLoaded(loadParameters, session, log, package, pendingPackageUpgrade.Dependency, dependencyPackage, pendingPackageUpgrade.DependencyVersionBeforeUpgrade))
                        {
                            log.Error($"Error while upgrading package [{package.Meta.Name}] for [{dependencyPackage.Meta.Name}] from version [{pendingPackageUpgrade.Dependency.Version}] to [{dependencyPackage.Meta.Version}]");
                            return false;
                        }
                    }

                    // Mark package as dirty
                    package.IsDirty = true;
                }

                // Mark package as ready
                package.State = PackageState.AssetsReady;

                // Freeze the package after loading the assets
                session.FreezePackage(package);

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"Error while pre-loading package [{package}]", ex);
                return false;
            }
        }

        private static PackageUpgrader CheckPackageUpgrade(ILogger log, Package dependentPackage, PackageDependency dependency, Package dependencyPackage)
        {
            // Don't do anything if source is a system (read-only) package for now
            // We only want to process local packages
            if (dependentPackage.IsSystem)
                return null;

            // Check if package might need upgrading
            var dependentPackagePreviousMinimumVersion = dependency.Version.MinVersion;
            if (dependentPackagePreviousMinimumVersion < dependencyPackage.Meta.Version)
            {
                // Find upgrader for given package
                // Note: If no upgrader is found, we assume it is still compatible with previous versions, so do nothing
                var packageUpgrader = AssetRegistry.GetPackageUpgrader(dependencyPackage.Meta.Name);
                if (packageUpgrader != null)
                {
                    // Check if upgrade is necessary
                    if (dependency.Version.MinVersion >= packageUpgrader.Attribute.UpdatedVersionRange.MinVersion)
                    {
                        return null;
                    }

                    // Check if upgrade is allowed
                    if (dependency.Version.MinVersion < packageUpgrader.Attribute.PackageMinimumVersion)
                    {
                        // Throw an exception, because the package update is not allowed and can't be done
                        throw new InvalidOperationException($"Upgrading package [{dependentPackage.Meta.Name}] to use [{dependencyPackage.Meta.Name}] from version [{dependentPackagePreviousMinimumVersion}] to [{dependencyPackage.Meta.Version}] is not supported");
                    }

                    log.Info($"Upgrading package [{dependentPackage.Meta.Name}] to use [{dependencyPackage.Meta.Name}] from version [{dependentPackagePreviousMinimumVersion}] to [{dependencyPackage.Meta.Version}] will be required");
                    return packageUpgrader;
                }
            }

            return null;
        }

        private class Library
        {
            public string SHA512;
            public string Type;
            public string Path;
            public List<string> Files;
        }
        
        private void PreLoadProjectDependencies(ILogger log, Project2 project, PackageCollection loadedPackages, PackageLoadParameters loadParameters)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (loadParameters == null) throw new ArgumentNullException(nameof(loadParameters));

            var projectDependencyErrors = false;

            // TODO: Remove and recheck Dependencies Ready if some secondary packages are removed?
            if (package.State >= PackageState.DependenciesReady)
                return;

            project.Dependencies.Clear();

            log.Verbose("Restore NuGet packages...");
            VSProjectHelper.RestoreNugetPackages(log, project.FullPath).Wait();

            var projectAssetsJsonPath = Path.Combine(project.FullPath.GetFullDirectory(), @"obj", LockFileFormat.AssetsFileName);
            if (File.Exists(projectAssetsJsonPath))
            {
                var format = new LockFileFormat();
                var projectAssets = format.Read(projectAssetsJsonPath);

                // Update dependencies
                project.Dependencies.AddRange(projectAssets.Libraries);

                // Load dependency (if external)

                // Compute output path
            }

            // 1. Load store package
            foreach (var packageDependency in project.Dependencies)
            {
                var loadedPackage = Packages.Find(packageDependency);
                if (loadedPackage == null)
                {
                    var file = PackageStore.Instance.GetPackageFileName(packageDependency.Name, new PackageVersionRange(new PackageVersion(packageDependency.Version.Version, packageDependency.Version.Release)), constraintProvider);

                    if (file == null)
                    {
                        // TODO: We need to support automatic download of packages. This is not supported yet when only Xenko
                        // package is supposed to be installed, but It will be required for full store
                        log.Error($"The project {project.FullPath?.GetFileNameWithoutExtension() ?? "[Untitled]"} depends on package {packageDependency} which is not installed");
                        projectDependencyErrors = true;
                        continue;
                    }

                    // Recursive load of the system package
                    loadedPackage = PreLoadPackage(log, file, true, loadParameters);
                }

                if (loadedPackage == null || loadedPackage.State < PackageState.DependenciesReady)
                    projectDependencyErrors = true;
            }

            // 2. Load local packages
            /*foreach (var packageReference in project.LocalDependencies)
            {
                // Check that the package was not already loaded, otherwise return the same instance
                if (Packages.ContainsById(packageReference.Id))
                {
                    continue;
                }

                // Expand the string of the location
                var newLocation = packageReference.Location;

                var subPackageFilePath = package.RootDirectory != null ? UPath.Combine(package.RootDirectory, newLocation) : newLocation;

                // Recursive load
                var loadedPackage = PreLoadPackage(log, subPackageFilePath.FullPath, false, loadedPackages, loadParameters);

                if (loadedPackage == null || loadedPackage.State < PackageState.DependenciesReady)
                    packageDependencyErrors = true;
            }*/

            // 3. Update package state
            if (!projectDependencyErrors)
            {
                package.State = PackageState.DependenciesReady;
            }
        }

        public class PendingPackageUpgrade
        {
            public readonly PackageUpgrader PackageUpgrader;
            public readonly PackageDependency Dependency;
            public readonly Package DependencyPackage;
            public readonly PackageVersionRange DependencyVersionBeforeUpgrade;

            public PendingPackageUpgrade(PackageUpgrader packageUpgrader, PackageDependency dependency, Package dependencyPackage)
            {
                PackageUpgrader = packageUpgrader;
                Dependency = dependency;
                DependencyPackage = dependencyPackage;
                DependencyVersionBeforeUpgrade = Dependency.Version;
            }
        }

        private static PackageAnalysisParameters GetPackageAnalysisParametersForLoad()
        {
            return new PackageAnalysisParameters()
            {
                IsPackageCheckDependencies = true,
                IsProcessingAssetReferences = true,
                IsLoggingAssetNotFoundAsError = true,
            };
        }
    }
}
