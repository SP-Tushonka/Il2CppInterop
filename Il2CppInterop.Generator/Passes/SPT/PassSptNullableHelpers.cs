using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;

namespace Il2CppInterop.Generator.Passes.SPT;

// Il2CppSystem.Nullable<T> stays a class, T? in its place would need T : struct on every instantiation. This adds the
// implicit conversion from T and extension methods copying to and from System.Nullable<T>. It also marks assemblies
// that carry extension methods, without the assembly attribute the C# compiler never looks for them.
public static class PassSptNullableHelpers
{
    public static void DoPass(RewriteGlobalContext context)
    {
        if (!context.Options.ValueTypeHelpers)
            return;

        var nullable = context.CorLib.TryGetTypeByName("System.Nullable`1");
        if (nullable != null)
        {
            AddImplicitFromValue(context.CorLib, nullable);
            AddExtensions(context.CorLib, nullable);
        }

        foreach (var assemblyContext in context.Assemblies)
            MarkExtensionAssembly(assemblyContext);
    }

    private static void AddImplicitFromValue(AssemblyRewriteContext assemblyContext, TypeRewriteContext nullable)
    {
        var type = nullable.NewType;
        var valueCtor = type.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType is GenericParameterSignature);
        if (valueCtor == null || type.Methods.Any(m => m.Name == "op_Implicit"))
            return;

        var self = nullable.SelfSubstitutedRef.ToTypeSignature();
        var op = new MethodDefinition("op_Implicit",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            MethodSignature.CreateStatic(self, [new GenericParameterSignature(GenericParameterType.Type, 0)]));
        op.Parameters[0].GetOrCreateDefinition().Name = "value";
        op.CilMethodBody = new();
        var body = op.CilMethodBody.Instructions;
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Newobj, nullable.SelfSubstitutedRef.CreateMemberReference(".ctor", valueCtor.Signature!));
        body.Add(CilOpCodes.Ret);
        type.Methods.Add(op);
    }

    private static void AddExtensions(AssemblyRewriteContext assemblyContext, TypeRewriteContext nullable)
    {
        var imports = assemblyContext.Imports;
        var module = nullable.NewType.DeclaringModule!;
        var il2cppType = nullable.NewType;
        var hasValue = il2cppType.Methods.FirstOrDefault(m => m.Name == "get_HasValue");
        var value = il2cppType.Methods.FirstOrDefault(m => m.Name == "get_Value");
        var valueCtor = il2cppType.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType is GenericParameterSignature);
        var emptyCtor = il2cppType.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0);
        if (hasValue == null || value == null || valueCtor == null || emptyCtor == null)
            return;

        var extensions = new TypeDefinition(il2cppType.Namespace, "NullableExtensions",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            imports.Module.Object().ToTypeDefOrRef());
        extensions.CustomAttributes.Add(new CustomAttribute(imports.Module.ExtensionAttributeCtor()));
        module.TopLevelTypes.Add(extensions);

        var typeParameter = new GenericParameterSignature(GenericParameterType.Type, 0);
        var methodParameter = new GenericParameterSignature(GenericParameterType.Method, 0);
        var systemNullable = new GenericInstanceTypeSignature(module.DefaultImporter.ImportType(typeof(Nullable<>)), true, [methodParameter]);
        var il2cppNullable = new GenericInstanceTypeSignature(il2cppType, false, [methodParameter]);

        var systemHasValue = systemNullable.ToTypeDefOrRef().CreateMemberReference("get_HasValue", MethodSignature.CreateInstance(imports.Module.Bool()));
        var systemValueOrDefault = systemNullable.ToTypeDefOrRef().CreateMemberReference("GetValueOrDefault", MethodSignature.CreateInstance(typeParameter));
        var systemCtor = systemNullable.ToTypeDefOrRef().CreateMemberReference(".ctor", MethodSignature.CreateInstance(imports.Module.Void(), [typeParameter]));
        var il2cppHasValue = il2cppNullable.ToTypeDefOrRef().CreateMemberReference(hasValue.Name!, hasValue.Signature!);
        var il2cppValue = il2cppNullable.ToTypeDefOrRef().CreateMemberReference(value.Name!, value.Signature!);
        var il2cppValueCtor = il2cppNullable.ToTypeDefOrRef().CreateMemberReference(".ctor", valueCtor.Signature!);
        var il2cppEmptyCtor = il2cppNullable.ToTypeDefOrRef().CreateMemberReference(".ctor", emptyCtor.Signature!);

        var toNullable = NewExtension(imports, extensions, "ToNullable", systemNullable, il2cppNullable, "value");
        var body = toNullable.CilMethodBody!.Instructions;
        var none = new CilInstructionLabel();
        var empty = new CilLocalVariable(systemNullable);
        toNullable.CilMethodBody.LocalVariables.Add(empty);
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Brfalse, none);
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Callvirt, il2cppHasValue);
        body.Add(CilOpCodes.Brfalse, none);
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Callvirt, il2cppValue);
        body.Add(CilOpCodes.Newobj, systemCtor);
        body.Add(CilOpCodes.Ret);
        none.Instruction = body.Add(CilOpCodes.Ldloca, empty);
        body.Add(CilOpCodes.Initobj, systemNullable.ToTypeDefOrRef());
        body.Add(CilOpCodes.Ldloc, empty);
        body.Add(CilOpCodes.Ret);

        var toIl2Cpp = NewExtension(imports, extensions, "ToIl2CppNullable", il2cppNullable, systemNullable, "value");
        body = toIl2Cpp.CilMethodBody!.Instructions;
        none = new CilInstructionLabel();
        body.Add(CilOpCodes.Ldarga, toIl2Cpp.Parameters[0]);
        body.Add(CilOpCodes.Call, systemHasValue);
        body.Add(CilOpCodes.Brfalse, none);
        body.Add(CilOpCodes.Ldarga, toIl2Cpp.Parameters[0]);
        body.Add(CilOpCodes.Call, systemValueOrDefault);
        body.Add(CilOpCodes.Newobj, il2cppValueCtor);
        body.Add(CilOpCodes.Ret);
        none.Instruction = body.Add(CilOpCodes.Newobj, il2cppEmptyCtor);
        body.Add(CilOpCodes.Ret);
    }

    private static MethodDefinition NewExtension(RuntimeAssemblyReferences imports, TypeDefinition owner, string name,
        TypeSignature returnType, TypeSignature parameterType, string parameterName)
    {
        var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(returnType, 1, [parameterType]));
        var typeParameter = new GenericParameter("T", GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint);
        typeParameter.Constraints.Add(new GenericParameterConstraint(imports.Module.ValueType().ToTypeDefOrRef()));
        method.GenericParameters.Add(typeParameter);
        method.Parameters[0].GetOrCreateDefinition().Name = parameterName;
        method.CustomAttributes.Add(new CustomAttribute(imports.Module.ExtensionAttributeCtor()));
        method.CilMethodBody = new();
        owner.Methods.Add(method);
        return method;
    }

    private static void MarkExtensionAssembly(AssemblyRewriteContext assemblyContext)
    {
        var assembly = assemblyContext.NewAssembly;
        var imports = assemblyContext.Imports;
        if (assembly.CustomAttributes.Any(a => a.Constructor?.DeclaringType?.FullName == "System.Runtime.CompilerServices.ExtensionAttribute"))
            return;
        if (!assemblyContext.Types.Any(t => t.Methods.Any(m => m.HasExtensionAttribute)) && assemblyContext != assemblyContext.GlobalContext.CorLib)
            return;

        assembly.CustomAttributes.Add(new CustomAttribute(imports.Module.ExtensionAttributeCtor()));
    }
}
