using System.Collections.Generic;

namespace Eyesolaris.ReferenceAssemblyGenerator
{
    internal class TypeConfiguration : ComplexEntityConfiguration
    {
        public string[] Properties { get; set; } = [];
        public string[] Fields { get; set; } = [];
        public string[] Events { get; set; } = [];
        public string[] Interfaces { get; set; } = [];
        public string[] InterfaceMethodsToKeep { get; set; } = [];
        public string[] Methods { get; set; } = [];
        public string[] InnerTypes { get; set; } = [];
        public IDictionary<string, PropertyConfiguration> PropertyConfiguration { get; set; }
            = new Dictionary<string, PropertyConfiguration>();
        public IDictionary<string, EventConfiguration> EventConfiguration { get; set; }
            = new Dictionary<string, EventConfiguration>();
        public IDictionary<string, TypeConfiguration> InnerTypeConfiguration { get; set; }
            = new Dictionary<string, TypeConfiguration>();
    }
}
