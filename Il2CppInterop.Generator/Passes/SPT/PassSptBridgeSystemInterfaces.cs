using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Microsoft.Extensions.Logging;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;

namespace Il2CppInterop.Generator.Passes.SPT;

// The generated collection interfaces extend their System counterparts, with default bodies forwarding to the il2cpp
// members. Any il2cpp collection then works with foreach, LINQ and every API taking IEnumerable<T> or IList<T>, and
// using works on il2cpp disposables. ICollection<T>.CopyTo is the one member with no il2cpp twin, it walks the
// enumerator into the managed array.
public static class PassSptBridgeSystemInterfaces
{
    private sealed record Bridge(string Il2CppType, Type SystemType, string[] Methods);

    private static readonly Bridge[] Bridges =
    [
        new("System.IDisposable", typeof(IDisposable), ["Dispose"]),
        new("System.Collections.IEnumerator", typeof(System.Collections.IEnumerator), ["MoveNext", "Reset", "get_Current"]),
        new("System.Collections.IEnumerable", typeof(System.Collections.IEnumerable), ["GetEnumerator"]),
        new("System.Collections.Generic.IEnumerator`1", typeof(IEnumerator<>), ["get_Current"]),
        new("System.Collections.Generic.IEnumerable`1", typeof(IEnumerable<>), ["GetEnumerator"]),
        new("System.Collections.Generic.IReadOnlyCollection`1", typeof(IReadOnlyCollection<>), ["get_Count"]),
        new("System.Collections.Generic.IReadOnlyList`1", typeof(IReadOnlyList<>), ["get_Item"]),
        new("System.Collections.Generic.ICollection`1", typeof(ICollection<>), ["get_Count", "get_IsReadOnly", "Add", "Clear", "Contains", "Remove", "CopyTo"]),
        new("System.Collections.Generic.IList`1", typeof(IList<>), ["get_Item", "set_Item", "IndexOf", "Insert", "RemoveAt"]),
    ];

    public static void DoPass(RewriteGlobalContext context)
    {
        if (!context.Options.BridgeSystemInterfaces)
            return;

        foreach (var bridge in Bridges)
        {
            var typeContext = context.CorLib.TryGetTypeByName(bridge.Il2CppType);
            if (typeContext != null)
                AddBridge(context, typeContext, bridge);
        }
    }

    private static void AddBridge(RewriteGlobalContext context, TypeRewriteContext typeContext, Bridge bridge)
    {
        var type = typeContext.NewType;
        var module = type.DeclaringModule!;

        var pairs = new List<(MethodDefinition? Target, MethodInfo System)>();
        foreach (var name in bridge.Methods)
        {
            var system = bridge.SystemType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            // il2cpp's CopyTo takes an il2cpp array, the System one a managed array, so it always gets the enumerator body
            var target = name == "CopyTo" ? null : typeContext.TryGetMethodByName(name)?.NewMethod;
            if (system == null || (target == null && name != "CopyTo") || (target != null && !ParametersMatch(module, target, system)))
            {
                Logger.Instance.LogWarning("{Type} has no matching {Method}, it keeps no System interface", bridge.Il2CppType, name);
                return;
            }
            pairs.Add((target, system));
        }

        var systemType = module.DefaultImporter.ImportType(bridge.SystemType);
        var systemRef = type.GenericParameters.Count == 0
            ? systemType
            : new GenericInstanceTypeSignature(systemType, false, type.GenericParameters.Select((_, i) => (TypeSignature)new GenericParameterSignature(GenericParameterType.Type, i)).ToArray()).ToTypeDefOrRef();
        type.Interfaces.Add(new InterfaceImplementation(systemRef));

        var systemName = bridge.SystemType.FullName!.Split('`')[0];
        foreach (var (target, system) in pairs)
        {
            var imported = module.DefaultImporter.ImportMethod(system);
            var signature = (MethodSignature)imported.Signature!;
            var declaration = new MemberReference(systemRef, imported.Name, signature);

            var forwarder = new MethodDefinition(systemName + "." + system.Name,
                MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(signature.ReturnType, signature.ParameterTypes.ToArray()));
            forwarder.CilMethodBody = new();

            if (target == null)
                EmitCopyTo(context, forwarder);
            else
                EmitForward(typeContext, forwarder, target);

            type.Methods.Add(forwarder);
            type.MethodImplementations.Add(new MethodImplementation(module.DefaultImporter.ImportMethod(declaration), forwarder));
        }
    }

    // The forwarder passes its arguments through untouched, so the il2cpp method has to take the same types
    private static bool ParametersMatch(ModuleDefinition module, MethodDefinition target, MethodInfo system)
    {
        var signature = (MethodSignature)module.DefaultImporter.ImportMethod(system).Signature!;
        if (signature.ParameterTypes.Count != target.Parameters.Count)
            return false;
        for (var i = 0; i < target.Parameters.Count; i++)
            if (!SignatureComparer.Default.Equals(signature.ParameterTypes[i], target.Parameters[i].ParameterType))
                return false;
        return true;
    }

    private static void EmitForward(TypeRewriteContext typeContext, MethodDefinition forwarder, MethodDefinition target)
    {
        var body = forwarder.CilMethodBody!.Instructions;
        body.Add(CilOpCodes.Ldarg_0);
        foreach (var parameter in forwarder.Parameters)
            body.Add(CilOpCodes.Ldarg, parameter);
        IMethodDefOrRef targetRef = typeContext.NewType.GenericParameters.Count == 0
            ? target
            : typeContext.SelfSubstitutedRef.CreateMemberReference(target.Name!, target.Signature!);
        body.Add(CilOpCodes.Callvirt, targetRef);
        body.Add(CilOpCodes.Ret);
    }

    // void CopyTo(T[] array, int index) over the il2cpp enumerator, the il2cpp CopyTo wants an il2cpp array
    private static void EmitCopyTo(RewriteGlobalContext context, MethodDefinition forwarder)
    {
        var element = new GenericParameterSignature(GenericParameterType.Type, 0);
        var enumerable = context.CorLib.GetTypeByName("System.Collections.Generic.IEnumerable`1");
        var enumeratorGeneric = context.CorLib.GetTypeByName("System.Collections.Generic.IEnumerator`1");
        var enumerator = context.CorLib.GetTypeByName("System.Collections.IEnumerator");
        var getEnumerator = enumerable.TryGetMethodByName("GetEnumerator")!.NewMethod;
        var getCurrent = enumeratorGeneric.TryGetMethodByName("get_Current")!.NewMethod;
        var moveNext = enumerator.TryGetMethodByName("MoveNext")!.NewMethod;

        var enumerableRef = new GenericInstanceTypeSignature(enumerable.NewType, false, [element]).ToTypeDefOrRef();
        var enumeratorRef = new GenericInstanceTypeSignature(enumeratorGeneric.NewType, false, [element]);
        var getEnumeratorRef = enumerableRef.CreateMemberReference(getEnumerator.Name!, getEnumerator.Signature!);
        var getCurrentRef = enumeratorRef.ToTypeDefOrRef().CreateMemberReference(getCurrent.Name!, getCurrent.Signature!);

        var methodBody = forwarder.CilMethodBody!;
        var local = new CilLocalVariable(enumeratorRef);
        methodBody.LocalVariables.Add(local);
        var body = methodBody.Instructions;
        var loop = new CilInstructionLabel();
        var end = new CilInstructionLabel();

        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Callvirt, getEnumeratorRef);
        body.Add(CilOpCodes.Stloc, local);
        loop.Instruction = body.Add(CilOpCodes.Ldloc, local);
        body.Add(CilOpCodes.Callvirt, moveNext);
        body.Add(CilOpCodes.Brfalse, end);
        body.Add(CilOpCodes.Ldarg, forwarder.Parameters[0]);
        body.Add(CilOpCodes.Ldarg, forwarder.Parameters[1]);
        body.Add(CilOpCodes.Ldloc, local);
        body.Add(CilOpCodes.Callvirt, getCurrentRef);
        body.Add(CilOpCodes.Stelem, element.ToTypeDefOrRef());
        body.Add(CilOpCodes.Ldarg, forwarder.Parameters[1]);
        body.Add(CilOpCodes.Ldc_I4_1);
        body.Add(CilOpCodes.Add);
        body.Add(CilOpCodes.Starg, forwarder.Parameters[1]);
        body.Add(CilOpCodes.Br, loop);
        end.Instruction = body.Add(CilOpCodes.Ret);
    }
}
