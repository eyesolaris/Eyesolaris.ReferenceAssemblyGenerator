using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Eyesolaris.ReferenceAssemblyFilter
{
    internal class Program
    {
        private static bool IsAttribute(ITypeDefOrRef? type)
        {
            if (type is null)
            {
                return false;
            }
            if (type.FullName == "System.Attribute")
            {
                return true;
            }
            if (type.FullName == "System.Object")
            {
                return false;
            }
            else
            {
                return IsAttribute(type.GetBaseType(throwOnResolveFailure: true));
            }
        }

        /*private static void AddAllReferences(ITypeDefOrRef type, HashSet<IAssembly> references)
        {
            references.Add(type.DefinitionAssembly);
            if (type.ContainsGenericParameter)
            {
                var var = type.TryGetGenericVar();
                var mVar = type.TryGetGenericMVar();
                var sig = type.TryGetGenericSig();
                var instSig = type.TryGetGenericInstSig();
            }
            ITypeDefOrRef? @base = type.GetBaseType(throwOnResolveFailure: true);
            if (@base != null)
            {
                AddAllReferences(@base, references);
            }
        }*/

        private static string GetFullMemberName(string fullTypeName, string signature)
        {
            string[] parts = signature.Split(' ', 2);
            if (parts.Length != 2)
            {
                throw new FormatException($"Invalid member name: {signature}");
            }
            string type = parts[0];
            string nameAndParams = parts[1];
            return $"{type} {fullTypeName}::{nameAndParams}";
        }

        private static string GetFullNestedClassName(string fullEnclosingTypeName, string typeName)
        {
            return $"{fullEnclosingTypeName}/{typeName}";
        }

        private static bool IsObsolete(IHasCustomAttribute type)
        {
            CustomAttribute? hasObsoleteAttribute = type.CustomAttributes.Where(a => a.TypeFullName == "System.ObsoleteAttribute").SingleOrDefault();
            string? obsoleteMessage = null;
            if (hasObsoleteAttribute is not null && hasObsoleteAttribute.HasConstructorArguments)
            {
                obsoleteMessage = (UTF8String)hasObsoleteAttribute.ConstructorArguments[0].Value;
            }
            if (hasObsoleteAttribute is not null)
            {
                if (obsoleteMessage == "Types with embedded references are not supported in this version of your compiler.")
                {
                    return false;
                }
                return true;
            }
            return false;
        }

        private static void ExecuteStripping(TypeDef type, TypeConfiguration? config, IReadOnlySet<string> removedTypes)
        {
            bool removeObsolete = config?.RemoveObsolete ?? Configuration.DEFAULT_REMOVE_OBSOLETE;
            // Populate sets
            HashSet<string> methodsToRemove;
            HashSet<string> propertiesToRemove;
            HashSet<string> fieldsToRemove;
            HashSet<string> eventsToRemove;
            HashSet<string> interfacesToRemove;
            HashSet<string> implMethodsToRemove;
            if (config is not null)
            {
                methodsToRemove = CreateSetForRemoving(
                    () => type.Methods.Select(m => m.FullName),
                    config.Methods,
                    config.Mode,
                    type.FullName);
                propertiesToRemove = CreateSetForRemoving(
                    () => type.Properties.Select(p => p.FullName),
                    config.Properties,
                    config.Mode,
                    type.FullName);
                fieldsToRemove = CreateSetForRemoving(
                    () => type.Fields.Select(f => f.FullName),
                    config.Fields,
                    config.Mode,
                    type.FullName);
                eventsToRemove = CreateSetForRemoving(
                    () => type.Events.Select(e => e.FullName),
                    config.Events,
                    config.Mode,
                    type.FullName);
                interfacesToRemove = CreateSetForRemoving(
                    () => type.Interfaces.Select(i => i.Interface.FullName),
                    config.Interfaces,
                    config.Mode,
                    null);
                implMethodsToRemove = new();
                foreach (var iface in type.Interfaces.Select(i => i.Interface).Where(i => interfacesToRemove.Contains(i.FullName)))
                {
                    foreach (MethodDef method in iface.ResolveTypeDefThrow().Methods)
                    {
                        implMethodsToRemove.Add(GetFullMemberName(iface.FullName, method.FullName));
                    }
                }
            }
            else
            {
                methodsToRemove = new();
                implMethodsToRemove = new();
                HashSet<string> emptySet = [];
                propertiesToRemove = emptySet;
                fieldsToRemove = emptySet;
                eventsToRemove = emptySet;
                interfacesToRemove = emptySet;
            }

            static void AddInterfaceMethodsToSet(HashSet<string> methods, InterfaceImpl iface)
            {
                TypeDef ifaceDef = iface.Interface.ResolveTypeDefThrow();
                foreach (MethodDef method in ifaceDef.Methods)
                {
                    methods.Add(method.FullName);
                }
                foreach (InterfaceImpl iface2 in ifaceDef.Interfaces)
                {
                    AddInterfaceMethodsToSet(methods, iface2);
                }
            }

            // Process all interfaces
            IList<InterfaceImpl> interfaces = type.Interfaces;
            for (int j = 0; j < interfaces.Count; j++)
            {
                InterfaceImpl iface = interfaces[j];
                if (interfacesToRemove.Contains(iface.Interface.FullName)
                    || removeObsolete && IsObsolete(iface.Interface))
                {
                    AddInterfaceMethodsToSet(implMethodsToRemove, iface);
                    interfaces.RemoveAt(j);
                    j--;
                }
            }

            static bool AllInnerTypesOk(IReadOnlySet<string> typesToRemove, IEnumerable<TypeSig> innerTypes)
            {
                foreach (TypeSig type in innerTypes)
                {
                    if (type is null || typesToRemove.Contains(type.FullName))
                    {
                        return false;
                    }
                    if (type.IsGenericParameter)
                    {
                        continue;
                    }
                    ITypeDefOrRef? typeScope = type.GetNonNestedTypeRefScope();
                    if (typeScope is null || typeScope.IsGenericParam)
                    {
                        switch (type.ElementType)
                        {
                            case ElementType.FnPtr:
                                FnPtrSig fn = (FnPtrSig)type;
                                if (!AllInnerTypesOk(typesToRemove, fn.MethodSig.Params.Prepend(fn.MethodSig.RetType)))
                                {
                                    return false;
                                }
                                break;
                        }
                        continue;
                    }
                    TypeDef typeDef = typeScope.ResolveTypeDefThrow();
                    if (!(typeDef.IsPublic || typeDef.IsNestedPublic || typeDef.IsNestedFamily || typeDef.IsNestedFamilyOrAssembly))
                    {
                        return false;
                    }
                    if (!AllInnerTypesOk(typesToRemove, typeDef.GenericParameters.SelectMany(p => p.GenericParamConstraints).Select(c => c.Constraint.ToTypeSig())))
                    {
                        return false;
                    }
                }
                return true;
            }

            static bool MethodSignatureOk(IReadOnlySet<string> typesToRemove, MethodDef method)
            {
                return AllInnerTypesOk(
                    typesToRemove,
                    method.Parameters
                    .Where(p => !p.IsHiddenThisParameter)
                    .Select(p => p.Type));
            }

            // Process methods
            IList<MethodDef> methods = type.Methods;
            for (int c = 0; c < methods.Count; c++)
            {
                MethodDef method = methods[c];
                // Clean overrides
                IList<MethodOverride> overrides = method.Overrides;
                for (int o = 0; o < overrides.Count; o++)
                {
                    MethodOverride @override = overrides[o];
                    if (implMethodsToRemove.Contains(@override.MethodDeclaration.FullName))
                    {
                        overrides.RemoveAt(o);
                        o--;
                    }
                }
                // Find out, whether to remove the method
                bool nonPublicApi = !(method.IsPublic || method.IsFamilyOrAssembly || method.IsFamily)
                    && !method.HasOverrides;
                bool hasRemovedTypesFromSignature = !MethodSignatureOk(removedTypes, method);
                bool removalRequestedByUser = methodsToRemove.Contains(method.FullName);
                bool obsoleteToRemove = removeObsolete && IsObsolete(method);
                if (nonPublicApi
                    || hasRemovedTypesFromSignature
                    || removalRequestedByUser
                    || obsoleteToRemove)
                {
                    methodsToRemove.Add(method.FullName);
                    methods.RemoveAt(c);
                    c--;
                }
                else
                {
                    method.Body = new(initLocals: false, [Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Throw)], [], []);
                }
            }
            // Process all fields
            IList<FieldDef> fields = type.Fields;
            for (int p = 0; p < fields.Count; p++)
            {
                FieldDef field = fields[p];
                if (!(field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly)
                    || fieldsToRemove.Contains(field.FullName)
                    || removedTypes.Contains(field.FieldType.FullName)
                    || removeObsolete && IsObsolete(field))
                {
                    fields.RemoveAt(p);
                    p--;
                }
            }

            static void RemoveUnneededMethods(HashSet<string> removedMethods, IList<MethodDef> methods)
            {
                for (int m = 0; m < methods.Count; m++)
                {
                    MethodDef method = methods[m];
                    if (removedMethods.Contains(method.FullName))
                    {
                        methods.RemoveAt(m);
                        m--;
                    }
                }
            }

            // Process all events
            IList<EventDef> events = type.Events;
            for (int e = 0; e < events.Count; e++)
            {
                EventDef @event = events[e];
                if (config?.EventConfiguration.TryGetValue(@event.FullName, out EventConfiguration? c) ?? false)
                {
                    bool removeAdd = (!c.Add) ?? false, removeRemove = (!c.Remove) ?? false;
                    bool removeOther = (!c.Other) ?? false;
                    if (removeAdd)
                    {
                        type.Remove(@event.AddMethod, removeEmptyPropertiesEvents: true);
                    }
                    if (removeRemove)
                    {
                        type.Remove(@event.RemoveMethod, removeEmptyPropertiesEvents: true);
                    }
                    if (removeOther)
                    {
                        IList<MethodDef> otherMethods = @event.OtherMethods;
                        while (otherMethods.Count > 0)
                        {
                            MethodDef other = otherMethods[0];
                            type.Remove(other, removeEmptyPropertiesEvents: false);
                        }
                    }
                }
                if (methodsToRemove.Contains(@event.AddMethod.FullName))
                {
                    @event.AddMethod = null;
                }
                if (methodsToRemove.Contains(@event.RemoveMethod.FullName))
                {
                    @event.RemoveMethod = null;
                }
                RemoveUnneededMethods(methodsToRemove, @event.OtherMethods);
                if (@event.IsEmpty || eventsToRemove.Contains(@event.FullName)
                    || removeObsolete && IsObsolete(@event))
                {
                    MethodDef?[] propertyMethods = @event.OtherMethods.Append(@event.AddMethod).Append(@event.RemoveMethod).ToArray();
                    foreach (MethodDef? m in propertyMethods)
                    {
                        if (m is not null)
                        {
                            type.Remove(m);
                        }
                    }
                    events.RemoveAt(e);
                    e--;
                }
            }
            // Process all properties
            IList<PropertyDef> properties = type.Properties;
            for (int p = 0; p < properties.Count; p++)
            {
                PropertyDef property = properties[p];
                if (config?.PropertyConfiguration.TryGetValue(property.FullName, out PropertyConfiguration? c) ?? false)
                {
                    bool removeSetter = (!c.Setter) ?? false, removeGetter = (!c.Getter) ?? false;
                    bool removeOther = (!c.Other) ?? false;
                    if (removeSetter)
                    {
                        IList<MethodDef> setMethods = property.SetMethods;
                        while (setMethods.Count > 0)
                        {
                            MethodDef setter = setMethods[0];
                            type.Remove(setter, removeEmptyPropertiesEvents: false);
                        }
                    }
                    if (removeGetter)
                    {
                        IList<MethodDef> getMethods = property.GetMethods;
                        while (getMethods.Count > 0)
                        {
                            MethodDef getter = getMethods[0];
                            type.Remove(getter, removeEmptyPropertiesEvents: false);
                        }
                    }
                    if (removeOther)
                    {
                        IList<MethodDef> otherMethods = property.OtherMethods;
                        while (otherMethods.Count > 0)
                        {
                            MethodDef other = otherMethods[0];
                            type.Remove(other, removeEmptyPropertiesEvents: false);
                        }
                    }
                }
                RemoveUnneededMethods(methodsToRemove, property.SetMethods);
                RemoveUnneededMethods(methodsToRemove, property.GetMethods);
                RemoveUnneededMethods(methodsToRemove, property.OtherMethods);
                if (property.IsEmpty || propertiesToRemove.Contains(property.FullName)
                    || removeObsolete && IsObsolete(property))
                {
                    MethodDef[] propertyMethods = property.GetMethods.Concat(property.SetMethods).Concat(property.OtherMethods).ToArray();
                    foreach (MethodDef m in propertyMethods)
                    {
                        type.Remove(m);
                    }
                    properties.RemoveAt(p);
                    p--;
                }
            }
        }

        private static void ProcessInnerTypes(TypeDef type, IReadOnlyDictionary<string, TypeConfiguration>? config, ISet<string> removedTypes, bool? removeObsolete)
        {
            bool removeObsoleteValue = removeObsolete ?? Configuration.DEFAULT_REMOVE_OBSOLETE;
            HashSet<string> typesToRemove;
            TypeConfiguration? typeConfig = null;
            if (config?.TryGetValue(type.FullName, out typeConfig) ?? false)
            {
                typesToRemove = CreateSetForRemoving(
                    () => type.NestedTypes.Select(t => GetFullNestedClassName(type.FullName, t.FullName)),
                    typeConfig.InnerTypes,
                    typeConfig.Mode,
                    type.FullName);
            }
            else
            {
                typesToRemove = [];
            }
            IList<TypeDef> nestedTypes = type.NestedTypes;
            for (int i = 0; i < nestedTypes.Count; i++)
            {
                TypeDef nestedType = nestedTypes[i];
                ProcessInnerTypes(
                    nestedType,
                    (IReadOnlyDictionary<string, TypeConfiguration>?)typeConfig?.InnerTypeConfiguration,
                    removedTypes,
                    typeConfig?.RemoveObsolete ?? removeObsoleteValue); // Use a base removeObsolete, if typeConfig.RemoveObsolete is not overriding a parent value
                if (!(nestedType.IsNestedPublic || nestedType.IsNestedFamily || nestedType.IsNestedFamilyOrAssembly)
                    || typesToRemove.Contains(nestedType.FullName)
                    || removeObsoleteValue && IsObsolete(nestedType))
                {
                    removedTypes.Add(nestedType.FullName);
                    nestedTypes.Remove(nestedType);
                    i--;
                }
            }
        }

        private static void ExecuteStripping(AssemblyDef assembly, AssemblyConfiguration config)
        {
            if (config.Rename is not null)
            {
                RenameAssembly rename = config.Rename;
                ModuleDef manifestModule = assembly.ManifestModule;
                if (!string.IsNullOrWhiteSpace(rename.NewName))
                {
                    assembly.Name = rename.NewName;
                    bool setExtension = manifestModule.Name.EndsWith(".dll");
                    manifestModule.Name = rename.NewName + (setExtension ? ".dll" : "");
                }
                if (!string.IsNullOrWhiteSpace(rename.NewVersion))
                {
                    assembly.Version = new Version(rename.NewVersion);
                }
            }
            bool removeObsoleteValue = config.RemoveObsolete ?? Configuration.DEFAULT_REMOVE_OBSOLETE;
            foreach (var module in assembly.Modules)
            {
                HashSet<string> typesToRemove = CreateSetForRemoving(
                    () => module.Types.Select(t => t.FullName),
                    config.Types,
                    config.Mode,
                    null);
                IList<TypeDef> types = module.Types;

                // First pass: process all inner types
                for (int i = 0; i < types.Count; i++)
                {
                    TypeDef type = types[i];
                    ProcessInnerTypes(
                        type,
                        (IReadOnlyDictionary<string, TypeConfiguration>)config.TypeConfiguration,
                        typesToRemove,
                        config.RemoveObsolete);
                }
                // Second pass: process remaining members
                for (int i = 0; i < types.Count; i++)
                {
                    TypeDef type = types[i];
                    TypeConfiguration? typeConfig = null;
                    bool checkForDeletion = !config.TypeConfiguration.TryGetValue(type.FullName, out typeConfig);
                    if (checkForDeletion)
                    {
                        if (((type.IsNotPublic
                            || type.IsNested && !(type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly))
                            || typesToRemove.Contains(type.FullName))
                            && !IsAttribute(type)
                            || removeObsoleteValue && IsObsolete(type))
                        {
                            typesToRemove.Add(type.FullName);
                            types.RemoveAt(i);
                            i--;
                            continue;
                        }
                    }
                    ExecuteStripping(type, typeConfig, typesToRemove);
                }
            }
        }

        private static HashSet<string> CreateSetForRemoving(Func<IEnumerable<string>> getAllNames, IEnumerable<string> list, Mode? listMode, string? fullTypeName)
        {
            HashSet<string> namesToRemove;
            if (fullTypeName is not null)
            {
                list = list.Select(n => GetFullMemberName(fullTypeName, n));
            }
            if (listMode == Mode.Remove)
            {
                // Remove all in a list
                namesToRemove = new HashSet<string>(list);
            }
            else if (listMode == Mode.Leave)
            {
                // Remove all except in a list
                namesToRemove = new HashSet<string>(getAllNames());
                namesToRemove.ExceptWith(list);
            }
            else
            {
                namesToRemove = [];
            }
            return namesToRemove;
        }

        static void Main(string[] args)
        {
            const string DEFAULT_DOTNET_PATH = "C:\\Program Files\\dotnet";
            const string SHARED_DIR = "shared";
            const string DOTNET_DIR = "Microsoft.NETCore.App";
            const string ASP_NET_CORE_DIR = "Microsoft.AspNetCore.App";
            const string WINDOWS_DIR = "Microsoft.WindowsDesktop.App";

            string sharedPath = Path.Combine(DEFAULT_DOTNET_PATH, SHARED_DIR);

            // First, fulfill the basic stripping

            string configRaw = File.ReadAllText("config.json");
            Configuration? configuration = JsonSerializer.Deserialize<Configuration>(configRaw);
            if (configuration is null)
            {
                throw new InvalidOperationException("Config is null");
            }

            foreach (var kv in configuration.Assemblies)
            {
                AssemblyResolver assemblyResolver = new AssemblyResolver();
                assemblyResolver.PreSearchPaths.Add("refs");
                assemblyResolver.PreSearchPaths.Add("libs");
                ModuleContext ctx = new ModuleContext(assemblyResolver, new Resolver(assemblyResolver));
                string fileName = kv.Key + ".dll";
                AssemblyDef assembly = AssemblyDef.Load(Path.Combine("libs", fileName), ctx);
                if (assembly.TryGetOriginalTargetFrameworkAttribute(out string? framework, out Version? version, out string? profile))
                {
                    if (framework == ".NETCoreApp")
                    {
                        assemblyResolver.UseGAC = false;

                        string GetActualVersionDir(string frameworkDir, Version frameworkVersion)
                        {
                            string searchPattern = $"{frameworkVersion.Major}.{frameworkVersion.Minor}*";

                            DirectoryInfo[] foundDirs = new DirectoryInfo(frameworkDir).GetDirectories(searchPattern);
                            Version maxVersion = frameworkVersion;
                            string? dirName = null;
                            foreach (DirectoryInfo dir in foundDirs)
                            {
                                Version dirVersion = new(dir.Name);
                                if (dirVersion > maxVersion)
                                {
                                    maxVersion = dirVersion;
                                    dirName = dir.Name;
                                }
                            }
                            if (dirName is null)
                            {
                                throw new InvalidOperationException($"Framework .NET {frameworkVersion} is not installed");
                            }
                            return Path.Combine(frameworkDir, dirName);
                        }

                        assemblyResolver.PreSearchPaths.Add(GetActualVersionDir(Path.Combine(sharedPath, DOTNET_DIR), version));
                        assemblyResolver.PreSearchPaths.Add(GetActualVersionDir(Path.Combine(sharedPath, ASP_NET_CORE_DIR), version));
                        assemblyResolver.PreSearchPaths.Add(GetActualVersionDir(Path.Combine(sharedPath, WINDOWS_DIR), version));
                    }
                }
                ExecuteStripping(assembly, kv.Value);
                Directory.CreateDirectory("out");
                foreach (ModuleDef module in assembly.Modules)
                {
                    string outName = Path.Combine("out", module.FullName);
                    module.Write(outName);
                }
            }
        }
    }
}
