using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Build.Evaluation;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using NuGet.Frameworks;
using NuGet.ProjectManagement;
using VSLangProj;
using VsWebSite;
using EnvDTEProject = EnvDTE.Project;
using EnvDTEProjectItem = EnvDTE.ProjectItem;
using EnvDTEProjectItems = EnvDTE.ProjectItems;
using EnvDTEProperty = EnvDTE.Property;
using MicrosoftBuildEvaluationProject = Microsoft.Build.Evaluation.Project;
using TaskBool = System.Threading.Tasks.Task<bool>;
using TaskEnvDTEProjectItem = System.Threading.Tasks.Task<EnvDTE.ProjectItem>;
using TaskEnvDTEProjectItems = System.Threading.Tasks.Task<EnvDTE.ProjectItems>;
using TaskIEnumerableEnvDTEProjectItem = System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<EnvDTE.ProjectItem>>;

namespace NuGet.PackageManagement.VisualStudio
{
    public static class EnvDTEProjectUtility
    {
        #region Constants and Statics
        public const string NuGetSolutionSettingsFolder = ".nuget";
        public const string PackageReferenceFile = "packages.config";

        private static readonly HashSet<string> SupportedProjectTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NuGetVSConstants.WebSiteProjectTypeGuid,
            NuGetVSConstants.CsharpProjectTypeGuid,
            NuGetVSConstants.VbProjectTypeGuid,
            NuGetVSConstants.CppProjectTypeGuid,
            NuGetVSConstants.JsProjectTypeGuid,
            NuGetVSConstants.CpsProjectTypeGuid,
            NuGetVSConstants.FsharpProjectTypeGuid,
            NuGetVSConstants.NemerleProjectTypeGuid,
            NuGetVSConstants.WixProjectTypeGuid,
            NuGetVSConstants.SynergexProjectTypeGuid,
            NuGetVSConstants.NomadForVisualStudioProjectTypeGuid,
            NuGetVSConstants.TDSProjectTypeGuid,
            NuGetVSConstants.DxJsProjectTypeGuid,
            NuGetVSConstants.DeploymentProjectTypeGuid
        };

        public const string WebConfig = "web.config";
        public const string AppConfig = "app.config";
        private const string BinFolder = "Bin";

        private static readonly Dictionary<string, string> KnownNestedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "web.debug.config", "web.config" },
            { "web.release.config", "web.config" }
        };

        private static readonly HashSet<string> UnsupportedProjectTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                                                                            NuGetVSConstants.LightSwitchProjectTypeGuid,
                                                                            NuGetVSConstants.InstallShieldLimitedEditionTypeGuid
                                                                        };

        private static readonly IEnumerable<string> FileKinds = new[] { NuGetVSConstants.VsProjectItemKindPhysicalFile, NuGetVSConstants.VsProjectItemKindSolutionItem };
        private static readonly IEnumerable<string> FolderKinds = new[] { NuGetVSConstants.VsProjectItemKindPhysicalFolder, NuGetVSConstants.TDSItemTypeGuid };

        // List of project types that cannot have references added to them
        private static readonly string[] UnsupportedProjectTypesForAddingReferences = new[]
            {
                NuGetVSConstants.WixProjectTypeGuid,
                NuGetVSConstants.CppProjectTypeGuid,
            };

        // List of project types that cannot have binding redirects added
        private static readonly string[] UnsupportedProjectTypesForBindingRedirects = new[]
            {
                NuGetVSConstants.WixProjectTypeGuid,
                NuGetVSConstants.JsProjectTypeGuid,
                NuGetVSConstants.NemerleProjectTypeGuid,
                NuGetVSConstants.CppProjectTypeGuid,
                NuGetVSConstants.SynergexProjectTypeGuid,
                NuGetVSConstants.NomadForVisualStudioProjectTypeGuid,
                NuGetVSConstants.DxJsProjectTypeGuid
            };

        private static readonly char[] PathSeparatorChars = new[] { Path.DirectorySeparatorChar };
        #endregion // Constants and Statics

        #region Get "Project" Information
        /// <summary>
        /// Returns the full path of the project directory.
        /// </summary>
        /// <param name="envDTEProject">The project.</param>
        /// <returns>The full path of the project directory.</returns>
        internal static string GetFullPath(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Debug.Assert(envDTEProject != null);
            if (IsUnloaded(envDTEProject))
            {
                // To get the directory of an unloaded project, we use the UniqueName property,
                // which is the path of the project file relative to the solution directory.
                var solutionDirectory = Path.GetDirectoryName(envDTEProject.DTE.Solution.FullName);
                var projectFileFullPath = Path.Combine(solutionDirectory, envDTEProject.UniqueName);
                return Path.GetDirectoryName(projectFileFullPath);
            }

            // Attempt to determine the project path using the available EnvDTE.Project properties.
            // Project systems using async load such as CPS may not have all properties populated 
            // for start up scenarios such as VS Templates. In these cases we need to fallback 
            // until we can find one containing the full path.

            // FullPath
            string fullPath = GetPropertyValue<string>(envDTEProject, "FullPath");

            if (!String.IsNullOrEmpty(fullPath))
            {
                // Some Project System implementations (JS metro app) return the project 
                // file as FullPath. We only need the parent directory
                if (File.Exists(fullPath))
                {
                    return Path.GetDirectoryName(fullPath);
                }

                return fullPath;
            }

            // C++ projects do not have FullPath property, but do have ProjectDirectory one.
            string projectDirectory = GetPropertyValue<string>(envDTEProject, "ProjectDirectory");

            if (!String.IsNullOrEmpty(projectDirectory))
            {
                return projectDirectory;
            }

            // FullName
            if (!String.IsNullOrEmpty(envDTEProject.FullName))
            {
                return Path.GetDirectoryName(envDTEProject.FullName);
            }

            Debug.Fail("Unable to find the project path");

            return null;
        }

        internal static References GetReferences(EnvDTEProject project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            dynamic projectObj = project.Object;
            var references = (References)projectObj.References;
            projectObj = null;
            return references;
        }

        public static bool IsSupported(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Debug.Assert(envDTEProject != null);

            if (SupportsINuGetProjectSystem(envDTEProject))
            {
                return true;
            }

            return envDTEProject.Kind != null && SupportedProjectTypes.Contains(envDTEProject.Kind) && !IsSharedProject(envDTEProject);
        }

        internal static bool IsSolutionFolder(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return envDTEProject.Kind != null && envDTEProject.Kind.Equals(NuGetVSConstants.VsProjectItemKindSolutionFolder, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnloaded(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            return NuGetVSConstants.UnloadedProjectTypeGuid.Equals(envDTEProject.Kind, StringComparison.OrdinalIgnoreCase);
        }

        private static T GetPropertyValue<T>(EnvDTEProject envDTEProject, string propertyName)
        {
            // Should be on the UI thread

            Debug.Assert(envDTEProject != null);
            if (envDTEProject.Properties == null)
            {
                // this happens in unit tests
                return default(T);
            }

            try
            {
                EnvDTEProperty property = envDTEProject.Properties.Item(propertyName);
                if (property != null)
                {
                    // REVIEW: Should this cast or convert?
                    return (T)property.Value;
                }
            }
            catch (ArgumentException)
            {
            }
            return default(T);
        }

        internal static AssemblyReferences GetAssemblyReferences(EnvDTEProject project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            dynamic projectObj = project.Object;
            var references = (AssemblyReferences)projectObj.References;
            projectObj = null;
            return references;
        }

        /// <summary>
        /// Returns the full path of the packages config file associated with the project.
        /// </summary>
        /// <param name="envDTEProject">The project.</param>
        internal static string GetPackagesConfigFullPath(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Debug.Assert(envDTEProject != null);
            var projectDirectory = GetFullPath(envDTEProject);

            var packagesConfigFullPath = Path.Combine(
                projectDirectory ?? String.Empty,
                NuGet.ProjectManagement.Constants.PackageReferenceFile);

            return packagesConfigFullPath;
        }

        /// <summary>
        /// Returns the full path of packages.<projectName>.config
        /// For example, if project is called "ConsoleApp1", return value is packages.ConsoleApp1.config
        /// </summary>
        /// <param name="envDTEProject">The project.</param>
        internal static string GetPackagesConfigWithProjectNameFullPath(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Debug.Assert(envDTEProject != null);
            var projectDirectory = GetFullPath(envDTEProject);

            var packagesConfigWithProjectNameFullPath = Path.Combine(
                projectDirectory ?? String.Empty,
                "packages." + GetName(envDTEProject) + ".config");

            return packagesConfigWithProjectNameFullPath;
        }

        internal static string GetName(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string name = envDTEProject.Name;
            if (IsJavaScriptProject(envDTEProject))
            {
                // The JavaScript project initially returns a "(loading..)" suffix to the project Name.
                // Need to get rid of it for the rest of NuGet to work properly.
                // TODO: Follow up with the VS team to see if this will be fixed eventually
                const string suffix = " (loading...)";
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - suffix.Length);
                }
            }
            return name;
        }

        public static string GetDisplayName(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string name = GetCustomUniqueName(envDTEProject);
            if (IsWebSite(envDTEProject))
            {
                name = PathHelper.SmartTruncate(name, 40);
            }
            return name;
        }

        /// <summary>
        /// This method is different from the GetName() method above in that for Website project, 
        /// it will always return the project name, instead of the full path to the website, when it uses Casini server.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "We can treat the website as running IISExpress if we can't get the WebSiteType property.")]
        internal static string GetProperName(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (IsWebSite(envDTEProject))
            {
                try
                {
                    // if the WebSiteType property of a WebSite project, it means the Website is configured to run with Casini, 
                    // as opposed to IISExpress. In which case, Project.Name will return the full path to the directory of the website. 
                    // We want to extract out the directory name only. 
                    object websiteType = envDTEProject.Properties.Item("WebSiteType").Value;
                    if (Convert.ToInt32(websiteType, CultureInfo.InvariantCulture) == 0)
                    {
                        // remove the trailing slash. 
                        string projectPath = envDTEProject.Name;
                        if (projectPath.Length > 0 && projectPath[projectPath.Length - 1] == Path.DirectorySeparatorChar)
                        {
                            projectPath = projectPath.Substring(0, projectPath.Length - 1);
                        }

                        // without the trailing slash, a directory looks like a file name. Hence, call GetFileName gives us the directory name.
                        return Path.GetFileName(projectPath);
                    }
                }
                catch (Exception)
                {
                    // ignore this exception if we can't get the WebSiteType property
                }
            }

            return GetName(envDTEProject);
        }

        internal static string GetUniqueName(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (IsWixProject(envDTEProject))
            {
                // Wix project doesn't offer UniqueName property
                return envDTEProject.FullName;
            }

            try
            {
                return envDTEProject.UniqueName;
            }
            catch (COMException)
            {
                return envDTEProject.FullName;
            }
        }

        /// <summary>
        /// Returns the unique name of the specified project including all solution folder names containing it.
        /// </summary>
        /// <remarks>
        /// This is different from the DTE Project.UniqueName property, which is the absolute path to the project file.
        /// </remarks>
        public static string GetCustomUniqueName(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (IsWebSite(envDTEProject))
            {
                // website projects always have unique name
                return envDTEProject.Name;
            }
            else
            {
                Stack<string> nameParts = new Stack<string>();

                EnvDTEProject cursor = envDTEProject;
                nameParts.Push(GetName(cursor));

                // walk up till the solution root
                while (cursor.ParentProjectItem != null && cursor.ParentProjectItem.ContainingProject != null)
                {
                    cursor = cursor.ParentProjectItem.ContainingProject;
                    nameParts.Push(GetName(cursor));
                }

                return String.Join("\\", nameParts);
            }
        }

        internal static bool IsExplicitlyUnsupported(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return envDTEProject.Kind == null || UnsupportedProjectTypes.Contains(envDTEProject.Kind);
        }

        public static bool IsParentProjectExplicitlyUnsupported(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (envDTEProject.ParentProjectItem == null || envDTEProject.ParentProjectItem.ContainingProject == null)
            {
                // this project is not a child of another project
                return false;
            }

            EnvDTEProject parentEnvDTEProject = envDTEProject.ParentProjectItem.ContainingProject;
            return IsExplicitlyUnsupported(parentEnvDTEProject);
        }

        /// <summary>
        /// Recursively retrieves all supported child projects of a virtual folder.
        /// </summary>
        /// <param name="project">The root container project</param>
        internal static IEnumerable<EnvDTE.Project> GetSupportedChildProjects(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsSolutionFolder(envDTEProject))
            {
                yield break;
            }

            var containerProjects = new Queue<EnvDTE.Project>();
            containerProjects.Enqueue(envDTEProject);

            while (containerProjects.Any())
            {
                var containerProject = containerProjects.Dequeue();
                foreach (EnvDTE.ProjectItem item in containerProject.ProjectItems)
                {
                    var nestedProject = item.SubProject;
                    if (nestedProject == null)
                    {
                        continue;
                    }
                    else if (IsSupported(nestedProject))
                    {
                        yield return nestedProject;
                    }
                    else if (IsSolutionFolder(nestedProject))
                    {
                        containerProjects.Enqueue(nestedProject);
                    }
                }
            }
        }

        internal static MicrosoftBuildEvaluationProject AsMicrosoftBuildEvaluationProject(EnvDTEProject envDTEproject)
        {
            // Need NOT be on the UI thread

            return ProjectCollection.GlobalProjectCollection.GetLoadedProjects(envDTEproject.FullName).FirstOrDefault() ??
                   ProjectCollection.GlobalProjectCollection.LoadProject(envDTEproject.FullName);
        }

        internal static NuGetFramework GetTargetNuGetFramework(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string targetFrameworkMoniker = GetTargetFrameworkString(envDTEProject);
            if (targetFrameworkMoniker != null)
            {
                var framework = NuGetFramework.Parse(targetFrameworkMoniker);
                //if the framework is .net core 4.5.1 return windows 8.1
                if (framework.Framework.Equals(FrameworkConstants.FrameworkIdentifiers.NetCore) && framework.Version.Equals(System.Version.Parse("4.5.1.0")))
                {
                    return new NuGetFramework(FrameworkConstants.FrameworkIdentifiers.Windows, System.Version.Parse("8.1"), framework.Profile, framework.Platform, framework.PlatformVersion);
                }
                //if the framework is .net core 4.5 return 8.0
                if (framework.Framework.Equals(FrameworkConstants.FrameworkIdentifiers.NetCore) && framework.Version.Equals(System.Version.Parse("4.5.0.0")))
                {
                    return new NuGetFramework(FrameworkConstants.FrameworkIdentifiers.Windows, System.Version.Parse("8.0"), framework.Profile, framework.Platform, framework.PlatformVersion);
                }
                return NuGetFramework.Parse(targetFrameworkMoniker);
            }

            return NuGetFramework.UnsupportedFramework;
        }

        private static string GetTargetFrameworkString(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            if (envDTEProject == null)
            {
                return null;
            }

            if (IsJavaScriptProject(envDTEProject))
            {
                // JavaScript apps do not have a TargetFrameworkMoniker property set.
                // We read the TargetPlatformIdentifier and TargetPlatformVersion instead

                string platformIdentifier = GetPropertyValue<string>(envDTEProject, "TargetPlatformIdentifier");
                string platformVersion = GetPropertyValue<string>(envDTEProject, "TargetPlatformVersion");

                // use the default values for JS if they were not given
                if (String.IsNullOrEmpty(platformVersion))
                    platformVersion = "0.0";

                if (String.IsNullOrEmpty(platformIdentifier))
                    platformIdentifier = "Windows";

                return String.Format(CultureInfo.InvariantCulture, "{0}, Version={1}", platformIdentifier, platformVersion);
            }

            if (IsNativeProject(envDTEProject))
            {
                // The C++ project does not have a TargetFrameworkMoniker property set. 
                // We hard-code the return value to Native.
                return "Native, Version=0.0";
            }

            string targetFramework = GetPropertyValue<string>(envDTEProject, "TargetFrameworkMoniker");

            // XNA project lies about its true identity, reporting itself as a normal .NET 4.0 project.
            // We detect it and changes its target framework to Silverlight4-WindowsPhone71
            if (".NETFramework,Version=v4.0".Equals(targetFramework, StringComparison.OrdinalIgnoreCase) &&
                IsXnaWindowsPhoneProject(envDTEProject))
            {
                return "Silverlight,Version=v4.0,Profile=WindowsPhone71";
            }

            return targetFramework;
        }

        internal static async TaskBool ContainsFile(EnvDTEProject envDTEProject, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.Equals(envDTEProject.Kind, NuGetVSConstants.WixProjectTypeGuid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(envDTEProject.Kind, NuGetVSConstants.NemerleProjectTypeGuid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(envDTEProject.Kind, NuGetVSConstants.FsharpProjectTypeGuid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(envDTEProject.Kind, NuGetVSConstants.JsProjectTypeGuid, StringComparison.OrdinalIgnoreCase))
            {
                // For Wix and Nemerle projects, IsDocumentInProject() returns not found
                // even though the file is in the project. So we use GetProjectItem()
                // instead. Nemerle is a high-level statically typed programming language for .NET platform
                // Note that pszMkDocument, the document moniker, passed to IsDocumentInProject(), must be a path to the file
                // for certain file-based project systems such as F#. And, not just a filename. For these project systems as well,
                // do the following
                EnvDTEProjectItem item = await GetProjectItemAsync(envDTEProject, path);
                return item != null;
            }
            else
            {
                IVsProject vsProject = (IVsProject)VsHierarchyUtility.ToVsHierarchy(envDTEProject);
                if (vsProject == null)
                {
                    return false;
                }
                int pFound;
                uint itemId;
                int hr = vsProject.IsDocumentInProject(path, out pFound, new VSDOCUMENTPRIORITY[0], out itemId);
                return ErrorHandler.Succeeded(hr) && pFound == 1;
            }
        }

        // Get the ProjectItems for a folder path
        public static async TaskEnvDTEProjectItems GetProjectItemsAsync(EnvDTEProject envDTEProject, string folderPath, bool createIfNotExists)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (String.IsNullOrEmpty(folderPath))
            {
                return envDTEProject.ProjectItems;
            }

            // Traverse the path to get at the directory
            string[] pathParts = folderPath.Split(PathSeparatorChars, StringSplitOptions.RemoveEmptyEntries);

            // 'cursor' can contain a reference to either a Project instance or ProjectItem instance. 
            // Both types have the ProjectItems property that we want to access.
            object cursor = envDTEProject;

            string fullPath = GetFullPath(envDTEProject);
            string folderRelativePath = String.Empty;

            foreach (string part in pathParts)
            {
                fullPath = Path.Combine(fullPath, part);
                folderRelativePath = Path.Combine(folderRelativePath, part);

                cursor = await GetOrCreateFolderAsync(envDTEProject, cursor, fullPath, folderRelativePath, part, createIfNotExists);
                if (cursor == null)
                {
                    return null;
                }
            }

            return GetProjectItems(cursor);
        }

        // 'parentItem' can be either a Project or ProjectItem
        private static async TaskEnvDTEProjectItem GetOrCreateFolderAsync(
            EnvDTEProject envDTEProject,
            object parentItem,
            string fullPath,
            string folderRelativePath,
            string folderName,
            bool createIfNotExists)
        {
            // Should be on the UI thread

            if (parentItem == null)
            {
                return null;
            }

            EnvDTEProjectItem subFolder;

            EnvDTEProjectItems envDTEProjectItems = GetProjectItems(parentItem);
            if (TryGetFolder(envDTEProjectItems, folderName, out subFolder))
            {
                // Get the sub folder
                return subFolder;
            }
            else if (createIfNotExists)
            {
                // The JS Metro project system has a bug whereby calling AddFolder() to an existing folder that
                // does not belong to the project will throw. To work around that, we have to manually include 
                // it into our project.
                if (IsJavaScriptProject(envDTEProject) && Directory.Exists(fullPath))
                {
                    bool succeeded = await IncludeExistingFolderToProjectAsync(envDTEProject, folderRelativePath);
                    if (succeeded)
                    {
                        // IMPORTANT: after including the folder into project, we need to get 
                        // a new EnvDTEProjecItems snapshot from the parent item. Otherwise, reusing 
                        // the old snapshot from above won't have access to the added folder.
                        envDTEProjectItems = GetProjectItems(parentItem);
                        if (TryGetFolder(envDTEProjectItems, folderName, out subFolder))
                        {
                            // Get the sub folder
                            return subFolder;
                        }
                    }
                    return null;
                }

                try
                {
                    return envDTEProjectItems.AddFromDirectory(fullPath);
                }
                catch (NotImplementedException)
                {
                    // This is the case for F#'s project system, we can't add from directory so we fall back
                    // to this impl
                    return envDTEProjectItems.AddFolder(folderName);
                }
            }

            return null;
        }

        private static bool TryGetFolder(EnvDTEProjectItems envDTEProjectItems, string name, out EnvDTEProjectItem envDTEProjectItem)
        {
            // Should be on the UI thread

            envDTEProjectItem = GetProjectItem(envDTEProjectItems, name, FolderKinds);

            return envDTEProjectItem != null;
        }

        private static async TaskBool IncludeExistingFolderToProjectAsync(EnvDTEProject envDTEProject, string folderRelativePath)
        {
            // Execute command to include the existing folder into project. Must do this on UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            IVsUIHierarchy projectHierarchy = (IVsUIHierarchy)VsHierarchyUtility.ToVsHierarchy(envDTEProject);

            uint itemId;
            int hr = projectHierarchy.ParseCanonicalName(folderRelativePath, out itemId);
            if (!ErrorHandler.Succeeded(hr))
            {
                return false;
            }

            hr = projectHierarchy.ExecCommand(
                            itemId,
                            ref VsMenus.guidStandardCommandSet2K,
                            (int)VSConstants.VSStd2KCmdID.INCLUDEINPROJECT,
                            0,
                            IntPtr.Zero,
                            IntPtr.Zero);

            return ErrorHandler.Succeeded(hr);
        }

        private static bool TryGetFile(EnvDTEProjectItems envDTEProjectItems, string name, out EnvDTEProjectItem envDTEProjectItem)
        {
            // Should be on the UI thread

            envDTEProjectItem = GetProjectItem(envDTEProjectItems, name, FileKinds);

            if (envDTEProjectItem == null)
            {
                // Try to get the nested project item
                return TryGetNestedFile(envDTEProjectItems, name, out envDTEProjectItem);
            }

            return envDTEProjectItem != null;
        }

        /// <summary>
        /// If we didn't find the project item at the top level, then we look one more level down.
        /// In VS files can have other nested files like foo.aspx and foo.aspx.cs or web.config and web.debug.config. 
        /// These are actually top level files in the file system but are represented as nested project items in VS.            
        /// </summary>
        private static bool TryGetNestedFile(EnvDTEProjectItems envDTEProjectItems, string name, out EnvDTEProjectItem envDTEProjectItem)
        {
            // Should be on the UI thread

            string parentFileName;
            if (!KnownNestedFiles.TryGetValue(name, out parentFileName))
            {
                parentFileName = Path.GetFileNameWithoutExtension(name);
            }

            // If it's not one of the known nested files then we're going to look up prefixes backwards
            // i.e. if we're looking for foo.aspx.cs then we look for foo.aspx then foo.aspx.cs as a nested file
            EnvDTEProjectItem parentEnvDTEProjectItem = GetProjectItem(envDTEProjectItems, parentFileName, FileKinds);

            if (parentEnvDTEProjectItem != null)
            {
                // Now try to find the nested file
                envDTEProjectItem = GetProjectItem(parentEnvDTEProjectItem.ProjectItems, name, FileKinds);
            }
            else
            {
                envDTEProjectItem = null;
            }

            return envDTEProjectItem != null;
        }

        private static EnvDTEProjectItem GetProjectItem(EnvDTEProjectItems envDTEProjectItems, string name, IEnumerable<string> allowedItemKinds)
        {
            // Should be on the UI thread

            try
            {
                EnvDTEProjectItem envDTEProjectItem = envDTEProjectItems.Item(name);
                if (envDTEProjectItem != null && allowedItemKinds.Contains(envDTEProjectItem.Kind, StringComparer.OrdinalIgnoreCase))
                {
                    return envDTEProjectItem;
                }
            }
            catch
            {
            }

            return null;
        }

        private static EnvDTEProjectItems GetProjectItems(object parent)
        {
            // Should be on the UI thread

            var envDTEProject = parent as EnvDTEProject;
            if (envDTEProject != null)
            {
                return envDTEProject.ProjectItems;
            }

            var envDTEProjectItem = parent as EnvDTEProjectItem;
            if (envDTEProjectItem != null)
            {
                return envDTEProjectItem.ProjectItems;
            }

            return null;
        }

        internal static async TaskEnvDTEProjectItem GetProjectItemAsync(EnvDTEProject envDTEProject, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string folderPath = Path.GetDirectoryName(path);
            string itemName = Path.GetFileName(path);

            EnvDTEProjectItems container = await GetProjectItemsAsync(envDTEProject, folderPath, createIfNotExists: false);

            EnvDTEProjectItem projectItem;
            // If we couldn't get the folder, or the child item doesn't exist, return null
            if (container == null ||
                (!TryGetFile(container, itemName, out projectItem) &&
                 !TryGetFolder(container, itemName, out projectItem)))
            {
                return null;
            }

            return projectItem;
        }

        internal static bool SupportsReferences(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return envDTEProject.Kind != null &&
                !UnsupportedProjectTypesForAddingReferences.Contains(envDTEProject.Kind, StringComparer.OrdinalIgnoreCase);
        }

        internal static bool SupportsBindingRedirects(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return (envDTEProject.Kind != null & !UnsupportedProjectTypesForBindingRedirects.Contains(envDTEProject.Kind, StringComparer.OrdinalIgnoreCase)) &&
                    !IsWindowsStoreApp(envDTEProject);
        }

        // TODO: Return null for library projects
        internal static string GetConfigurationFile(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return IsWebProject(envDTEProject) ? WebConfig : AppConfig;
        }

        internal static FrameworkName GetDotNetFrameworkName(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string targetFrameworkMoniker = GetTargetFrameworkString(envDTEProject);
            if (targetFrameworkMoniker != null)
            {
                return new FrameworkName(targetFrameworkMoniker);
            }

            return null;
        }

        internal static HashSet<string> GetAssemblyClosure(EnvDTEProject envDTEProject, IDictionary<string, HashSet<string>> visitedProjects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            HashSet<string> assemblies;
            if (visitedProjects.TryGetValue(envDTEProject.UniqueName, out assemblies))
            {
                return assemblies;
            }

            assemblies = new HashSet<string>(PathComparer.Default);
            var localProjectAssemblies = GetLocalProjectAssemblies(envDTEProject);
            CollectionsUtility.AddRange(assemblies, localProjectAssemblies);
            var referencedProjects = GetReferencedProjects(envDTEProject);
            foreach (var project in referencedProjects)
            {
                var assemblyClosure = GetAssemblyClosure(project, visitedProjects);
                CollectionsUtility.AddRange(assemblies, assemblyClosure);
            }

            visitedProjects.Add(envDTEProject.UniqueName, assemblies);

            return assemblies;
        }

        private static HashSet<string> GetLocalProjectAssemblies(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            if (IsWebSite(envDTEProject))
            {
                var websiteLocalAssemblies = GetWebsiteLocalAssemblies(envDTEProject);
                return websiteLocalAssemblies;
            }

            var assemblies = new HashSet<string>(PathComparer.Default);
            References references;
            try
            {
                references = GetReferences(envDTEProject);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                //References property doesn't exist, project does not have references
                references = null;
            }
            if (references != null)
            {
                foreach (Reference reference in references)
                {
                    // Get the referenced project from the reference if any
                    if (reference.SourceProject == null &&
                        reference.CopyLocal &&
                        File.Exists(reference.Path))
                    {
                        assemblies.Add(reference.Path);
                    }
                }
            }
            return assemblies;
        }

        private static HashSet<string> GetWebsiteLocalAssemblies(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            var assemblies = new HashSet<string>(PathComparer.Default);
            AssemblyReferences references = GetAssemblyReferences(envDTEProject);
            foreach (AssemblyReference reference in references)
            {
                // For websites only include bin assemblies
                if (reference.ReferencedProject == null &&
                    reference.ReferenceKind == AssemblyReferenceType.AssemblyReferenceBin &&
                    File.Exists(reference.FullPath))
                {
                    assemblies.Add(reference.FullPath);
                }
            }

            // For website projects, we always add .refresh files that point to the corresponding binaries in packages. In the event of bin deployed assemblies that are also GACed,
            // the ReferenceKind is not AssemblyReferenceBin. Consequently, we work around this by looking for any additional assembly declarations specified via .refresh files.
            string envDTEProjectPath = GetFullPath(envDTEProject);
            CollectionsUtility.AddRange(assemblies, RefreshFileUtility.ResolveRefreshPaths(envDTEProjectPath));

            return assemblies;
        }

        private class PathComparer : IEqualityComparer<string>
        {
            public static readonly PathComparer Default = new PathComparer();
            public bool Equals(string x, string y)
            {
                return Path.GetFileName(x).Equals(Path.GetFileName(y));
            }

            public int GetHashCode(string obj)
            {
                return Path.GetFileName(obj).GetHashCode();
            }
        }

        internal static IList<EnvDTEProject> GetReferencedProjects(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (IsWebSite(envDTEProject))
            {
                return GetWebsiteReferencedProjects(envDTEProject);
            }

            var envDTEProjects = new List<EnvDTEProject>();
            References references;
            try
            {
                references = GetReferences(envDTEProject);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                //References property doesn't exist, project does not have references
                references = null;
            }
            if (references != null)
            {
                foreach (Reference reference in references)
                {
                    // Get the referenced project from the reference if any
                    if (reference.SourceProject != null)
                    {
                        envDTEProjects.Add(reference.SourceProject);
                    }
                }
            }
            return envDTEProjects;
        }

        private static IList<EnvDTEProject> GetWebsiteReferencedProjects(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            var envDTEProjects = new List<EnvDTEProject>();
            AssemblyReferences references = GetAssemblyReferences(envDTEProject);
            foreach (AssemblyReference reference in references)
            {
                if (reference.ReferencedProject != null)
                {
                    envDTEProjects.Add(reference.ReferencedProject);
                }
            }
            return envDTEProjects;
        }

        internal static async TaskIEnumerableEnvDTEProjectItem GetChildItems(EnvDTEProject envDTEProject, string path, string filter, string desiredKind)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            EnvDTEProjectItems projectItems = await GetProjectItemsAsync(envDTEProject, path, createIfNotExists: false);

            if (projectItems == null)
            {
                return Enumerable.Empty<EnvDTEProjectItem>();
            }

            Regex matcher = filter.Equals("*.*", StringComparison.OrdinalIgnoreCase) ? null : GetFilterRegex(filter);

            return from EnvDTEProjectItem p in projectItems
                   where desiredKind.Equals(p.Kind, StringComparison.OrdinalIgnoreCase) &&
                         (matcher == null || matcher.IsMatch(p.Name))
                   select p;
        }

        private static Regex GetFilterRegex(string wildcard)
        {
            // Need NOT be on the UI thread

            string pattern = String.Join(String.Empty, wildcard.Split('.').Select(GetPattern));
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture);
        }

        private static string GetPattern(string token)
        {
            // Need NOT be on the UI thread

            return token == "*" ? @"(.*)" : @"(" + token + ")";
        }

        #endregion // Get "Project" Information


        #region Check Project Types

        private static bool IsJavaScriptProject(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            return envDTEProject != null && NuGetVSConstants.JsProjectTypeGuid.Equals(envDTEProject.Kind, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsXnaWindowsPhoneProject(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            // XNA projects will have this property set
            const string xnaPropertyValue = "Microsoft.Xna.GameStudio.CodeProject.WindowsPhoneProjectPropertiesExtender.XnaRefreshLevel";
            return envDTEProject != null &&
                   "Windows Phone OS 7.1".Equals(GetPropertyValue<string>(envDTEProject, xnaPropertyValue), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNativeProject(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            return envDTEProject != null && NuGetVSConstants.CppProjectTypeGuid.Equals(envDTEProject.Kind, StringComparison.OrdinalIgnoreCase) && !IsClr(envDTEProject);
        }

        /// <summary>
        /// Checks if a native project type really is a managed project, by checking the CLRSupport item. 
        /// </summary>
        internal static bool IsClr(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool isClr = false;

            // always return false here until we fix clr support issue
            // Null properties on the DTE project item are a common source of bugs, make sure everything is non-null before attempting this check.    
            if (envDTEProject != null && envDTEProject.FullName != null)
            {
                var vcx = new VcxProject(envDTEProject.FullName);
            }
            return isClr;
        }

        private static bool IsWebProject(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            string[] types = VsHierarchyUtility.GetProjectTypeGuids(envDTEProject);
            return types.Contains(NuGetVSConstants.WebSiteProjectTypeGuid, StringComparer.OrdinalIgnoreCase) ||
                   types.Contains(NuGetVSConstants.WebApplicationProjectTypeGuid, StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsWebSite(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return envDTEProject.Kind != null && envDTEProject.Kind.Equals(NuGetVSConstants.WebSiteProjectTypeGuid, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWindowsStoreApp(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            string[] types = VsHierarchyUtility.GetProjectTypeGuids(envDTEProject);
            return types.Contains(NuGetVSConstants.WindowsStoreProjectTypeGuid, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsWixProject(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            return envDTEProject.Kind != null && envDTEProject.Kind.Equals(NuGetVSConstants.WixProjectTypeGuid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Check if the project has the SharedAssetsProject capability. This is true
        /// for shared projects in universal apps.
        /// </summary>
        private static bool IsSharedProject(EnvDTEProject envDTEProject)
        {
            // Should be on the UI thread

            var hier = VsHierarchyUtility.ToVsHierarchy(envDTEProject);

            return hier.IsCapabilityMatch("SharedAssetsProject");
        }

        internal static bool SupportsINuGetProjectSystem(EnvDTEProject envDTEProject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var projectKProject = VSNuGetProjectFactory.GetProjectKProject(envDTEProject);
            return projectKProject != null;
        }
        #endregion // Check Project Types

        #region Act on Project
        internal static void EnsureCheckedOutIfExists(EnvDTEProject envDTEProject, string root, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var fullPath = FileSystemUtility.GetFullPath(root, path);
            var dte = envDTEProject.DTE;
            if (dte != null)
            {
                DTESourceControlUtility.EnsureCheckedOutIfExists(dte.SourceControl, fullPath);
            }
        }

        internal static async TaskBool DeleteProjectItemAsync(EnvDTEProject envDTEProject, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            EnvDTEProjectItem projectItem = await GetProjectItemAsync(envDTEProject, path);
            if (projectItem == null)
            {
                return false;
            }

            projectItem.Delete();
            return true;
        }

        internal static void AddImportStatement(EnvDTEProject project, string targetsPath, ImportLocation location)
        {
            // Need NOT be on the UI Thread
            MicrosoftBuildEvaluationProjectUtility.AddImportStatement(AsMSBuildProject(project), targetsPath, location);
        }

        internal static void RemoveImportStatement(EnvDTEProject project, string targetsPath)
        {
            // Need NOT be on the UI Thread
            MicrosoftBuildEvaluationProjectUtility.RemoveImportStatement(AsMSBuildProject(project), targetsPath);
        }

        private static MicrosoftBuildEvaluationProject AsMSBuildProject(EnvDTEProject project)
        {
            // Need NOT be on the UI Thread
            return ProjectCollection.GlobalProjectCollection.GetLoadedProjects(project.FullName).FirstOrDefault() ??
                   ProjectCollection.GlobalProjectCollection.LoadProject(project.FullName);
        }

        internal static void Save(EnvDTEProject project, string projectFullPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var fullPath = FileSystemUtility.GetFullPath(projectFullPath, project.FullName);
            FileSystemUtility.MakeWriteable(project.FullName);
            project.Save();
        }
        #endregion
    }
}