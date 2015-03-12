﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuGet.ProjectManagement;

namespace NuGet.PackageManagement.VisualStudio
{
    public static class RefreshFileUtility
    {
        private const string RefreshFileExtension = ".refresh";

        /// <summary>
        /// Creates a .refresh file in bin directory of the IFileSystem that points to the assembly being installed. 
        /// This works around issues in DTE's AddReference method when dealing with GACed binaries.
        /// </summary>
        /// <remarks>Adds the file to the DTE project system</remarks>
        /// <param name="projectSystem">the web site project system where this will be added</param>
        /// <param name="assemblyPath">The path to the assembly being added</param>
        public static void CreateRefreshFile(WebSiteProjectSystem projectSystem, string assemblyPath, INuGetProjectContext nuGetProjectContext)
        {
            if (projectSystem == null)
            {
                throw new ArgumentNullException("projectSystem");
            }

            if (assemblyPath == null)
            {
                throw new ArgumentNullException("assemblyPath");
            }

            string refreshFilePath = CreateRefreshFilePath(projectSystem.ProjectFullPath, assemblyPath);

            if (!FileSystemUtility.FileExists(projectSystem.ProjectFullPath, refreshFilePath))
            {
                try
                {
                    using (var stream = CreateRefreshFileStream(projectSystem.ProjectFullPath, assemblyPath))
                    {
                        // TODO: log to nuGetProjectContext?
                        projectSystem.AddFile(refreshFilePath, stream);
                    }
                }
                catch (UnauthorizedAccessException exception)
                {
                    // log IO permission error
                    ExceptionHelper.WriteToActivityLog(exception);
                }
            }
        }

        /// <summary>
        /// Creates the full path of the .refresh file
        /// </summary>
        public static string CreateRefreshFilePath(string root, string assemblyPath)
        {
            string referenceName = Path.GetFileName(assemblyPath);
            return Path.Combine("bin", referenceName + RefreshFileExtension);
        }

        /// <summary>
        /// Creates a stream with the relative path to the assembly
        /// </summary>
        public static Stream CreateRefreshFileStream(string root, string assemblyPath)
        {
            string projectPath = PathUtility.EnsureTrailingSlash(root);
            string relativeAssemblyPath = PathUtility.GetRelativePath(projectPath, assemblyPath);

            return StreamUtility.StreamFromString(relativeAssemblyPath);
        }

        /// <summary>
        /// Creates a .refresh file in bin directory of the IFileSystem that points to the assembly being installed. 
        /// This works around issues in DTE's AddReference method when dealing with GACed binaries.
        /// </summary>
        /// <remarks>Adds the file to disk ONLY!</remarks>
        /// <param name="root">the root path is dte full path</param>
        /// <param name="assemblyPath">The relative path to the assembly being added</param>
        public static void CreateRefreshFile(string root, string assemblyPath, INuGetProjectContext nuGetProjectContext)
        {
            string refreshFilePath = CreateRefreshFilePath(root, assemblyPath);

            if (!FileSystemUtility.FileExists(root, refreshFilePath))
            {
                try
                {
                    using (var stream = CreateRefreshFileStream(root, assemblyPath))
                    {
                        FileSystemUtility.AddFile(root, refreshFilePath, stream, nuGetProjectContext);
                    }
                }
                catch (UnauthorizedAccessException exception)
                {
                    // log IO permission error
                    ExceptionHelper.WriteToActivityLog(exception);
                }
            }
        }

        public static IEnumerable<string> ResolveRefreshPaths(string root)
        {
            // Resolve all .refresh files from the website's bin directory. Once resolved, verify the path exists on disk and that they look like an assembly reference. 
            return from file in FileSystemUtility.GetFiles(root, "bin", "*" + RefreshFileExtension, recursive: false)
                   let resolvedPath = SafeResolveRefreshPath(root, file)
                   where resolvedPath != null &&
                         FileSystemUtility.FileExists(root, resolvedPath) &&
                         Constants.AssemblyReferencesExtensions.Contains(Path.GetExtension(resolvedPath))
                   select resolvedPath;
        }

        private static string SafeResolveRefreshPath(string root, string file)
        {
            string relativePath;
            try
            {
                using (var stream = File.OpenRead(FileSystemUtility.GetFullPath(root, file)))
                {
                    using (var streamReader = new StreamReader(stream))
                    {
                        relativePath = streamReader.ReadToEnd();
                    }
                }
                return FileSystemUtility.GetFullPath(root, relativePath);
            }
            catch
            {
                // Ignore the .refresh file if it cannot be read.
            }
            return null;
        }
    }
}
