﻿using System.Collections.Generic;

namespace Eyesolaris.ReferenceAssemblyGenerator
{
    internal class Configuration
    {
        internal const bool DEFAULT_REMOVE_OBSOLETE = true;

        public IDictionary<string, AssemblyConfiguration> Assemblies { get; set; }
            = new Dictionary<string, AssemblyConfiguration>();
    }
}
