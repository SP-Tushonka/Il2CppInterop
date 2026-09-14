using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;

namespace Il2CppInterop.Generator.Passes;

public static class Pass19CopyMethodParameters
{
    public static void DoPass(RewriteGlobalContext context)
    {
        foreach (var assemblyContext in context.Assemblies)
        {
            foreach (var typeContext in assemblyContext.Types)
            {
                foreach (var methodRewriteContext in typeContext.Methods)
                {
                    var originalMethod = methodRewriteContext.OriginalMethod;
                    var newMethod = methodRewriteContext.NewMethod;

                    foreach (var originalMethodParameter in originalMethod.Parameters)
                    {
                        var newName = originalMethodParameter.Name.IsObfuscated(context.Options)
                            ? $"param_{originalMethodParameter.Sequence}"
                            : originalMethodParameter.Name;

                        var newParameter = newMethod.AddParameter(
                            assemblyContext.RewriteTypeRef(originalMethodParameter.ParameterType),
                            newName,
                            originalMethodParameter.GetOrCreateDefinition().Attributes & ~ParameterAttributes.HasFieldMarshal);

                        if (originalMethodParameter.IsParamsArray())
                        {
                            newParameter.Definition!.Constant = null;
                            newParameter.Definition.IsOptional = true;
                        }
                        else
                        {
                            newParameter.Definition!.Constant = originalMethodParameter.Definition!.Constant;
                        }
                    }

                    var paramsMethod = context.CreateParamsMethod(originalMethod, newMethod, assemblyContext.Imports,
                        type => assemblyContext.RewriteTypeRef(type));
                    if (paramsMethod != null) typeContext.NewType.Methods.Add(paramsMethod);
                }
            }
        }

        // Binding compares signatures, so it waits until the parameters exist
        foreach (var assemblyContext in context.Assemblies)
            foreach (var typeContext in assemblyContext.Types)
                foreach (var methodRewriteContext in typeContext.Methods)
                    BindExplicitImplementation(context, assemblyContext, typeContext, methodRewriteContext);
    }

    // Arrays rewrite differently for a generic parameter and a concrete element type. A body whose signature
    // no longer matches the instantiated interface method stays a plain public method.
    private static void BindExplicitImplementation(RewriteGlobalContext context, AssemblyRewriteContext assemblyContext,
        TypeRewriteContext typeContext, MethodRewriteContext methodContext)
    {
        var declaration = methodContext.ExplicitInterfaceMethod;
        if (declaration == null)
            return;

        var interfaceMethod = declaration.Resolve();
        var interfaceMethodContext = interfaceMethod?.DeclaringType == null
            ? null
            : context.GetNewTypeForOriginal(interfaceMethod.DeclaringType).TryGetMethodByOldMethod(interfaceMethod);
        if (interfaceMethodContext == null)
        {
            methodContext.DropExplicitImplementation();
            return;
        }

        var interfaceRef = assemblyContext.RewriteTypeRef(declaration.DeclaringType!);
        var declarationSignature = interfaceMethodContext.NewMethod.Signature!;
        var instantiated = interfaceRef.ToTypeSignature() is GenericInstanceTypeSignature instance
            ? declarationSignature.InstantiateGenericTypes(new GenericContext(instance, null))
            : declarationSignature;
        if (!SignatureComparer.Default.Equals(instantiated, methodContext.NewMethod.Signature))
        {
            methodContext.DropExplicitImplementation();
            return;
        }

        var declarationRef = new MemberReference(interfaceRef, interfaceMethodContext.NewMethod.Name, declarationSignature);
        typeContext.NewType.MethodImplementations.Add(new MethodImplementation(
            typeContext.NewType.DeclaringModule!.DefaultImporter.ImportMethod(declarationRef), methodContext.NewMethod));

        var token = interfaceMethod!.ExtractToken();
        if (token == 0 || methodContext.OriginalMethod.HasGenericParameters())
            return;

        var imports = assemblyContext.Imports;
        var pointerField = new FieldDefinition("NativeInterfaceMethodInfoPtr_" + methodContext.UnmangledNameWithSignature,
            FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly, imports.Module.IntPtr());
        typeContext.NewType.Fields.Add(pointerField);
        methodContext.InterfaceMethodInfoPointerField = new MemberReference(typeContext.SelfSubstitutedRef, pointerField.Name, new FieldSignature(imports.Module.IntPtr()));
        methodContext.ExplicitInterfaceRef = interfaceRef;
        methodContext.ExplicitInterfaceToken = (int)token;
    }
}
