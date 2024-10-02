using System.Reflection;

namespace Eyesolaris.ReferenceAssemblyFilter
{
    internal enum Mode
    {
        [Obfuscation(Exclude = true, StripAfterObfuscation = true)]
        Leave,
        [Obfuscation(Exclude = true, StripAfterObfuscation = true)]
        Remove
    }
}
