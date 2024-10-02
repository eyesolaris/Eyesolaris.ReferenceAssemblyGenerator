using System;
using System.Text.Json.Serialization;

namespace Eyesolaris.ReferenceAssemblyFilter
{
    internal abstract class CompexEntityConfiguration : IJsonOnDeserialized
    {
        [JsonConverter(typeof(JsonStringEnumConverter<Mode>))]
        public Mode? Mode { get; set; }
        public bool? RemoveObsolete { get; set; } = Configuration.DEFAULT_REMOVE_OBSOLETE;

        public void OnDeserialized()
        {
            if (Mode < ReferenceAssemblyFilter.Mode.Leave || Mode > ReferenceAssemblyFilter.Mode.Remove)
            {
                throw new InvalidOperationException("Mode is invalid");
            }
        }
    }
}
