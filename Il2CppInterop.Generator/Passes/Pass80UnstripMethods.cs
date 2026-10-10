using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Generator.Passes;

public static class Pass80UnstripMethods
{
    public static void DoPass(RewriteGlobalContext context)
    {
        var methodsUnstripped = 0;
        var methodsIgnored = 0;

        foreach (var unityAssembly in context.UnityAssemblies.Assemblies)
        {
            var processedAssembly = context.TryGetAssemblyByName(unityAssembly.Name);
            if (processedAssembly == null) continue;
            var imports = processedAssembly.Imports;

            foreach (var unityType in unityAssembly.ManifestModule!.GetAllTypes())
            {
                var processedType = processedAssembly.TryGetTypeByName(unityType.FullName);
                if (processedType == null) continue;

                foreach (var unityMethod in unityType.Methods)
                {
                    var isICall = (unityMethod.ImplAttributes & MethodImplAttributes.InternalCall) != 0;
                    // A class constructor allocates its il2cpp object itself, see UnstripTranslator.EmitObjectAllocation
                    var classConstructor = !IsDelegate(processedType.NewType)
                        && (processedType.ComputedTypeSpecifics == TypeRewriteContext.TypeSpecifics.ReferenceType
                            || (processedType.OriginalType == null && processedType.NewType.IsReferenceType()));
                    // A struct il2cpp stripped whole is rebuilt from its plain fields, so it constructs like a blittable one
                    var plainStruct = processedType.ComputedTypeSpecifics == TypeRewriteContext.TypeSpecifics.BlittableStruct
                        || (processedType.OriginalType == null && processedType.NewType.IsValueType);
                    if (unityMethod.IsConstructor && (unityMethod.IsStatic || (!plainStruct && !classConstructor))) continue;
                    // An abstract method is restored so callers in the base class compile. It stays virtual with a
                    // throwing body, a truly abstract one would stop every subclass missing its override from loading
                    var isAbstract = unityMethod.IsAbstract;
                    if (isAbstract && unityType.IsInterface) continue;
                    if (!unityMethod.HasMethodBody && !isICall && !isAbstract) continue; // CoreCLR chokes on no-body methods

                    var processedMethod = processedType.TryGetMethodByUnityAssemblyMethod(unityMethod);
                    if (processedMethod != null) continue;

                    var returnType = ResolveTypeInNewAssemblies(context, unityMethod.Signature!.ReturnType, imports);
                    if (returnType == null)
                    {
                        Logger.Instance.LogTrace("Method {UnityMethod} has unsupported return type {UnityMethodReturnType}", unityMethod.ToString(), unityMethod.Signature.ReturnType.ToString());
                        methodsIgnored++;
                        continue;
                    }

                    var newAttributes = (unityMethod.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
                    // A virtual Finalize becomes the wrapper's CLR finalizer and runs the il2cpp object's destructor
                    // whenever the wrapper is collected, so it stays a plain method like the native ones
                    if (unityMethod.Name == "Finalize" && !unityMethod.IsStatic && unityMethod.Parameters.Count == 0)
                        newAttributes &= ~(MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final);
                    newAttributes &= ~MethodAttributes.Abstract;
                    var newMethod = new MethodDefinition(unityMethod.Name,
                        newAttributes,
                        MethodSignatureCreator.CreateMethodSignature(newAttributes, returnType, unityMethod.Signature.GenericParameterCount));
                    newMethod.CilMethodBody = new();
                    var hadBadParameter = false;
                    foreach (var unityMethodParameter in unityMethod.Parameters)
                    {
                        var convertedType =
                            ResolveTypeInNewAssemblies(context, unityMethodParameter.ParameterType, imports);
                        if (convertedType == null)
                        {
                            hadBadParameter = true;
                            Logger.Instance.LogTrace("Method {UnityMethod} has unsupported parameter type {UnityMethodParameter}", unityMethod.ToString(), unityMethodParameter.ToString());
                            break;
                        }

                        newMethod.AddParameter(convertedType, unityMethodParameter.Name, unityMethodParameter.Definition?.Attributes ?? default);
                    }

                    if (hadBadParameter)
                    {
                        methodsIgnored++;
                        continue;
                    }

                    // A stripped constructor can map onto the signature of one the wrapper already has, such as the pointer constructor
                    if (unityMethod.IsConstructor && processedType.NewType.Methods.Any(existing => existing.IsConstructor && !existing.IsStatic
                        && existing.Signature!.ParameterTypes.SequenceEqual(newMethod.Signature!.ParameterTypes, SignatureComparer.Default)))
                    {
                        methodsIgnored++;
                        continue;
                    }

                    foreach (var unityMethodGenericParameter in unityMethod.GenericParameters)
                    {
                        var newParameter = new GenericParameter(unityMethodGenericParameter.Name.MakeValidInSource());
                        newParameter.Attributes = unityMethodGenericParameter.Attributes;
                        ConstraintRewriter.Rewrite(unityMethodGenericParameter, newParameter, imports,
                            type => ResolveTypeInNewAssemblies(context, type, imports));

                        newMethod.GenericParameters.Add(newParameter);
                    }

                    if (isICall)
                    {
                        var delegateType =
                            UnstripGenerator.CreateDelegateTypeForICallMethod(unityMethod, newMethod, imports);
                        processedType.NewType.NestedTypes.Add(delegateType);

                        processedType.NewType.Methods.Add(newMethod);

                        var delegateField = UnstripGenerator.GenerateStaticCtorSuffix(processedType.NewType,
                            delegateType, unityMethod, imports);
                        UnstripGenerator.GenerateInvokerMethodBody(newMethod, delegateField, delegateType,
                            processedType, imports);
                    }
                    else if (isAbstract)
                    {
                        UnstripTranslator.ReplaceBodyWithException(newMethod, imports, "Abstract method without an override");
                        processedType.NewType.Methods.Add(newMethod);
                    }
                    else
                    {
                        Pass81FillUnstrippedMethodBodies.PushMethod(unityMethod, newMethod, processedType, imports);
                        processedType.NewType.Methods.Add(newMethod);
                    }

                    if (unityMethod.IsGetMethod)
                    {
                        var property = GetOrCreateProperty(unityMethod, newMethod);
                        property.GetMethod = newMethod;
                    }
                    else if (unityMethod.IsSetMethod)
                    {
                        var property = GetOrCreateProperty(unityMethod, newMethod);
                        property.SetMethod = newMethod;
                    }

                    var paramsMethod = unityMethod.IsConstructor && classConstructor ? null : context.CreateParamsMethod(unityMethod, newMethod, imports,
                        type => ResolveTypeInNewAssemblies(context, type, imports));
                    if (paramsMethod != null) processedType.NewType.Methods.Add(paramsMethod);

                    methodsUnstripped++;
                }
            }
        }

        Logger.Instance.LogInformation("Restored {UnstrippedMethods} methods", methodsUnstripped);
        Logger.Instance.LogInformation("Failed to restore {IgnoredMethods} methods", methodsIgnored);
    }

    private static bool IsDelegate(TypeDefinition type)
    {
        return type.BaseType?.FullName is "System.MulticastDelegate" or "Il2CppSystem.MulticastDelegate" or "Il2CppSystem.Delegate";
    }

    private static PropertyDefinition GetOrCreateProperty(MethodDefinition unityMethod, MethodDefinition newMethod)
    {
        var unityProperty =
            unityMethod.DeclaringType!.Properties.Single(
                it => it.SetMethod == unityMethod || it.GetMethod == unityMethod);
        var newProperty = newMethod.DeclaringType?.Properties.SingleOrDefault(it =>
            it.Name == unityProperty.Name && it.Signature!.ParameterTypes.Count == unityProperty.Signature!.ParameterTypes.Count &&
            it.Signature.ParameterTypes.SequenceEqual(unityProperty.Signature.ParameterTypes, SignatureComparer.Default));
        if (newProperty == null)
        {
            TypeSignature propertyType;
            IEnumerable<TypeSignature> parameterTypes;
            if (unityMethod.IsGetMethod)
            {
                propertyType = newMethod.Signature!.ReturnType;
                parameterTypes = newMethod.Signature.ParameterTypes;
            }
            else
            {
                propertyType = newMethod.Signature!.ParameterTypes.Last();
                parameterTypes = newMethod.Signature.ParameterTypes.Take(newMethod.Signature.ParameterTypes.Count - 1);
            }

            var propertySignature = unityProperty.Signature!.HasThis
                ? PropertySignature.CreateInstance(propertyType, parameterTypes)
                : PropertySignature.CreateStatic(propertyType, parameterTypes);
            newProperty = new PropertyDefinition(unityProperty.Name, unityProperty.Attributes, propertySignature);
            newMethod.DeclaringType!.Properties.Add(newProperty);
        }

        return newProperty;
    }

    /// <summary>
    ///     Whether spans resolve to the CLR types while a method body is translated. The translator sets it only for
    ///     a body whose il2cpp form needs a span member il2cpp does not have.
    /// </summary>
    [ThreadStatic] internal static bool AmbientClrSpans;

    /// <summary>
    ///     Resolve a Unity type to its type in the generated assemblies and import it
    /// </summary>
    /// <param name="context">Rewrite context</param>
    /// <param name="unityType">Type in the Unity assemblies</param>
    /// <param name="imports">Runtime references of the target module</param>
    /// <param name="useSystemCorlibPrimitives">Keep primitives and string as CLR types</param>
    /// <param name="clrSpans">Map spans to the CLR types, by default as <see cref="AmbientClrSpans"/> says</param>
    /// <returns>Imported type, or null when it cannot be resolved</returns>
    internal static TypeSignature? ResolveTypeInNewAssemblies(RewriteGlobalContext context, TypeSignature? unityType,
        RuntimeAssemblyReferences imports, bool useSystemCorlibPrimitives = true, bool? clrSpans = null)
    {
        var resolved = ResolveTypeInNewAssembliesRaw(context, unityType, imports, useSystemCorlibPrimitives, clrSpans);
        return resolved != null ? imports.Module.DefaultImporter.ImportTypeSignature(resolved) : null;
    }

    /// <summary>
    ///     Corlib types rebuilt code keeps on the CLR. Each says whether it is a value type, which the unity-libs
    ///     references cannot tell, and whether .NET exposes it through System.Memory.
    /// </summary>
    private static readonly Dictionary<string, (bool ValueType, bool InSystemMemory)> ClrOnlyCorlibTypes = new()
    {
        ["System.Span`1"] = (true, false),
        ["System.ReadOnlySpan`1"] = (true, false),
        ["System.MemoryExtensions"] = (false, true),
        ["System.Runtime.InteropServices.MemoryMarshal"] = (false, false),
        // The modreq of a ref readonly return, which has to match the CLR method it marks
        ["System.Runtime.InteropServices.InAttribute"] = (false, false),
    };

    internal static bool IsClrOnlyCorlibType(string? fullName) => fullName != null && ClrOnlyCorlibTypes.ContainsKey(fullName);

    /// <summary>
    ///     Check whether a signature type is or contains one of the corlib types rebuilt code keeps on the CLR
    /// </summary>
    /// <param name="type">Type in the Unity assemblies</param>
    /// <returns>Whether it mentions a CLR only corlib type</returns>
    internal static bool MentionsClrOnlyType(TypeSignature? type) => type switch
    {
        null => false,
        GenericInstanceTypeSignature generic => MentionsClrOnlyType(generic.GenericType.ToTypeSignature()) || generic.TypeArguments.Any(MentionsClrOnlyType),
        TypeSpecificationSignature specification => MentionsClrOnlyType(specification.BaseType),
        _ => ClrOnlyCorlibTypes.ContainsKey(type.FullName),
    };

    private static readonly AssemblyReference SystemMemory =
        new("System.Memory", new Version(6, 0, 0, 0), false, [0xcc, 0x7b, 0x13, 0xff, 0xcd, 0x2d, 0xdd, 0x51]);

    internal static TypeSignature? ResolveTypeInNewAssembliesRaw(RewriteGlobalContext context, TypeSignature? unityType,
        RuntimeAssemblyReferences imports, bool useSystemCorlibPrimitives = true, bool? clrSpans = null)
    {
        if (unityType is null)
            return null;

        if (unityType is GenericParameterSignature genericParameterSignature)
            return new GenericParameterSignature(imports.Module, genericParameterSignature.ParameterType, genericParameterSignature.Index);

        if (unityType is ByReferenceTypeSignature)
        {
            var resolvedElementType = ResolveTypeInNewAssemblies(context, unityType.GetElementType(), imports, clrSpans: clrSpans);
            return resolvedElementType?.MakeByReferenceType();
        }

        if (unityType is ArrayBaseTypeSignature arrayType)
        {
            if (arrayType.Rank != 1) return null;
            var resolvedElementType = ResolveTypeInNewAssemblies(context, unityType.GetElementType(), imports, clrSpans: clrSpans);
            if (resolvedElementType == null) return null;
            if (resolvedElementType.FullName == "System.String")
                return imports.Il2CppStringArray;
            var genericBase = resolvedElementType switch
            {
                GenericParameterSignature => imports.Il2CppArrayBase,
                { IsValueType: true } => imports.Il2CppStructArray,
                _ => imports.Il2CppReferenceArray
            };
            return new GenericInstanceTypeSignature(genericBase.ToTypeDefOrRef(), false, [resolvedElementType]);
        }

        if (unityType is PointerTypeSignature)
        {
            var resolvedElementType = ResolveTypeInNewAssemblies(context, unityType.GetElementType(), imports, clrSpans: clrSpans);
            return resolvedElementType?.MakePointerType();
        }

        if (unityType is PinnedTypeSignature)
        {
            var resolvedElementType = ResolveTypeInNewAssemblies(context, unityType.GetElementType(), imports, clrSpans: clrSpans);
            return resolvedElementType?.MakePinnedType();
        }

        if (unityType is CustomModifierTypeSignature customModifier)
        {
            var resolvedElementType = ResolveTypeInNewAssemblies(context, customModifier.BaseType, imports, clrSpans: clrSpans);
            var resolvedModifierType = ResolveTypeInNewAssemblies(context, customModifier.ModifierType.ToTypeSignature(), imports, clrSpans: clrSpans);
            return resolvedElementType is not null && resolvedModifierType is not null
                ? new CustomModifierTypeSignature(resolvedModifierType.ToTypeDefOrRef(), customModifier.IsRequired, resolvedElementType)
                : null;
        }

        if (unityType is GenericInstanceTypeSignature genericInstance)
        {
            var baseRef = ResolveTypeInNewAssembliesRaw(context, genericInstance.GenericType.ToTypeSignature(), imports, clrSpans: clrSpans);
            if (baseRef == null) return null;
            var newInstance = new GenericInstanceTypeSignature(baseRef.ToTypeDefOrRef(), baseRef.IsValueType());
            foreach (var unityGenericArgument in genericInstance.TypeArguments)
            {
                var resolvedArgument = ResolveTypeInNewAssemblies(context, unityGenericArgument, imports, clrSpans: clrSpans);
                if (resolvedArgument == null) return null;
                newInstance.TypeArguments.Add(resolvedArgument);
            }

            return newInstance;
        }

        if (unityType is BoxedTypeSignature)
            return null; // Boxed types are not yet supported

        if (unityType is FunctionPointerTypeSignature)
            return null; // Function pointers are not yet supported

        if (unityType is SentinelTypeSignature)
            return unityType; // SentinelTypeSignature has no state and be reused.

        if (unityType.DeclaringType != null)
        {
            var enclosingResolvedType = ResolveTypeInNewAssembliesRaw(context, unityType.DeclaringType.ToTypeSignature(), imports);
            if (enclosingResolvedType == null) return null;
            var resolvedNestedType = enclosingResolvedType.Resolve()!.NestedTypes
                .FirstOrDefault(it => it.Name == unityType.Name);

            return resolvedNestedType?.ToTypeSignature();
        }

        var targetAssemblyName = unityType.Scope!.Name!;
        if (targetAssemblyName.EndsWith(".dll"))
            targetAssemblyName = targetAssemblyName.Substring(0, targetAssemblyName.Length - 4);

        if (useSystemCorlibPrimitives && (unityType.IsPrimitive() || unityType.ElementType is ElementType.String or ElementType.Void))
            return imports.Module.CorLibTypeFactory.FromElementType(unityType.ElementType);

        // Unity 6 bindings pin managed strings and arrays through spans before an icall, which only works on CLR spans
        if ((clrSpans ?? AmbientClrSpans) && ClrOnlyCorlibTypes.TryGetValue(unityType.FullName, out var clrType) && targetAssemblyName is "mscorlib" or "netstandard" or "System.Runtime" or "System.Memory")
        {
            IResolutionScope scope = clrType.InSystemMemory
                ? imports.Module.DefaultImporter.ImportScope(SystemMemory)
                : imports.Module.CorLibTypeFactory.CorLibScope;
            return new TypeReference(imports.Module, scope, unityType.Namespace, unityType.Name).ToTypeSignature(clrType.ValueType);
        }

        if (targetAssemblyName == "UnityEngine")
            foreach (var assemblyRewriteContext in context.Assemblies)
            {
                if (!assemblyRewriteContext.NewAssembly.Name.StartsWith("UnityEngine"))
                    continue;

                var newTypeInAnyUnityAssembly =
                    assemblyRewriteContext.TryGetTypeByName(unityType.FullName)?.NewType;
                if (newTypeInAnyUnityAssembly != null)
                    return newTypeInAnyUnityAssembly.ToTypeSignature();
            }

        var targetAssembly = context.TryGetAssemblyByName(targetAssemblyName);
        var newType = targetAssembly?.TryGetTypeByName(unityType.FullName)?.NewType.ToTypeSignature();

        return newType;
    }
}
