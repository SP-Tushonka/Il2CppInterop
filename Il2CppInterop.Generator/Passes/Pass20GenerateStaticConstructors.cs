using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Generator.Passes;

public static class Pass20GenerateStaticConstructors
{
    private static int ourTokenlessMethods;

    public static void DoPass(RewriteGlobalContext context)
    {
        foreach (var assemblyContext in context.Assemblies)
            foreach (var typeContext in assemblyContext.Types)
                GenerateStaticProxy(assemblyContext, typeContext);

        Logger.Instance.LogTrace("Tokenless method count: {TokenlessMethodCount}", ourTokenlessMethods);
    }

    private static void GenerateStaticProxy(AssemblyRewriteContext assemblyContext, TypeRewriteContext typeContext)
    {
        var oldType = typeContext.OriginalType;
        var newType = typeContext.NewType;
        if (newType.IsEnum) return;

        var staticCtorMethod = newType.GetOrCreateStaticConstructor();

        var ctorBuilder = staticCtorMethod.CilMethodBody!.Instructions;
        ctorBuilder.Clear();

        if (newType.IsNested)
        {
            ctorBuilder.Add(OpCodes.Ldsfld,
                assemblyContext.GlobalContext.GetNewTypeForOriginal(oldType.DeclaringType!).ClassPointerFieldRef);
            ctorBuilder.Add(OpCodes.Ldstr, oldType.Name ?? "");
            ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_GetIl2CppNestedType.Value);
        }
        else
        {
            ctorBuilder.Add(OpCodes.Ldstr, oldType.DeclaringModule?.Name ?? "");
            ctorBuilder.Add(OpCodes.Ldstr, oldType.Namespace ?? "");
            ctorBuilder.Add(OpCodes.Ldstr, oldType.Name ?? "");
            ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_GetIl2CppClass.Value);
        }

        if (oldType.HasGenericParameters())
        {
            var il2CppTypeTypeRewriteContext = assemblyContext.GlobalContext.GetAssemblyByName("mscorlib")
                .GetTypeByName("System.Type");
            var il2CppSystemTypeRef = newType.DeclaringModule!.DefaultImporter.ImportType(il2CppTypeTypeRewriteContext.NewType);

            var il2CppTypeHandleTypeRewriteContext = assemblyContext.GlobalContext.GetAssemblyByName("mscorlib")
                .GetTypeByName("System.RuntimeTypeHandle");
            var il2CppSystemTypeHandleRef = newType.DeclaringModule.DefaultImporter.ImportType(il2CppTypeHandleTypeRewriteContext.NewType);

            ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_il2cpp_class_get_type.Value);
            ctorBuilder.Add(OpCodes.Call,
                new MemberReference(il2CppSystemTypeRef, "internal_from_handle", MethodSignature.CreateStatic(il2CppSystemTypeRef.ToTypeSignature(), [assemblyContext.Imports.Module.IntPtr()])));

            ctorBuilder.Add(OpCodes.Ldc_I4, oldType.GenericParameters.Count);

            ctorBuilder.Add(OpCodes.Newarr, il2CppSystemTypeRef);

            for (var i = 0; i < oldType.GenericParameters.Count; i++)
            {
                ctorBuilder.Add(OpCodes.Dup);
                ctorBuilder.Add(OpCodes.Ldc_I4, i);

                var param = oldType.GenericParameters[i];
                var storeRef = assemblyContext.Imports.Il2CppClassPointerStore
                    .MakeGenericInstanceType(new GenericParameterSignature(GenericParameterType.Type, param.Number));
                var fieldRef = new MemberReference(storeRef.ToTypeDefOrRef(), "NativeClassPtr", new FieldSignature(assemblyContext.Imports.Module.IntPtr()));
                ctorBuilder.Add(OpCodes.Ldsfld, fieldRef);

                ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_il2cpp_class_get_type.Value);

                ctorBuilder.Add(OpCodes.Call,
                    new MemberReference(il2CppSystemTypeRef, "internal_from_handle", MethodSignature.CreateStatic(il2CppSystemTypeRef.ToTypeSignature(), [assemblyContext.Imports.Module.IntPtr()])));
                ctorBuilder.Add(OpCodes.Stelem_Ref);
            }

            var il2CppTypeArray = assemblyContext.Imports.Il2CppReferenceArray.MakeGenericInstanceType(il2CppSystemTypeRef.ToTypeSignature());
            ctorBuilder.Add(OpCodes.Newobj,
                new MemberReference(il2CppTypeArray.ToTypeDefOrRef(), ".ctor", MethodSignature.CreateInstance(assemblyContext.Imports.Module.Void(), [new GenericParameterSignature(GenericParameterType.Type, 0).MakeSzArrayType()])));
            ctorBuilder.Add(OpCodes.Call,
                ReferenceCreator.CreateInstanceMethodReference(nameof(Type.MakeGenericType), il2CppSystemTypeRef.ToTypeSignature(), il2CppSystemTypeRef, il2CppTypeArray));

            ctorBuilder.Add(OpCodes.Call,
                ReferenceCreator.CreateInstanceMethodReference(typeof(Type).GetProperty(nameof(Type.TypeHandle))!.GetMethod!.Name,
                    il2CppSystemTypeHandleRef.ToTypeSignature(), il2CppSystemTypeRef));
            ctorBuilder.Add(OpCodes.Ldfld,
                ReferenceCreator.CreateFieldReference("value", assemblyContext.Imports.Module.IntPtr(), il2CppSystemTypeHandleRef));

            ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_il2cpp_class_from_type.Value);
        }

        ctorBuilder.Add(OpCodes.Stsfld, typeContext.ClassPointerFieldRef);

        if (oldType.IsBeforeFieldInit)
        {
            ctorBuilder.Add(OpCodes.Ldsfld, typeContext.ClassPointerFieldRef);
            ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_il2cpp_runtime_class_init.Value);
        }

        if (oldType.IsEnum)
        {
            ctorBuilder.Add(OpCodes.Ret);
            return;
        }

        foreach (var field in typeContext.Fields)
        {
            ctorBuilder.Add(OpCodes.Ldsfld, typeContext.ClassPointerFieldRef);
            ctorBuilder.Add(OpCodes.Ldstr, field.OriginalField.Name!);
            ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_GetIl2CppField.Value);
            ctorBuilder.Add(OpCodes.Stsfld, field.PointerField);
            if (field.OffsetField == null)
                continue;

            ctorBuilder.Add(OpCodes.Ldsfld, field.PointerField);
            ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_il2cpp_field_get_offset.Value);
            ctorBuilder.Add(OpCodes.Stsfld, field.OffsetField);
        }

        foreach (var method in typeContext.Methods)
        {
            ctorBuilder.Add(OpCodes.Ldsfld, typeContext.ClassPointerFieldRef);

            var token = method.OriginalMethod.ExtractToken();
            if (token == 0)
            {
                ourTokenlessMethods++;

                ctorBuilder.Add(
                    method.OriginalMethod.GenericParameters.Count > 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                ctorBuilder.Add(OpCodes.Ldstr, method.OriginalMethod.Name!);
                ctorBuilder.EmitLoadTypeNameString(assemblyContext.Imports, method.OriginalMethod,
                    method.OriginalMethod.Signature!.ReturnType, method.NewMethod.Signature!.ReturnType);
                ctorBuilder.Add(OpCodes.Ldc_I4, method.OriginalMethod.Parameters.Count);
                ctorBuilder.Add(OpCodes.Newarr, assemblyContext.Imports.Module.String().ToTypeDefOrRef());

                for (var i = 0; i < method.OriginalMethod.Parameters.Count; i++)
                {
                    ctorBuilder.Add(OpCodes.Dup);
                    ctorBuilder.Add(OpCodes.Ldc_I4, i);
                    ctorBuilder.EmitLoadTypeNameString(assemblyContext.Imports, method.OriginalMethod,
                        method.OriginalMethod.Parameters[i].ParameterType,
                        method.NewMethod.Parameters[i].ParameterType);
                    ctorBuilder.Add(OpCodes.Stelem_Ref);
                }

                ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_GetIl2CppMethod.Value);
            }
            else
            {
                ctorBuilder.Add(OpCodes.Ldc_I4, (int)token);
                ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_GetIl2CppMethodByToken.Value);
            }

            ctorBuilder.Add(OpCodes.Stsfld, method.NonGenericMethodInfoPointerField);

            if (!CanCallDirectly(typeContext, method))
                continue;

            var directCallField = new FieldDefinition("NativeDirectCallPtr_" + method.UnmangledNameWithSignature,
                FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly,
                assemblyContext.Imports.Module.IntPtr());
            newType.Fields.Add(directCallField);
            method.DirectCallPointerField = new MemberReference(typeContext.SelfSubstitutedRef, directCallField.Name,
                new FieldSignature(directCallField.Signature!.FieldType));

            ctorBuilder.Add(OpCodes.Ldsfld, method.NonGenericMethodInfoPointerField);
            ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_GetDirectCallPointer.Value);
            ctorBuilder.Add(OpCodes.Stsfld, method.DirectCallPointerField);
        }

        foreach (var method in typeContext.Methods)
        {
            if (method.InterfaceMethodInfoPointerField == null)
                continue;

            var interfaceStore = new GenericInstanceTypeSignature(assemblyContext.Imports.Il2CppClassPointerStore.ToTypeDefOrRef(),
                assemblyContext.Imports.Il2CppClassPointerStore.IsValueType(), [method.ExplicitInterfaceRef!.ToTypeSignature()]);
            ctorBuilder.Add(OpCodes.Ldsfld, new MemberReference(newType.DeclaringModule!.DefaultImporter.ImportType(interfaceStore.ToTypeDefOrRef()),
                "NativeClassPtr", new FieldSignature(assemblyContext.Imports.Module.IntPtr())));
            ctorBuilder.Add(OpCodes.Ldc_I4, method.ExplicitInterfaceToken);
            ctorBuilder.Add(OpCodes.Call, assemblyContext.Imports.IL2CPP_GetIl2CppMethodByToken.Value);
            ctorBuilder.Add(OpCodes.Stsfld, method.InterfaceMethodInfoPointerField);
        }

        ctorBuilder.Add(OpCodes.Ret);
    }

    // An instance method on a class that Pass50 does not dispatch virtually, either parameterless or void taking one
    // primitive. Whether its native body really is trivial enough is only known once the game is running.
    private static bool CanCallDirectly(TypeRewriteContext typeContext, MethodRewriteContext method)
    {
        var original = method.OriginalMethod;
        if (typeContext.ComputedTypeSpecifics != TypeRewriteContext.TypeSpecifics.ReferenceType || typeContext.OriginalType.IsInterface)
            return false;

        if (original.IsStatic || original.IsConstructor || original.HasGenericParameters())
            return false;

        if (!original.DeclaringType!.IsSealed && (method.InterfaceMethodInfoPointerField != null || !original.IsFinal) &&
            (original.IsVirtual || original.IsAbstract))
            return false;

        var returnType = original.Signature!.ReturnType;
        var returnsVoid = returnType.ElementType == ElementType.Void;
        if (original.Parameters.Count == 1)
            return returnsVoid && IsPrimitive(original.Parameters[0].ParameterType);
        if (original.Parameters.Count != 0)
            return false;

        if (returnsVoid || IsPrimitive(returnType))
            return true;
        if (returnType is CorLibTypeSignature corLibType)
            return corLibType.ElementType is ElementType.String or ElementType.Object;
        return returnType is not GenericParameterSignature && !returnType.IsPointerLike() && !returnType.IsValueType();
    }

    private static bool IsPrimitive(TypeSignature type)
    {
        if (type is CorLibTypeSignature corLibType)
            return corLibType.ElementType is ElementType.Boolean or ElementType.Char or ElementType.I1 or ElementType.U1 or
                ElementType.I2 or ElementType.U2 or ElementType.I4 or ElementType.U4 or ElementType.I8 or ElementType.U8 or
                ElementType.R4 or ElementType.R8 or ElementType.I or ElementType.U;

        return type is not GenericParameterSignature && !type.IsPointerLike() && type.IsValueType() && type.Resolve()?.IsEnum == true;
    }

    private static void EmitLoadTypeNameString(this ILProcessor ctorBuilder, RuntimeAssemblyReferences imports,
        MethodDefinition originalMethod, TypeSignature originalTypeReference, TypeSignature newTypeReference)
    {
        if (originalMethod.HasGenericParameters() || originalTypeReference.FullName == "System.Void")
        {
            ctorBuilder.Add(OpCodes.Ldstr, originalTypeReference.FullName);
        }
        else
        {
            ctorBuilder.Add(newTypeReference is ByReferenceTypeSignature ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            ctorBuilder.Add(OpCodes.Call,
                imports.Module.DefaultImporter.ImportMethod(
                    imports.IL2CPP_RenderTypeName.Value
                        .MakeGenericInstanceMethod([newTypeReference is ByReferenceTypeSignature ? newTypeReference.GetElementType() : newTypeReference])));
        }
    }
}
