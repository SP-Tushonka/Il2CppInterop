using AsmResolver.DotNet;

namespace Il2CppInterop;

internal static class Utils
{
    // References between the dummy assemblies only resolve inside one shared context
    public static List<AssemblyDefinition> LoadAssembliesFrom(DirectoryInfo directoryInfo)
    {
        var context = new RuntimeContext(DotNetRuntimeInfo.NetCoreApp(6, 0));
        return directoryInfo.EnumerateFiles("*.dll").Select(f => context.LoadAssembly(f.FullName)).ToList();
    }
}
