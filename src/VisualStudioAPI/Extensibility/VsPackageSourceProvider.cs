﻿using NuGet.Configuration;
using NuGet.Protocol.Core;
using NuGet.Protocol.Core.Types;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuGet.VisualStudio
{
    [Export(typeof(IVsPackageSourceProvider))]
    public class VsPackageSourceProvider : IVsPackageSourceProvider
    {
        private readonly IPackageSourceProvider _packageSourceProvider;

        [ImportingConstructor]
        public VsPackageSourceProvider(ISourceRepositoryProvider sourceRepositoryProvider)
        {
            _packageSourceProvider = sourceRepositoryProvider.PackageSourceProvider;
            _packageSourceProvider.PackageSourcesChanged += PackageSourcesChanged;
        }

        public IEnumerable<KeyValuePair<string, string>> GetSources(bool includeUnOfficial, bool includeDisabled)
        {
            List<KeyValuePair<string, string>> sources = new List<KeyValuePair<string, string>>();

            foreach (PackageSource source in _packageSourceProvider.LoadPackageSources())
            {
                if ((IsOfficial(source) || includeUnOfficial) && (source.IsEnabled || includeDisabled))
                {
                    // Name -> Source Uri
                    var pair = new KeyValuePair<string, string>(source.Name, source.Source);
                    sources.Add(pair);
                }
            }

            return sources;
        }

        public event EventHandler SourcesChanged;

        private void PackageSourcesChanged(object sender, EventArgs e)
        {
            if (SourcesChanged != null)
            {
                // No information is given in the event args, callers must re-request GetSources
                SourcesChanged(this, new EventArgs());
            }
        }

        private static bool IsOfficial(PackageSource source)
        {
            bool official = source.IsOfficial;

            // override the official flag if the domain is nuget.org
            if (source.Source.StartsWith("http://www.nuget.org/", StringComparison.OrdinalIgnoreCase)
                || source.Source.StartsWith("https://www.nuget.org/", StringComparison.OrdinalIgnoreCase)
                || source.Source.StartsWith("http://api.nuget.org/", StringComparison.OrdinalIgnoreCase)
                || source.Source.StartsWith("https://api.nuget.org/", StringComparison.OrdinalIgnoreCase))
            {
                official = true;
            }

            return official;
        }
    }
}
