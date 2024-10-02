using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Eyesolaris.ReferenceAssemblyGenerator
{
    internal class AssemblyConfiguration : ComplexEntityConfiguration
    {
        public bool MakeReferenceAssembly { get; set; } = true;
        public RenameAssembly? Rename { get; set; }
        public string[] Types { get; set; } = [];

        public IDictionary<string, TypeConfiguration> TypeConfiguration { get; set; }
            = new Dictionary<string, TypeConfiguration>();
    }
}
