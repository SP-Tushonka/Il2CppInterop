using AsmResolver.DotNet;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;

namespace Il2CppInterop.Generator.Passes;

public static class Pass13FillGenericConstraints
{
    public static void DoPass(RewriteGlobalContext context)
    {
        foreach (var assemblyContext in context.Assemblies)
        {
            foreach (var typeContext in assemblyContext.Types)
            {
                for (var i = 0; i < typeContext.OriginalType.GenericParameters.Count; i++)
                {
                    ConstraintRewriter.Rewrite(typeContext.OriginalType.GenericParameters[i], typeContext.NewType.GenericParameters[i],
                        assemblyContext.Imports, type => assemblyContext.RewriteTypeRef(type));
                }
            }
        }
    }
}
