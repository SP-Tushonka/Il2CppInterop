using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2CppInterop.Runtime.Runtime;

// Finds the generated wrapper of an il2cpp class by name, so the pool can hand out the most derived wrapper instead
// of one of the static type. Generic instantiations and arrays stay with the static type.
internal static class Il2CppWrapperTypes
{
    private static readonly ConcurrentDictionary<IntPtr, Type?> ourTypes = new();
    private static readonly ConcurrentDictionary<Type, Func<IntPtr, Il2CppObjectBase>> ourInitializers = new();

    public static Type? Resolve(IntPtr klass)
    {
        return ourTypes.GetOrAdd(klass, static k => ResolveUncached(k));
    }

    public static Il2CppObjectBase Create(Type type, IntPtr pointer)
    {
        return ourInitializers.GetOrAdd(type, static t => MakeInitializer(t))(pointer);
    }

    private static Type? ResolveUncached(IntPtr klass)
    {
        try
        {
            if (IL2CPP.il2cpp_class_is_inflated(klass) || IL2CPP.il2cpp_class_get_rank(klass) != 0)
                return null;

            var imageName = IL2CPP.il2cpp_image_get_name_(IL2CPP.il2cpp_class_get_image(klass));
            if (imageName == null)
                return null;
            if (imageName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                imageName = imageName.Substring(0, imageName.Length - 4);

            var names = new List<string>();
            var outer = klass;
            for (var declaring = klass; declaring != IntPtr.Zero; declaring = IL2CPP.il2cpp_class_get_declaring_type(declaring))
            {
                names.Add(ValidName(IL2CPP.il2cpp_class_get_name_(declaring) ?? ""));
                outer = declaring;
            }
            names.Reverse();

            var nested = string.Join("+", names);
            var ns = IL2CPP.il2cpp_class_get_namespace_(outer) ?? "";
            var typeNames = ns.Length == 0 ? new[] { nested } : new[] { ns + "." + nested, "Il2Cpp" + ns + "." + nested };

            // System.String is generated as a wrapper class whose methods treat this as a managed string, an instance
            // of it is unusable, so il2cpp strings keep the static type
            if (ns == "System" && nested == "String")
                return null;

            // The prefixed assembly goes first, .NET itself answers to mscorlib and System and forwards to the real BCL types
            foreach (var assemblyName in new[] { "Il2Cpp" + imageName, imageName })
            {
                var assembly = FindAssembly(assemblyName);
                if (assembly == null)
                    continue;
                foreach (var typeName in typeNames)
                {
                    var type = assembly.GetType(typeName);
                    if (type != null && !type.IsValueType && !type.IsGenericTypeDefinition && typeof(Il2CppObjectBase).IsAssignableFrom(type))
                        return type;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static Assembly? FindAssembly(string name)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
        if (loaded != null)
            return loaded;
        try
        {
            return Assembly.Load(name);
        }
        catch
        {
            return null;
        }
    }

    // Mirrors the generator's MakeValidInSource
    private static string ValidName(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!(char.IsDigit(c) || c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z' || c == '_' || c == '`'))
                chars[i] = '_';
        }
        var result = new string(chars);
        return result.Length > 0 && char.IsDigit(result[0]) ? "_" + result : result;
    }

    private static Func<IntPtr, Il2CppObjectBase> MakeInitializer(Type type)
    {
        var store = typeof(Il2CppObjectBase.InitializerStore<>).MakeGenericType(type);
        var initializer = store.GetProperty(nameof(Il2CppObjectBase.InitializerStore<Il2CppObjectBase>.Initializer), BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        return (Func<IntPtr, Il2CppObjectBase>)initializer;
    }
}
