﻿using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Dot42.CompilerLib.Target;
using Dot42.CompilerLib.Target.Dex;
using Dot42.CompilerLib.XModel;
using Dot42.DexLib;

namespace Dot42.CompilerLib.Structure.DotNet
{
    /// <summary>
    /// Holder for info regarding a single .NET delegate type.
    /// </summary>
    [DebuggerDisplay("{@Type}")]
    internal sealed class DelegateType
    {
        private readonly AssemblyCompiler compiler;
        private readonly XTypeDefinition delegateType;
        private readonly ClassDefinition interfaceClass;
        private readonly Dictionary<XMethodDefinition, DelegateInstanceType> instances = new Dictionary<XMethodDefinition, DelegateInstanceType>();
        private Prototype invokePrototype;
        private Prototype equalsPrototype;
        private Prototype clonePrototype;
        private readonly XMethodDefinition invokeMethod;
        private readonly XMethodDefinition equalsMethod;
        private readonly XMethodDefinition cloneMethod;

        /// <summary>
        /// Default ctor
        /// </summary>
        public DelegateType(AssemblyCompiler compiler, XTypeDefinition delegateType, ClassDefinition interfaceClass, Dex target, NameConverter nsConverter)
        {
            this.compiler = compiler;
            this.delegateType = delegateType;
            this.interfaceClass = interfaceClass;

            // Build invoke prototype
            invokeMethod = delegateType.Methods.First(x => x.EqualsName("Invoke"));
            equalsMethod = FindMethod(delegateType, "EqualsWithoutInvocationList");
            cloneMethod = FindMethod(delegateType, "CloneWithNewInvocationList");
        }

        /// <summary>
        /// Gets the generated Dex delegate interface class.
        /// </summary>
        public ClassDefinition InterfaceClass
        {
            get { return interfaceClass; }
        }

        /// <summary>
        /// Gets the .NET delegate type
        /// </summary>
        public XTypeDefinition Type
        {
            get { return delegateType; }
        }

        /// <summary>
        /// get all created instances
        /// </summary>
        public ICollection<DelegateInstanceType> Instances { get { return instances.Values; } }

        /// <summary>
        /// Gets the instance type that calls the given method.
        /// Create if needed.
        /// </summary>
        public DelegateInstanceType GetOrCreateInstance(ISourceLocation sequencePoint, DexTargetPackage targetPackage, XMethodDefinition calledMethod)
        {
            DelegateInstanceType result;
            if (instances.TryGetValue(calledMethod, out result))
                return result;

            // Ensure prototype exists
            if (invokePrototype == null)
            {
                invokePrototype = PrototypeBuilder.BuildPrototype(compiler, targetPackage, interfaceClass, invokeMethod);
            }

            if ((equalsPrototype == null) && (equalsMethod != null))
            {
                equalsPrototype = PrototypeBuilder.BuildPrototype(compiler, targetPackage, interfaceClass, equalsMethod);
            }

            if ((clonePrototype == null) && (cloneMethod != null))
            {
                clonePrototype = PrototypeBuilder.BuildPrototype(compiler, targetPackage, interfaceClass, cloneMethod);
            }

            // Not found, build it.
            result = DelegateInstanceTypeBuilder.Create(sequencePoint, compiler, targetPackage, InterfaceClass, 
                                                        invokeMethod, invokePrototype, 
                                                        equalsMethod, equalsPrototype, 
                                                        cloneMethod, clonePrototype, 
                                                        calledMethod);
            instances.Add(calledMethod, result);
            return result;
        }

        private XMethodDefinition FindMethod(XTypeDefinition delegateType, string methodName)
        {
            while (true)
            {
                var ret = delegateType.Methods.FirstOrDefault(x => x.EqualsName(methodName));
                if (ret != null)
                    return ret;

                var baseType = delegateType.BaseType;
                if (baseType == null)
                    return null;

                delegateType = baseType.Resolve();
            }
            return null;
        }
    }


}
