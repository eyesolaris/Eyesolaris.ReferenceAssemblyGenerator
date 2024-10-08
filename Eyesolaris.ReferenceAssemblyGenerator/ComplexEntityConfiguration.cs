﻿using System;
using System.Text.Json.Serialization;

namespace Eyesolaris.ReferenceAssemblyGenerator
{
    internal abstract class ComplexEntityConfiguration : IJsonOnDeserialized
    {
        [JsonConverter(typeof(JsonStringEnumConverter<Mode>))]
        public Mode? Mode { get; set; }
        public bool? RemoveObsolete { get; set; } = Configuration.DEFAULT_REMOVE_OBSOLETE;

        public void OnDeserialized()
        {
            if (Mode < ReferenceAssemblyGenerator.Mode.Keep || Mode > ReferenceAssemblyGenerator.Mode.Remove)
            {
                throw new InvalidOperationException("Mode is invalid");
            }
        }
    }
}
