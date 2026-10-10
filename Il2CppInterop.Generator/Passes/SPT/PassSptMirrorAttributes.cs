using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;

namespace Il2CppInterop.Generator.Passes.SPT;

// An il2cpp attribute class wraps to an Il2CppObjectBase, which C# refuses as an attribute. Each UnityEngine one gets a
// twin named <Name>Attribute deriving the runtime's Il2CppMirroredAttribute, so [RequireComponent(typeof(T))] binds to
// it, and the class injector builds the real attribute from it. Names already ending in Attribute are left alone, a
// twin without the suffix would collide with System types such as Range.
public static class PassSptMirrorAttributes
{
    private const string AttributeSuffix = "Attribute";
    private const int ClassTarget = 4;

    public static int MirroredCount;

    public static void DoPass(RewriteGlobalContext context)
    {
        if (!context.Options.MirrorAttributes)
            return;

        var taken = new HashSet<string>(context.Assemblies.SelectMany(assembly => assembly.Types).Select(type => type.NewType.FullName));
        foreach (var assemblyContext in context.Assemblies)
        {
            var assemblyName = assemblyContext.OriginalAssembly.Name!.ToString();
            if (!assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal))
                continue;

            foreach (var typeContext in assemblyContext.Types.ToList())
            {
                var original = typeContext.OriginalType;
                if (!original.IsPublic || original.IsAbstract || original.IsInterface || original.GenericParameters.Count > 0
                    || original.Name!.ToString().EndsWith(AttributeSuffix, StringComparison.Ordinal) || !DerivesFromAttribute(original))
                    continue;

                var twinName = typeContext.NewType.Name!.ToString() + AttributeSuffix;
                var twinNamespace = typeContext.NewType.Namespace?.ToString();
                var twinFullName = string.IsNullOrEmpty(twinNamespace) ? twinName : twinNamespace + "." + twinName;
                if (taken.Contains(twinFullName))
                    continue;

                var unityType = context.UnityAssemblies.GetTypeByName(assemblyName, original.FullName);
                if (CreateTwin(context, assemblyContext, typeContext, unityType, twinName))
                {
                    taken.Add(twinFullName);
                    MirroredCount++;
                }
            }
        }
    }

    private static bool DerivesFromAttribute(TypeDefinition type)
    {
        for (var baseType = type.BaseType; baseType != null; baseType = baseType.Resolve()?.BaseType)
        {
            if (baseType.FullName == "System.Attribute")
                return true;
        }

        return false;
    }

    private static bool HasInstanceFields(TypeDefinition type)
    {
        for (TypeDefinition? current = type; current != null && current.FullName != "System.Attribute"; current = current.BaseType?.Resolve())
        {
            if (current.Fields.Any(field => !field.IsStatic))
                return true;
        }

        return false;
    }

    private static bool CreateTwin(RewriteGlobalContext context, AssemblyRewriteContext assemblyContext, TypeRewriteContext typeContext,
        TypeDefinition? unityType, string twinName)
    {
        var module = typeContext.NewType.DeclaringModule!;
        var native = new List<(MethodRewriteContext Method, TypeSignature[] Parameters)>();
        foreach (var method in typeContext.Methods)
        {
            var originalMethod = method.OriginalMethod;
            if (!originalMethod.IsConstructor || originalMethod.IsStatic || !originalMethod.IsPublic)
                continue;

            var parameters = MapParameters(context, module, originalMethod);
            if (parameters != null)
                native.Add((method, parameters));
        }

        // A game strips the constructors it never calls. Unity's own constructor is usable when all it does is store its
        // arguments, the runtime then allocates the attribute and sets those fields
        var stored = new List<(MethodDefinition? Method, TypeSignature[] Parameters, string[] Fields)>();
        foreach (var unityMethod in unityType?.Methods ?? [])
        {
            if (!unityMethod.IsConstructor || unityMethod.IsStatic || !unityMethod.IsPublic)
                continue;

            var parameters = MapParameters(context, module, unityMethod);
            if (parameters == null || native.Any(constructor => SameParameters(constructor.Parameters, parameters)))
                continue;

            var fields = StoredFields(unityMethod, typeContext.NewType);
            if (fields != null)
                stored.Add((unityMethod, parameters, fields));
        }

        // Without Unity's assembly a class with no constructors and no fields can still be allocated as is
        if (native.Count == 0 && stored.Count == 0 && !typeContext.OriginalType.Methods.Any(method => method.IsConstructor && !method.IsStatic)
            && !HasInstanceFields(typeContext.OriginalType))
            stored.Add((null, [], []));

        if (native.Count == 0 && stored.Count == 0)
            return false;

        var twin = new TypeDefinition(typeContext.NewType.Namespace, twinName,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            assemblyContext.Imports.Il2CppMirroredAttribute.ToTypeDefOrRef());
        AddAttributeUsage(module, twin);

        var type = module.Type();
        var mirrored = assemblyContext.Imports.Il2CppMirroredAttribute.ToTypeDefOrRef();
        var constructorBase = mirrored.CreateMemberReference(".ctor",
            MethodSignature.CreateInstance(module.Void(), [type, type.MakeSzArrayType(), module.Object().MakeSzArrayType()]));
        var fieldsBase = mirrored.CreateMemberReference(".ctor",
            MethodSignature.CreateInstance(module.Void(), [type, module.String().MakeSzArrayType(), module.Object().MakeSzArrayType()]));
        var getTypeFromHandle = module.TypeGetTypeFromHandle();

        foreach (var (method, parameters) in native)
        {
            var wrapperParameters = method.NewMethod.Signature!.ParameterTypes;
            var body = AddConstructor(twin, module, method.OriginalMethod, parameters, typeContext.NewType, getTypeFromHandle);
            AddArray(body, type, parameters.Length, i =>
            {
                body.Add(CilOpCodes.Ldtoken, wrapperParameters[i].ToTypeDefOrRef());
                body.Add(CilOpCodes.Call, getTypeFromHandle);
            });
            AddArguments(body, module, twin.Methods[^1], parameters);
            body.Add(CilOpCodes.Call, constructorBase);
            body.Add(CilOpCodes.Ret);
        }

        foreach (var (method, parameters, fields) in stored)
        {
            var body = AddConstructor(twin, module, method, parameters, typeContext.NewType, getTypeFromHandle);
            AddArray(body, module.String(), fields.Length, i => body.Add(CilOpCodes.Ldstr, fields[i]));
            AddArguments(body, module, twin.Methods[^1], parameters);
            body.Add(CilOpCodes.Call, fieldsBase);
            body.Add(CilOpCodes.Ret);
        }

        module.TopLevelTypes.Add(twin);
        return true;
    }

    /// <summary>
    ///     Add a twin constructor and load this plus the wrapper type, the first two base constructor arguments
    /// </summary>
    private static CilInstructionCollection AddConstructor(TypeDefinition twin, ModuleDefinition module, MethodDefinition? source,
        TypeSignature[] parameters, TypeDefinition wrapper, IMethodDescriptor getTypeFromHandle)
    {
        var constructor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(module.Void(), parameters));
        for (var i = 0; i < parameters.Length; i++)
            constructor.Parameters[i].GetOrCreateDefinition().Name = source!.Parameters[i].Name;

        constructor.CilMethodBody = new();
        twin.Methods.Add(constructor);
        var body = constructor.CilMethodBody.Instructions;
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Ldtoken, wrapper);
        body.Add(CilOpCodes.Call, getTypeFromHandle);
        return body;
    }

    private static void AddArray(CilInstructionCollection body, TypeSignature elementType, int length, Action<int> loadElement)
    {
        body.Add(CilOpCodes.Ldc_I4, length);
        body.Add(CilOpCodes.Newarr, elementType.ToTypeDefOrRef());
        for (var i = 0; i < length; i++)
        {
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Ldc_I4, i);
            loadElement(i);
            body.Add(CilOpCodes.Stelem_Ref);
        }
    }

    private static void AddArguments(CilInstructionCollection body, ModuleDefinition module, MethodDefinition constructor, TypeSignature[] parameters)
    {
        AddArray(body, module.Object(), parameters.Length, i =>
        {
            body.Add(CilOpCodes.Ldarg, constructor.Parameters[i]);
            if (parameters[i].IsValueType)
                body.Add(CilOpCodes.Box, parameters[i].ToTypeDefOrRef());
        });
    }

    /// <summary>
    ///     Read which field each argument lands in when a constructor only calls Attribute() and stores its arguments
    /// </summary>
    /// <param name="constructor">Unity's constructor</param>
    /// <param name="wrapper">Generated wrapper, which needs a field accessor for every stored field</param>
    /// <returns>Field per argument, null when the body does anything else or an argument is not stored once</returns>
    private static string[]? StoredFields(MethodDefinition constructor, TypeDefinition wrapper)
    {
        var instructions = constructor.CilMethodBody?.Instructions;
        if (instructions == null)
            return null;

        var fields = new string?[constructor.Parameters.Count];
        var i = 0;
        while (i < instructions.Count)
        {
            var instruction = instructions[i];
            if (instruction.OpCode.Code is CilCode.Nop or CilCode.Ret)
            {
                i++;
                continue;
            }

            if (!instruction.IsLdarg() || instruction.GetParameter(constructor.Parameters) != constructor.Parameters.ThisParameter || i + 1 >= instructions.Count)
                return null;

            var next = instructions[i + 1];
            if (next.OpCode.Code == CilCode.Call && next.Operand is IMethodDescriptor baseConstructor
                && baseConstructor.Name == ".ctor" && baseConstructor.DeclaringType?.FullName == "System.Attribute")
            {
                i += 2;
                continue;
            }

            if (i + 2 >= instructions.Count || !next.IsLdarg() || instructions[i + 2].OpCode.Code != CilCode.Stfld
                || instructions[i + 2].Operand is not IFieldDescriptor field || field.DeclaringType?.FullName != constructor.DeclaringType!.FullName)
                return null;

            var parameter = next.GetParameter(constructor.Parameters);
            if (parameter == constructor.Parameters.ThisParameter || fields[parameter.Index] != null
                || !wrapper.Properties.Any(property => property.Name == field.Name && property.SetMethod != null))
                return null;

            fields[parameter.Index] = field.Name!;
            i += 3;
        }

        return fields.All(field => field != null) ? fields.Select(field => field!).ToArray() : null;
    }

    private static TypeSignature[]? MapParameters(RewriteGlobalContext context, ModuleDefinition module, MethodDefinition method)
    {
        var parameters = method.Signature!.ParameterTypes.Select(parameter => MapParameter(context, module, parameter)).ToArray();
        return parameters.All(parameter => parameter != null) ? parameters.Select(parameter => parameter!).ToArray() : null;
    }

    private static bool SameParameters(TypeSignature[] left, TypeSignature[] right) =>
        left.Length == right.Length && left.Select((parameter, i) => parameter.FullName == right[i].FullName).All(same => same);

    // Attribute arguments can only be primitives, strings, types and enums
    private static TypeSignature? MapParameter(RewriteGlobalContext context, ModuleDefinition module, TypeSignature parameter)
    {
        switch (parameter.ElementType)
        {
            case ElementType.Boolean:
            case ElementType.Char:
            case ElementType.I1:
            case ElementType.U1:
            case ElementType.I2:
            case ElementType.U2:
            case ElementType.I4:
            case ElementType.U4:
            case ElementType.I8:
            case ElementType.U8:
            case ElementType.R4:
            case ElementType.R8:
                return module.CorLibTypeFactory.FromElementType(parameter.ElementType);
            case ElementType.String:
                return module.String();
        }

        if (parameter.FullName == "System.Type")
            return module.Type();

        var definition = parameter.Resolve();
        if (definition == null || !definition.IsEnum)
            return null;

        // Unity's own assemblies hold their enums under the same names as the game's
        var enumContext = context.TryGetNewTypeForOriginal(definition)
                          ?? context.TryGetAssemblyByName(definition.DeclaringModule?.Assembly?.Name)?.TryGetTypeByName(definition.FullName);
        return enumContext == null ? null : module.DefaultImporter.ImportType(enumContext.NewType).ToTypeSignature(true);
    }

    // Class only, the injector reports attributes of injected classes and nothing else reads the twins
    private static void AddAttributeUsage(ModuleDefinition module, TypeDefinition twin)
    {
        var targets = module.ImportCorlibReference("System.AttributeTargets");
        var usage = module.ImportCorlibReference("System.AttributeUsageAttribute");
        var constructor = usage.ToTypeDefOrRef().CreateMemberReference(".ctor", MethodSignature.CreateInstance(module.Void(), [targets]));
        var signature = new CustomAttributeSignature(
            [new CustomAttributeArgument(targets, ClassTarget)],
            [new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Property, "AllowMultiple", module.Bool(), new CustomAttributeArgument(module.Bool(), true))]);
        twin.CustomAttributes.Add(new CustomAttribute(constructor, signature));
    }
}
