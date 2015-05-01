using System;
using System.Threading.Tasks;
using NuGet.PackageManagement;

namespace NuGetConsole.Host.PowerShellProvider
{
    /// <summary>
    /// This host is used when PowerShell 2.0 runtime is not installed in the system. It's basically a no-op host.
    /// </summary>
    internal class UnsupportedHost : IHost
    {
        public bool IsCommandEnabled
        {
            get
            {
                return false;
            }
        }

        public Task InitializeAsync(IConsole console)
        {
            // display the error message at the beginning
            console.Write(Resources.Host_PSNotInstalled, System.Windows.Media.Colors.Red, null);
            return Task.FromResult(0);
        }

        public string Prompt
        {
            get
            {
                return String.Empty;
            }
        }

        public bool Execute(IConsole console, string command, object[] inputs)
        {
            return false;
        }

        public void Abort()
        {
        }

        public string ActivePackageSource
        {
            get
            {
                return String.Empty;
            }
            set
            {
            }
        }

        public string[] GetPackageSources()
        {
            return new string[0];
        }

        public string DefaultProject
        {
            get
            {
                return String.Empty;
            }
        }

        public void SetDefaultProjectIndex(int index)
        {
        }

        public string[] GetAvailableProjects()
        {
            return new string[0];
        }

        public void SetDefaultRunspace()
        {
        }


        public PackageManagementContext PackageManagementContext
        {
            get
            {
                return null;
            }
            set
            {
            }
        }
    }
}