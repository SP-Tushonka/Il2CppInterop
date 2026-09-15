using System.Collections.Generic;
using AsmResolver.DotNet;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;

namespace Il2CppInterop.Generator.Passes.SPT;

// The seal only stops injected types from deriving from the wrapper. Static classes, delegates and value type
// wrappers keep it, nothing can subclass those anyway.
public static class PassSptUnsealClasses
{
    public static void DoPass(RewriteGlobalContext context)
    {
        if (!context.Options.UnsealClasses)
            return;

        foreach (var assemblyContext in context.Assemblies)
        {
            foreach (var type in assemblyContext.NewAssembly.ManifestModule!.GetAllTypes())
            {
                TypeRewriteContext typeContext;
                try
                {
                    typeContext = context.GetContextForNewType(type);
                }
                catch (KeyNotFoundException)
                {
                    continue;
                }

                if (typeContext.OriginalType != null ? KeepsSeal(typeContext.OriginalType) : KeepsSeal(type))
                    continue;

                type.IsSealed = false;
            }
        }
    }

    private static bool KeepsSeal(TypeDefinition type)
    {
        if (!type.IsSealed || type.IsAbstract || type.IsInterface || type.IsEnum || type.IsValueType || type.IsValueType())
            return true;
        return type.BaseType?.FullName is "System.MulticastDelegate" or "System.Delegate" or "Il2CppSystem.MulticastDelegate" or "Il2CppSystem.Delegate";
    }
}
