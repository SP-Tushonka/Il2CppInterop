using System.Reflection;
using System.Reflection.Emit;

namespace Il2CppInterop.Common;

public static class Il2CppInteropUtils
{
    private static FieldInfo? GetFieldInfoFromMethod(MethodBase method, string prefix)
    {
        var body = method.GetMethodBody();
        if (body == null) throw new ArgumentException("Target method may not be abstract");
        var methodModule = method.DeclaringType.Assembly.Modules.Single();
        foreach (var (opCode, opArg) in MiniIlParser.Decode(body.GetILAsByteArray()))
        {
            if (opCode != OpCodes.Ldsfld) continue;

            var fieldInfo = methodModule.ResolveField((int)opArg, method.DeclaringType.GenericTypeArguments, method.GetGenericArguments());
            if (fieldInfo == null) continue;

            // Accessors load the cached offset field, the field info pointer lives in the NativeFieldInfoPtr_ field of the same name
            if (prefix == "NativeFieldInfoPtr_" && fieldInfo.Name.StartsWith("NativeFieldOffset_"))
                return fieldInfo.DeclaringType!.GetField(prefix + fieldInfo.Name.Substring("NativeFieldOffset_".Length), BindingFlags.Static | BindingFlags.NonPublic);

            if (fieldInfo.FieldType != typeof(IntPtr)) continue;

            if (fieldInfo.Name.StartsWith(prefix)) return fieldInfo;

            // Resolve generic method info pointer fields
            if (method.IsGenericMethod && fieldInfo.DeclaringType.Name.StartsWith("MethodInfoStoreGeneric_") && fieldInfo.Name == "Pointer") return fieldInfo;
        }

        return null;
    }

    public static FieldInfo GetIl2CppMethodInfoPointerFieldForGeneratedMethod(MethodBase method)
    {
        return GetFieldInfoFromMethod(method, "NativeMethodInfoPtr_");
    }

    public static FieldInfo GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(MethodBase method)
    {
        return GetFieldInfoFromMethod(method, "NativeFieldInfoPtr_");
    }
}
