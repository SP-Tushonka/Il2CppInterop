using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Generator.Passes.SPT;

// il2cpp events become C# events typed by the System delegate, so += takes a lambda. The accessors convert and call
// the generated add_ and remove_ methods, which keep their names for patches. C# rejects an event whose type is not
// a CLR delegate, which is why the il2cpp delegate wrapper cannot be the event type. A field like event names its
// backing field after the event, that field's property moves to <Name>Field so the event can own the name.
public static class PassSptEvents
{
    public static void DoPass(RewriteGlobalContext context)
    {
        if (!context.Options.GenerateEvents)
            return;

        var added = 0;
        var skipped = 0;
        foreach (var assemblyContext in context.Assemblies)
        {
            var module = assemblyContext.NewAssembly.ManifestModule!;
            foreach (var typeContext in assemblyContext.Types)
                foreach (var original in typeContext.OriginalType.Events)
                {
                    if (original.AddMethod == null || original.RemoveMethod == null || original.EventType == null)
                        continue;

                    var add = typeContext.TryGetMethodByOldMethod(original.AddMethod)?.NewMethod;
                    var remove = typeContext.TryGetMethodByOldMethod(original.RemoveMethod)?.NewMethod;
                    if (add == null || remove == null || !add.IsPublic || !remove.IsPublic || add.Parameters.Count != 1 || remove.Parameters.Count != 1)
                        continue;

                    var conversion = FindConversion(context, module, add.Parameters[0].ParameterType);
                    var name = original.Name.MakeValidInSource();
                    if (conversion == null || !ClaimName(typeContext.NewType, name))
                    {
                        skipped++;
                        continue;
                    }

                    var (systemType, convert) = conversion.Value;
                    var addAccessor = Accessor(typeContext, "add_" + name + "_Managed", add, systemType, convert);
                    var removeAccessor = Accessor(typeContext, "remove_" + name + "_Managed", remove, systemType, convert);
                    typeContext.NewType.Events.Add(new EventDefinition(name, EventAttributes.None, systemType.ToTypeDefOrRef())
                    {
                        AddMethod = addAccessor,
                        RemoveMethod = removeAccessor,
                    });
                    added++;
                }
        }

        Logger.Instance.LogInformation("{Added} events created, {Skipped} kept as add_/remove_ methods", added, skipped);
    }

    // The System delegate type and the conversion Pass60 generated for an il2cpp delegate type, null for anything else
    private static (TypeSignature SystemType, IMethodDefOrRef Conversion)? FindConversion(RewriteGlobalContext context, ModuleDefinition module, TypeSignature parameterType)
    {
        GenericInstanceTypeSignature? instance = null;
        TypeDefinition? definition;
        if (parameterType is GenericInstanceTypeSignature generic)
        {
            instance = generic;
            definition = generic.GenericType.Resolve();
        }
        else if (parameterType is TypeDefOrRefSignature plain)
        {
            definition = plain.Type.Resolve();
        }
        else
        {
            return null;
        }

        if (definition == null || !IsIl2CppDelegate(context, definition))
            return null;

        var conversion = definition.Methods.FirstOrDefault(m =>
            m.Name == "op_Implicit" && m.Parameters.Count == 1 && m.IsStatic
            && (m.Parameters[0].ParameterType.FullName.StartsWith("System.Action") || m.Parameters[0].ParameterType.FullName.StartsWith("System.Func")));
        if (conversion == null)
            return null;

        var systemType = conversion.Parameters[0].ParameterType;
        IMethodDefOrRef conversionRef = conversion;
        if (instance != null)
        {
            systemType = systemType.InstantiateGenericTypes(new GenericContext(instance, null));
            conversionRef = instance.ToTypeDefOrRef().CreateMemberReference(conversion.Name!, conversion.Signature!);
        }

        return (module.DefaultImporter.ImportTypeSignature(systemType), module.DefaultImporter.ImportMethod(conversionRef));
    }

    private static bool IsIl2CppDelegate(RewriteGlobalContext context, TypeDefinition definition)
    {
        try
        {
            return context.GetContextForNewType(definition).OriginalType.BaseType?.FullName == "System.MulticastDelegate";
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private static MethodDefinition Accessor(TypeRewriteContext typeContext, string name, MethodDefinition target, TypeSignature systemType, IMethodDefOrRef convert)
    {
        var type = typeContext.NewType;
        var module = type.DeclaringModule!;
        var attributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | (target.IsStatic ? MethodAttributes.Static : 0);
        var signature = target.IsStatic
            ? MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [systemType])
            : MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, [systemType]);
        var accessor = new MethodDefinition(name, attributes, signature);
        accessor.Parameters[0].GetOrCreateDefinition().Name = "value";
        accessor.CilMethodBody = new();
        var body = accessor.CilMethodBody.Instructions;
        if (!target.IsStatic)
            body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Ldarg, accessor.Parameters[0]);
        body.Add(CilOpCodes.Call, convert);
        IMethodDefOrRef targetRef = type.GenericParameters.Count == 0
            ? target
            : typeContext.SelfSubstitutedRef.CreateMemberReference(target.Name!, target.Signature!);
        body.Add(target.IsStatic ? CilOpCodes.Call : CilOpCodes.Callvirt, targetRef);
        body.Add(CilOpCodes.Ret);
        type.Methods.Add(accessor);
        return accessor;
    }

    // The backing field's property moves aside, anything else holding the name keeps it and the event is skipped
    private static bool ClaimName(TypeDefinition type, string name)
    {
        if (type.Methods.Any(m => m.Name == name) || type.NestedTypes.Any(t => t.Name == name) || type.Events.Any(e => e.Name == name) || type.Fields.Any(f => f.Name == name))
            return false;

        var property = type.Properties.FirstOrDefault(p => p.Name == name);
        if (property == null)
            return true;

        var moved = name + "Field";
        if (type.Properties.Any(p => p.Name == moved) || type.Methods.Any(m => m.Name == moved))
            return false;

        property.Name = moved;
        if (property.GetMethod != null)
            property.GetMethod.Name = "get_" + moved;
        if (property.SetMethod != null)
            property.SetMethod.Name = "set_" + moved;
        return true;
    }
}
