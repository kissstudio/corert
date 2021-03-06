// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;

using System.Reflection.Runtime.General;
using System.Reflection.Runtime.Assemblies;

using Internal.Reflection.Core;
using Internal.Runtime.TypeLoader;

using Internal.Metadata.NativeFormat;

namespace Internal.Reflection.Execution
{
    //=============================================================================================================================
    // The assembly resolution policy for Project N's emulation of "classic reflection."
    //
    // The policy is very simple: the only assemblies that can be "loaded" are those that are statically linked into the running
    // native process. There is no support for probing for assemblies in directories, user-supplied files, GACs, NICs or any
    // other repository.
    //=============================================================================================================================
    public sealed partial class AssemblyBinderImplementation : AssemblyBinder
    {
        private AssemblyBinderImplementation()
        {
            _scopeGroups = new KeyValuePair<RuntimeAssemblyName, ScopeDefinitionGroup>[0];
            ModuleList.AddModuleRegistrationCallback(RegisterModule);
        }

        public static AssemblyBinderImplementation Instance { get; } = new AssemblyBinderImplementation();

        partial void BindEcmaByteArray(byte[] rawAssembly, byte[] rawSymbolStore, ref AssemblyBindResult bindResult, ref Exception exception, ref bool? result);
        partial void BindEcmaAssemblyName(RuntimeAssemblyName refName, ref AssemblyBindResult result, ref Exception exception, ref bool resultBoolean);
        partial void InsertEcmaLoadedAssemblies(List<AssemblyBindResult> loadedAssemblies);

        public sealed override bool Bind(byte[] rawAssembly, byte[] rawSymbolStore, out AssemblyBindResult bindResult, out Exception exception)
        {
            bool? result = null;
            exception = null;
            bindResult = default(AssemblyBindResult);

            BindEcmaByteArray(rawAssembly, rawSymbolStore, ref bindResult, ref exception, ref result);

            // If the Ecma assembly binder isn't linked in, simply throw PlatformNotSupportedException
            if (!result.HasValue)
                throw new PlatformNotSupportedException();
            else
                return result.Value;
        }

        public sealed override bool Bind(RuntimeAssemblyName refName, out AssemblyBindResult result, out Exception exception)
        {
            bool foundMatch = false;
            result = default(AssemblyBindResult);
            exception = null;

            refName = refName.CanonicalizePublicKeyToken();

            // At least one real-world app calls Type.GetType() for "char" using the assembly name "mscorlib". To accomodate this,
            // we will adopt the desktop CLR rule that anything named "mscorlib" automatically binds to the core assembly.
            bool useMscorlibNameCompareFunc = false;
            RuntimeAssemblyName compareRefName = refName;
            if (refName.Name == "mscorlib")
            {
                useMscorlibNameCompareFunc = true;
                compareRefName = AssemblyNameParser.Parse(AssemblyBinder.DefaultAssemblyNameForGetType);
            }

            foreach (KeyValuePair<RuntimeAssemblyName, ScopeDefinitionGroup> group in ScopeGroups)
            {
                bool nameMatches;
                if (useMscorlibNameCompareFunc)
                {
                    nameMatches = MscorlibAssemblyNameMatches(compareRefName, group.Key);
                }
                else
                {
                    nameMatches = AssemblyNameMatches(refName, group.Key);
                }

                if (nameMatches)
                {
                    if (foundMatch)
                    {
                        exception = new AmbiguousMatchException();
                        return false;
                    }

                    foundMatch = true;
                    ScopeDefinitionGroup scopeDefinitionGroup = group.Value;

                    result.Reader = scopeDefinitionGroup.CanonicalScope.Reader;
                    result.ScopeDefinitionHandle = scopeDefinitionGroup.CanonicalScope.Handle;
                    result.OverflowScopes = scopeDefinitionGroup.OverflowScopes;
                }
            }

            BindEcmaAssemblyName(refName, ref result, ref exception, ref foundMatch);
            if (exception != null)
                return false;

            if (!foundMatch)
            {
                exception = new IOException(SR.Format(SR.FileNotFound_AssemblyNotFound, refName.FullName));
                return false;
            }

            return true;
        }

        public sealed override IList<AssemblyBindResult> GetLoadedAssemblies()
        {
            List<AssemblyBindResult> loadedAssemblies = new List<AssemblyBindResult>(ScopeGroups.Length);
            foreach (KeyValuePair<RuntimeAssemblyName, ScopeDefinitionGroup> group in ScopeGroups)
            {
                ScopeDefinitionGroup scopeDefinitionGroup = group.Value;

                AssemblyBindResult result = default(AssemblyBindResult);
                result.Reader = scopeDefinitionGroup.CanonicalScope.Reader;
                result.ScopeDefinitionHandle = scopeDefinitionGroup.CanonicalScope.Handle;
                result.OverflowScopes = scopeDefinitionGroup.OverflowScopes;
                loadedAssemblies.Add(result);
            }

            InsertEcmaLoadedAssemblies(loadedAssemblies);

            return loadedAssemblies;
        }

        //
        // Name match routine for mscorlib references
        //
        private bool MscorlibAssemblyNameMatches(RuntimeAssemblyName coreAssemblyName, RuntimeAssemblyName defName)
        {
            //
            // The defName came from trusted metadata so it should be fully specified.
            //
            Debug.Assert(defName.Version != null);
            Debug.Assert(defName.CultureName != null);

            Debug.Assert((coreAssemblyName.Flags & AssemblyNameFlags.PublicKey) == 0);
            Debug.Assert((defName.Flags & AssemblyNameFlags.PublicKey) == 0);

            if (defName.Name != coreAssemblyName.Name)
                return false;
            byte[] defPkt = defName.PublicKeyOrToken;
            if (defPkt == null)
                return false;
            if (!ArePktsEqual(defPkt, coreAssemblyName.PublicKeyOrToken))
                return false;
            return true;
        }

        //
        // Encapsulates the assembly ref->def matching policy.
        //
        private bool AssemblyNameMatches(RuntimeAssemblyName refName, RuntimeAssemblyName defName)
        {
            //
            // The defName came from trusted metadata so it should be fully specified.
            //
            Debug.Assert(defName.Version != null);
            Debug.Assert(defName.CultureName != null);

            Debug.Assert((defName.Flags & AssemblyNameFlags.PublicKey) == 0);
            Debug.Assert((refName.Flags & AssemblyNameFlags.PublicKey) == 0);

            if (!(refName.Name.Equals(defName.Name, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (refName.Version != null)
            {
                int compareResult = refName.Version.CompareTo(defName.Version);
                if (compareResult > 0)
                    return false;
            }

            if (refName.CultureName != null)
            {
                if (!(refName.CultureName.Equals(defName.CultureName)))
                    return false;
            }

            AssemblyNameFlags materialRefNameFlags = refName.Flags.ExtractAssemblyNameFlags();
            AssemblyNameFlags materialDefNameFlags = defName.Flags.ExtractAssemblyNameFlags();
            if (materialRefNameFlags != materialDefNameFlags)
            {
                return false;
            }

            byte[] refPublicKeyToken = refName.PublicKeyOrToken;
            if (refPublicKeyToken != null)
            {
                byte[] defPublicKeyToken = defName.PublicKeyOrToken;
                if (defPublicKeyToken == null)
                    return false;
                if (!ArePktsEqual(refPublicKeyToken, defPublicKeyToken))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// This callback gets called whenever a module gets registered. It adds the metadata reader
        /// for the new module to the available scopes. The lock in ExecutionEnvironmentImplementation ensures
        /// that this function may never be called concurrently so that we can assume that two threads
        /// never update the reader and scope list at the same time.
        /// </summary>
        /// <param name="moduleInfo">Module to register</param>
        private void RegisterModule(ModuleInfo moduleInfo)
        {
            NativeFormatModuleInfo nativeFormatModuleInfo = moduleInfo as NativeFormatModuleInfo;

            if (nativeFormatModuleInfo == null)
            {
                return;
            }

            LowLevelDictionaryWithIEnumerable<RuntimeAssemblyName, ScopeDefinitionGroup> scopeGroups = new LowLevelDictionaryWithIEnumerable<RuntimeAssemblyName, ScopeDefinitionGroup>();
            foreach (KeyValuePair<RuntimeAssemblyName, ScopeDefinitionGroup> oldGroup in _scopeGroups)
            {
                scopeGroups.Add(oldGroup.Key, oldGroup.Value);
            }
            AddScopesFromReaderToGroups(scopeGroups, nativeFormatModuleInfo.MetadataReader);

            // Update reader and scope list
            KeyValuePair<RuntimeAssemblyName, ScopeDefinitionGroup>[] scopeGroupsArray = new KeyValuePair<RuntimeAssemblyName, ScopeDefinitionGroup>[scopeGroups.Count];
            int i = 0;
            foreach (KeyValuePair<RuntimeAssemblyName, ScopeDefinitionGroup> data in scopeGroups)
            {
                scopeGroupsArray[i] = data;
                i++;
            }

            _scopeGroups = scopeGroupsArray;
        }

        private KeyValuePair<RuntimeAssemblyName, ScopeDefinitionGroup>[] ScopeGroups
        {
            get
            {
                return _scopeGroups;
            }
        }

        private void AddScopesFromReaderToGroups(LowLevelDictionaryWithIEnumerable<RuntimeAssemblyName, ScopeDefinitionGroup> groups, MetadataReader reader)
        {
            foreach (ScopeDefinitionHandle scopeDefinitionHandle in reader.ScopeDefinitions)
            {
                RuntimeAssemblyName defName = scopeDefinitionHandle.ToRuntimeAssemblyName(reader).CanonicalizePublicKeyToken();
                ScopeDefinitionGroup scopeDefinitionGroup;
                if (groups.TryGetValue(defName, out scopeDefinitionGroup))
                {
                    scopeDefinitionGroup.AddOverflowScope(new QScopeDefinition(reader, scopeDefinitionHandle));
                }
                else
                {
                    scopeDefinitionGroup = new ScopeDefinitionGroup(new QScopeDefinition(reader, scopeDefinitionHandle));
                    groups.Add(defName, scopeDefinitionGroup);
                }
            }
        }

        private static bool ArePktsEqual(byte[] pkt1, byte[] pkt2)
        {
            if (pkt1.Length != pkt2.Length)
                return false;
            for (int i = 0; i < pkt1.Length; i++)
            {
                if (pkt1[i] != pkt2[i])
                    return false;
            }
            return true;
        }

        private volatile KeyValuePair<RuntimeAssemblyName, ScopeDefinitionGroup>[] _scopeGroups;

        private class ScopeDefinitionGroup
        {
            public ScopeDefinitionGroup(QScopeDefinition canonicalScope)
            {
                _canonicalScope = canonicalScope;
            }

            public QScopeDefinition CanonicalScope { get { return _canonicalScope; } }

            public IEnumerable<QScopeDefinition> OverflowScopes
            {
                get
                {
                    return _overflowScopes.ToArray();
                }
            }

            public void AddOverflowScope(QScopeDefinition overflowScope)
            {
                _overflowScopes.Add(overflowScope);
            }

            private readonly QScopeDefinition _canonicalScope;
            private ArrayBuilder<QScopeDefinition> _overflowScopes;
        }
    }
}


