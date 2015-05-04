﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Dot42.CecilExtensions;
using Dot42.CompilerLib.Extensions;
using Dot42.CompilerLib.Structure.DotNet;
using Dot42.CompilerLib.Target.Dex;
using Dot42.CompilerLib.XModel;
using Dot42.DexLib;
using Dot42.DexLib.Instructions;
using Dot42.Mapping;
using Dot42.Utility;

namespace Dot42.CompilerLib.CompilerCache
{
    public class DexMethodBodyCompilerCache
    {
        private readonly AssemblyModifiedDetector _modifiedDetector;
        private readonly Dex _dex;

        private readonly DexLookup _dexLookup;
        private readonly MapFileLookup _map;

        private readonly Dictionary<Tuple<string, string>, Tuple<TypeEntry, MethodEntry>> _methodsByScopeId = new Dictionary<Tuple<string, string>, Tuple<TypeEntry, MethodEntry>>();

        private int statCacheHits;
        private int statCacheMisses;

        public bool IsEnabled { get { return _dex != null && _map != null; } }

        public DexMethodBodyCompilerCache()
        {
        }

        public DexMethodBodyCompilerCache(string cacheDirectory, Func<Mono.Cecil.AssemblyDefinition, string> filenameFromAssembly, string dexFilename = "classes.dex")
        {
            dexFilename = Path.Combine(cacheDirectory, dexFilename);
            var mapfile = Path.ChangeExtension(dexFilename, ".d42map");

            if (!File.Exists(dexFilename) || !File.Exists(mapfile))
                return;

            try
            {
                var dex = Dex.Read(dexFilename);
                var map = new MapFileLookup(new MapFile(mapfile));

                _modifiedDetector = new AssemblyModifiedDetector(filenameFromAssembly, map);

                foreach (var type in map.TypeEntries)
                {
                    if(type.ScopeId == null)
                        continue;
                    var typeScopeId = GetTypeScopeId(type);
                    foreach (var method in type.Methods)
                    {
                        if (type.ScopeId == null)
                            continue;
                        
                        var scopeKey = Tuple.Create(typeScopeId, method.ScopeId);
                        _methodsByScopeId[scopeKey] = Tuple.Create(type, method);
                    }
                }

                _dexLookup = new DexLookup(dex);

                _dex = dex;
                _map = map;
            }
            catch (Exception ex)
            {
                DLog.Warning(DContext.CompilerCodeGenerator, "unable to initialize compiler cache", ex);
                throw;
            }
        }

        public void PrintStatistics()
        {
            if(IsEnabled)
                DLog.Warning(DContext.CompilerCodeGenerator, "Dex method body compiler cache: {0} hits and {1} misses.", statCacheHits, statCacheMisses);
        }

        public MethodBody GetFromCache(MethodDefinition targetMethod, XMethodDefinition sourceMethod, AssemblyCompiler compiler, DexTargetPackage targetPackage)
        {
            var ret = GetFromCacheImpl(targetMethod, sourceMethod, compiler, targetPackage);
            
            if (ret != null) Interlocked.Increment(ref statCacheHits);
            else             Interlocked.Increment(ref statCacheMisses);
            
            return ret;
        }

        public MethodBody GetFromCacheImpl(MethodDefinition targetMethod, XMethodDefinition sourceMethod, AssemblyCompiler compiler, DexTargetPackage targetPackage)
        {
            if (!IsEnabled)
                return null;

            var ilMethod = sourceMethod as XModel.DotNet.XBuilder.ILMethodDefinition;
            var javaMethod = sourceMethod as XModel.Java.XBuilder.JavaMethodDefinition;
            if (ilMethod != null)
            {
                var assembly = ilMethod.OriginalMethod.DeclaringType.Module.Assembly;

                if (_modifiedDetector.IsModified(assembly))
                    return null;
            }
            else if(javaMethod != null)
            {
                // TODO: implement this for Java methods as well.
                //       all that's missing seems to be some kind of
                //       modification detection.
                return null;
            }
            else
            {
                // TODO: synthetic methods could be resolved from the cache as well.
                //       check if this would bring any performance benefits.
                return null;
            }
                
            

            Tuple<TypeEntry, MethodEntry> entry;

            string typeScopeId = sourceMethod.DeclaringType.ScopeId;
            string methodScopeId = sourceMethod.DeclaringType.Methods.IndexOf(sourceMethod).ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (!_methodsByScopeId.TryGetValue(Tuple.Create(typeScopeId, methodScopeId), out entry))
                return null;

            var cachedMethod = _dexLookup.GetMethod(entry.Item1.DexName, entry.Item2.DexName, entry.Item2.DexSignature);

            if (cachedMethod == null)
                return null;

            try
            {
                var body = DexMethodBodyCloner.Clone(targetMethod, cachedMethod);
                FixReferences(body, compiler, targetPackage);
                return body;
            }
            catch (CompilerCacheResolveException ex)
            {
                Trace.WriteLine(string.Format("Compiler cache: error while converting cached body: {0}: {1}. Not using cached body.", sourceMethod, ex.Message));
                return null;
            }
            catch (Exception ex)
            {
                DLog.Error(DContext.CompilerCodeGenerator, "Compiler cache: error while converting cached body: {0}: {1}. Not using cached body.", sourceMethod, ex.Message);
                Trace.WriteLine(string.Format("Compiler cache: error while converting cached body: {0}: {1}. Not using cached body.", sourceMethod, ex.Message));
                return null;
            }
        }

        /// <summary>
        /// Operands refering to types, methods or fields need to be fixed, as they might have
        /// gotten another name in the target package. he same applies for catch references.
        /// </summary>
        private void FixReferences(MethodBody body, AssemblyCompiler compiler,  DexTargetPackage targetPackage)
        {
            // fix operands
            foreach (var ins in body.Instructions)
            {
                var fieldRef = ins.Operand as FieldReference;
                var methodRef = ins.Operand as MethodReference;
                var classRef = ins.Operand as ClassReference;

                if (classRef != null)
                {
                    ins.Operand = ConvertClassReference(classRef, compiler, targetPackage);
                }
                else if (fieldRef != null)
                {
                    ins.Operand = ConvertFieldReference(fieldRef, compiler, targetPackage);
                }
                else if (methodRef != null)
                {
                    ins.Operand = ConvertMethodReference(methodRef, compiler, targetPackage);
                }
            }

            // fix catch clauses
            foreach (var @catch in body.Exceptions.SelectMany(e => e.Catches))
            {
                if (@catch.Type != null)
                    @catch.Type = ConvertTypeReference(@catch.Type, compiler, targetPackage);
            }

        }
     
        private TypeReference ConvertTypeReference(TypeReference sourceRef, AssemblyCompiler compiler, DexTargetPackage targetPackage)
        {
            if (sourceRef is PrimitiveType)
            {
                return sourceRef;
            }

            if (sourceRef is ByReferenceType)
            {
                var type = (ByReferenceType) sourceRef;
                var elementType = ConvertTypeReference(type.ElementType, compiler, targetPackage);
                return new ByReferenceType(elementType);
            }

            if (sourceRef is ArrayType)
            {
                var arrayType = (ArrayType) sourceRef;
                var elementType = ConvertTypeReference(arrayType.ElementType,compiler, targetPackage);
                return new ArrayType(elementType);
            }
            
            // must be ClassReference
            return ConvertClassReference((ClassReference)sourceRef, compiler, targetPackage);
        }

        private ClassReference ConvertClassReference(ClassReference sourceRef, AssemblyCompiler compiler, DexTargetPackage targetPackage)
        {
            TypeEntry type = _map.GetTypeBySignature(sourceRef.Descriptor);

            if (IsDelegateType(type))
            {
                // special delegate handling.
                return GetDelegateInstanceType(type, sourceRef, compiler, targetPackage).InstanceDefinition;
            }
            else
            {
                var xTypeDef = ResolveToType(type, sourceRef, compiler);
                return xTypeDef.GetClassReference(targetPackage);
            }
        }

        private MethodReference ConvertMethodReference(MethodReference methodRef, AssemblyCompiler compiler, DexTargetPackage targetPackage)
        {
            TypeEntry typeEntry;
            MethodEntry methodEntry = _map.GetMethodByDexSignature(methodRef.Owner.Fullname, methodRef.Name, methodRef.Prototype.ToSignature());
            string scopeId;

            if (methodEntry != null)
            {
                // important to do this indirection, to correctly resolve methods in
                // the "__generated" class
                typeEntry = _map.GetTypeByMethodId(methodEntry.Id);
                scopeId = methodEntry.ScopeId;
            }
            else
            {
                typeEntry = _map.GetTypeBySignature(methodRef.Owner.Descriptor);
                
                // special delegate handling
                if (IsDelegateType(typeEntry))
                {
                    var delInstanceType = GetDelegateInstanceType(typeEntry, methodRef.Owner, compiler, targetPackage);
                    return new MethodReference(delInstanceType.InstanceDefinition, methodRef.Name, methodRef.Prototype);
                }
                scopeId = methodRef.Name + methodRef.Prototype.ToSignature();
            }
            
            var xTypeDef =  ResolveToType(typeEntry, methodRef.Owner, compiler);


            var methodDef = xTypeDef.GetMethodByScopeId(scopeId);

            if (methodDef == null)
            {
                throw new CompilerCacheResolveException("unable to resolve method by it's scope id: " + methodRef + " (" + scopeId + ")");
            }

            return methodDef.GetReference(targetPackage);
        }

        /// <summary>
        /// Delegate methods are created unfortunately during the compilation phase in AstCompiler.VisitExpression.
        /// Model this behaviour here.
        /// </summary>
        private DelegateInstanceType GetDelegateInstanceType(TypeEntry typeEntry, ClassReference classRef, AssemblyCompiler compiler, DexTargetPackage targetPackage)
        {
            var scopeIds = typeEntry.ScopeId.Split(new[] { ":delegate:" }, StringSplitOptions.None);

            var typeScopId = scopeIds[0];
            var xTypeDef = compiler.Module.GetTypeByScopeID(GetTypeScopeId(typeEntry.Scope, typeScopId, typeEntry.Name));
            var delegateType = compiler.GetDelegateType(xTypeDef);
            
            var calledMethodId = scopeIds[1];
            var calledTypeScopeId = calledMethodId.Split('|')[0];
            var calledMethodScope = calledMethodId.Split('|')[1];

            var calledTypeDef = compiler.Module.GetTypeByScopeID(calledTypeScopeId);
            var calledMethod = calledTypeDef.GetMethodByScopeId(calledMethodScope);

            // NOTE: we are loosing the SequencePoint (DebugInfo) here. I'm not sure if this
            //       was ever valuable anyways.
            var delInstanceType = delegateType.GetOrCreateInstance(null, targetPackage, calledMethod);
            return delInstanceType;
        }

        private object ConvertFieldReference(FieldReference fieldRef, AssemblyCompiler compiler, DexTargetPackage targetPackage)
        {
            var classRef = ConvertClassReference(fieldRef.Owner, compiler, targetPackage);
            var typeRef = ConvertTypeReference(fieldRef.Type, compiler, targetPackage);

            // I don't believe we have to protect ourselfs from field name changes. 
            // Except for obfuscation, there is no reason to rename fields. They are 
            // independent of other classes.
            
            // TODO: handle generated class.
            if(classRef.Name == "__generated")
                throw new CompilerCacheResolveException("unable to resolve fields in __generated: " + fieldRef);

            return new FieldReference(classRef, fieldRef.Name, typeRef);
        }

        /// <summary>
        /// will throw if type is not found.
        /// </summary>
        private static XTypeDefinition ResolveToType(TypeEntry type, ClassReference sourceRef, AssemblyCompiler compiler)
        {
            XTypeDefinition xTypeDef = null;

            if (type != null)
            {
                string scopeId = GetTypeScopeId(type);
                xTypeDef = compiler.Module.GetTypeByScopeID(scopeId);
            }

            if (xTypeDef == null)
            {
                throw new CompilerCacheResolveException("unable to resolve " + sourceRef);
            }
            return xTypeDef;
        }

        private static string GetTypeScopeId(TypeEntry type)
        {
            return GetTypeScopeId(type.Scope, type.ScopeId, type.Name);
        }

        private static string GetTypeScopeId(string scope, string scopeId, string typeFullname)
        {
            return scope == null ? typeFullname : string.Join(":", scope, scopeId);
        }

        private bool IsDelegateType(TypeEntry type)
        {
            return type.ScopeId.Contains(":delegate:");
        }

    }
}
