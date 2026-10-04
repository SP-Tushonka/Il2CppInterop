using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Iced.Intel;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using Microsoft.Extensions.Logging;
using Decoder = Iced.Intel.Decoder;

namespace Il2CppInterop.Runtime;

public static unsafe partial class IL2CPP
{
    private static readonly Dictionary<string, IntPtr> ourImagesMap = new();

    static IL2CPP()
    {
        var domain = il2cpp_domain_get();
        if (domain == IntPtr.Zero)
        {
            Logger.Instance.LogError("No il2cpp domain found; sad!");
            return;
        }

        uint assembliesCount = 0;
        var assemblies = il2cpp_domain_get_assemblies(domain, ref assembliesCount);
        for (var i = 0; i < assembliesCount; i++)
        {
            var image = il2cpp_assembly_get_image(assemblies[i]);
            var name = il2cpp_image_get_name_(image)!;
            ourImagesMap[name] = image;
        }
    }

    internal static IntPtr GetIl2CppImage(string name)
    {
        if (ourImagesMap.ContainsKey(name)) return ourImagesMap[name];
        return IntPtr.Zero;
    }

    internal static IntPtr[] GetIl2CppImages()
    {
        return ourImagesMap.Values.ToArray();
    }

    public static IntPtr GetIl2CppClass(string assemblyName, string namespaze, string className)
    {
        if (!ourImagesMap.TryGetValue(assemblyName, out var image))
        {
            Logger.Instance.LogError("Assembly {AssemblyName} is not registered in il2cpp", assemblyName);
            return IntPtr.Zero;
        }

        var clazz = il2cpp_class_from_name(image, namespaze, className);
        return clazz;
    }

    public static IntPtr GetIl2CppField(IntPtr clazz, string fieldName)
    {
        if (clazz == IntPtr.Zero) return IntPtr.Zero;

        var field = il2cpp_class_get_field_from_name(clazz, fieldName);
        if (field == IntPtr.Zero)
            Logger.Instance.LogError(
                "Field {FieldName} was not found on class {ClassName}", fieldName, il2cpp_class_get_name_(clazz));
        return field;
    }

    // A wrapper's static constructor looks up every method of its class, which a scan per lookup made quadratic
    private static readonly ConcurrentDictionary<IntPtr, Dictionary<int, IntPtr>> ourMethodsByToken = new();

    public static IntPtr GetIl2CppMethodByToken(IntPtr clazz, int token)
    {
        if (clazz == IntPtr.Zero)
            return NativeStructUtils.GetMethodInfoForMissingMethod(token.ToString());

        var methods = ourMethodsByToken.GetOrAdd(clazz, static klass =>
        {
            var byToken = new Dictionary<int, IntPtr>();
            var iter = IntPtr.Zero;
            IntPtr method;
            while ((method = il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                byToken.TryAdd((int)il2cpp_method_get_token(method), method);
            return byToken;
        });
        if (methods.TryGetValue(token, out var found))
            return found;

        var className = il2cpp_class_get_name_(clazz);
        Logger.Instance.LogTrace("Unable to find method {ClassName}::{Token}", className, token);

        return NativeStructUtils.GetMethodInfoForMissingMethod(className + "::" + token);
    }

    public static IntPtr GetIl2CppMethod(IntPtr clazz, bool isGeneric, string methodName, string returnTypeName,
        params string[] argTypes)
    {
        if (clazz == IntPtr.Zero)
            return NativeStructUtils.GetMethodInfoForMissingMethod(methodName + "(" + string.Join(", ", argTypes) +
                                                                   ")");

        returnTypeName = Regex.Replace(returnTypeName, "\\`\\d+", "").Replace('/', '.').Replace('+', '.');
        for (var index = 0; index < argTypes.Length; index++)
        {
            var argType = argTypes[index];
            argTypes[index] = Regex.Replace(argType, "\\`\\d+", "").Replace('/', '.').Replace('+', '.');
        }

        var methodsSeen = 0;
        var lastMethod = IntPtr.Zero;
        var iter = IntPtr.Zero;
        IntPtr method;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
        {
            if (il2cpp_method_get_name_(method) != methodName)
                continue;

            if (il2cpp_method_get_param_count(method) != argTypes.Length)
                continue;

            if (il2cpp_method_is_generic(method) != isGeneric)
                continue;

            var returnType = il2cpp_method_get_return_type(method);
            var returnTypeNameActual = il2cpp_type_get_name_(returnType);
            if (returnTypeNameActual != returnTypeName)
                continue;

            methodsSeen++;
            lastMethod = method;

            var badType = false;
            for (var i = 0; i < argTypes.Length; i++)
            {
                var paramType = il2cpp_method_get_param(method, (uint)i);
                var typeName = il2cpp_type_get_name_(paramType);
                if (typeName != argTypes[i])
                {
                    badType = true;
                    break;
                }
            }

            if (badType) continue;

            return method;
        }

        var className = il2cpp_class_get_name_(clazz);

        if (methodsSeen == 1)
        {
            Logger.Instance.LogTrace(
                "Method {ClassName}::{MethodName} was stubbed with a random matching method of the same name", className, methodName);
            Logger.Instance.LogTrace(
                "Stubby return type/target: {LastMethod} / {ReturnTypeName}", il2cpp_type_get_name_(il2cpp_method_get_return_type(lastMethod)), returnTypeName);
            Logger.Instance.LogTrace("Stubby parameter types/targets follow:");
            for (var i = 0; i < argTypes.Length; i++)
            {
                var paramType = il2cpp_method_get_param(lastMethod, (uint)i);
                var typeName = il2cpp_type_get_name_(paramType);
                Logger.Instance.LogTrace("    {TypeName} / {ArgType}", typeName, argTypes[i]);
            }

            return lastMethod;
        }

        Logger.Instance.LogTrace("Unable to find method {ClassName}::{MethodName}; signature follows", className, methodName);
        Logger.Instance.LogTrace("    return {ReturnTypeName}", returnTypeName);
        foreach (var argType in argTypes)
            Logger.Instance.LogTrace("    {ArgType}", argType);
        Logger.Instance.LogTrace("Available methods of this name follow:");
        iter = IntPtr.Zero;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
        {
            if (il2cpp_method_get_name_(method) != methodName)
                continue;

            var nParams = il2cpp_method_get_param_count(method);
            Logger.Instance.LogTrace("Method starts");
            Logger.Instance.LogTrace(
                "     return {MethodTypeName}", il2cpp_type_get_name_(il2cpp_method_get_return_type(method)));
            for (var i = 0; i < nParams; i++)
            {
                var paramType = il2cpp_method_get_param(method, (uint)i);
                var typeName = il2cpp_type_get_name_(paramType);
                Logger.Instance.LogTrace("    {TypeName}", typeName);
            }

            return method;
        }

        return NativeStructUtils.GetMethodInfoForMissingMethod(className + "::" + methodName + "(" +
                                                               string.Join(", ", argTypes) + ")");
    }

    public static string? Il2CppStringToManaged(IntPtr il2CppString)
    {
        if (il2CppString == IntPtr.Zero) return null;

        var length = il2cpp_string_length(il2CppString);
        var chars = il2cpp_string_chars(il2CppString);

        return new string(chars, 0, length);
    }

    public static IntPtr ManagedStringToIl2Cpp(string? str)
    {
        if (str == null) return IntPtr.Zero;

        fixed (char* chars = str)
        {
            return il2cpp_string_new_utf16(chars, str.Length);
        }
    }

    public static IntPtr Il2CppObjectBaseToPtr(Il2CppObjectBase obj)
    {
        return obj?.Pointer ?? IntPtr.Zero;
    }

    public static IntPtr Il2CppObjectBaseToPtrNotNull(Il2CppObjectBase obj)
    {
        return obj?.Pointer ?? throw new NullReferenceException();
    }

    /// <summary>
    /// Returns the native body of an instance method when it is at most one load from its receiver, one constant,
    /// or one store of its first argument into the receiver, followed by a ret. Such a body cannot throw, so wrappers
    /// call it directly instead of through il2cpp_runtime_invoke.
    /// </summary>
    public static IntPtr GetDirectCallPointer(IntPtr methodInfo)
    {
        if (methodInfo == IntPtr.Zero || !Environment.Is64BitProcess)
            return IntPtr.Zero;

        var code = UnityVersionHandler.Wrap((Il2CppMethodInfo*)methodInfo).MethodPointer;
        if (code == IntPtr.Zero)
            return IntPtr.Zero;

        var decoder = Decoder.Create(64, new UnmanagedCodeReader((byte*)code, 32));
        decoder.IP = (ulong)code;

        decoder.Decode(out var first);
        if (decoder.LastError != DecoderError.None)
            return IntPtr.Zero;
        if (IsRet(first))
            return code;
        if (!IsReceiverLoad(first) && !IsConstant(first) && !IsReceiverStore(first))
            return IntPtr.Zero;

        decoder.Decode(out var ret);
        return decoder.LastError == DecoderError.None && IsRet(ret) ? code : IntPtr.Zero;
    }

    private sealed class UnmanagedCodeReader(byte* code, int length) : CodeReader
    {
        private int myOffset;

        public override int ReadByte() => myOffset < length ? code[myOffset++] : -1;
    }

    // MSVC ends an empty function with ret 0
    private static bool IsRet(in Instruction instruction) =>
        instruction.Mnemonic == Mnemonic.Ret && (instruction.OpCount == 0 || instruction.Immediate16 == 0);

    // A Harmony detour or any other jmp at the entry fails these, so a patched method keeps the invoke path
    private static bool IsReceiverLoad(in Instruction instruction)
    {
        if (instruction.Mnemonic is not (Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx or Mnemonic.Movsxd or
            Mnemonic.Movss or Mnemonic.Movsd or Mnemonic.Movq or Mnemonic.Movd))
            return false;

        if (instruction.OpCount != 2 || instruction.Op0Kind != OpKind.Register || !IsReceiverMemory(instruction, 1))
            return false;

        var destination = instruction.Op0Register;
        return destination == Register.XMM0 || destination.GetFullRegister() == Register.RAX;
    }

    private static bool IsReceiverStore(in Instruction instruction)
    {
        if (instruction.Mnemonic is not (Mnemonic.Mov or Mnemonic.Movss or Mnemonic.Movsd or Mnemonic.Movq or Mnemonic.Movd))
            return false;

        if (instruction.OpCount != 2 || !IsReceiverMemory(instruction, 0) || instruction.Op1Kind != OpKind.Register)
            return false;

        var source = instruction.Op1Register;
        return source == Register.XMM1 || source.GetFullRegister() == Register.RDX;
    }

    private static bool IsReceiverMemory(in Instruction instruction, int operand) =>
        instruction.GetOpKind(operand) == OpKind.Memory && instruction.MemoryBase == Register.RCX &&
        instruction.MemoryIndex == Register.None && instruction.SegmentPrefix == Register.None;

    // Constants come as an immediate, a register zeroing itself, or a read from the image's constant data
    private static bool IsConstant(in Instruction instruction)
    {
        if (instruction.OpCount != 2 || instruction.Op0Kind != OpKind.Register)
            return false;

        var destination = instruction.Op0Register;
        if (destination.GetFullRegister() == Register.RAX)
            return instruction.Mnemonic == Mnemonic.Mov && instruction.Op1Kind is OpKind.Immediate8 or OpKind.Immediate32 or OpKind.Immediate64 or OpKind.Immediate32to64 ||
                   instruction.Mnemonic == Mnemonic.Xor && instruction.Op1Kind == OpKind.Register && instruction.Op1Register == destination;

        if (destination != Register.XMM0)
            return false;
        return instruction.Mnemonic is Mnemonic.Xorps or Mnemonic.Xorpd && instruction.Op1Kind == OpKind.Register && instruction.Op1Register == Register.XMM0 ||
               instruction.Mnemonic is Mnemonic.Movss or Mnemonic.Movsd && instruction.Op1Kind == OpKind.Memory && instruction.IsIPRelativeMemoryOperand;
    }

    // A null wrapper passed for a struct parameter stands for the struct's default. il2cpp_object_new hands back a zeroed box.
    public static IntPtr Il2CppValueTypeToPtr(Il2CppObjectBase obj, IntPtr klass)
    {
        if (obj != null)
        {
            return obj.Pointer;
        }

        if (klass == IntPtr.Zero)
        {
            throw new NullReferenceException();
        }

        return il2cpp_object_new(klass);
    }

    // The CLR has already picked the override, so a wrapper runs on an injected object only for a base call or a method it
    // does not override. il2cpp dispatch would send a base call straight back to the override.
    public static IntPtr ResolveVirtualMethod(IntPtr obj, IntPtr method)
    {
        var injectedImage = Injection.InjectorHelpers.InjectedImage;
        // Every virtual call passes here. image is the first field of Il2CppClass in every supported version.
        if (obj != IntPtr.Zero && injectedImage != null && *(IntPtr*)il2cpp_object_get_class(obj) == (IntPtr)injectedImage.ImagePointer)
        {
            uint implementationFlags = 0;
            if ((il2cpp_method_get_flags(method, ref implementationFlags) & (uint)Il2CppMethodFlags.METHOD_ATTRIBUTE_ABSTRACT) == 0)
                return method;
        }

        return il2cpp_object_get_virtual_method(obj, method);
    }

    public static IntPtr GetIl2CppNestedType(IntPtr enclosingType, string nestedTypeName)
    {
        if (enclosingType == IntPtr.Zero) return IntPtr.Zero;

        var iter = IntPtr.Zero;
        IntPtr nestedTypePtr;
        if (il2cpp_class_is_inflated(enclosingType))
        {
            Logger.Instance.LogTrace("Original class was inflated, falling back to reflection");

            return RuntimeReflectionHelper.GetNestedTypeViaReflection(enclosingType, nestedTypeName);
        }

        while ((nestedTypePtr = il2cpp_class_get_nested_types(enclosingType, ref iter)) != IntPtr.Zero)
            if (il2cpp_class_get_name_(nestedTypePtr) == nestedTypeName)
                return nestedTypePtr;

        Logger.Instance.LogError(
            "Nested type {NestedTypeName} on {EnclosingTypeName} not found!", nestedTypeName, il2cpp_class_get_name_(enclosingType));

        return IntPtr.Zero;
    }

    public static void ThrowIfNull(object arg)
    {
        if (arg == null)
            throw new NullReferenceException();
    }

    public static T ResolveICall<T>(string signature) where T : Delegate
    {
        var icallPtr = il2cpp_resolve_icall(signature);
        if (icallPtr == IntPtr.Zero)
        {
            Logger.Instance.LogTrace("ICall {Signature} not resolved", signature);
            return GenerateDelegateForMissingICall<T>(signature);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(icallPtr);
    }

    private static T GenerateDelegateForMissingICall<T>(string signature) where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke")!;

        var trampoline = new DynamicMethod("(missing icall delegate) " + typeof(T).FullName,
            invoke.ReturnType, invoke.GetParameters().Select(it => it.ParameterType).ToArray(), typeof(IL2CPP), true);
        var bodyBuilder = trampoline.GetILGenerator();

        bodyBuilder.Emit(OpCodes.Ldstr, $"ICall with signature {signature} was not resolved");
        bodyBuilder.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor(new[] { typeof(string) })!);
        bodyBuilder.Emit(OpCodes.Throw);

        return (T)trampoline.CreateDelegate(typeof(T));
    }

    private readonly record struct NullableLayout(IntPtr ValueClass, int HasValueOffset, int ValueOffset);

    private static readonly ConcurrentDictionary<IntPtr, NullableLayout> NullableLayouts = new();

    private static NullableLayout GetNullableLayout(IntPtr nullableClass)
    {
        return NullableLayouts.GetOrAdd(nullableClass, static klass =>
        {
            il2cpp_runtime_class_init(klass);
            var header = IntPtr.Size * 2;
            var valueField = GetIl2CppField(klass, "value");
            var hasValue = (int)il2cpp_field_get_offset(GetIl2CppField(klass, "hasValue")) - header;
            var value = (int)il2cpp_field_get_offset(valueField) - header;
            return new NullableLayout(il2cpp_class_from_type(il2cpp_field_get_type(valueField)), hasValue, value);
        });
    }

    private static IntPtr CreateNullableBox(IntPtr nullableClass, IntPtr valueData)
    {
        var layout = GetNullableLayout(nullableClass);
        var box = il2cpp_object_new(nullableClass);
        if (valueData == IntPtr.Zero)
            return box;

        var data = il2cpp_object_unbox(box);
        CopyValue(box, data + layout.ValueOffset, valueData, layout.ValueClass);
        *(byte*)(data + layout.HasValueOffset) = 1;
        return box;
    }

    /// <summary>
    /// Boxes the <c>Nullable&lt;T&gt;</c> stored at <paramref name="data"/>. il2cpp_value_box boxes a nullable ECMA style,
    /// null when empty and only the T payload otherwise, which the wrapper cannot read.
    /// </summary>
    public static IntPtr BoxNullable(IntPtr nullableClass, IntPtr data)
    {
        var layout = GetNullableLayout(nullableClass);
        var hasValue = *(byte*)(data + layout.HasValueOffset) != 0;
        return CreateNullableBox(nullableClass, hasValue ? data + layout.ValueOffset : IntPtr.Zero);
    }

    /// <summary>
    /// Turns what il2cpp returned for a <c>Nullable&lt;T&gt;</c>, null or a box holding only the T payload, into a real nullable box.
    /// </summary>
    public static IntPtr RebuildNullableBox(IntPtr nullableClass, IntPtr boxed)
    {
        return CreateNullableBox(nullableClass, boxed == IntPtr.Zero ? IntPtr.Zero : boxed + IntPtr.Size * 2);
    }

    // Interface calls ask these of every resolved method, and the answer for a method or class never changes
    private static readonly ConcurrentDictionary<IntPtr, bool> ourValueTypeMethods = new();
    private static readonly ConcurrentDictionary<IntPtr, bool> ourValueTypeClasses = new();

    public static bool MethodBelongsToValueType(IntPtr method)
    {
        if (ourValueTypeMethods.TryGetValue(method, out var isValueType))
            return isValueType;
        return ourValueTypeMethods[method] = il2cpp_class_is_valuetype(il2cpp_method_get_class(method));
    }

    public static bool ClassIsValueType(IntPtr klass)
    {
        if (ourValueTypeClasses.TryGetValue(klass, out var isValueType))
            return isValueType;
        return ourValueTypeClasses[klass] = il2cpp_class_is_valuetype(klass);
    }

    internal static bool IsIl2CppNullable(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition().FullName == "Il2CppSystem.Nullable`1";
    }

    // What a generic argument is never changes, so it is worked out once instead of on every generic return
    private static class GenericArgument<T>
    {
        public static readonly bool IsNullable = IsIl2CppNullable(typeof(T));

        private static int ourClassIsValueType;

        public static bool ClassIsValueType
        {
            get
            {
                if (ourClassIsValueType == 0)
                    ourClassIsValueType = il2cpp_class_is_valuetype(Il2CppClassPointerStore<T>.NativeClassPtr) ? 1 : 2;
                return ourClassIsValueType == 1;
            }
        }
    }

    public static T? PointerToValueGeneric<T>(IntPtr objectPointer, bool isFieldPointer, bool valueTypeWouldBeBoxed)
    {
        if (GenericArgument<T>.IsNullable)
        {
            var nullableClass = Il2CppClassPointerStore<T>.NativeClassPtr;
            objectPointer = isFieldPointer || !valueTypeWouldBeBoxed
                ? BoxNullable(nullableClass, objectPointer)
                : RebuildNullableBox(nullableClass, objectPointer);
            return objectPointer == IntPtr.Zero ? default : Il2CppObjectBase.WrapValueBox<T>(objectPointer);
        }

        // A field or out storage holds a blittable value as is, so it is read without boxing it only to unbox it again
        if (typeof(T).IsValueType && (isFieldPointer || !valueTypeWouldBeBoxed))
            return Unsafe.Read<T>((void*)objectPointer);

        // At most one of these two boxes a value type: il2cpp_value_box copies from the address it is
        // given, so boxing a pointer that is already a box would copy that box's header, not the value.
        if (isFieldPointer)
        {
            if (GenericArgument<T>.ClassIsValueType)
                objectPointer = il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, objectPointer);
            else
                objectPointer = *(IntPtr*)objectPointer;
        }
        else if (!valueTypeWouldBeBoxed && GenericArgument<T>.ClassIsValueType)
        {
            objectPointer = il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, objectPointer);
        }

        if (typeof(T) == typeof(string))
            return (T)(object)Il2CppStringToManaged(objectPointer);

        if (objectPointer == IntPtr.Zero)
            return default;

        if (typeof(T).IsValueType)
            return Il2CppObjectBase.UnboxUnsafe<T>(objectPointer);

        // A struct wrapper's box was made by the invoke or the box above for this one value
        if (GenericArgument<T>.ClassIsValueType)
            return Il2CppObjectBase.WrapValueBox<T>(objectPointer);

        return Il2CppObjectPool.Get<T>(objectPointer);
    }

    public static string RenderTypeName<T>(bool addRefMarker = false)
    {
        return RenderTypeName(typeof(T), addRefMarker);
    }

    public static string RenderTypeName(Type t, bool addRefMarker = false)
    {
        if (addRefMarker) return RenderTypeName(t) + "&";
        if (t.IsArray) return RenderTypeName(t.GetElementType()) + "[]";
        if (t.IsByRef) return RenderTypeName(t.GetElementType()) + "&";
        if (t.IsPointer) return RenderTypeName(t.GetElementType()) + "*";
        if (t.IsGenericParameter) return t.Name;

        if (t.IsGenericType)
        {
            if (t.TypeHasIl2CppArrayBase())
                return RenderTypeName(t.GetGenericArguments()[0]) + "[]";

            var builder = new StringBuilder();
            builder.Append(t.GetGenericTypeDefinition().FullNameObfuscated().TrimIl2CppPrefix());
            builder.Append('<');
            var genericArguments = t.GetGenericArguments();
            for (var i = 0; i < genericArguments.Length; i++)
            {
                if (i != 0) builder.Append(',');
                builder.Append(RenderTypeName(genericArguments[i]));
            }

            builder.Append('>');
            return builder.ToString();
        }

        if (t == typeof(Il2CppStringArray))
            return "System.String[]";

        return t.FullNameObfuscated().TrimIl2CppPrefix();
    }

    private static string FullNameObfuscated(this Type t)
    {
        var obfuscatedNameAnnotations = t.GetCustomAttribute<ObfuscatedNameAttribute>();
        if (obfuscatedNameAnnotations == null) return t.FullName;
        return obfuscatedNameAnnotations.ObfuscatedName;
    }

    private static string TrimIl2CppPrefix(this string s)
    {
        return s.StartsWith("Il2Cpp") ? s.Substring("Il2Cpp".Length) : s;
    }

    private static bool TypeHasIl2CppArrayBase(this Type type)
    {
        if (type == null) return false;
        if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();
        if (type == typeof(Il2CppArrayBase<>)) return true;
        return TypeHasIl2CppArrayBase(type.BaseType);
    }

    // this is called if there's no actual il2cpp_gc_wbarrier_set_field()
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FieldWriteWbarrierStub(IntPtr obj, IntPtr targetAddress, IntPtr value)
    {
        // ignore obj
        *(IntPtr*)targetAddress = value;
    }

    // Older il2cpp builds have no barrier export, the generator makes the same choice when it writes setters
    private static class WriteBarrier
    {
        public static readonly bool Exported =
            NativeLibrary.TryGetExport(NativeLibrary.Load("GameAssembly"), "il2cpp_gc_wbarrier_set_field", out _);
    }

    /// <summary>
    /// Stores an object reference into il2cpp memory through the GC write barrier. Incremental collection misses a
    /// reference stored without it and can free the object. il2cpp ignores obj, so a byref target passes zero.
    /// </summary>
    public static void WriteReference(IntPtr obj, IntPtr target, IntPtr value)
    {
        if (WriteBarrier.Exported)
            il2cpp_gc_wbarrier_set_field(obj, target, value);
        else
            *(IntPtr*)target = value;
    }

    /// <summary>
    /// Stores an object reference through a byref, which can point into a heap object, see <see cref="WriteReference"/>.
    /// </summary>
    public static void WriteByRef(IntPtr target, IntPtr value)
    {
        WriteReference(IntPtr.Zero, target, value);
    }

    /// <summary>
    /// Copies a struct of class klass into il2cpp memory. il2cpp only exports the single slot barrier, so when the
    /// struct holds references every pointer sized slot is stored again through it after the copy.
    /// </summary>
    public static void CopyValue(IntPtr obj, IntPtr target, IntPtr source, IntPtr klass)
    {
        // Both lookups are cheaper as native calls than through a dictionary keyed by class
        uint align = 0;
        var size = il2cpp_class_value_size(klass, ref align);

        Buffer.MemoryCopy((void*)source, (void*)target, size, size);
        if (!WriteBarrier.Exported || !il2cpp_class_has_references(klass))
            return;

        for (var offset = 0; offset + IntPtr.Size <= size; offset += IntPtr.Size)
            il2cpp_gc_wbarrier_set_field(obj, target + offset, *(IntPtr*)(target + offset));
    }

    /// <summary>
    /// Stores the struct a box holds into il2cpp memory, see <see cref="CopyValue"/>.
    /// </summary>
    public static void StoreValue(IntPtr obj, IntPtr target, IntPtr box, IntPtr klass)
    {
        if (box == IntPtr.Zero)
            throw new NullReferenceException();
        CopyValue(obj, target, il2cpp_object_unbox(box), klass);
    }

    // IL2CPP Functions
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_init(IntPtr domain_name);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_init_utf16(IntPtr domain_name);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_shutdown();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_set_config_dir(IntPtr config_path);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_set_data_dir(IntPtr data_path);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_set_temp_dir(IntPtr temp_path);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_set_commandline_arguments(int argc, IntPtr argv, IntPtr basedir);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_set_commandline_arguments_utf16(int argc, IntPtr argv, IntPtr basedir);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_set_config_utf16(IntPtr executablePath);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_set_config(IntPtr executablePath);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_set_memory_callbacks(IntPtr callbacks);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_get_corlib();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_add_internal_call(IntPtr name, IntPtr method);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_resolve_icall([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_alloc(uint size);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_free(IntPtr ptr);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_array_class_get(IntPtr element_class, uint rank);

    // il2cpp_array_length, il2cpp_object_get_class, il2cpp_object_unbox and the two string accessors read the
    // Il2CppObject, Il2CppArray and Il2CppString headers, which every supported il2cpp version lays out the same.
    // Reading them here skips a P/Invoke transition on the hottest paths of every wrapper.
    public static uint il2cpp_array_length(IntPtr array) => (uint)*(nuint*)(array + 3 * IntPtr.Size);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint il2cpp_array_get_byte_length(IntPtr array);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_array_new(IntPtr elementTypeInfo, ulong length);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_array_new_specific(IntPtr arrayTypeInfo, ulong length);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_array_new_full(IntPtr array_class, ref ulong lengths, ref ulong lower_bounds);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_bounded_array_class_get(IntPtr element_class, uint rank,
        [MarshalAs(UnmanagedType.I1)] bool bounded);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int il2cpp_array_element_size(IntPtr array_class);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_assembly_get_image(IntPtr assembly);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_class_enum_basetype(IntPtr klass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_is_generic(IntPtr klass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_is_inflated(IntPtr klass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_is_assignable_from(IntPtr klass, IntPtr oklass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_is_subclass_of(IntPtr klass, IntPtr klassc,
        [MarshalAs(UnmanagedType.I1)] bool check_interfaces);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_has_parent(IntPtr klass, IntPtr klassc);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_class_from_il2cpp_type(IntPtr type);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_class_from_name(IntPtr image, [MarshalAs(UnmanagedType.LPUTF8Str)] string namespaze,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_class_from_system_type(IntPtr type);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_class_get_element_class(IntPtr klass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_class_get_events(IntPtr klass, ref IntPtr iter);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_class_get_fields(IntPtr klass, ref IntPtr iter);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_class_get_nested_types(IntPtr klass, ref IntPtr iter);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_class_get_interfaces(IntPtr klass, ref IntPtr iter);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_class_get_properties(IntPtr klass, ref IntPtr iter);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_class_get_property_from_name(IntPtr klass, IntPtr name);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_class_get_field_from_name(IntPtr klass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_class_get_methods(IntPtr klass, ref IntPtr iter);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_class_get_method_from_name(IntPtr klass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int argsCount);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern nint il2cpp_class_get_name(IntPtr klass);

    public static string? il2cpp_class_get_name_(IntPtr klass)
        => Marshal.PtrToStringUTF8(il2cpp_class_get_name(klass));

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern nint il2cpp_class_get_namespace(IntPtr klass);

    public static string? il2cpp_class_get_namespace_(IntPtr klass)
        => Marshal.PtrToStringUTF8(il2cpp_class_get_namespace(klass));

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_class_get_parent(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_class_get_declaring_type(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int il2cpp_class_instance_size(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint il2cpp_class_num_fields(IntPtr enumKlass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_is_valuetype(IntPtr klass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int il2cpp_class_value_size(IntPtr klass, ref uint align);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_is_blittable(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int il2cpp_class_get_flags(IntPtr klass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_is_abstract(IntPtr klass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_is_interface(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int il2cpp_class_array_element_size(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_class_from_type(IntPtr type);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_class_get_type(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint il2cpp_class_get_type_token(IntPtr klass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_has_attribute(IntPtr klass, IntPtr attr_class);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_has_references(IntPtr klass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_class_is_enum(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_class_get_image(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern nint il2cpp_class_get_assemblyname(IntPtr klass);

    public static string? il2cpp_class_get_assemblyname_(IntPtr klass)
        => Marshal.PtrToStringUTF8(il2cpp_class_get_assemblyname(klass));

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int il2cpp_class_get_rank(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint il2cpp_class_get_bitmap_size(IntPtr klass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void il2cpp_class_get_bitmap(IntPtr klass, ref uint bitmap);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_stats_dump_to_file(IntPtr path);

    //[DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    //public extern static ulong il2cpp_stats_get_value(IL2CPP_Stat stat);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_domain_get();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_domain_assembly_open(IntPtr domain, IntPtr name);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr* il2cpp_domain_get_assemblies(IntPtr domain, ref uint size);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr
        il2cpp_exception_from_name_msg(IntPtr image, IntPtr name_space, IntPtr name, IntPtr msg);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_get_exception_argument_null(IntPtr arg);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_format_exception(IntPtr ex, void* message, int message_size);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_format_stack_trace(IntPtr ex, void* output, int output_size);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_unhandled_exception(IntPtr ex);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int il2cpp_field_get_flags(IntPtr field);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern nint il2cpp_field_get_name(IntPtr field);

    public static string? il2cpp_field_get_name_(IntPtr field)
        => Marshal.PtrToStringUTF8(il2cpp_field_get_name(field));

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_field_get_parent(IntPtr field);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint il2cpp_field_get_offset(IntPtr field);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_field_get_type(IntPtr field);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_field_get_value(IntPtr obj, IntPtr field, void* value);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_field_get_value_object(IntPtr field, IntPtr obj);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_field_has_attribute(IntPtr field, IntPtr attr_class);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_field_set_value(IntPtr obj, IntPtr field, void* value);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_field_static_get_value(IntPtr field, void* value);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_field_static_set_value(IntPtr field, void* value);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_field_set_value_object(IntPtr instance, IntPtr field, IntPtr value);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_gc_collect(int maxGenerations);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int il2cpp_gc_collect_a_little();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_gc_disable();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_gc_enable();

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_gc_is_disabled();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern long il2cpp_gc_get_used_size();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern long il2cpp_gc_get_heap_size();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_gc_wbarrier_set_field(IntPtr obj, IntPtr targetAddress, IntPtr gcObj);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint il2cpp_gchandle_new(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool pinned);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint il2cpp_gchandle_new_weakref(IntPtr obj,
        [MarshalAs(UnmanagedType.I1)] bool track_resurrection);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_gchandle_get_target(nint gchandle);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_gchandle_free(nint gchandle);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_unity_liveness_calculation_begin(IntPtr filter, int max_object_count,
        IntPtr callback, IntPtr userdata, IntPtr onWorldStarted, IntPtr onWorldStopped);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_unity_liveness_calculation_end(IntPtr state);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_unity_liveness_calculation_from_root(IntPtr root, IntPtr state);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_unity_liveness_calculation_from_statics(IntPtr state);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_method_get_return_type(IntPtr method);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_method_get_declaring_type(IntPtr method);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern nint il2cpp_method_get_name(IntPtr method);

    public static string? il2cpp_method_get_name_(IntPtr method)
        => Marshal.PtrToStringUTF8(il2cpp_method_get_name(method));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IntPtr il2cpp_method_get_from_reflection(IntPtr method)
    {
        if (UnityVersionHandler.HasGetMethodFromReflection) return _il2cpp_method_get_from_reflection(method);
        Il2CppReflectionMethod* reflectionMethod = (Il2CppReflectionMethod*)method;
        return (IntPtr)reflectionMethod->method;
    }

    [DllImport("GameAssembly", EntryPoint = nameof(il2cpp_method_get_from_reflection), CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr _il2cpp_method_get_from_reflection(IntPtr method);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_method_get_object(IntPtr method, IntPtr refclass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_method_is_generic(IntPtr method);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_method_is_inflated(IntPtr method);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_method_is_instance(IntPtr method);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint il2cpp_method_get_param_count(IntPtr method);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_method_get_param(IntPtr method, uint index);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_method_get_class(IntPtr method);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_method_has_attribute(IntPtr method, IntPtr attr_class);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint il2cpp_method_get_flags(IntPtr method, ref uint iflags);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint il2cpp_method_get_token(IntPtr method);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern nint il2cpp_method_get_param_name(IntPtr method, uint index);

    public static string? il2cpp_method_get_param_name_(IntPtr method, uint index)
        => Marshal.PtrToStringUTF8(il2cpp_method_get_param_name(method, index));

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install(IntPtr prof, IntPtr shutdown_callback);

    // [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    // public extern static void il2cpp_profiler_set_events(IL2CPP_ProfileFlags events);
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install_enter_leave(IntPtr enter, IntPtr fleave);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install_allocation(IntPtr callback);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install_gc(IntPtr callback, IntPtr heap_resize_callback);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install_fileio(IntPtr callback);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_profiler_install_thread(IntPtr start, IntPtr end);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint il2cpp_property_get_flags(IntPtr prop);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_property_get_get_method(IntPtr prop);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_property_get_set_method(IntPtr prop);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern nint il2cpp_property_get_name(IntPtr prop);

    public static string? il2cpp_property_get_name_(IntPtr prop)
        => Marshal.PtrToStringUTF8(il2cpp_property_get_name(prop));

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_property_get_parent(IntPtr prop);

    public static IntPtr il2cpp_object_get_class(IntPtr obj) => *(IntPtr*)obj;

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint il2cpp_object_get_size(IntPtr obj);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_object_get_virtual_method(IntPtr obj, IntPtr method);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_object_new(IntPtr klass);

    public static IntPtr il2cpp_object_unbox(IntPtr obj) => obj + 2 * IntPtr.Size;

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_value_box(IntPtr klass, IntPtr data);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_monitor_enter(IntPtr obj);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_monitor_try_enter(IntPtr obj, uint timeout);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_monitor_exit(IntPtr obj);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_monitor_pulse(IntPtr obj);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_monitor_pulse_all(IntPtr obj);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_monitor_wait(IntPtr obj);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_monitor_try_wait(IntPtr obj, uint timeout);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_runtime_invoke(IntPtr method, IntPtr obj, void** param, ref IntPtr exc);

    // param can be of Il2CppObject*
    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_runtime_invoke_convert_args(IntPtr method, IntPtr obj, void** param,
        int paramCount, ref IntPtr exc);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_runtime_class_init(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_runtime_object_init(IntPtr obj);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void il2cpp_runtime_object_init_exception(IntPtr obj, ref IntPtr exc);

    // [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    // public extern static void il2cpp_runtime_unhandled_exception_policy_set(IL2CPP_RuntimeUnhandledExceptionPolicy value);
    public static int il2cpp_string_length(IntPtr str) => *(int*)(str + 2 * IntPtr.Size);

    public static char* il2cpp_string_chars(IntPtr str) => (char*)(str + 2 * IntPtr.Size + sizeof(int));

    // il2cpp reads these strings as UTF-8, where CharSet.Ansi passed the system code page
    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_string_new([MarshalAs(UnmanagedType.LPUTF8Str)] string str);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_string_new_len([MarshalAs(UnmanagedType.LPUTF8Str)] string str, uint length);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_string_new_utf16(char* text, int len);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr il2cpp_string_new_wrapper([MarshalAs(UnmanagedType.LPUTF8Str)] string str);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_string_intern(string str);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_string_is_interned(string str);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_thread_current();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_thread_attach(IntPtr domain);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_thread_detach(IntPtr thread);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void** il2cpp_thread_get_all_attached_threads(ref uint size);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_is_vm_thread(IntPtr thread);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_current_thread_walk_frame_stack(IntPtr func, IntPtr user_data);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_thread_walk_frame_stack(IntPtr thread, IntPtr func, IntPtr user_data);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_current_thread_get_top_frame(IntPtr frame);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_thread_get_top_frame(IntPtr thread, IntPtr frame);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_current_thread_get_frame_at(int offset, IntPtr frame);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_thread_get_frame_at(IntPtr thread, int offset, IntPtr frame);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int il2cpp_current_thread_get_stack_depth();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int il2cpp_thread_get_stack_depth(IntPtr thread);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_type_get_object(IntPtr type);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int il2cpp_type_get_type(IntPtr type);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_type_get_class_or_element_class(IntPtr type);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern nint il2cpp_type_get_name(IntPtr type);

    public static string? il2cpp_type_get_name_(IntPtr type)
        => Marshal.PtrToStringUTF8(il2cpp_type_get_name(type));

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_type_is_byref(IntPtr type);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint il2cpp_type_get_attrs(IntPtr type);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_type_equals(IntPtr type, IntPtr otherType);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_type_get_assembly_qualified_name(IntPtr type);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_image_get_assembly(IntPtr image);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern nint il2cpp_image_get_name(IntPtr image);

    public static string? il2cpp_image_get_name_(IntPtr image)
        => Marshal.PtrToStringUTF8(il2cpp_image_get_name(image));

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern nint il2cpp_image_get_filename(IntPtr image);

    public static string? il2cpp_image_get_filename_(IntPtr image)
        => Marshal.PtrToStringUTF8(il2cpp_image_get_filename(image));

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_image_get_entry_point(IntPtr image);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint il2cpp_image_get_class_count(IntPtr image);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_image_get_class(IntPtr image, uint index);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_capture_memory_snapshot();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_free_captured_memory_snapshot(IntPtr snapshot);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_set_find_plugin_callback(IntPtr method);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_register_log_callback(IntPtr method);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_debugger_set_agent_options(IntPtr options);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_is_debugger_attached();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_unity_install_unitytls_interface(void* unitytlsInterfaceStruct);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_custom_attrs_from_class(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_custom_attrs_from_method(IntPtr method);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_custom_attrs_get_attr(IntPtr ainfo, IntPtr attr_klass);

    [LibraryImport("GameAssembly"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool il2cpp_custom_attrs_has_attr(IntPtr ainfo, IntPtr attr_klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr il2cpp_custom_attrs_construct(IntPtr cinfo);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void il2cpp_custom_attrs_free(IntPtr ainfo);
}
