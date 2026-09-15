using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;

namespace Il2CppInterop.Generator.Passes.SPT;

// A wrapped value type is a class over a box, so assignment shares the box. Clone gives a copy, and a constructor
// over every field builds one in a single expression.
public static class PassSptValueTypeHelpers
{
    public static void DoPass(RewriteGlobalContext context)
    {
        if (!context.Options.ValueTypeHelpers)
            return;

        foreach (var assemblyContext in context.Assemblies)
            foreach (var typeContext in assemblyContext.Types)
            {
                if (typeContext.ComputedTypeSpecifics != TypeRewriteContext.TypeSpecifics.NonBlittableStruct || typeContext.OriginalType.IsEnum)
                    continue;

                AddClone(assemblyContext, typeContext);
                AddFieldConstructor(assemblyContext, typeContext);
            }
    }

    private static void AddClone(AssemblyRewriteContext assemblyContext, TypeRewriteContext typeContext)
    {
        // il2cpp_value_box turns a Nullable into its value or null, so Nullable gets no Clone
        if (typeContext.OriginalType.FullName == "System.Nullable`1")
            return;

        var type = typeContext.NewType;
        if (type.Methods.Any(m => m.Name == "Clone" && m.Parameters.Count == 0))
            return;

        var imports = assemblyContext.Imports;
        var clone = new MethodDefinition("Clone", MethodAttributes.Public | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(typeContext.SelfSubstitutedRef.ToTypeSignature()));
        clone.CilMethodBody = new();
        var body = clone.CilMethodBody.Instructions;
        body.Add(CilOpCodes.Ldsfld, typeContext.ClassPointerFieldRef);
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
        body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_unbox.Value);
        body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_value_box.Value);
        body.Add(CilOpCodes.Newobj, ReferenceCreator.CreateInstanceMethodReference(".ctor", imports.Module.Void(), typeContext.SelfSubstitutedRef, imports.Module.IntPtr()));
        body.Add(CilOpCodes.Ret);
        type.Methods.Add(clone);
    }

    private static void AddFieldConstructor(AssemblyRewriteContext assemblyContext, TypeRewriteContext typeContext)
    {
        var type = typeContext.NewType;
        var imports = assemblyContext.Imports;

        var setters = new List<(MethodDefinition Setter, TypeSignature Type, string Name)>();
        foreach (var field in typeContext.Fields)
        {
            if (field.OriginalField.IsStatic)
                continue;
            var property = type.Properties.FirstOrDefault(p => p.Name == field.UnmangledName);
            if (property?.SetMethod == null || property.Signature == null)
                return;
            setters.Add((property.SetMethod, property.Signature.ReturnType, field.UnmangledName));
        }

        if (setters.Count == 0)
            return;

        var parameterTypes = setters.Select(s => s.Type).ToArray();
        if (type.Methods.Any(m => m.Name == ".ctor" && m.Parameters.Count == parameterTypes.Length
                                  && m.Parameters.Select(p => p.ParameterType).Zip(parameterTypes, (a, b) => SignatureComparer.Default.Equals(a, b)).All(x => x)))
            return;

        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(imports.Module.Void(), parameterTypes));
        for (var i = 0; i < setters.Count; i++)
            ctor.Parameters[i].GetOrCreateDefinition().Name = ParameterName(setters[i].Name, i);

        ctor.CilMethodBody = new();
        var body = ctor.CilMethodBody.Instructions;
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Call, ReferenceCreator.CreateInstanceMethodReference(".ctor", imports.Module.Void(), typeContext.SelfSubstitutedRef));
        for (var i = 0; i < setters.Count; i++)
        {
            var setter = setters[i].Setter;
            IMethodDefOrRef setterRef = type.GenericParameters.Count == 0
                ? setter
                : typeContext.SelfSubstitutedRef.CreateMemberReference(setter.Name!, setter.Signature!);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldarg, ctor.Parameters[i]);
            body.Add(CilOpCodes.Call, setterRef);
        }
        body.Add(CilOpCodes.Ret);
        type.Methods.Add(ctor);
    }

    private static string ParameterName(string fieldName, int index)
    {
        var name = fieldName.TrimStart('_');
        if (name.Length == 0)
            return "value" + index;
        return char.IsDigit(name[0]) ? "_" + name : name;
    }
}
