using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Eyesolaris.ReferenceAssemblyGenerator
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

        private static void ExecuteStripping(TypeDef type, TypeConfiguration? config, IReadOnlySet<string> removedTypes, bool makeReferenceAssembly)
        {
            bool removeObsolete = config?.RemoveObsolete ?? Configuration.DEFAULT_REMOVE_OBSOLETE;
            // Populate sets
            Dictionary<string, bool> methodsToRemove;
            Dictionary<string, bool> propertiesToRemove;
            Dictionary<string, bool> fieldsToRemove;
            Dictionary<string, bool> eventsToRemove;
            IReadOnlyDictionary<string, bool> interfacesToRemove;
            HashSet<string> implMethodsToRemove = [];

            HashSet<string> removedMethods = [];
            HashSet<string> removedProperties = [];
            HashSet<string> removedFields = [];
            HashSet<string> removedEvents = [];
            HashSet<string> removedInterfaces = [];
            IReadOnlySet<string> interfaceMethodsToKeep = ImmutableHashSet<string>.Empty;
            if (config is not null)
            {
                methodsToRemove = CreateDictForRemoving(
                    () => type.Methods.Select(m => m.FullName),
                    config.Methods,
                    config.Mode,
                    type.FullName);
                propertiesToRemove = CreateDictForRemoving(
                    () => type.Properties.Select(p => p.FullName),
                    config.Properties,
                    config.Mode,
                    type.FullName);
                fieldsToRemove = CreateDictForRemoving(
                    () => type.Fields.Select(f => f.FullName),
                    config.Fields,
                    config.Mode,
                    type.FullName);
                eventsToRemove = CreateDictForRemoving(
                    () => type.Events.Select(e => e.FullName),
                    config.Events,
                    config.Mode,
                    type.FullName);
                interfacesToRemove = CreateInterfaceDictForRemoving(
                    () => type.Interfaces.Select(i => i.Interface.FullName),
                    config.Interfaces,
                    config.InterfaceMethodsToKeep,
                    config.Mode,
                    type.FullName,
                    out interfaceMethodsToKeep);
                //implMethodsToRemove = implMethodsToRemoveDict;
                /*foreach (var iface in type.Interfaces.Select(i => i.Interface).Where(i => interfacesToRemove.ContainsKey(i.FullName)))
                {
                    foreach (MethodDef method in iface.ResolveTypeDefThrow().Methods)
                    {
                        implMethodsToRemoveDict.Add(GetFullMemberName(iface.FullName, method.FullName), interfacesToRemove[iface.FullName]);
                    }
                }*/
            }
            else
            {
                ImmutableDictionary<string, bool> emptyDict = ImmutableDictionary<string, bool>.Empty;
                methodsToRemove = new();
                propertiesToRemove = new();
                fieldsToRemove = new();
                eventsToRemove = new();
                interfacesToRemove = emptyDict;
            }

            void CheckInterfaceMembersToRemoval(InterfaceImpl iface, IReadOnlyList<TypeSig> genericArguments)
            {
                static void AddAllMembers(IEnumerable<string> names, IDictionary<string, bool> dict)
                {
                    foreach (string name in names)
                    {
                        if (!dict.ContainsKey(name))
                        {
                            dict.Add(name, true);
                        }
                    }
                }

                //List<TypeSig> actualGenericArguments = new List<TypeSig>(genericArguments);
                TypeDef ifaceDef = iface.Interface.ResolveTypeDefThrow();
                // TODO: Work out a manual generic resolving
                //GenericInstSig? interfaceSignature = iface.Interface.ToTypeSig().ToGenericInstSig();
                /*if (interfaceSignature is not null)
                {
                    foreach (TypeSig type in interfaceSignature.GenericArguments)
                    {
                        actualGenericArguments.Add(type);
                    }
                }*/
                foreach (MethodDef method in ifaceDef.Methods)
                {
                    var overridesDefs = type.Methods.SelectMany(m => m.Overrides).Select(o => KeyValuePair.Create(o.MethodDeclaration.ResolveMethodDefThrow(), o.MethodBody.ResolveMethodDefThrow()));
                    MethodDef? overridingMethod = overridesDefs
                        .Where(d => d.Key.FullName == method.FullName)
                        .Select(d => d.Value)
                        .SingleOrDefault();
                    if (overridingMethod is null)
                    {
                        overridingMethod = type.Methods
                            .Where(m => m.IsVirtual && m.IsFinal)
                            .Where(m => m.Name == method.Name && m.ParamDefs.Count == method.ParamDefs.Count)
                            .SingleOrDefault();
                        if (overridingMethod is null)
                        {
                            // Interface has a default implementation
                            continue;
                        }
                    }
                    if (interfaceMethodsToKeep.Contains(overridingMethod.FullName))
                    {
                        continue;
                    }
                    if (!methodsToRemove.ContainsKey(overridingMethod.FullName))
                    {
                        // Check a method for sure removal if and only if this behavior is not overriden already
                        methodsToRemove[overridingMethod.FullName] = true;
                    }
                    implMethodsToRemove.Add(overridingMethod.FullName);
                }
                // Leave next two statements to capture non-generic members, just in case
                //AddAllMembers(ifaceDef.Properties.Select(p => p.FullName), propertiesToRemove);
                //AddAllMembers(ifaceDef.Events.Select(p => p.FullName), eventsToRemove);
                /*foreach (InterfaceImpl iface2 in ifaceDef.Interfaces)
                {
                    CheckInterfaceMembersToRemoval(iface2, actualGenericArguments);
                }*/
            }

            // Process all interfaces
            IList<InterfaceImpl> interfaces = type.Interfaces;
            for (int j = 0; j < interfaces.Count; j++)
            {
                InterfaceImpl iface = interfaces[j];
                if (interfacesToRemove.ContainsKey(iface.Interface.FullName)
                    || removeObsolete && IsObsolete(iface.Interface))
                {
                    CheckInterfaceMembersToRemoval(iface, []);
                    interfaces.RemoveAt(j);
                    j--;
                }
            }

            static bool AllSignatureTypesOk(IReadOnlySet<string> removedTypes, IEnumerable<TypeSig> innerTypes)
            {
                foreach (TypeSig type in innerTypes)
                {
                    if (type is null || removedTypes.Contains(type.FullName))
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
                                if (!AllSignatureTypesOk(removedTypes, fn.MethodSig.Params.Prepend(fn.MethodSig.RetType)))
                                {
                                    return false;
                                }
                                break;
                        }
                        continue;
                    }
                    if (removedTypes.Contains(typeScope.FullName))
                    {
                        return false;
                    }
                    TypeDef typeDef = typeScope.ResolveTypeDefThrow();
                    if (!(typeDef.IsPublic || typeDef.IsNestedPublic || typeDef.IsNestedFamily || typeDef.IsNestedFamilyOrAssembly))
                    {
                        return false;
                    }
                    if (!AllSignatureTypesOk(removedTypes, typeDef.GenericParameters.SelectMany(p => p.GenericParamConstraints).Select(c => c.Constraint.ToTypeSig())))
                    {
                        return false;
                    }
                }
                return true;
            }

            static bool MethodSignatureOk(IReadOnlySet<string> removedTypes, MethodDef method)
            {
                return AllSignatureTypesOk(
                    removedTypes,
                    method.Parameters
                    .Where(p => !p.IsHiddenThisParameter)
                    .Select(p => p.Type)
                    .Prepend(method.ReturnType));
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
                    if (implMethodsToRemove.Contains(@override.MethodBody.FullName))
                    {
                        overrides.RemoveAt(o);
                        o--;
                    }
                }
                // Find out, whether to remove the method
                bool nonPublicApi = !(method.IsPublic || method.IsFamilyOrAssembly || method.IsFamily)
                    && !method.HasOverrides && makeReferenceAssembly;
                bool hasRemovedTypesFromSignature = !MethodSignatureOk(removedTypes, method);
                bool removalRequestedByUser = methodsToRemove.ContainsKey(method.FullName);
                bool obsoleteToRemove = removeObsolete && IsObsolete(method);
                if (nonPublicApi
                    || hasRemovedTypesFromSignature
                    || removalRequestedByUser
                    || obsoleteToRemove)
                {
                    removedMethods.Add(method.FullName);
                    type.Remove(method, removeEmptyPropertiesEvents: true);
                    c--;
                }
                else if (makeReferenceAssembly && (!type.IsInterface || method.Body is not null))
                {
                    method.Body = new(initLocals: false, [Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Throw)], [], []);
                }
            }
            // Process all fields
            IList<FieldDef> fields = type.Fields;
            for (int p = 0; p < fields.Count; p++)
            {
                FieldDef field = fields[p];
                bool nonPublicApi = !(field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly) && makeReferenceAssembly;
                if (nonPublicApi
                    || fieldsToRemove.ContainsKey(field.FullName)
                    || removedTypes.Contains(field.FieldType.FullName)
                    || removeObsolete && IsObsolete(field))
                {
                    fields.RemoveAt(p);
                    p--;
                }
            }

            static void RemoveUnneededMethods(IReadOnlySet<string> removedMethods, IList<MethodDef> methods)
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
                MethodDef? addMethod = @event.AddMethod;
                MethodDef? removeMethod = @event.RemoveMethod;
                if (addMethod is not null && methodsToRemove.ContainsKey(addMethod.FullName))
                {
                    type.Remove(addMethod, removeEmptyPropertiesEvents: true);
                    @event.AddMethod = null;
                }
                if (removeMethod is not null && methodsToRemove.ContainsKey(removeMethod.FullName))
                {
                    type.Remove(removeMethod, removeEmptyPropertiesEvents: true);
                    @event.RemoveMethod = null;
                }
                RemoveUnneededMethods(removedMethods, @event.OtherMethods);
                if (@event.IsEmpty || eventsToRemove.ContainsKey(@event.FullName)
                    || removeObsolete && IsObsolete(@event))
                {
                    MethodDef?[] propertyMethods = @event.OtherMethods.Append(@event.AddMethod).Append(@event.RemoveMethod).ToArray();
                    foreach (MethodDef? m in propertyMethods)
                    {
                        if (m is not null)
                        {
                            type.Remove(m, removeEmptyPropertiesEvents: true);
                        }
                    }
                    //events.RemoveAt(e); Event will be removed automatically
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
                            type.Remove(setter, removeEmptyPropertiesEvents: true);
                        }
                    }
                    if (removeGetter)
                    {
                        IList<MethodDef> getMethods = property.GetMethods;
                        while (getMethods.Count > 0)
                        {
                            MethodDef getter = getMethods[0];
                            type.Remove(getter, removeEmptyPropertiesEvents: true);
                        }
                    }
                    if (removeOther)
                    {
                        IList<MethodDef> otherMethods = property.OtherMethods;
                        while (otherMethods.Count > 0)
                        {
                            MethodDef other = otherMethods[0];
                            type.Remove(other, removeEmptyPropertiesEvents: true);
                        }
                    }
                }
                RemoveUnneededMethods(removedMethods, property.SetMethods);
                RemoveUnneededMethods(removedMethods, property.GetMethods);
                RemoveUnneededMethods(removedMethods, property.OtherMethods);
                if (property.IsEmpty || propertiesToRemove.ContainsKey(property.FullName)
                    || removeObsolete && IsObsolete(property))
                {
                    MethodDef[] propertyMethods = property.GetMethods.Concat(property.SetMethods).Concat(property.OtherMethods).ToArray();
                    foreach (MethodDef m in propertyMethods)
                    {
                        type.Remove(m, removeEmptyPropertiesEvents: true);
                    }
                    //properties.RemoveAt(p); Property will be removed automatically
                    p--;
                }
            }
        }

        private static void ProcessInnerTypes(TypeDef type, IReadOnlyDictionary<string, TypeConfiguration>? config, ISet<string> removedTypes, bool? removeObsolete)
        {
            bool removeObsoleteValue = removeObsolete ?? Configuration.DEFAULT_REMOVE_OBSOLETE;
            Dictionary<string, bool> typesToRemove;
            TypeConfiguration? typeConfig = null;
            if (config?.TryGetValue(type.FullName, out typeConfig) ?? false)
            {
                typesToRemove = CreateDictForRemoving(
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
                    || typesToRemove.ContainsKey(nestedType.FullName)
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
                Dictionary<string, bool> typesToRemove = CreateDictForRemoving(
                    () => module.Types.Select(t => t.FullName),
                    config.Types,
                    config.Mode,
                    null);
                HashSet<string> removedTypes = new();
                IList<TypeDef> types = module.Types;

                // First pass: process all types, including inner types
                for (int i = 0; i < types.Count; i++)
                {
                    TypeDef type = types[i];
                    TypeConfiguration? typeConfig = null;
                    bool checkForDeletion = !config.TypeConfiguration.TryGetValue(type.FullName, out typeConfig);
                    if (checkForDeletion)
                    {
                        if (((type.IsNotPublic
                            || type.IsNested && !(type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly))
                            || typesToRemove.ContainsKey(type.FullName))
                            && !IsAttribute(type)
                            || removeObsoleteValue && IsObsolete(type)
                            || typesToRemove.TryGetValue(type.FullName, out bool surelyRemove) && surelyRemove)
                        {
                            removedTypes.Add(type.FullName);
                            types.RemoveAt(i);
                            i--;
                            continue;
                        }
                    }
                    ProcessInnerTypes(
                        type,
                        (IReadOnlyDictionary<string, TypeConfiguration>)config.TypeConfiguration,
                        removedTypes,
                        config.RemoveObsolete);
                }
                // Second pass: process remaining members
                for (int i = 0; i < types.Count; i++)
                {
                    TypeDef type = types[i];
                    config.TypeConfiguration.TryGetValue(type.FullName, out TypeConfiguration? typeConfig);
                    ExecuteStripping(type, typeConfig, removedTypes, config.MakeReferenceAssembly);
                }
            }
        }

        private static Dictionary<string, bool> CreateInterfaceDictForRemoving(Func<IEnumerable<string>> getAllNames, IEnumerable<string> list, IEnumerable<string> methodNamesToKeep, Mode? listMode, string fullTypeName, out IReadOnlySet<string> methodsToKeep)
        {
            var dict = CreateDictForRemoving(getAllNames, list, listMode, null);
            methodsToKeep = methodNamesToKeep.Select(n => GetFullMemberName(fullTypeName, n)).ToHashSet();
            return dict;
        }

        private static Dictionary<string, bool> CreateDictForRemoving(Func<IEnumerable<string>> getAllNames, IEnumerable<string> list, Mode? listMode, string? fullTypeName)
        {
            Dictionary<string, bool> namesToRemove;
            if (fullTypeName is not null)
            {
                list = list.Select(n => GetFullMemberName(fullTypeName, n));
            }
            if (listMode == Mode.Remove)
            {
                // Remove all in a list
                namesToRemove = new Dictionary<string, bool>(list.Select(name => new KeyValuePair<string, bool>(name, true)));
            }
            else if (listMode == Mode.Keep)
            {
                // Remove all except in a list
                namesToRemove = new Dictionary<string, bool>(getAllNames().Select(name => new KeyValuePair<string, bool>(name, false))
                    .Except(list.Select(name => KeyValuePair.Create(name, false))));
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
                AssemblyDef assembly = AssemblyDef.Load(Path.Combine("refs", fileName), ctx);
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
                foreach (ModuleDefMD module in assembly.Modules)
                {
                    string outName = Path.Combine("out", module.FullName);
                    module.Write(outName);
                }
            }
        }
    }
}
