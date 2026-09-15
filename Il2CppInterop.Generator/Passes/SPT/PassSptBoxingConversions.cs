using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;

namespace Il2CppInterop.Generator.Passes.SPT;

// Blittable structs box into Il2CppSystem.Object by implicit conversion and unbox by cast, the way C# boxing reads.
public static class PassSptBoxingConversions
{
    public static void DoPass(RewriteGlobalContext context)
    {
        if (!context.Options.ValueTypeHelpers)
            return;

        var objectType = context.CorLib.GetTypeByName("System.Object").NewType;
        foreach (var assemblyContext in context.Assemblies)
        {
            var module = assemblyContext.NewAssembly.ManifestModule!;
            var objectRef = module.DefaultImporter.ImportType(objectType).ToTypeSignature();
            foreach (var typeContext in assemblyContext.Types)
            {
                if (typeContext.ComputedTypeSpecifics != TypeRewriteContext.TypeSpecifics.BlittableStruct || typeContext.OriginalType.IsEnum)
                    continue;
                if (typeContext.OriginalType.FullName == "System.Void")
                    continue;
                if (typeContext.NewType.Methods.Any(m => m.Name == "op_Implicit" || m.Name == "op_Explicit"))
                    continue;

                var box = typeContext.NewType.Methods.FirstOrDefault(m => m.Name == "BoxIl2CppObject" && m.Parameters.Count == 0);
                if (box == null)
                    continue;

                AddBox(assemblyContext, typeContext, objectRef, box);
                AddUnbox(assemblyContext, typeContext, objectRef);
            }
        }
    }

    private static void AddBox(AssemblyRewriteContext assemblyContext, TypeRewriteContext typeContext, TypeSignature objectRef, MethodDefinition box)
    {
        var self = typeContext.SelfSubstitutedRef.ToTypeSignature();
        var op = new MethodDefinition("op_Implicit",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            MethodSignature.CreateStatic(objectRef, [self]));
        op.Parameters[0].GetOrCreateDefinition().Name = "value";
        op.CilMethodBody = new();
        var body = op.CilMethodBody.Instructions;
        body.Add(CilOpCodes.Ldarga, op.Parameters[0]);
        IMethodDefOrRef boxRef = typeContext.NewType.GenericParameters.Count == 0
            ? box
            : typeContext.SelfSubstitutedRef.CreateMemberReference(box.Name!, box.Signature!);
        body.Add(CilOpCodes.Call, boxRef);
        body.Add(CilOpCodes.Ret);
        typeContext.NewType.Methods.Add(op);
    }

    private static void AddUnbox(AssemblyRewriteContext assemblyContext, TypeRewriteContext typeContext, TypeSignature objectRef)
    {
        var imports = assemblyContext.Imports;
        var module = typeContext.NewType.DeclaringModule!;
        var self = typeContext.SelfSubstitutedRef.ToTypeSignature();
        var isAssignable = ReferenceCreator.CreateStaticMethodReference("il2cpp_class_is_assignable_from", imports.Module.Bool(),
            imports.Il2Cpp.ToTypeDefOrRef(), imports.Module.IntPtr(), imports.Module.IntPtr());
        var invalidCast = module.DefaultImporter.ImportType(typeof(InvalidCastException))
            .CreateMemberReference(".ctor", MethodSignature.CreateInstance(imports.Module.Void(), [imports.Module.String()]));

        var op = new MethodDefinition("op_Explicit",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            MethodSignature.CreateStatic(self, [objectRef]));
        op.Parameters[0].GetOrCreateDefinition().Name = "value";
        op.CilMethodBody = new();
        var pointer = new CilLocalVariable(imports.Module.IntPtr());
        op.CilMethodBody.LocalVariables.Add(pointer);
        var body = op.CilMethodBody.Instructions;
        var matches = new CilInstructionLabel();

        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
        body.Add(CilOpCodes.Stloc, pointer);
        body.Add(CilOpCodes.Ldsfld, typeContext.ClassPointerFieldRef);
        body.Add(CilOpCodes.Ldloc, pointer);
        body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_class.Value);
        body.Add(CilOpCodes.Call, module.DefaultImporter.ImportMethod(isAssignable));
        body.Add(CilOpCodes.Brtrue, matches);
        body.Add(CilOpCodes.Ldstr, "Object is not a boxed " + typeContext.NewType.FullName);
        body.Add(CilOpCodes.Newobj, module.DefaultImporter.ImportMethod(invalidCast));
        body.Add(CilOpCodes.Throw);
        matches.Instruction = body.Add(CilOpCodes.Ldloc, pointer);
        body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_unbox.Value);
        body.Add(CilOpCodes.Ldobj, typeContext.SelfSubstitutedRef);
        body.Add(CilOpCodes.Ret);
        typeContext.NewType.Methods.Add(op);
    }
}
