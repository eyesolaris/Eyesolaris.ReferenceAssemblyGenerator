using System;
using System.Text.Json.Serialization;

namespace Eyesolaris.ReferenceAssemblyGenerator
{
    internal class RenameAssembly : IJsonOnDeserialized
    {
        public string? NewName { get; set; }
        public string? NewVersion { get; set; }
        public void OnDeserialized()
        {
            if (string.IsNullOrWhiteSpace(NewName) && string.IsNullOrWhiteSpace(NewVersion))
            {
                throw new InvalidOperationException("Rename object is invalid");
            }
        }
    }
}
