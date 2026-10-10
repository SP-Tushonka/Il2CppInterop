using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Generator.Passes.SPT;

// Generated generics read Il2CppClassPointerStore<T>.NativeClassPtr to build il2cpp instantiations such as
// AddComponent<T> or List<T>. A plugin class that was never registered gave them a null class. Each such read of a
// generic parameter becomes Il2CppClassPointerStore.GenericArgumentPointer<T>(), which registers the class first. Reads
// for concrete types stay as they are, a generated type is never injected.
public static class PassSptGenericArgumentRegistration
{
    public static void DoPass(RewriteGlobalContext context)
    {
        if (!context.Options.ArgumentExtensions)
            return;

        var replaced = 0;
        foreach (var assemblyContext in context.Assemblies)
        {
            var module = assemblyContext.NewAssembly.ManifestModule!;
            var scope = assemblyContext.Imports.DelegateSupport.ToTypeDefOrRef().Scope!;
            var store = new TypeReference(module, scope, "Il2CppInterop.Runtime", "Il2CppClassPointerStore");
            var helper = store.CreateMemberReference("GenericArgumentPointer", MethodSignature.CreateStatic(module.CorLibTypeFactory.IntPtr, 1, []));

            foreach (var type in module.GetAllTypes())
                foreach (var method in type.Methods)
                {
                    if (method.CilMethodBody == null)
                        continue;

                    foreach (var instruction in method.CilMethodBody.Instructions)
                    {
                        if (instruction.OpCode.Code != CilCode.Ldsfld || instruction.Operand is not IFieldDescriptor field || field.Name != "NativeClassPtr")
                            continue;
                        if (field.DeclaringType is not TypeSpecification specification
                            || specification.Signature is not GenericInstanceTypeSignature instance
                            || instance.GenericType.FullName != "Il2CppInterop.Runtime.Il2CppClassPointerStore`1"
                            || instance.TypeArguments[0] is not GenericParameterSignature)
                            continue;

                        instruction.OpCode = CilOpCodes.Call;
                        instruction.Operand = helper.MakeGenericInstanceMethod([instance.TypeArguments[0]]);
                        replaced++;
                    }
                }
        }

        Logger.Instance.LogInformation("{Replaced} generic class pointer reads register their argument on first use", replaced);
    }
}
