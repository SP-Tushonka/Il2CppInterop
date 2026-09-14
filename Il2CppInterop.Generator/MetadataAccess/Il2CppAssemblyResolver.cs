using AsmResolver.DotNet;

namespace Il2CppInterop.Generator.MetadataAccess;

// Owns the runtime context the generated assemblies live in. Nothing outside that set is meant to resolve,
// so the fallback resolver never finds anything.
internal sealed class Il2CppAssemblyResolver : IAssemblyResolver
{
    public RuntimeContext Context { get; }

    public Il2CppAssemblyResolver()
    {
        Context = new RuntimeContext(Utils.CorlibReferences.TargetRuntime, this);
    }

    public ResolutionStatus Resolve(AssemblyDescriptor assembly, ModuleDefinition originModule,
        out AssemblyDefinition result)
    {
        result = null!;
        return ResolutionStatus.AssemblyNotFound;
    }
}
