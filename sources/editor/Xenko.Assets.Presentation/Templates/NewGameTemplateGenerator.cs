// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xenko.Core.Assets;
using Xenko.Core.Assets.Editor.Components.TemplateDescriptions;
using Xenko.Core.Assets.Templates;
using Xenko.Core;
using Xenko.Core.Annotations;
using Xenko.Core.IO;
using Xenko.Core.Mathematics;
using Xenko.Core.Serialization;
using Xenko.Core.Presentation.Services;
using Xenko.Assets.Entities;
using Xenko.Assets.Materials;
using Xenko.Assets.Skyboxes;
using Xenko.Assets.Textures;
using Xenko.Engine;
using Xenko.Graphics;
using Xenko.Rendering;
using Xenko.Rendering.Compositing;
using Xenko.Rendering.Lights;
using Xenko.Rendering.Materials;
using Xenko.Rendering.Materials.ComputeColors;
using Xenko.Rendering.ProceduralModels;
using Xenko.Rendering.Skyboxes;
using Xenko.Assets.Models;
using Xenko.Assets.Rendering;
using Xenko.Assets.Templates;

namespace Xenko.Assets.Presentation.Templates
{
    /// <summary>
    /// Generator to create a whole package, game library and game executables
    /// </summary>
    public class NewGameTemplateGenerator : SessionTemplateGenerator
    {
        // Used to find BasicCameraController script (it has to match .xktpl)
        private const string CameraScriptDefaultOutputName = "BasicCameraController";

        private static readonly PropertyKey<List<SelectedSolutionPlatform>> PlatformsKey = new PropertyKey<List<SelectedSolutionPlatform>>("Platforms", typeof(NewGameTemplateGenerator));
        private static readonly PropertyKey<List<UDirectory>> AssetsKey = new PropertyKey<List<UDirectory>>("AssetPackages", typeof(NewGameTemplateGenerator));
        private static readonly PropertyKey<GraphicsProfile> GraphicsProfileKey = new PropertyKey<GraphicsProfile>("GraphicsProfile", typeof(NewGameTemplateGenerator));
        private static readonly PropertyKey<bool> IsHDRKey = new PropertyKey<bool>("IsHDR", typeof(NewGameTemplateGenerator));
        private static readonly PropertyKey<DisplayOrientation> OrientationKey = new PropertyKey<DisplayOrientation>("Orientation", typeof(NewGameTemplateGenerator));

        public static readonly NewGameTemplateGenerator Default = new NewGameTemplateGenerator();

        public static readonly Guid TemplateId = new Guid("81d2adea-37b1-4711-834c-0d73a05c206c");

        /// <summary>
        /// Sets the parameters required by this template when running in <see cref="TemplateGeneratorParameters.Unattended"/> mode.
        /// </summary>
        public static void SetParameters([NotNull] SessionTemplateGeneratorParameters parameters, [NotNull] IEnumerable<SelectedSolutionPlatform> platforms, GraphicsProfile graphicsProfile = GraphicsProfile.Level_10_0, bool isHDR = true, DisplayOrientation orientation = DisplayOrientation.Default, IEnumerable<UDirectory> assets = null)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            if (platforms == null) throw new ArgumentNullException(nameof(platforms));

            parameters.SetTag(PlatformsKey, new List<SelectedSolutionPlatform>(platforms));
            if (assets != null)
                parameters.SetTag(AssetsKey, new List<UDirectory>(assets));
            parameters.SetTag(IsHDRKey, isHDR);
            parameters.SetTag(GraphicsProfileKey, graphicsProfile);
            parameters.SetTag(OrientationKey, orientation);
        }

        public override bool IsSupportingTemplate(TemplateDescription templateDescription)
        {
            if (templateDescription == null) throw new ArgumentNullException(nameof(templateDescription));
            return templateDescription.Id == TemplateId;
        }

        public override async Task<bool> PrepareForRun(SessionTemplateGeneratorParameters parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            parameters.Validate();

            var defaultNamespace = Utilities.BuildValidNamespaceName(parameters.Name);
            if (!parameters.Unattended)
            {
                var window = new GameTemplateWindow(AssetRegistry.SupportedPlatforms, defaultNamespace);

                await window.ShowModal();

                if (window.Result == DialogResult.Cancel)
                    return false;

                parameters.Namespace = Utilities.BuildValidNamespaceName(window.Namespace);

                SetParameters(parameters, window.SelectedPlatforms, window.SelectedGraphicsProfile, window.IsHDR, window.Orientation, window.SelectedPackages);
            }

            return true;
        }

        protected override bool Generate(SessionTemplateGeneratorParameters parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            // Structure of files to generate:
            //     $Name$.xkpkg
            //     $Name$.targets
            //     Assets\
            //     $Name$.Game\
            //     $Name$.Windows\
            //     $Name$.Android\
            //     $Name$.iOS\

            var logger = parameters.Logger;
            var platforms = parameters.GetTag(PlatformsKey);
            var name = parameters.Name;
            var outputDirectory = parameters.OutputDirectory;
            var orientation = parameters.GetTag(OrientationKey);

            // Generate the package
            var package = NewPackageTemplateGenerator.GeneratePackage(parameters);

            // Generate projects for this package
            var session = parameters.Session;

            var sharedProfile = package.Profiles.FindSharedProfile();

            // Setup the assets folder
            Directory.CreateDirectory(UPath.Combine(outputDirectory, (UDirectory)"Assets/Shared"));

            //write gitignore
            WriteGitIgnore(parameters);

            var projectGameName = Utilities.BuildValidNamespaceName(name) + ".Game";

            var stepIndex = 0;
            var stepCount = platforms.Count + 1;

            // Log progress
            ProjectTemplateGeneratorHelper.Progress(logger, $"Generating {projectGameName}...", stepIndex++, stepCount);

            // Generate the Game library
            var projectGameReference = ProjectTemplateGeneratorHelper.GenerateTemplate(parameters, platforms, package, "ProjectLibrary.Game/ProjectLibrary.Game.ttproj", projectGameName, PlatformType.Shared, null, null, ProjectType.Library, orientation);
            projectGameReference.Type = ProjectType.Library;
            sharedProfile.ProjectReferences.Add(projectGameReference);

            session.Projects.Add(new Project2(session, Guid.NewGuid(), projectGameReference.Location.ToWindowsPath()) { Package = package });

            // Load missing references
            session.LoadMissingReferences(parameters.Logger);

            var previousCurrent = session.CurrentPackage;
            session.CurrentPackage = package;

            // Add Effects as an asset folder in order to load xksl
            sharedProfile.AssetFolders.Add(new AssetFolder(projectGameName + "/Effects"));

            // Generate executable projects for each platform
            ProjectTemplateGeneratorHelper.UpdatePackagePlatforms(parameters, platforms, orientation, projectGameReference.Id, name, package, false);

            // Add asset packages
            CopyAssetPacks(parameters);

            // Load assets from HDD
            package.LoadTemporaryAssets(logger);

            // Validate assets
            package.ValidateAssets(true, false, logger);

            // Setup GraphicsCompositor using DefaultGraphicsCompositor
            var graphicsProfile = parameters.GetTag(GraphicsProfileKey);
            var defaultCompositorUrl = graphicsProfile >= GraphicsProfile.Level_10_0 ? XenkoPackageUpgrader.DefaultGraphicsCompositorLevel10Url : XenkoPackageUpgrader.DefaultGraphicsCompositorLevel9Url;
            var defaultCompositor = session.FindAsset(defaultCompositorUrl);

            var graphicsCompositor = new AssetItem("GraphicsCompositor", defaultCompositor.CreateDerivedAsset());
            package.Assets.Add(graphicsCompositor);
            graphicsCompositor.IsDirty = true;

            // Setup GameSettingsAsset
            var gameSettingsAsset = GameSettingsFactory.Create();

            gameSettingsAsset.GetOrCreate<EditorSettings>().RenderingMode = parameters.GetTag(IsHDRKey) ? RenderingMode.HDR : RenderingMode.LDR;
            gameSettingsAsset.GraphicsCompositor = AttachedReferenceManager.CreateProxyObject<GraphicsCompositor>(graphicsCompositor.ToReference());

            var renderingSettings = gameSettingsAsset.GetOrCreate<RenderingSettings>();
            renderingSettings.DefaultGraphicsProfile = parameters.GetTag(GraphicsProfileKey);
            renderingSettings.DisplayOrientation = (RequiredDisplayOrientation)orientation;

            var gameSettingsAssetItem = new AssetItem(GameSettingsAsset.GameSettingsLocation, gameSettingsAsset);
            package.Assets.Add(gameSettingsAssetItem);
            gameSettingsAssetItem.IsDirty = true;

            // Add assets to the package
            AddAssets(parameters, package, projectGameReference, projectGameName);

            // Log done
            ProjectTemplateGeneratorHelper.Progress(logger, "Done", stepCount, stepCount);

            session.CurrentPackage = previousCurrent;
            return true;
        }

        private void AddAssets(SessionTemplateGeneratorParameters parameters, Package package, ProjectReference projectGameReference, string projectGameName)
        {
            // Sets a default scene
            CreateAndSetNewScene(parameters, package, projectGameReference, projectGameName);
        }

        /// <summary>
        /// Creates default scene, with a ground plane, sphere, directional light and camera.
        /// If graphicsProfile is 10+, add cubemap light, otherwise ambient light.
        /// Also properly setup graphics pipeline depending on if HDR is set or not
        /// </summary>
        /// <param name="parameters">The parameters.</param>
        /// <param name="package">The package in which to create these assets.</param>
        private void CreateAndSetNewScene(SessionTemplateGeneratorParameters parameters, Package package, ProjectReference projectGameReference, string projectGameName)
        {
            var logger = parameters.Logger;

            var graphicsProfile = parameters.GetTag(GraphicsProfileKey);
            var isHDR = parameters.GetTag(IsHDRKey);

            if (graphicsProfile < GraphicsProfile.Level_10_0)
                isHDR = false;

            // Create the material for the sphere
            var sphereMaterial = new MaterialAsset
            {
                Attributes =
                {
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(Color.FromBgra(0xFF8C8C8C))),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                }
            };

            if (isHDR)
            {
                // Create HDR part of material
                sphereMaterial.Attributes.Specular = new MaterialMetalnessMapFeature(new ComputeFloat(1.0f));
                sphereMaterial.Attributes.SpecularModel = new MaterialSpecularMicrofacetModelFeature();
                sphereMaterial.Attributes.MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.65f));
            }

            var sphereMaterialAssetItem = new AssetItem("Sphere Material", sphereMaterial);
            package.Assets.Add(sphereMaterialAssetItem);
            sphereMaterialAssetItem.IsDirty = true;

            // Create the sphere model
            var sphereModel = new ProceduralModelAsset
            {
                Type = new SphereProceduralModel
                {
                    MaterialInstance = { Material = AttachedReferenceManager.CreateProxyObject<Material>(sphereMaterialAssetItem.Id, sphereMaterialAssetItem.Location) },
                    Tessellation = 30,
                },
            };
            var sphereModelAssetItem = new AssetItem("Sphere", sphereModel);
            package.Assets.Add(sphereModelAssetItem);
            sphereModelAssetItem.IsDirty = true;

            // Create sphere entity
            var sphereEntity = new Entity("Sphere") { new ModelComponent(AttachedReferenceManager.CreateProxyObject<Model>(sphereModelAssetItem.Id, sphereModelAssetItem.Location)) };
            sphereEntity.Transform.Position = new Vector3(0.0f, 0.5f, 0.0f);

            // Create the material for the ground
            var groundMaterial = new MaterialAsset
            {
                Attributes =
                {
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(Color.FromBgra(0xFF242424))),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                }
            };

            if (isHDR)
            {
                // Create HDR part of material
                groundMaterial.Attributes.Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0.0f));
                groundMaterial.Attributes.SpecularModel = new MaterialSpecularMicrofacetModelFeature();
                groundMaterial.Attributes.MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.1f));
            }

            var groundMaterialAssetItem = new AssetItem("Ground Material", groundMaterial);
            package.Assets.Add(groundMaterialAssetItem);
            groundMaterialAssetItem.IsDirty = true;

            // Create the ground model
            var groundModel = new ProceduralModelAsset
            {
                Type = new PlaneProceduralModel
                {
                    Size = new Vector2(10.0f, 10.0f),
                    MaterialInstance = { Material = AttachedReferenceManager.CreateProxyObject<Material>(groundMaterialAssetItem.Id, groundMaterialAssetItem.Location) },
                },
            };
            var groundModelAssetItem = new AssetItem("Ground", groundModel);
            package.Assets.Add(groundModelAssetItem);
            groundModelAssetItem.IsDirty = true;

            // Create the ground entity
            var groundEntity = new Entity("Ground") { new ModelComponent(AttachedReferenceManager.CreateProxyObject<Model>(groundModelAssetItem.Id, groundModelAssetItem.Location)) };

            // Copy file in Resources
            var skyboxFilename = (UFile)(isHDR ? "skybox_texture_hdr.dds" : "skybox_texture_ldr.dds");
            try
            {
                var resources = UPath.Combine(parameters.OutputDirectory, (UDirectory)"Resources");
                Directory.CreateDirectory(resources.ToWindowsPath());

                // TODO: Hardcoded due to the fact that part of the template is in another folder in dev build
                // We might want to extend TemplateFolder to support those cases
                var dataDirectory = ProjectTemplateGeneratorHelper.GetTemplateDataDirectory(parameters.Description);
                var skyboxFullPath = UPath.Combine(dataDirectory, skyboxFilename).ToWindowsPath();
                File.Copy(skyboxFullPath, UPath.Combine(resources, skyboxFilename).ToWindowsPath(), true);
            }
            catch (Exception ex)
            {
                logger.Error("Unexpected exception while copying cubemap", ex);
            }

            // Create the texture asset
            var skyboxTextureAsset = new TextureAsset { Source = Path.Combine(@"..\..\Resources", skyboxFilename), IsCompressed = isHDR, Type = new ColorTextureType { UseSRgbSampling = false } };
            var skyboxTextureAssetItem = new AssetItem("Skybox texture", skyboxTextureAsset);
            package.Assets.Add(skyboxTextureAssetItem);
            skyboxTextureAssetItem.IsDirty = true;

            // Create the skybox asset
            var skyboxAsset = new SkyboxAsset
            {
                CubeMap = AttachedReferenceManager.CreateProxyObject<Texture>(skyboxTextureAssetItem.Id, skyboxTextureAssetItem.Location)
            };
            var skyboxAssetItem = new AssetItem("Skybox", skyboxAsset);
            package.Assets.Add(skyboxAssetItem);
            skyboxAssetItem.IsDirty = true;

            // Create the scene
            var defaultSceneAsset = isHDR ? SceneHDRFactory.Create() : SceneLDRFactory.Create();

            defaultSceneAsset.Hierarchy.Parts.Add(new EntityDesign(groundEntity));
            defaultSceneAsset.Hierarchy.RootParts.Add(groundEntity);

            defaultSceneAsset.Hierarchy.Parts.Add(new EntityDesign(sphereEntity));
            defaultSceneAsset.Hierarchy.RootParts.Add(sphereEntity);

            var sceneAssetItem = new AssetItem(GameSettingsAsset.DefaultSceneLocation, defaultSceneAsset);
            package.Assets.Add(sceneAssetItem);
            sceneAssetItem.IsDirty = true;

            // Sets the scene created as default in the shared profile
            var gameSettingsAsset = package.Assets.Find(GameSettingsAsset.GameSettingsLocation);
            if (gameSettingsAsset != null)
            {
                ((GameSettingsAsset)gameSettingsAsset.Asset).DefaultScene = AttachedReferenceManager.CreateProxyObject<Scene>(sceneAssetItem.Id, sceneAssetItem.Location);
                gameSettingsAsset.IsDirty = true;
            }

            var skyboxEntity = defaultSceneAsset.Hierarchy.Parts.Select(x => x.Value.Entity).Single(x => x.Name == SceneBaseFactory.SkyboxEntityName);
            skyboxEntity.Get<BackgroundComponent>().Texture = skyboxAsset.CubeMap;
            if (isHDR)
            {
                skyboxEntity.Get<LightComponent>().Type = new LightSkybox
                {
                    Skybox = AttachedReferenceManager.CreateProxyObject<Skybox>(skyboxAssetItem.Id, skyboxAssetItem.Location)
                };
            }

            var cameraEntity = defaultSceneAsset.Hierarchy.Parts.Select(x => x.Value.Entity).Single(x => x.Name == SceneBaseFactory.CameraEntityName);
            var graphicsCompositor = package.Assets.Select(x => x.Asset).OfType<GraphicsCompositorAsset>().Single();
            cameraEntity.Components.Get<CameraComponent>().Slot = graphicsCompositor.Cameras.Single().ToSlotId();

            // Let's add camera script
            CreateCameraScript(parameters, package, projectGameReference, projectGameName, cameraEntity, sceneAssetItem);
        }

        /// <summary>
        /// Copy any referenced asset packages to the project's folder
        /// </summary>
        private static void CopyAssetPacks(SessionTemplateGeneratorParameters parameters)
        {
            var logger = parameters.Logger;

            var installDir = DirectoryHelper.GetInstallationDirectory("Xenko");
            var assetPackagesDir = (DirectoryHelper.IsRootDevDirectory(installDir)) ?
                UDirectory.Combine(installDir, @"samples\Templates\Packs") :
                UDirectory.Combine(ProjectTemplateGeneratorHelper.GetTemplateDataDirectory(parameters.Description).GetParent(), @"Samples\Templates\Packs");
            var assetPacks = parameters.TryGetTag(AssetsKey);
            if (assetPacks == null)
                return;

            try
            {
                var outAssetDir = UPath.Combine(parameters.OutputDirectory, (UDirectory)"Assets");
                var outResourceDir = UPath.Combine(parameters.OutputDirectory, (UDirectory)"Resources");

                foreach (var uDirectory in assetPacks)
                {
                    var packageDir = UDirectory.Combine(assetPackagesDir, uDirectory);

                    var inputAssetDir = UDirectory.Combine(packageDir, "Assets");
                    foreach (var directory in FileUtility.EnumerateDirectories(inputAssetDir, SearchDirection.Down))
                    {
                        foreach (var file in directory.GetFiles())
                        {
                            var relativeFile = new UFile(file.FullName).MakeRelative(inputAssetDir);

                            var outputFile = UPath.Combine(outAssetDir, relativeFile);
                            var outputFileDirectory = outputFile.GetParent();
                            if (!Directory.Exists(outputFileDirectory))
                            {
                                Directory.CreateDirectory(outputFileDirectory);
                            }

                            File.Copy(file.FullName, outputFile, true);
                        }
                    }

                    var inputResourceDir = UDirectory.Combine(packageDir, "Resources");
                    foreach (var directory in FileUtility.EnumerateDirectories(inputResourceDir, SearchDirection.Down))
                    {
                        foreach (var file in directory.GetFiles())
                        {
                            var relativeFile = new UFile(file.FullName).MakeRelative(inputResourceDir);

                            var outputFile = UPath.Combine(outResourceDir, relativeFile);
                            {   // Create the output directory if needed
                                var outputFileDirectory = outputFile.GetParent();
                                if (!Directory.Exists(outputFileDirectory))
                                {
                                    Directory.CreateDirectory(outputFileDirectory);
                                }
                            }

                            File.Copy(file.FullName, outputFile, true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error("Unexpected exception while copying asset packages", ex);
            }

        }

        private void CreateCameraScript(SessionTemplateGeneratorParameters parameters, Package package, ProjectReference projectGameReference, string projectGameName, Entity cameraEntity, AssetItem sceneAssetItem)
        {
            var logger = parameters.Logger;

            // Create camera script
            var cameraScriptTemplate = TemplateManager.FindTemplates(package.Session).OfType<TemplateAssetDescription>().FirstOrDefault(x => x.DefaultOutputName == CameraScriptDefaultOutputName);
            if (cameraScriptTemplate == null)
                throw new InvalidOperationException($"Could not find template for script '{CameraScriptDefaultOutputName}'");

            var cameraScriptParameters = new AssetTemplateGeneratorParameters(projectGameName)
            {
                Name = cameraScriptTemplate.DefaultOutputName,
                Description = cameraScriptTemplate,
                Namespace = parameters.Namespace,
                Package = package,
                Logger = logger,
                Unattended = true,
            };
            ScriptTemplateGenerator.SetClassName(cameraScriptParameters, cameraScriptTemplate.DefaultOutputName);
            if (!ScriptTemplateGenerator.Default.PrepareForRun(cameraScriptParameters).Result || !ScriptTemplateGenerator.Default.Run(cameraScriptParameters))
            {
                throw new InvalidOperationException($"Could not create script '{CameraScriptDefaultOutputName}'");
            }

            // Force save after having created the script
            // Note: We do that AFTER GameSettings is dirty, otherwise it would ask for an assembly reload (game settings saved might mean new graphics API)
            SaveSession(parameters);

            parameters.Logger.Verbose("Restore NuGet packages...");
            VSProjectHelper.RestoreNugetPackages(parameters.Logger, parameters.Session.SolutionPath).Wait();

            logger.Verbose("Compiling game assemblies...");
            parameters.Session.UpdateAssemblyReferences(logger);

            //if (package.State < PackageState.DependenciesReady)
            //{
            //    logger.Warning("Assembly references were not compiled properly");
            //    return;
            //}
            logger.Verbose("Game assemblies compiled...");

            // Create the Camera script in Camera entity
            // Since we rely on lot of string check, added some null checking and try/catch to not crash the apps in case something went wrong
            // We only emit warnings rather than errors, so that the user could continue
            try
            {
                var gameAssembly = package.LoadedAssemblies.FirstOrDefault(x => x.ProjectReference == projectGameReference)?.Assembly;
                if (gameAssembly == null)
                {
                    logger.Warning("Can't load Game assembly");
                    return;
                }

                var cameraScriptType = package.LoadedAssemblies.First(x => x.ProjectReference == projectGameReference).Assembly.GetType($"{parameters.Namespace}.{CameraScriptDefaultOutputName}");
                if (cameraScriptType == null)
                {
                    logger.Warning($"Could not find script '{CameraScriptDefaultOutputName}' in Game assembly");
                    return;
                }

                cameraEntity.Add((EntityComponent)Activator.CreateInstance(cameraScriptType));
                sceneAssetItem.IsDirty = true;
            }
            catch (Exception e)
            {
                logger.Warning($"Could not instantiate {CameraScriptDefaultOutputName} script", e);
            }
        }
    }
}
