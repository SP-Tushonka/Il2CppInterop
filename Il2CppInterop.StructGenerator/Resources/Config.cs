using Il2CppInterop.StructGenerator.TypeGenerators;

namespace Il2CppInterop.StructGenerator.Resources;

internal static class Config
{
    // NOTE: Ignores are handled BEFORE renames
    public static readonly string[] ClassForcedIgnores =
    {
        // Ignore the reflection structs in object-internals.h
        "Il2CppPropertyInfo",
        "Il2CppMethodInfo"
    };

    public static readonly Dictionary<string, string> ClassRenames = new()
    {
        ["TypeInfo"] = "Il2CppClass",
        ["FieldInfo"] = "Il2CppFieldInfo",
        ["EventInfo"] = "Il2CppEventInfo",
        ["PropertyInfo"] = "Il2CppPropertyInfo",
        ["MethodInfo"] = "Il2CppMethodInfo"
    };

    public static readonly Dictionary<string, Type> ClassToGenerator = new()
    {
        ["Il2CppClass"] = typeof(Il2CppClassGenerator),
        ["Il2CppType"] = typeof(Il2CppTypeGenerator),
        ["Il2CppAssembly"] = typeof(Il2CppAssemblyGenerator),
        ["Il2CppAssemblyName"] = typeof(Il2CppAssemblyNameGenerator),
        ["Il2CppFieldInfo"] = typeof(Il2CppFieldInfoGenerator),
        ["Il2CppImage"] = typeof(Il2CppImageGenerator),
        ["Il2CppEventInfo"] = typeof(Il2CppEventInfoGenerator),
        ["Il2CppException"] = typeof(Il2CppExceptionGenerator),
        ["Il2CppPropertyInfo"] = typeof(Il2CppPropertyInfoGenerator),
        ["Il2CppMethodInfo"] = typeof(Il2CppMethodInfoGenerator)
    };

    /// <summary>
    ///     Standard library headers by name, holding only the declarations the struct headers name. Template bodies
    ///     using them are never instantiated.
    /// </summary>
    public static readonly Dictionary<string, string> StlStubs = new()
    {
        ["cstddef"] = "#include <stddef.h>\n",
        ["string"] = "namespace std {\ntemplate<class C> struct char_traits;\ntemplate<class T> class allocator;\n" +
                     "template<class C, class T = char_traits<C>, class A = allocator<C> > class basic_string;\n" +
                     "typedef basic_string<char> string;\n}\n",
        ["type_traits"] = "",
        ["vector"] = "",
        ["cmath"] = "",
        ["atomic"] = "",
    };

    public static readonly string[] MetadataVersionContainers =
    {
        Path.Combine("vm", "MetadataCache.cpp"),
        Path.Combine("vm", "GlobalMetadata.cpp")
    };
}
