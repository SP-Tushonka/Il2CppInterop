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
            foreach (var typeContext in assemblyContext.Types)
            {
                var original = typeContext.OriginalType;
                if (!original.IsSealed || original.IsAbstract || original.IsInterface || original.IsEnum || original.IsValueType())
                    continue;
                if (original.BaseType?.FullName is "System.MulticastDelegate" or "System.Delegate")
                    continue;

                typeContext.NewType.IsSealed = false;
            }
        }
    }
}
