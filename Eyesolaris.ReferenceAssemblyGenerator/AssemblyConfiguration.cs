using System.Collections.Generic;

namespace Eyesolaris.ReferenceAssemblyFilter
{
    internal class AssemblyConfiguration : CompexEntityConfiguration
    {
        public RenameAssembly? Rename { get; set; }
        public string[] Types { get; set; } = [];

        public IDictionary<string, TypeConfiguration> TypeConfiguration { get; set; }
            = new Dictionary<string, TypeConfiguration>();
    }
}
