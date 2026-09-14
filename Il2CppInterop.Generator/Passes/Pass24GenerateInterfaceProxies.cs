using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;

namespace Il2CppInterop.Generator.Passes;

// A value typed by an interface still needs a concrete object to hold the pointer. Every interface nests
// an Il2CppProxy implementing only that interface and the runtime instantiates it for returns and casts.
public static class Pass24GenerateInterfaceProxies
{
    public const string ProxyName = "Il2CppProxy";

    public static void DoPass(RewriteGlobalContext context)
    {
        foreach (var assemblyContext in context.Assemblies)
            foreach (var typeContext in assemblyContext.Types)
                if (typeContext.OriginalType.IsInterface)
                    GenerateProxy(assemblyContext, typeContext);
    }

    private static void GenerateProxy(AssemblyRewriteContext assemblyContext, TypeRewriteContext typeContext)
    {
        var imports = assemblyContext.Imports;
        var interfaceType = typeContext.NewType;
        var proxy = new TypeDefinition(null, ProxyName,
            TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            imports.Il2CppObjectBase.ToTypeDefOrRef());
        interfaceType.NestedTypes.Add(proxy);

        // Nested types redeclare the enclosing generic parameters, same as the C# compiler does
        foreach (var interfaceParameter in interfaceType.GenericParameters)
        {
            var proxyParameter = new GenericParameter(interfaceParameter.Name, interfaceParameter.Attributes);
            foreach (var constraint in interfaceParameter.Constraints)
                proxyParameter.Constraints.Add(new GenericParameterConstraint(constraint.Constraint));
            proxy.GenericParameters.Add(proxyParameter);
        }

        proxy.Interfaces.Add(new InterfaceImplementation(typeContext.SelfSubstitutedRef));

        var pointerCtor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(imports.Module.Void()));
        pointerCtor.AddParameter(imports.Module.IntPtr(), "pointer");
        pointerCtor.CilMethodBody = new();
        var ctorBody = pointerCtor.CilMethodBody.Instructions;
        ctorBody.Add(OpCodes.Ldarg_0);
        ctorBody.Add(OpCodes.Ldarg_1);
        ctorBody.Add(OpCodes.Call, new MemberReference(imports.Il2CppObjectBase.ToTypeDefOrRef(), ".ctor",
            MethodSignature.CreateInstance(imports.Module.Void(), [imports.Module.IntPtr()])));
        ctorBody.Add(OpCodes.Ret);
        proxy.Methods.Add(pointerCtor);

        TypeSignature proxySignature;
        if (proxy.GenericParameters.Count == 0)
        {
            proxySignature = proxy.ToTypeSignature();
        }
        else
        {
            var instance = new GenericInstanceTypeSignature(proxy, false);
            foreach (var proxyParameter in proxy.GenericParameters)
                instance.TypeArguments.Add(proxyParameter.ToTypeSignature());
            proxySignature = instance;
        }

        // The proxy shares the interface's il2cpp class so class pointer lookups on a proxy instance resolve
        var proxyStore = new GenericInstanceTypeSignature(imports.Il2CppClassPointerStore.ToTypeDefOrRef(),
            imports.Il2CppClassPointerStore.IsValueType(), [proxySignature]);
        var proxyPointerField = ReferenceCreator.CreateFieldReference("NativeClassPtr", imports.Module.IntPtr(),
            interfaceType.DeclaringModule!.DefaultImporter.ImportType(proxyStore.ToTypeDefOrRef()));
        var staticCtor = proxy.GetOrCreateStaticConstructor();
        var staticBody = staticCtor.CilMethodBody!.Instructions;
        staticBody.Clear();
        staticBody.Add(OpCodes.Ldsfld, typeContext.ClassPointerFieldRef);
        staticBody.Add(OpCodes.Stsfld, proxyPointerField);
        staticBody.Add(OpCodes.Ret);
    }
}
