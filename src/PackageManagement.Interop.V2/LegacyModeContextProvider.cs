﻿extern alias Legacy;
using Legacy.NuGet;
using NuGet.VisualStudio;
using System.ComponentModel.Composition;

namespace NuGet.PackageManagement.Interop.V2
{
    /// <summary>
    /// LegacyCompatContextProvider imports the needed legacy components into one context.
    /// </summary>
    [Export(typeof(ILegacyModeContextProvider))]
    public class LegacyCompatContextProvider : ILegacyModeContextProvider
    {
        private readonly LegacyModeContext _context;

        [ImportingConstructor]
        internal LegacyCompatContextProvider(IVsPackageManagerFactory packageManagerFactory, 
            IPackageRepositoryFactory repositoryFactory,
            IPackageSourceProvider sourceProvider,
            ISolutionManager solutionManager)
        {
            _context = new LegacyModeContext(packageManagerFactory, repositoryFactory, sourceProvider, solutionManager);
        }

        public LegacyModeContext GetContext()
        {
            return _context;
        }
    }
}
