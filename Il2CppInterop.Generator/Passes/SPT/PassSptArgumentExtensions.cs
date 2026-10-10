using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Generator.Passes.SPT;

// Mods write lambdas, typeof and C# iterators where the game takes an il2cpp delegate, Il2CppSystem.Type or an il2cpp
// IEnumerator. Every public instance method taking one gets a variant of the same name taking the managed type.
// A method with a single overload gets an extension method, so Harmony patches and reflection looking it up by name
// stay unambiguous, and C# only tries extensions when no instance method applies. A method that is already overloaded
// gets a real overload, which an unqualified call such as StartCoroutine inside a MonoBehaviour also finds, with a
// lower overload resolution priority so null or an il2cpp argument still binds the original.
public static class PassSptArgumentExtensions
{
    public static void DoPass(RewriteGlobalContext context)
    {
        if (!context.Options.ArgumentExtensions)
            return;

        var added = 0;
        var overloads = 0;
        var ambiguous = 0;
        foreach (var assemblyContext in context.Assemblies)
        {
            var module = assemblyContext.NewAssembly.ManifestModule!;
            TypeDefinition? owner = null;
            var signatures = new Dictionary<string, MethodDefinition?>();
            foreach (var typeContext in assemblyContext.Types)
            {
                var type = typeContext.NewType;
                // Interfaces get extensions too, a proxy's methods are the interface's own
                if (type.IsValueType || !IsVisible(type) || type.Name == "Il2CppProxy")
                    continue;

                var originals = type.Methods.ToList();
                var overloaded = originals.Where(m => m.IsPublic && !m.IsStatic).GroupBy(m => m.Name?.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
                var typeOverloads = new Dictionary<string, MethodDefinition?>();
                foreach (var method in originals)
                {
                    if (!method.IsPublic || method.IsStatic || method.IsSpecialName || method.IsRuntimeSpecialName || method.Signature == null)
                        continue;

                    // An interface never gains members, an overload there would have to be a default implementation
                    if (!type.IsInterface && overloaded.Contains(method.Name?.Value))
                    {
                        var overload = TryCreateOverload(context, assemblyContext, module, typeContext, method);
                        if (overload == null)
                            continue;

                        var overloadKey = Key(overload);
                        if (originals.Any(m => Key(m) == overloadKey) || typeOverloads.ContainsKey(overloadKey))
                        {
                            if (typeOverloads.TryGetValue(overloadKey, out var clash) && clash != null)
                            {
                                type.Methods.Remove(clash);
                                typeOverloads[overloadKey] = null;
                                overloads--;
                                ambiguous++;
                            }

                            ambiguous++;
                            continue;
                        }

                        typeOverloads[overloadKey] = overload;
                        type.Methods.Add(overload);
                        overloads++;
                        continue;
                    }

                    var extension = TryCreate(context, assemblyContext, module, type, method);
                    if (extension == null)
                        continue;

                    // Two originals whose arguments map to the same managed types would make every call ambiguous
                    var key = Key(extension);
                    if (signatures.TryGetValue(key, out var earlier))
                    {
                        if (earlier != null)
                        {
                            earlier.DeclaringType!.Methods.Remove(earlier);
                            signatures[key] = null;
                            added--;
                            ambiguous++;
                        }

                        ambiguous++;
                        continue;
                    }

                    signatures[key] = extension;
                    owner ??= CreateOwner(assemblyContext, module);
                    owner.Methods.Add(extension);
                    added++;
                }
            }

            if (owner == null)
                continue;
            if (owner.Methods.Count == 0)
                module.TopLevelTypes.Remove(owner);
            else if (!assemblyContext.NewAssembly.CustomAttributes.Any(a => a.Constructor?.DeclaringType?.FullName == "System.Runtime.CompilerServices.ExtensionAttribute"))
                assemblyContext.NewAssembly.CustomAttributes.Add(new CustomAttribute(assemblyContext.Imports.Module.ExtensionAttributeCtor()));
        }

        Logger.Instance.LogInformation("{Added} argument extension methods and {Overloads} overloads created, {Ambiguous} left out as ambiguous",
            added, overloads, ambiguous);
    }

    /// <summary>
    ///     Build an instance overload for an already overloaded method, or null when no parameter converts
    /// </summary>
    /// <param name="context">Global rewrite context</param>
    /// <param name="assemblyContext">Assembly the method belongs to</param>
    /// <param name="module">Module of the declaring type</param>
    /// <param name="typeContext">Declaring type</param>
    /// <param name="method">Instance method to forward to</param>
    /// <returns>Overload not yet added to the type</returns>
    private static MethodDefinition? TryCreateOverload(RewriteGlobalContext context, AssemblyRewriteContext assemblyContext, ModuleDefinition module,
        TypeRewriteContext typeContext, MethodDefinition method)
    {
        var parameterTypes = new List<TypeSignature>();
        var conversions = new IMethodDescriptor[]?[method.Parameters.Count];
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var parameterType = method.Parameters[i].ParameterType;
            var conversion = FindConversion(context, assemblyContext, module, parameterType);
            if (conversion != null)
            {
                parameterType = conversion.Value.ManagedType;
                conversions[i] = conversion.Value.Calls;
            }

            parameterTypes.Add(parameterType);
        }

        if (conversions.All(conversion => conversion == null))
            return null;

        var methodArity = method.GenericParameters.Count;
        var overload = new MethodDefinition(method.Name, MethodAttributes.Public | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(method.Signature!.ReturnType, methodArity, parameterTypes));
        foreach (var parameter in method.GenericParameters)
        {
            var copy = new GenericParameter(parameter.Name, parameter.Attributes);
            foreach (var constraint in parameter.Constraints)
            {
                if (constraint.Constraint != null)
                    copy.Constraints.Add(new GenericParameterConstraint(constraint.Constraint));
            }

            overload.GenericParameters.Add(copy);
        }

        for (var i = 0; i < method.Parameters.Count; i++)
            CopyParameter(method.Parameters[i].Definition, overload.Parameters[i].GetOrCreateDefinition(), module);
        overload.CustomAttributes.Add(LowerPriority(module));

        var type = typeContext.NewType;
        IMethodDefOrRef target = type.GenericParameters.Count == 0 ? method : typeContext.SelfSubstitutedRef.CreateMemberReference(method.Name!, method.Signature);
        IMethodDescriptor call = methodArity == 0
            ? target
            : target.MakeGenericInstanceMethod(Enumerable.Range(0, methodArity).Select(i => (TypeSignature)new GenericParameterSignature(GenericParameterType.Method, i)).ToArray());

        overload.CilMethodBody = new();
        var body = overload.CilMethodBody.Instructions;
        body.Add(CilOpCodes.Ldarg_0);
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            body.Add(CilOpCodes.Ldarg, overload.Parameters[i]);
            foreach (var conversionCall in conversions[i] ?? [])
                body.Add(CilOpCodes.Call, conversionCall);
        }

        body.Add(CilOpCodes.Callvirt, module.DefaultImporter.ImportMethod(call));
        body.Add(CilOpCodes.Ret);
        return overload;
    }

    // [OverloadResolutionPriority(-1)], named directly since the generator's own framework predates the attribute
    private static CustomAttribute LowerPriority(ModuleDefinition module)
    {
        var scope = (IResolutionScope?)module.AssemblyReferences.FirstOrDefault(reference => reference.Name == "System.Runtime")
                    ?? ((TypeReference)module.ExtensionAttribute().ToTypeDefOrRef()).Scope!;
        var attribute = new TypeReference(module, scope, "System.Runtime.CompilerServices", "OverloadResolutionPriorityAttribute");
        var constructor = attribute.CreateMemberReference(".ctor", MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, [module.CorLibTypeFactory.Int32]));
        return new CustomAttribute(constructor, new CustomAttributeSignature(new CustomAttributeArgument(module.CorLibTypeFactory.Int32, -1)));
    }

    /// <summary>
    ///     Build the extension for one method, or null when no parameter takes a convertible il2cpp type by value
    /// </summary>
    /// <param name="context">Global rewrite context</param>
    /// <param name="assemblyContext">Assembly the method belongs to</param>
    /// <param name="module">Module the extension goes into</param>
    /// <param name="type">Declaring type</param>
    /// <param name="method">Instance method to forward to</param>
    /// <returns>Extension method not yet added to a type</returns>
    private static MethodDefinition? TryCreate(RewriteGlobalContext context, AssemblyRewriteContext assemblyContext, ModuleDefinition module,
        TypeDefinition type, MethodDefinition method)
    {
        var typeArity = type.GenericParameters.Count;
        var methodArity = method.GenericParameters.Count;

        // The type's generic parameters come first on the extension, then the method's own
        var typeArguments = Enumerable.Range(0, typeArity).Select(i => (TypeSignature)new GenericParameterSignature(GenericParameterType.Method, i)).ToArray();
        var methodArguments = Enumerable.Range(typeArity, methodArity).Select(i => (TypeSignature)new GenericParameterSignature(GenericParameterType.Method, i)).ToArray();
        var self = typeArity == 0 ? type.ToTypeSignature() : new GenericInstanceTypeSignature(type, false, typeArguments);
        var generics = new GenericContext(self as GenericInstanceTypeSignature, methodArity == 0 ? null : new GenericInstanceMethodSignature(methodArguments));

        var parameterTypes = new List<TypeSignature> { self };
        var conversions = new IMethodDescriptor[]?[method.Parameters.Count];
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var parameterType = method.Parameters[i].ParameterType.InstantiateGenericTypes(generics);
            var conversion = FindConversion(context, assemblyContext, module, parameterType);
            if (conversion != null)
            {
                parameterType = conversion.Value.ManagedType;
                conversions[i] = conversion.Value.Calls;
            }

            parameterTypes.Add(parameterType);
        }

        if (conversions.All(conversion => conversion == null))
            return null;

        var returnType = method.Signature!.ReturnType.InstantiateGenericTypes(generics);
        var extension = new MethodDefinition(method.Name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(returnType, typeArity + methodArity, parameterTypes));
        CopyGenericParameters(type.GenericParameters, extension, generics, "");
        CopyGenericParameters(method.GenericParameters, extension, generics,
            type.GenericParameters.Any(t => method.GenericParameters.Any(m => m.Name == t.Name)) ? "M" : "");

        extension.Parameters[0].GetOrCreateDefinition().Name = "self";
        for (var i = 0; i < method.Parameters.Count; i++)
            CopyParameter(method.Parameters[i].Definition, extension.Parameters[i + 1].GetOrCreateDefinition(), module);
        extension.CustomAttributes.Add(new CustomAttribute(assemblyContext.Imports.Module.ExtensionAttributeCtor()));

        IMethodDefOrRef target = typeArity == 0 ? method : self.ToTypeDefOrRef().CreateMemberReference(method.Name!, method.Signature);
        IMethodDescriptor call = methodArity == 0 ? target : target.MakeGenericInstanceMethod(methodArguments);

        extension.CilMethodBody = new();
        var body = extension.CilMethodBody.Instructions;
        body.Add(CilOpCodes.Ldarg, extension.Parameters[0]);
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            body.Add(CilOpCodes.Ldarg, extension.Parameters[i + 1]);
            foreach (var conversionCall in conversions[i] ?? [])
                body.Add(CilOpCodes.Call, conversionCall);
        }

        body.Add(CilOpCodes.Callvirt, module.DefaultImporter.ImportMethod(call));
        body.Add(CilOpCodes.Ret);
        return extension;
    }

    /// <summary>
    ///     The managed type a parameter can take instead, with the calls that turn it back into the il2cpp type
    /// </summary>
    /// <param name="context">Global rewrite context</param>
    /// <param name="assemblyContext">Assembly the extension goes into</param>
    /// <param name="module">Module the extension goes into</param>
    /// <param name="parameterType">Parameter type of the original method</param>
    /// <returns>Managed type and conversion calls, null when the parameter stays as it is</returns>
    private static (TypeSignature ManagedType, IMethodDescriptor[] Calls)? FindConversion(RewriteGlobalContext context,
        AssemblyRewriteContext assemblyContext, ModuleDefinition module, TypeSignature parameterType)
    {
        var importer = module.DefaultImporter;
        var helpers = new TypeReference(module, assemblyContext.Imports.DelegateSupport.ToTypeDefOrRef().Scope, "Il2CppInterop.Runtime", "ManagedArguments");
        switch (parameterType.FullName)
        {
            case "Il2CppSystem.Type":
            {
                var systemType = importer.ImportTypeSignature(typeof(Type));
                var toType = helpers.CreateMemberReference("ToIl2CppType", MethodSignature.CreateStatic(parameterType, [systemType]));
                return (systemType, [toType]);
            }
            case "Il2CppSystem.Collections.IEnumerator":
            {
                var systemEnumerator = importer.ImportTypeSignature(typeof(System.Collections.IEnumerator));
                var toPointer = helpers.CreateMemberReference("ToIl2CppEnumerator",
                    MethodSignature.CreateStatic(module.CorLibTypeFactory.IntPtr, [systemEnumerator]));
                var wrap = assemblyContext.Imports.Il2CppObjectPool_Get.Value.MakeGenericInstanceMethod([parameterType]);
                return (systemEnumerator, [toPointer, importer.ImportMethod(wrap)]);
            }
        }

        var conversion = PassSptEvents.FindConversion(context, module, parameterType);
        return conversion == null ? null : (conversion.Value.SystemType, [conversion.Value.Conversion]);
    }

    private static void CopyGenericParameters(IList<GenericParameter> source, MethodDefinition target, GenericContext generics, string prefix)
    {
        foreach (var parameter in source)
        {
            // Methods cannot declare variance
            var copy = new GenericParameter(prefix + parameter.Name, parameter.Attributes & ~GenericParameterAttributes.VarianceMask);
            foreach (var constraint in parameter.Constraints)
            {
                if (constraint.Constraint != null)
                    copy.Constraints.Add(new GenericParameterConstraint(constraint.Constraint.ToTypeSignature().InstantiateGenericTypes(generics).ToTypeDefOrRef()));
            }

            target.GenericParameters.Add(copy);
        }
    }

    private static void CopyParameter(ParameterDefinition? source, ParameterDefinition target, ModuleDefinition module)
    {
        if (source == null)
            return;

        target.Name = source.Name;
        target.Attributes = source.Attributes & (ParameterAttributes.Optional | ParameterAttributes.HasDefault);
        if (source.Constant != null)
            target.Constant = new Constant(source.Constant.Type, source.Constant.Value);
        if (source.CustomAttributes.Any(a => a.Constructor?.DeclaringType?.FullName == typeof(ParamArrayAttribute).FullName))
            target.CustomAttributes.Add(new CustomAttribute(module.ParamArrayAttributeCtor()));
    }

    // In the global namespace, so a call like onClick.AddListener needs no using for the namespace of the type that
    // declares the method. Named after the assembly, the global namespace is shared by every assembly.
    private static TypeDefinition CreateOwner(AssemblyRewriteContext assemblyContext, ModuleDefinition module)
    {
        var owner = new TypeDefinition("", Sanitize(assemblyContext.NewAssembly.Name!) + "ArgumentExtensions",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            assemblyContext.Imports.Module.Object().ToTypeDefOrRef());
        owner.CustomAttributes.Add(new CustomAttribute(assemblyContext.Imports.Module.ExtensionAttributeCtor()));
        module.TopLevelTypes.Add(owner);
        return owner;
    }

    private static string Key(MethodDefinition extension)
    {
        var key = new StringBuilder(extension.Name).Append('`').Append(extension.GenericParameters.Count);
        foreach (var parameter in extension.Signature!.ParameterTypes)
            key.Append('|').Append(parameter.FullName);
        return key.ToString();
    }

    private static bool IsVisible(TypeDefinition type)
    {
        for (var current = type; current != null; current = current.DeclaringType)
        {
            if (current.DeclaringType == null ? !current.IsPublic : !current.IsNestedPublic)
                return false;
        }

        return true;
    }

    private static string Sanitize(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());
}
