// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

using Xenko.Core.Assets;
using Xenko.Core.Assets.Templates;
using Xenko.Core;
using Xenko.Core.Diagnostics;
using Xenko.Core.Extensions;
using Xenko.Core.IO;
using Xenko.Core.Presentation.Services;
using Xenko.Core.Presentation.Windows;
using Xenko.Core.ProjectTemplating;
using Xenko.Assets.Templates;
using Xenko.Graphics;

namespace Xenko.Assets.Presentation.Templates
{
    /// <summary>
    /// This generator will update the platforms of a given package.
    /// </summary>
    public class UpdatePlatformsTemplateGenerator : TemplateGeneratorBase<PackageTemplateGeneratorParameters>
    {
        private static readonly PropertyKey<List<SelectedSolutionPlatform>> PlatformsKey = new PropertyKey<List<SelectedSolutionPlatform>>("Platforms", typeof(UpdatePlatformsTemplateGenerator));
        private static readonly PropertyKey<bool> ForcePlatformRegenerationKey = new PropertyKey<bool>("ForcePlatformRegeneration", typeof(UpdatePlatformsTemplateGenerator));
        private static readonly PropertyKey<DisplayOrientation> OrientationKey = new PropertyKey<DisplayOrientation>("Orientation", typeof(UpdatePlatformsTemplateGenerator));

        public static readonly UpdatePlatformsTemplateGenerator Default = new UpdatePlatformsTemplateGenerator();

        public static readonly Guid TemplateId = new Guid("446B52D3-A6A8-4274-A357-736ADEA87321");

        public override bool IsSupportingTemplate(TemplateDescription templateDescription)
        {
            if (templateDescription == null) throw new ArgumentNullException(nameof(templateDescription));
            return templateDescription.Id == TemplateId;
        }

        public static void SetPlatforms(PackageTemplateGeneratorParameters parameters, IEnumerable<SelectedSolutionPlatform> platforms) => parameters.SetTag(PlatformsKey, new List<SelectedSolutionPlatform>(platforms));

        public static void SetForcePlatformRegeneration(PackageTemplateGeneratorParameters parameters, bool value) => parameters.SetTag(ForcePlatformRegenerationKey, value);

        public static void SetOrientation(PackageTemplateGeneratorParameters parameters, DisplayOrientation displayOrientation) => parameters.SetTag(OrientationKey, displayOrientation);

        public override async Task<bool> PrepareForRun(PackageTemplateGeneratorParameters parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            parameters.Validate();

            var package = parameters.Package;
            var gameSettingsAsset = package.GetGameSettingsAsset();
            if (gameSettingsAsset == null)
            {
                parameters.Logger.Error($"Could not find game settings asset at location [{GameSettingsAsset.GameSettingsLocation}]");
                return false;
            }

            // If there are no executable/shared projects in this package, we can't work on it
            var sharedProfile = package.Profiles.FindSharedProfile();
            var existingPlatformTypesWithExe = new HashSet<PlatformType>(package.Profiles.Where(profile => profile.Platform != PlatformType.Shared && profile.ProjectReferences.Any(projectRef => projectRef.Type == ProjectType.Executable)).Select(item => item.Platform));
            if (sharedProfile == null || existingPlatformTypesWithExe.Count == 0)
            {
                parameters.Logger.Error("The selected package does not contain a shared profile with executable projects");
                return false;
            }

            var defaultNamespace = GetDefaultNamespace(parameters);

            if (parameters.Unattended)
            {
                if (!parameters.HasTag(PlatformsKey))
                    throw new InvalidOperationException("The platforms must be set with SetPlatforms before calling PrepareForRun if DontAskForPlatforms is true.");
                parameters.SetTag(ForcePlatformRegenerationKey, true);
            }
            else
            {
                var window = new UpdatePlatformsWindow(existingPlatformTypesWithExe)
                {
                    ForcePlatformRegenerationVisible = parameters.TryGetTag(ForcePlatformRegenerationKey)
                };

                await window.ShowModal();

                if (window.Result == DialogResult.Cancel)
                    return false;

                parameters.SetTag(PlatformsKey, new List<SelectedSolutionPlatform>(window.SelectedPlatforms));
                parameters.SetTag(ForcePlatformRegenerationKey, window.ForcePlatformRegeneration);
            }
            parameters.SetTag(OrientationKey, (DisplayOrientation)gameSettingsAsset.GetOrCreate<RenderingSettings>().DisplayOrientation);
            parameters.Namespace = defaultNamespace;

            return true;
        }

        public sealed override bool Run(PackageTemplateGeneratorParameters parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            var logger = parameters.Logger;            
            var name = parameters.Name;
            var package = parameters.Package;
            
            // Generate executable projects for each platform
            var sharedProfile = package.Profiles.FindSharedProfile();

            // TODO: this is not a safe way to get a game project see PDX-1128
            var gameProjectRef = FindSharedGameProject(package, sharedProfile, logger);

            if (gameProjectRef == null)
            {
                return false;
            }

            // We need to patch the previous game project in order to regenerate property groups
            var templateGameLibrary = PrepareTemplate(parameters, "ProjectLibrary.Game/ProjectLibrary.Game.ttproj", PlatformType.Shared, null, null, ProjectType.Library);
            var options = ProjectTemplateGeneratorHelper.GetOptions(parameters);
            var newGameTargetFrameworks = templateGameLibrary.GeneratePart(@"..\Common.TargetFrameworks.targets.t4", logger, options);
            PatchGameProject(newGameTargetFrameworks, gameProjectRef.Location.FullPath);

            // Generate missing platforms
            bool forceGenerating = parameters.GetTag(ForcePlatformRegenerationKey);
            ProjectTemplateGeneratorHelper.UpdatePackagePlatforms(parameters, parameters.GetTag(PlatformsKey), parameters.GetTag(OrientationKey), gameProjectRef.Id, name, package, forceGenerating);

            // Save the session after the update
            // FIXME: Saving like this is not supported anymore - let's not save for now be we should provide a proper way to save!
            //var result = package.Session.Save();
            //result.CopyTo(logger);

            // Log done
            ProjectTemplateGeneratorHelper.Progress(logger, "Done", 1, 1);
            return true;
        }

        private static string GetDefaultNamespace(PackageTemplateGeneratorParameters parameters)
        {
            var logger = parameters.Logger;
            var package = parameters.Package;

            var defaultNamespace = package.Meta.RootNamespace;

            if (string.IsNullOrWhiteSpace(defaultNamespace))
            {
                // Get the shared profile
                var sharedProfile = package.Profiles.FindSharedProfile();
                if (sharedProfile != null)
                {
                    // Get the game project
                    var gameProjectRef = FindSharedGameProject(package, sharedProfile, logger);
                    if (gameProjectRef != null)
                    {

                        try
                        {
                            var project = VSProjectHelper.LoadProject(gameProjectRef.Location);
                            defaultNamespace = project.GetPropertyValue("RootNamespace");
                        }
                        catch (Exception e)
                        {
                            e.Ignore();
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(defaultNamespace))
            {
                defaultNamespace = parameters.Name.Replace(' ', '_');
            }

            return defaultNamespace;
        }

        private static ProjectTemplate PrepareTemplate(PackageTemplateGeneratorParameters parameters, UFile templateRelativePath, PlatformType platformType, string currentProfile, GraphicsPlatform? graphicsPlatform, ProjectType projectType)
        {
            ProjectTemplateGeneratorHelper.AddOption(parameters, "Platforms", parameters.GetTag(PlatformsKey).Select(x => x.Platform).ToList());
            ProjectTemplateGeneratorHelper.AddOption(parameters, "CurrentPlatform", platformType);
            ProjectTemplateGeneratorHelper.AddOption(parameters, "CurrentProfile", currentProfile);
            ProjectTemplateGeneratorHelper.AddOption(parameters, "Orientation", parameters.GetTag(OrientationKey));
            var package = parameters.Package;
            return ProjectTemplateGeneratorHelper.PrepareTemplate(parameters, package, templateRelativePath, platformType, graphicsPlatform, projectType);
        }

        private static ProjectReference FindSharedGameProject(Package package, PackageProfile sharedProfile, ILogger logger)
        {
            if (sharedProfile == null) throw new ArgumentNullException(nameof(sharedProfile));

            // TODO: this is not a reliable way to get a game project see PDX-1128
            var gameProjectRef = sharedProfile.ProjectReferences.FirstOrDefault(projectRef => projectRef.Type == ProjectType.Library && projectRef.Location.FullPath.EndsWith("Game.csproj", StringComparison.InvariantCultureIgnoreCase));
            if (gameProjectRef == null)
            {
                logger.Error($"Unable to find the game project reference from the package [{package.Meta.Name}]");
                return null;
            }
            return gameProjectRef;
        }

        private static bool MatchPropertyGroup(XElement element)
        {
            return element.Name.LocalName == "PropertyGroup";
        }

        private static void PatchGameProject(string partialContent, string filePath)
        {
            bool TargetFrameworkFilter(XElement x) => x.Name == "TargetFramework" || x.Name == "TargetFrameworks";

            var content = "<Project><PropertyGroup>" +
                          partialContent + "</PropertyGroup></Project>";
            var docPatch = XDocument.Load(new StringReader(content));

            var docToModify = XDocument.Load(new StringReader(File.ReadAllText(filePath)));

            var newPropertyGroups = docPatch.Root.Elements().Where(MatchPropertyGroup).ToList();

            var currentPropertyGroups = docToModify.Root.Elements().Where(MatchPropertyGroup).ToList();
            var targetFrameworkReplaced = false;
            foreach (var property in currentPropertyGroups.SelectMany(x => x.Elements()).ToList())
            {
                if (TargetFrameworkFilter(property))
                {
                    if (!targetFrameworkReplaced)
                    {
                        property.ReplaceWith(newPropertyGroups.Elements().Where(TargetFrameworkFilter));
                        targetFrameworkReplaced = true;
                    }
                    else
                    {
                        property.Remove();
                    }
                }
            }

            docToModify.Save(filePath);
        }
    }
}