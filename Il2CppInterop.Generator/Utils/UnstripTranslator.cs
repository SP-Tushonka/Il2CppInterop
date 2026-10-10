using System.Diagnostics;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Passes;

using Il2CppInterop.Generator.Extensions;

namespace Il2CppInterop.Generator.Utils;

public static class UnstripTranslator
{
    /// <summary>
    /// What the last failed translation stopped on, an instruction or the stage before the body, for the trace log
    /// </summary>
    public static string? LastFailure { get; private set; }

    /// <summary>
    ///     Set while translating with il2cpp spans when the body needs a span member only the CLR has
    /// </summary>
    [ThreadStatic] private static bool s_needsClrSpans;

    /// <summary>
    ///     Translate a Unity method body into the generated assemblies. A body that pins or reads span memory is
    ///     translated again with CLR spans, since the il2cpp span wrappers return refs into a boxed copy.
    /// </summary>
    /// <param name="original">Method in the Unity assemblies</param>
    /// <param name="target">Generated method to fill</param>
    /// <param name="typeRewriteContext">Context of the declaring type</param>
    /// <param name="imports">Runtime references of the target module</param>
    /// <returns>Whether the body was translated</returns>
    public static bool TranslateMethod(MethodDefinition original, MethodDefinition target,
        TypeRewriteContext typeRewriteContext, RuntimeAssemblyReferences imports)
    {
        try
        {
            Pass80UnstripMethods.AmbientClrSpans = false;
            s_needsClrSpans = false;
            var translated = TranslateMethodBody(original, target, typeRewriteContext, imports);
            if (!s_needsClrSpans)
                return translated;

            // Spans received are converted on entry, nothing converts one back for a return
            if (Pass80UnstripMethods.MentionsClrOnlyType(original.Signature!.ReturnType))
                return false;

            Pass80UnstripMethods.AmbientClrSpans = true;
            return TranslateMethodBody(original, target, typeRewriteContext, imports);
        }
        finally
        {
            Pass80UnstripMethods.AmbientClrSpans = false;
        }
    }

    private static bool TranslateMethodBody(MethodDefinition original, MethodDefinition target,
        TypeRewriteContext typeRewriteContext, RuntimeAssemblyReferences imports)
    {
        if (original.CilMethodBody is null)
            return true;

        target.CilMethodBody = new();
        LastFailure = "local variable types";

        var globalContext = typeRewriteContext.AssemblyContext.GlobalContext;
        Dictionary<CilLocalVariable, CilLocalVariable> localVariableMap = new();
        foreach (var variableDefinition in original.CilMethodBody.LocalVariables)
        {
            var variableType =
                Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, variableDefinition.VariableType,
                    imports);
            if (variableType == null)
                return false;
            var newVariableDefinition = new CilLocalVariable(variableType);
            target.CilMethodBody.LocalVariables.Add(newVariableDefinition);
            localVariableMap.Add(variableDefinition, newVariableDefinition);
        }

        // We expand macros because our instructions are not mapped one-to-one,
        // so specialized instructions like Br_S need to be expanded to Br for safety.
        // In pass 90, we optimize all macros, so we won't need to worry about that here.
        original.CilMethodBody.Instructions.ExpandMacros();

        List<KeyValuePair<CilInstructionLabel, CilInstructionLabel>> labelMap = new();
        Dictionary<CilInstruction, CilInstruction> instructionMap = new();
        HashSet<CilInstruction>? branchTargets = null;

        var targetBuilder = target.CilMethodBody.Instructions;

        var classConstructor = original.IsConstructor && !original.IsStatic && target.DeclaringType != null && !target.DeclaringType.IsValueType();
        LastFailure = "object allocation, neither the class nor its base exists in il2cpp";
        if (classConstructor && !EmitObjectAllocation(target, globalContext, imports))
            return false;

        LastFailure = "span parameter, only a Span or ReadOnlySpan of a struct converts";
        var clrSpanParameters = Pass80UnstripMethods.AmbientClrSpans ? EmitClrSpanParameters(original, target, globalContext, imports) : [];
        if (clrSpanParameters == null)
            return false;

        foreach (var bodyInstruction in original.CilMethodBody.Instructions)
        {
            LastFailure = bodyInstruction.ToString();
            if (bodyInstruction.Operand is null)
            {
                CilInstruction newInstruction;
                switch (bodyInstruction.OpCode.Code)
                {
                    case CilCode.Ldlen:
                        //This is Il2CppArrayBase.Length
                        newInstruction = targetBuilder.Add(OpCodes.Callvirt,
                            imports.Module.DefaultImporter.ImportMethod(imports.Il2CppArrayBase_get_Length.Value));
                        break;

                    case CilCode.Ldelem_Ref:
                    case CilCode.Ldelem_I1:
                    case CilCode.Ldelem_U1:
                    case CilCode.Ldelem_I2:
                    case CilCode.Ldelem_U2:
                    case CilCode.Ldelem_I4:
                    case CilCode.Ldelem_U4:
                    case CilCode.Ldelem_I8:
                    case CilCode.Ldelem_I:
                        {
                            // The opcode does not name the element type, which may be a primitive, bool, char or an enum,
                            // so it is read off the array being loaded from
                            var elementType = FindArrayElementType(original.CilMethodBody, bodyInstruction, 1, ref branchTargets);
                            var newElementType = elementType == null ? null : Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, elementType, imports);
                            if (newElementType == null)
                                return false;

                            var getMethod = imports.Il2CppArrayBase_get_Item.Get(newElementType);
                            newInstruction = targetBuilder.Add(OpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(getMethod));
                        }
                        break;

                    case CilCode.Stelem_Ref:
                    case >= CilCode.Stelem_I and <= CilCode.Stelem_I8:
                        {
                            var elementType = FindArrayElementType(original.CilMethodBody, bodyInstruction, 2, ref branchTargets);
                            var newElementType = elementType == null ? null : Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, elementType, imports);
                            if (newElementType == null)
                                return false;

                            var setMethod = imports.Il2CppArrayBase_set_Item.Get(newElementType);
                            newInstruction = targetBuilder.Add(OpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(setMethod));
                        }
                        break;

                    case CilCode.Ldelem_R4:
                        {
                            var getMethod = imports.Il2CppArrayBase_get_Item.Get(imports.Module.CorLibTypeFactory.Single);
                            newInstruction = targetBuilder.Add(OpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(getMethod));
                        }
                        break;

                    case CilCode.Ldelem_R8:
                        {
                            var getMethod = imports.Il2CppArrayBase_get_Item.Get(imports.Module.CorLibTypeFactory.Double);
                            newInstruction = targetBuilder.Add(OpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(getMethod));
                        }
                        break;

                    case CilCode.Stelem_R4:
                        {
                            var setMethod = imports.Il2CppArrayBase_set_Item.Get(imports.Module.CorLibTypeFactory.Single);
                            newInstruction = targetBuilder.Add(OpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(setMethod));
                        }
                        break;

                    case CilCode.Stelem_R8:
                        {
                            var setMethod = imports.Il2CppArrayBase_set_Item.Get(imports.Module.CorLibTypeFactory.Double);
                            newInstruction = targetBuilder.Add(OpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(setMethod));
                        }
                        break;

                    case >= CilCode.Ldind_I1 and <= CilCode.Ldind_Ref:
                        //This is for by ref parameters
                        goto default;

                    case >= CilCode.Stind_Ref and <= CilCode.Stind_R8:
                        //This is for by ref parameters
                        goto default;

                    default:
                        //Noop, ldnull, ldarg_0, mul, add, etc.
                        newInstruction = targetBuilder.Add(bodyInstruction.OpCode);
                        break;
                }

                instructionMap.Add(bodyInstruction, newInstruction);
            }
            else if (bodyInstruction.OpCode.OperandType == CilOperandType.InlineField)
            {
                // This code doesn't handle fields in the corlib types well.
                // Static fields are fine, but references to instance fields can't be redirected.

                var fieldArg = (IFieldDescriptor)bodyInstruction.Operand;
                var useSystemCorlibType = fieldArg.Signature?.HasThis ?? true;
                var fieldDeclarer =
                    Pass80UnstripMethods.ResolveTypeInNewAssembliesRaw(globalContext, fieldArg.DeclaringType!.ToTypeSignature(), imports, useSystemCorlibType);
                if (fieldDeclarer == null)
                    return false;
                var fieldDeclarerDefinition = fieldDeclarer.Resolve();
                if (fieldDeclarerDefinition == null)
                    return false;

                var fieldDeclarerContext = globalContext.GetContextForNewType(fieldDeclarerDefinition);
                var propertyName = fieldDeclarerContext.Fields.SingleOrDefault(it => it.OriginalField.Name == fieldArg.Name)?.UnmangledName;

                var newField = fieldDeclarerDefinition.Fields.SingleOrDefault(it => it.Name == fieldArg.Name)
                    ?? fieldDeclarerDefinition.Fields.SingleOrDefault(it => it.Name == propertyName);
                if (newField != null)
                {
                    var newInstruction = targetBuilder.Add(bodyInstruction.OpCode, imports.Module.DefaultImporter.ImportField(newField));
                    instructionMap.Add(bodyInstruction, newInstruction);
                }
                else
                {
                    if (propertyName == null)
                    {
                        return false;
                    }
                    else if (bodyInstruction.OpCode == OpCodes.Ldfld || bodyInstruction.OpCode == OpCodes.Ldsfld)
                    {
                        var getterMethod = fieldDeclarerDefinition.Properties
                            .SingleOrDefault(it => it.Name == propertyName)?.GetMethod;
                        if (getterMethod == null)
                            return false;

                        var newInstruction = targetBuilder.Add(OpCodes.Call, imports.Module.DefaultImporter.ImportMethod(getterMethod));
                        instructionMap.Add(bodyInstruction, newInstruction);
                    }
                    else if (bodyInstruction.OpCode == OpCodes.Stfld || bodyInstruction.OpCode == OpCodes.Stsfld)
                    {
                        var setterMethod = fieldDeclarerDefinition.Properties
                            .SingleOrDefault(it => it.Name == propertyName)?.SetMethod;
                        if (setterMethod == null)
                            return false;

                        var newInstruction = targetBuilder.Add(OpCodes.Call, imports.Module.DefaultImporter.ImportMethod(setterMethod));
                        instructionMap.Add(bodyInstruction, newInstruction);
                    }
                    else if (bodyInstruction.OpCode == OpCodes.Ldflda)
                    {
                        var fieldContext = fieldDeclarerContext.Fields.Single(it => it.OriginalField.Name == fieldArg.Name);
                        var newInstruction = EmitInstanceFieldAddress(target, fieldArg, fieldContext, fieldDeclarer, globalContext, imports);
                        if (newInstruction == null)
                            return false;

                        instructionMap.Add(bodyInstruction, newInstruction);
                    }
                    else
                    {
                        // Ldsflda, the address of a static field
                        return false;
                    }
                }
            }
            else if (bodyInstruction.OpCode.OperandType == CilOperandType.InlineMethod)
            {
                // This code doesn't handle methods in the corlib types well.
                // Static methods are fine, but references to instance methods can't be redirected.

                var methodArg = (IMethodDescriptor)bodyInstruction.Operand;
                var useSystemCorlibType = methodArg.Signature?.HasThis ?? true;

                if (!Pass80UnstripMethods.AmbientClrSpans && NeedsClrSpanMember(methodArg))
                {
                    s_needsClrSpans = true;
                    return false;
                }

                // A span over an array stays an il2cpp span when a generated wrapper consumes it right away
                var clrSpans = Pass80UnstripMethods.AmbientClrSpans &&
                               !(IsSpanFromArray(methodArg) && ConsumerWantsIl2CppSpans(original.CilMethodBody, bodyInstruction, ref branchTargets, globalContext, imports));
                if (clrSpans)
                {
                    var spanFromArray = EmitSpanFromArray(target, bodyInstruction, methodArg, globalContext, imports);
                    if (spanFromArray != null)
                    {
                        instructionMap.Add(bodyInstruction, spanFromArray);
                        continue;
                    }
                }

                var constrainedToClrStruct = targetBuilder.Count > 0
                    && targetBuilder[targetBuilder.Count - 1].OpCode == OpCodes.Constrained
                    && targetBuilder[targetBuilder.Count - 1].Operand is ITypeDefOrRef constrainedOperand
                    // A corlib primitive does not resolve from the generated module, the corlib factory still knows it
                    && (constrainedOperand.IsValueType() || imports.Module.CorLibTypeFactory.FromType(constrainedOperand)?.IsValueType == true);
                if (constrainedToClrStruct && methodArg.Signature != null && methodArg.Signature.HasThis && methodArg.DeclaringType?.FullName is "System.Object" or "System.ValueType")
                {
                    // The receiver is a plain CLR struct, so the call has to stay in the CLR corlib
                    var objectRef = imports.Module.Object().ToTypeDefOrRef();
                    IMethodDescriptor? clrMethod = methodArg.Name?.Value switch
                    {
                        "GetHashCode" when methodArg.Signature.ParameterTypes.Count == 0 => ReferenceCreator.CreateInstanceMethodReference("GetHashCode", imports.Module.Int(), objectRef),
                        "ToString" when methodArg.Signature.ParameterTypes.Count == 0 => ReferenceCreator.CreateInstanceMethodReference("ToString", imports.Module.String(), objectRef),
                        "Equals" when methodArg.Signature.ParameterTypes.Count == 1 => ReferenceCreator.CreateInstanceMethodReference("Equals", imports.Module.Bool(), objectRef, imports.Module.Object()),
                        _ => null,
                    };
                    if (clrMethod == null)
                        return false;

                    var clrInstruction = targetBuilder.Add(bodyInstruction.OpCode, imports.Module.DefaultImporter.ImportMethod(clrMethod));
                    instructionMap.Add(bodyInstruction, clrInstruction);
                    continue;
                }
                if (classConstructor && bodyInstruction.OpCode.Code == CilCode.Call && methodArg.Name == ".ctor" && methodArg.Signature is { HasThis: true })
                {
                    // The object already exists, so a chained base(...) or this(...) runs on it instead of allocating another
                    var chained = methodArg.DeclaringType?.FullName;
                    var baseName = original.DeclaringType!.BaseType?.FullName;
                    CilInstruction? chainInstruction;
                    if (chained == "System.Object" && chained == baseName)
                        chainInstruction = targetBuilder.Add(OpCodes.Pop);
                    else if (chained == baseName || chained == original.DeclaringType.FullName)
                        chainInstruction = EmitNativeConstructorCall(target.CilMethodBody, methodArg, globalContext, imports);
                    else
                        return false;

                    if (chainInstruction == null)
                        return false;

                    instructionMap.Add(bodyInstruction, chainInstruction);
                    continue;
                }

                var methodDeclarer =
                    Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, methodArg.DeclaringType?.ToTypeSignature(), imports, useSystemCorlibType, clrSpans);
                if (methodDeclarer == null)
                    return false;

                var newMethodSignature = TranslateSignature(methodArg.Signature!, globalContext, imports, clrSpans);
                if (newMethodSignature == null)
                    return false;

                // A wrapper generated for a method il2cpp kept takes il2cpp spans, only rebuilt code takes CLR ones. A corlib
                // static with neither, like string to ReadOnlySpan<char>, is a CLR corlib method.
                if (clrSpans && (Pass80UnstripMethods.MentionsClrOnlyType(methodArg.Signature!.ReturnType) || methodArg.Signature.ParameterTypes.Any(Pass80UnstripMethods.MentionsClrOnlyType)))
                {
                    var generatedDeclarer = Pass80UnstripMethods.ResolveTypeInNewAssembliesRaw(globalContext, methodArg.DeclaringType?.ToTypeSignature(), imports, useSystemCorlibType);
                    var il2cppSignature = TranslateSignature(methodArg.Signature, globalContext, imports, false);
                    if (!DeclaresMethod(generatedDeclarer, methodArg.Name, newMethodSignature))
                    {
                        if (il2cppSignature != null && DeclaresMethod(generatedDeclarer, methodArg.Name, il2cppSignature))
                            newMethodSignature = il2cppSignature;
                        else if (!useSystemCorlibType && IsGeneratedCorlibType(generatedDeclarer))
                            methodDeclarer = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, methodArg.DeclaringType?.ToTypeSignature(), imports, true) ?? methodDeclarer;
                    }
                }

                // Math only takes and returns primitives, and il2cpp strips most of it
                if (methodArg.DeclaringType?.FullName is "System.Math" or "System.MathF" &&
                    newMethodSignature.ParameterTypes.Append(newMethodSignature.ReturnType).All(it => it is CorLibTypeSignature))
                    methodDeclarer = new TypeReference(imports.Module, imports.Module.CorLibTypeFactory.CorLibScope, "System", methodArg.DeclaringType.Name).ToTypeSignature(false);

                // Generated wrappers return a plain ref where the caller marks a ref readonly with a modreq
                if (newMethodSignature.ReturnType is CustomModifierTypeSignature { BaseType: ByReferenceTypeSignature } modified)
                {
                    var generatedDeclarer = Pass80UnstripMethods.ResolveTypeInNewAssembliesRaw(globalContext, methodArg.DeclaringType?.ToTypeSignature(), imports, useSystemCorlibType);
                    var plain = TranslateSignature(methodArg.Signature, globalContext, imports, clrSpans)!;
                    plain.ReturnType = modified.BaseType;
                    if (!DeclaresMethod(generatedDeclarer, methodArg.Name, newMethodSignature) && DeclaresMethod(generatedDeclarer, methodArg.Name, plain))
                        newMethodSignature = plain;
                }

                // The JIT faults on constrained. over a CLR struct followed by a call into an il2cpp class
                if (constrainedToClrStruct && methodArg.Signature.HasThis && !methodDeclarer.IsValueType())
                    return false;

                // The generator renames Object.GetType to keep System.Type.GetType reachable
                var methodName = methodArg.Name;
                if (methodArg.Signature.HasThis && methodArg.Name == "GetType" && methodArg.Signature.ParameterTypes.Count == 0 && methodArg.DeclaringType?.FullName == "System.Object")
                    methodName = "GetIl2CppType";

                var memberReference = new MemberReference(methodDeclarer.ToTypeDefOrRef(), methodName, newMethodSignature);

                IMethodDescriptor newMethod;
                if (methodArg is MethodSpecification genericMethod)
                {
                    if (genericMethod.Signature is null)
                        return false;

                    TypeSignature[] typeArguments = new TypeSignature[genericMethod.Signature.TypeArguments.Count];
                    for (var i = 0; i < genericMethod.Signature.TypeArguments.Count; i++)
                    {
                        var newTypeArgument = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, genericMethod.Signature.TypeArguments[i], imports);
                        if (newTypeArgument == null)
                            return false;

                        typeArguments[i] = newTypeArgument;
                    }

                    newMethod = memberReference.MakeGenericInstanceMethod(typeArguments);
                }
                else
                {
                    newMethod = memberReference;
                }

                var newInstruction = targetBuilder.Add(bodyInstruction.OpCode, imports.Module.DefaultImporter.ImportMethod(newMethod));
                instructionMap.Add(bodyInstruction, newInstruction);
            }
            else if (bodyInstruction.OpCode.OperandType == CilOperandType.InlineType)
            {
                var targetType = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, ((ITypeDefOrRef)bodyInstruction.Operand).ToTypeSignature(), imports);
                if (targetType == null)
                    return false;

                if ((bodyInstruction.OpCode == OpCodes.Castclass && !targetType.IsValueType) ||
                    (bodyInstruction.OpCode == OpCodes.Unbox_Any && targetType is GenericParameterSignature))
                {
                    // Compilers use unbox.any for casting to generic parameter types.
                    // Castclass is only used for reference types.
                    // Both translate to a Cast<T>() that, like the cast it replaces, lets null through.
                    var newInstruction = targetBuilder.Add(OpCodes.Call,
                        imports.Module.DefaultImporter.ImportMethod(imports.IL2CPP_CastOrNull.Value.MakeGenericInstanceMethod([targetType])));
                    instructionMap.Add(bodyInstruction, newInstruction);
                }
                else if (bodyInstruction.OpCode == OpCodes.Isinst && !targetType.IsValueType)
                {
                    var newInstruction = targetBuilder.Add(OpCodes.Call,
                        imports.Module.DefaultImporter.ImportMethod(imports.IL2CPP_TryCastOrNull.Value.MakeGenericInstanceMethod([targetType])));
                    instructionMap.Add(bodyInstruction, newInstruction);
                }
                else if (bodyInstruction.OpCode == OpCodes.Newarr)
                {
                    var newInstruction = targetBuilder.Add(OpCodes.Conv_I8);

                    IMethodDescriptor constructor;
                    if (targetType.IsValueType)
                    {
                        constructor = imports.Il2CppStructArrayctor_size.Get(targetType);
                    }
                    else
                    {
                        var il2cppTypeArray = targetType.FullName == "System.String"
                            ? imports.Il2CppStringArray.ToTypeDefOrRef()
                            : imports.Il2CppReferenceArray.MakeGenericInstanceType(targetType).ToTypeDefOrRef();
                        constructor = ReferenceCreator.CreateInstanceMethodReference(".ctor", imports.Module.Void(), il2cppTypeArray, imports.Module.Long());
                    }

                    targetBuilder.Add(OpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(constructor));
                    instructionMap.Add(bodyInstruction, newInstruction);
                }
                else if (bodyInstruction.OpCode == OpCodes.Ldelema)
                {
                    // Only value type elements sit inline in a struct array. A readonly. prefix has to stay right before the
                    // ldelema it applies to, which the rewrite below cannot keep.
                    if (!targetType.IsValueType || (targetBuilder.Count > 0 && targetBuilder[targetBuilder.Count - 1].OpCode == OpCodes.Readonly))
                        return false;

                    var newInstruction = EmitStructArrayElementAddress(target.CilMethodBody, targetType, imports);
                    instructionMap.Add(bodyInstruction, newInstruction);
                }
                else if (bodyInstruction.OpCode == OpCodes.Ldelem)
                {
                    var getMethod = imports.Il2CppArrayBase_get_Item.Get(targetType);
                    var newInstruction = targetBuilder.Add(OpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(getMethod));
                    instructionMap.Add(bodyInstruction, newInstruction);
                }
                else if (bodyInstruction.OpCode == OpCodes.Stelem)
                {
                    var setMethod = imports.Il2CppArrayBase_set_Item.Get(targetType);
                    var newInstruction = targetBuilder.Add(OpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(setMethod));
                    instructionMap.Add(bodyInstruction, newInstruction);
                }
                else
                {
                    var newInstruction = targetBuilder.Add(bodyInstruction.OpCode, targetType.ToTypeDefOrRef());
                    instructionMap.Add(bodyInstruction, newInstruction);
                }
            }
            else if (bodyInstruction.OpCode.OperandType == CilOperandType.InlineSig)
            {
                // todo: rewrite sig if this ever happens in unity types
                return false;
            }
            else if (bodyInstruction.OpCode.OperandType == CilOperandType.InlineTok)
            {
                Debug.Assert(bodyInstruction.OpCode.Code is CilCode.Ldtoken);
                switch (bodyInstruction.Operand)
                {
                    case ITypeDefOrRef typeDefOrRef:
                        {
                            var targetTok = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, typeDefOrRef.ToTypeSignature(), imports);
                            if (targetTok == null)
                                return false;

                            var newInstruction = targetBuilder.Add(OpCodes.Call,
                                imports.Module.DefaultImporter.ImportMethod(imports.Il2CppSystemRuntimeTypeHandleGetRuntimeTypeHandle.Value.MakeGenericInstanceMethod([targetTok])));
                            instructionMap.Add(bodyInstruction, newInstruction);
                        }
                        break;
                    default:
                        // Ldtoken is also used for members, which is not implemented.
                        return false;
                }
            }
            else if (bodyInstruction.OpCode.OperandType is CilOperandType.InlineSwitch && bodyInstruction.Operand is IReadOnlyList<ICilLabel> labels)
            {
                List<ICilLabel> newLabels = new(labels.Count);
                for (var i = 0; i < labels.Count; i++)
                {
                    if (labels[i] is CilInstructionLabel oldLabel)
                    {
                        var newLabel = new CilInstructionLabel();
                        labelMap.Add(new(oldLabel, newLabel));
                        newLabels.Add(newLabel);
                    }
                    else
                    {
                        return false;
                    }
                }
                var newInstruction = targetBuilder.Add(bodyInstruction.OpCode, newLabels);
                instructionMap.Add(bodyInstruction, newInstruction);
            }
            else if (bodyInstruction.Operand is string or Utf8String
                || bodyInstruction.Operand.GetType().IsPrimitive)
            {
                var newInstruction = new CilInstruction(bodyInstruction.OpCode, bodyInstruction.Operand);
                targetBuilder.Add(newInstruction);
                instructionMap.Add(bodyInstruction, newInstruction);
            }
            else if (bodyInstruction.Operand is Parameter parameter)
            {
                // A span passed on to a generated method stays the il2cpp object it came as
                if (clrSpanParameters.TryGetValue(parameter.MethodSignatureIndex, out var spanLocal) &&
                    !(bodyInstruction.OpCode.Code == CilCode.Ldarg && ConsumerWantsIl2CppSpans(original.CilMethodBody, bodyInstruction, ref branchTargets, globalContext, imports)))
                {
                    var opCode = bodyInstruction.OpCode.Code switch
                    {
                        CilCode.Ldarg => OpCodes.Ldloc,
                        CilCode.Ldarga => OpCodes.Ldloca,
                        _ => OpCodes.Stloc,
                    };
                    instructionMap.Add(bodyInstruction, targetBuilder.Add(opCode, spanLocal));
                    continue;
                }

                var newInstruction = targetBuilder.Add(bodyInstruction.OpCode, target.Parameters.GetBySignatureIndex(parameter.MethodSignatureIndex));
                instructionMap.Add(bodyInstruction, newInstruction);
            }
            else if (bodyInstruction.Operand is CilLocalVariable localVariable)
            {
                var newInstruction = targetBuilder.Add(bodyInstruction.OpCode, localVariableMap[localVariable]);
                instructionMap.Add(bodyInstruction, newInstruction);
            }
            else if (bodyInstruction.Operand is CilInstructionLabel label)
            {
                var newLabel = new CilInstructionLabel();
                labelMap.Add(new(label, newLabel));
                var newInstruction = targetBuilder.Add(bodyInstruction.OpCode, newLabel);
                instructionMap.Add(bodyInstruction, newInstruction);
            }
            else
            {
                return false;
            }
        }

        foreach ((var oldLabel, var newLabel) in labelMap)
        {
            newLabel.Instruction = instructionMap[oldLabel.Instruction!];
        }

        // Copy exception handlers
        LastFailure = "exception handler, only catch (object) is supported";
        foreach (var exceptionHandler in original.CilMethodBody.ExceptionHandlers)
        {
            var newExceptionHandler = new CilExceptionHandler
            {
                HandlerType = exceptionHandler.HandlerType
            };

            switch (exceptionHandler.TryStart)
            {
                case null:
                    break;
                case CilInstructionLabel { Instruction: not null } tryStart:
                    newExceptionHandler.TryStart = new CilInstructionLabel(instructionMap[tryStart.Instruction]);
                    break;
                default:
                    return false;
            }

            switch (exceptionHandler.TryEnd)
            {
                case null:
                    break;
                case CilInstructionLabel { Instruction: not null } tryEnd:
                    newExceptionHandler.TryEnd = new CilInstructionLabel(instructionMap[tryEnd.Instruction]);
                    break;
                default:
                    return false;
            }

            switch (exceptionHandler.HandlerStart)
            {
                case null:
                    break;
                case CilInstructionLabel { Instruction: not null } handlerStart:
                    newExceptionHandler.HandlerStart = new CilInstructionLabel(instructionMap[handlerStart.Instruction]);
                    break;
                default:
                    return false;
            }

            switch (exceptionHandler.HandlerEnd)
            {
                case null:
                    break;
                case CilInstructionLabel { Instruction: not null } handlerEnd:
                    newExceptionHandler.HandlerEnd = new CilInstructionLabel(instructionMap[handlerEnd.Instruction]);
                    break;
                default:
                    return false;
            }

            switch (exceptionHandler.FilterStart)
            {
                case null:
                    break;
                case CilInstructionLabel { Instruction: not null } filterStart:
                    newExceptionHandler.FilterStart = new CilInstructionLabel(instructionMap[filterStart.Instruction]);
                    break;
                default:
                    return false;
            }

            switch (exceptionHandler.ExceptionType?.ToTypeSignature())
            {
                case null:
                    break;
                case CorLibTypeSignature { ElementType: ElementType.Object }:
                    newExceptionHandler.ExceptionType = imports.Module.CorLibTypeFactory.Object.ToTypeDefOrRef();
                    break;
                default:
                    // In the future, we will throw exact exceptions, but we don't right now,
                    // so attempting to catch a specific exception type will always fail.
                    return false;
            }

            target.CilMethodBody.ExceptionHandlers.Add(newExceptionHandler);
        }

        LastFailure = null;
        return true;
    }

    /// <summary>
    /// Find the element type of the array an operand-less ldelem or stelem works on. The instruction that pushed the
    /// array is found by walking back through the stack within the same basic block, and its static type gives the element.
    /// </summary>
    /// <param name="body">Original method body, with macros expanded</param>
    /// <param name="instruction">ldelem or stelem instruction</param>
    /// <param name="depth">Stack slots above the array, 1 for a load and 2 for a store</param>
    /// <param name="branchTargets">Branch targets of the body, collected on first use</param>
    /// <returns>Element type, or null when the array's producer or its type cannot be determined</returns>
    private static TypeSignature? FindArrayElementType(CilMethodBody body, CilInstruction instruction, int depth, ref HashSet<CilInstruction>? branchTargets)
    {
        branchTargets ??= CollectBranchTargets(body);
        var instructions = body.Instructions;
        var needed = depth;
        for (var i = instructions.IndexOf(instruction) - 1; i >= 0; i--)
        {
            var current = instructions[i];
            var pushed = current.GetStackPushCount();
            if (needed < pushed)
            {
                if (current.OpCode.Code != CilCode.Dup)
                {
                    return pushed == 1 && ProducedType(current) is SzArrayTypeSignature array ? array.BaseType : null;
                }

                // Either copy is the value dup consumed, which is then on top of the stack
                needed = 0;
            }
            else
            {
                needed -= pushed;
                needed += current.GetStackPopCount(body);
            }

            // Before a branch target the stack can come from more than one path
            if (branchTargets.Contains(current))
                return null;
        }

        return null;
    }

    /// <summary>
    /// Emit the address of a struct array element for a stack of array then index, as array.AsSpan()[index]. The span
    /// points into the il2cpp array, which the il2cpp GC does not move.
    /// </summary>
    /// <param name="body">Target method body</param>
    /// <param name="elementType">Element type in the generated assemblies</param>
    /// <param name="imports">Runtime references of the target module</param>
    /// <returns>First emitted instruction</returns>
    private static CilInstruction EmitStructArrayElementAddress(CilMethodBody body, TypeSignature elementType, RuntimeAssemblyReferences imports)
    {
        var module = imports.Module;
        var spanType = new TypeReference(module, module.CorLibTypeFactory.CorLibScope, "System", "Span`1");
        var spanOfElement = new GenericInstanceTypeSignature(spanType, true, [elementType]);
        var spanOfParameter = new GenericInstanceTypeSignature(spanType, true, [new GenericParameterSignature(GenericParameterType.Type, 0)]);

        var asSpan = new MemberReference(imports.Il2CppStructArray.MakeGenericInstanceType(elementType).ToTypeDefOrRef(), "AsSpan",
            MethodSignature.CreateInstance(spanOfParameter));
        var getItem = new MemberReference(spanOfElement.ToTypeDefOrRef(), "get_Item",
            MethodSignature.CreateInstance(new GenericParameterSignature(GenericParameterType.Type, 0).MakeByReferenceType(), [module.CorLibTypeFactory.Int32]));

        var indexLocal = new CilLocalVariable(module.CorLibTypeFactory.Int32);
        var spanLocal = new CilLocalVariable(spanOfElement);
        body.LocalVariables.Add(indexLocal);
        body.LocalVariables.Add(spanLocal);

        var instructions = body.Instructions;
        var first = instructions.Add(OpCodes.Stloc, indexLocal);
        instructions.Add(OpCodes.Call, module.DefaultImporter.ImportMethod(asSpan));
        instructions.Add(OpCodes.Stloc, spanLocal);
        instructions.Add(OpCodes.Ldloca, spanLocal);
        instructions.Add(OpCodes.Ldloc, indexLocal);
        instructions.Add(OpCodes.Call, module.DefaultImporter.ImportMethod(getItem));
        return first;
    }

    /// <summary>
    /// Start an unstripped class constructor by allocating its il2cpp object and chaining to the wrapper's pointer
    /// constructor. A class il2cpp still has is allocated as itself. A stripped class is allocated as its il2cpp base,
    /// so its own fields live on the managed wrapper and its overrides are only seen by managed callers.
    /// </summary>
    /// <param name="target">Constructor being generated</param>
    /// <param name="globalContext">Rewrite context</param>
    /// <param name="imports">Runtime references of the target module</param>
    /// <returns>False when neither the class nor its direct base exists in il2cpp</returns>
    private static bool EmitObjectAllocation(MethodDefinition target, RewriteGlobalContext globalContext, RuntimeAssemblyReferences imports)
    {
        var self = target.DeclaringType!;
        if (self.HasGenericParameters())
            return false;

        TypeDefinition allocated;
        ITypeDefOrRef chainTo;
        if (globalContext.GetContextForNewType(self).OriginalType != null)
        {
            allocated = self;
            chainTo = self;
        }
        else
        {
            var baseType = self.BaseType?.Resolve();
            if (baseType == null || baseType.HasGenericParameters() || !HasIl2CppClass(baseType, globalContext))
                return false;

            allocated = baseType;
            chainTo = self.BaseType!;
        }

        var module = imports.Module;
        var store = new GenericInstanceTypeSignature(imports.Il2CppClassPointerStore.ToTypeDefOrRef(), imports.Il2CppClassPointerStore.IsValueType(),
            [allocated.ToTypeSignature()]);
        var classPointer = ReferenceCreator.CreateFieldReference("NativeClassPtr", module.IntPtr(), store.ToTypeDefOrRef());

        var instructions = target.CilMethodBody!.Instructions;
        instructions.Add(OpCodes.Ldarg_0);
        instructions.Add(OpCodes.Ldsfld, module.DefaultImporter.ImportField(classPointer));
        instructions.Add(OpCodes.Call, imports.IL2CPP_il2cpp_object_new.Value);
        instructions.Add(OpCodes.Call, module.DefaultImporter.ImportMethod(
            ReferenceCreator.CreateInstanceMethodReference(".ctor", module.Void(), chainTo, module.IntPtr())));
        return true;
    }

    /// <summary>
    /// Replace a base(...) or this(...) call in an unstripped class constructor with ClassInjector.InvokeBaseConstructor,
    /// which runs that native constructor on the object the prologue allocated
    /// </summary>
    /// <param name="body">Target method body</param>
    /// <param name="constructor">Constructor the original chains to</param>
    /// <param name="globalContext">Rewrite context</param>
    /// <param name="imports">Runtime references of the target module</param>
    /// <returns>First emitted instruction, or null when that constructor does not exist in il2cpp</returns>
    private static CilInstruction? EmitNativeConstructorCall(CilMethodBody body, IMethodDescriptor constructor, RewriteGlobalContext globalContext,
        RuntimeAssemblyReferences imports)
    {
        var baseType = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, constructor.DeclaringType!.ToTypeSignature(), imports);
        var baseDefinition = baseType?.Resolve();
        var baseContext = baseDefinition == null ? null : TryGetIl2CppContext(baseDefinition, globalContext);
        var unityConstructor = constructor.Resolve();
        if (baseType == null || baseContext == null || unityConstructor == null || baseContext.TryGetMethodByUnityAssemblyMethod(unityConstructor) == null)
            return null;

        // The signature is written against the type's own generic parameters, so it is instantiated with its arguments first
        var genericContext = new GenericContext(constructor.DeclaringType!.ToTypeSignature() as GenericInstanceTypeSignature, null);
        var parameterTypes = new List<TypeSignature>();
        foreach (var parameter in constructor.Signature!.ParameterTypes)
        {
            var newType = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, parameter.InstantiateGenericTypes(genericContext), imports);
            if (newType == null || newType is ByReferenceTypeSignature or PointerTypeSignature)
                return null;

            parameterTypes.Add(newType);
        }

        var module = imports.Module;
        var objectType = module.CorLibTypeFactory.Object;
        var locals = parameterTypes.Select(type => new CilLocalVariable(type)).ToArray();
        foreach (var local in locals)
            body.LocalVariables.Add(local);

        var instructions = body.Instructions;
        CilInstruction? first = null;
        for (var i = locals.Length - 1; i >= 0; i--)
        {
            var store = instructions.Add(OpCodes.Stloc, locals[i]);
            first ??= store;
        }

        var array = instructions.Add(OpCodes.Ldc_I4, locals.Length);
        first ??= array;
        instructions.Add(OpCodes.Newarr, objectType.ToTypeDefOrRef());
        for (var i = 0; i < locals.Length; i++)
        {
            instructions.Add(OpCodes.Dup);
            instructions.Add(OpCodes.Ldc_I4, i);
            instructions.Add(OpCodes.Ldloc, locals[i]);
            if (parameterTypes[i].IsValueType)
                instructions.Add(OpCodes.Box, parameterTypes[i].ToTypeDefOrRef());
            instructions.Add(OpCodes.Stelem_Ref);
        }

        var classInjector = new TypeReference(module, imports.Il2CppObjectBase.ToTypeDefOrRef().Scope, "Il2CppInterop.Runtime.Injection", "ClassInjector");
        var invoke = new MemberReference(classInjector, "InvokeBaseConstructor",
            MethodSignature.CreateStatic(module.Void(), 1, [imports.Il2CppObjectBase, objectType.MakeSzArrayType()]));
        instructions.Add(OpCodes.Call, module.DefaultImporter.ImportMethod(invoke.MakeGenericInstanceMethod([baseType])));
        return first;
    }

    /// <summary>
    /// Emit the address of an instance field inside the il2cpp object, for a stack holding the object. Only fields of
    /// a blittable value type qualify, because a reference written through the address would skip il2cpp's write barrier.
    /// </summary>
    /// <param name="target">Method being generated</param>
    /// <param name="field">Field the original takes the address of</param>
    /// <param name="fieldContext">Rewrite context of the field</param>
    /// <param name="declarer">Field's declaring type in the generated assemblies</param>
    /// <param name="globalContext">Rewrite context</param>
    /// <param name="imports">Runtime references of the target module</param>
    /// <returns>First emitted instruction, or null when the field cannot be addressed</returns>
    private static CilInstruction? EmitInstanceFieldAddress(MethodDefinition target, IFieldDescriptor field, FieldRewriteContext fieldContext,
        TypeSignature declarer, RewriteGlobalContext globalContext, RuntimeAssemblyReferences imports)
    {
        // A struct wrapper is addressed through a local or argument, which leaves a reference to the wrapper on the stack
        if (declarer is GenericInstanceTypeSignature || fieldContext.OffsetField == null
            || fieldContext.DeclaringType.ComputedTypeSpecifics != TypeRewriteContext.TypeSpecifics.ReferenceType)
            return null;

        var fieldType = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, field.Signature!.FieldType, imports);
        if (fieldType == null || !fieldType.IsValueType)
            return null;

        var module = imports.Module;
        var instructions = target.CilMethodBody!.Instructions;
        var first = instructions.Add(OpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
        if (SignatureComparer.Default.Equals(declarer, target.DeclaringType!.ToTypeSignature()))
        {
            instructions.Add(OpCodes.Ldsfld, module.DefaultImporter.ImportField(fieldContext.OffsetField));
        }
        else
        {
            // The cached offset is private to the declaring type, so another type looks it up by name once and keeps
            // it in a field of its own. Zero is never an instance field offset since the object header comes first.
            var owner = target.DeclaringType!;
            var cache = owner.GenericParameters.Count == 0 ? GetOffsetCache(owner, declarer, field, module) : null;
            var cached = new CilInstructionLabel();
            if (cache != null)
            {
                instructions.Add(OpCodes.Ldsfld, cache);
                instructions.Add(OpCodes.Dup);
                instructions.Add(OpCodes.Brtrue_S, cached);
                instructions.Add(OpCodes.Pop);
            }

            var store = new GenericInstanceTypeSignature(imports.Il2CppClassPointerStore.ToTypeDefOrRef(), imports.Il2CppClassPointerStore.IsValueType(), [declarer]);
            instructions.Add(OpCodes.Ldsfld, module.DefaultImporter.ImportField(
                ReferenceCreator.CreateFieldReference("NativeClassPtr", module.IntPtr(), store.ToTypeDefOrRef())));
            instructions.Add(OpCodes.Ldstr, field.Name!.Value);
            instructions.Add(OpCodes.Call, imports.IL2CPP_GetIl2CppField.Value);
            instructions.Add(OpCodes.Call, imports.IL2CPP_il2cpp_field_get_offset.Value);

            if (cache != null)
            {
                instructions.Add(OpCodes.Dup);
                instructions.Add(OpCodes.Stsfld, cache);
                cached.Instruction = instructions.Add(OpCodes.Nop);
            }
        }

        instructions.Add(OpCodes.Conv_U);
        instructions.Add(OpCodes.Add);
        return first;
    }

    private static FieldDefinition GetOffsetCache(TypeDefinition owner, TypeSignature declarer, IFieldDescriptor field, ModuleDefinition module)
    {
        var name = $"ForeignFieldOffset_{declarer.FullName}_{field.Name}";
        var cache = owner.Fields.FirstOrDefault(it => it.Name == name);
        if (cache != null)
            return cache;

        cache = new FieldDefinition(name, FieldAttributes.Private | FieldAttributes.Static, module.UInt());
        owner.Fields.Add(cache);
        return cache;
    }

    private static MethodSignature? TranslateSignature(MethodSignature original, RewriteGlobalContext globalContext,
        RuntimeAssemblyReferences imports, bool clrSpans)
    {
        var returnType = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, original.ReturnType, imports, clrSpans: clrSpans);
        if (returnType == null)
            return null;

        var signature = original.HasThis
            ? MethodSignature.CreateInstance(returnType, original.GenericParameterCount, [])
            : MethodSignature.CreateStatic(returnType, original.GenericParameterCount, []);
        foreach (var parameter in original.ParameterTypes)
        {
            var parameterType = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, parameter, imports, clrSpans: clrSpans);
            if (parameterType == null)
                return null;
            signature.ParameterTypes.Add(parameterType);
        }

        return signature;
    }

    /// <summary>
    ///     Check whether a generated type declares a method. Signatures compare by name because the declarer's methods
    ///     live in another module than the signature.
    /// </summary>
    /// <param name="declarer">Declaring type as resolved before importing, so a generated type is its definition</param>
    /// <param name="name">Method name</param>
    /// <param name="signature">Translated signature</param>
    /// <returns>Whether the method exists on the generated type</returns>
    private static bool DeclaresMethod(TypeSignature? declarer, string? name, MethodSignature signature)
    {
        var type = GeneratedDefinition(declarer);
        if (type == null)
            return false;

        return type.Methods.Any(method => method.Name == name && method.Signature != null &&
            method.Signature.ParameterTypes.Count == signature.ParameterTypes.Count &&
            method.Signature.ReturnType.FullName == signature.ReturnType.FullName &&
            method.Signature.ParameterTypes.Select(it => it.FullName).SequenceEqual(signature.ParameterTypes.Select(it => it.FullName)));
    }

    private static TypeDefinition? GeneratedDefinition(TypeSignature? declarer) => declarer switch
    {
        GenericInstanceTypeSignature generic => generic.GenericType as TypeDefinition,
        TypeDefOrRefSignature plain => plain.Type as TypeDefinition,
        _ => null,
    };

    private static bool IsGeneratedCorlibType(TypeSignature? declarer) =>
        GeneratedDefinition(declarer)?.DeclaringModule?.Assembly?.Name?.Value == "Il2Cppmscorlib";

    /// <summary>
    ///     Check whether a call pins a span, reads through a ref into one or uses MemoryMarshal. The il2cpp span
    ///     wrappers have no counterpart for these.
    /// </summary>
    /// <param name="method">Called method in the Unity assemblies</param>
    /// <returns>Whether the call needs CLR spans</returns>
    private static bool NeedsClrSpanMember(IMethodDescriptor method)
    {
        var declaringSignature = method.DeclaringType?.ToTypeSignature();
        var declaringName = declaringSignature is GenericInstanceTypeSignature instance ? instance.GenericType.FullName : declaringSignature?.FullName;
        if (!Pass80UnstripMethods.IsClrOnlyCorlibType(declaringName))
            return false;

        return declaringName == "System.Runtime.InteropServices.MemoryMarshal" || method.Name == "GetPinnableReference" ||
               method.Signature?.ReturnType is ByReferenceTypeSignature ||
               method.Signature?.ReturnType is CustomModifierTypeSignature { BaseType: ByReferenceTypeSignature };
    }

    private static bool IsSpanFromArray(IMethodDescriptor method)
    {
        var declaringSignature = method.DeclaringType?.ToTypeSignature();
        var declaringName = declaringSignature is GenericInstanceTypeSignature instance ? instance.GenericType.FullName : declaringSignature?.FullName;
        return method.Signature?.ParameterTypes.Count == 1 && method.Signature.ParameterTypes[0] is SzArrayTypeSignature &&
               (declaringName is "System.Span`1" or "System.ReadOnlySpan`1" && method.Name?.Value is ".ctor" or "op_Implicit" ||
                declaringName == "System.MemoryExtensions" && method.Name == "AsSpan");
    }

    /// <summary>
    ///     Check whether the value an instruction pushes goes to a generated method that only exists with il2cpp spans
    /// </summary>
    /// <param name="body">Original method body</param>
    /// <param name="instruction">Instruction pushing the value</param>
    /// <param name="branchTargets">Branch targets of the body, collected on first use</param>
    /// <param name="globalContext">Rewrite context</param>
    /// <param name="imports">Runtime references of the target module</param>
    /// <returns>Whether the value has to stay an il2cpp span</returns>
    private static bool ConsumerWantsIl2CppSpans(CilMethodBody body, CilInstruction instruction, ref HashSet<CilInstruction>? branchTargets,
        RewriteGlobalContext globalContext, RuntimeAssemblyReferences imports)
    {
        branchTargets ??= CollectBranchTargets(body);
        var consumer = FindConsumer(body, instruction, branchTargets, out var argument);
        if (consumer?.Operand is not IMethodDescriptor next || next.Signature == null ||
            consumer.OpCode.Code is not (CilCode.Call or CilCode.Callvirt or CilCode.Newobj))
            return false;

        var parameter = argument - (next.Signature.HasThis && consumer.OpCode.Code != CilCode.Newobj ? 1 : 0);
        if (parameter < 0 || !Pass80UnstripMethods.MentionsClrOnlyType(next.Signature.ParameterTypes[parameter]))
            return false;

        var declarer = Pass80UnstripMethods.ResolveTypeInNewAssembliesRaw(globalContext, next.DeclaringType?.ToTypeSignature(), imports, next.Signature.HasThis);
        var clrSignature = TranslateSignature(next.Signature, globalContext, imports, true);
        var il2cppSignature = TranslateSignature(next.Signature, globalContext, imports, false);
        return clrSignature != null && il2cppSignature != null &&
               !DeclaresMethod(declarer, next.Name, clrSignature) && DeclaresMethod(declarer, next.Name, il2cppSignature);
    }

    /// <summary>
    ///     Find the instruction that pops the value another instruction pushes
    /// </summary>
    /// <param name="body">Method body</param>
    /// <param name="producer">Instruction pushing one value</param>
    /// <param name="branchTargets">Branch targets of the body</param>
    /// <param name="position">Position of the value among the ones the consumer pops</param>
    /// <returns>Consuming instruction, or null when a branch comes first</returns>
    private static CilInstruction? FindConsumer(CilMethodBody body, CilInstruction producer, HashSet<CilInstruction> branchTargets, out int position)
    {
        position = -1;
        if (producer.GetStackPushCount() != 1)
            return null;

        var instructions = body.Instructions;
        var depth = 1;
        for (var i = instructions.IndexOf(producer) + 1; i < instructions.Count; i++)
        {
            var current = instructions[i];
            // From a branch target on the stack can come from more than one path
            if (branchTargets.Contains(current))
                return null;

            var popped = current.GetStackPopCount(body);
            if (popped >= depth)
            {
                position = popped - depth;
                return current;
            }

            if (current.OpCode.FlowControl is not (CilFlowControl.Next or CilFlowControl.Call))
                return null;
            depth += current.GetStackPushCount() - popped;
        }

        return null;
    }

    /// <summary>
    ///     Emit a CLR span local for every span parameter. The parameter keeps its il2cpp type and the local views
    ///     the same memory.
    /// </summary>
    /// <param name="original">Method in the Unity assemblies</param>
    /// <param name="target">Generated method being filled</param>
    /// <param name="globalContext">Rewrite context</param>
    /// <param name="imports">Runtime references of the target module</param>
    /// <returns>Locals by parameter signature index, or null for a span parameter that cannot convert</returns>
    private static Dictionary<int, CilLocalVariable>? EmitClrSpanParameters(MethodDefinition original, MethodDefinition target,
        RewriteGlobalContext globalContext, RuntimeAssemblyReferences imports)
    {
        Dictionary<int, CilLocalVariable> locals = [];
        var module = imports.Module;
        foreach (var parameter in original.Parameters)
        {
            if (!Pass80UnstripMethods.MentionsClrOnlyType(parameter.ParameterType))
                continue;
            if (parameter.ParameterType is not GenericInstanceTypeSignature spanType ||
                spanType.GenericType.FullName is not ("System.Span`1" or "System.ReadOnlySpan`1"))
                return null;

            var elementType = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, spanType.TypeArguments[0], imports);
            var clrSpanType = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, spanType, imports, clrSpans: true);
            if (elementType == null || !elementType.IsValueType || clrSpanType == null)
                return null;

            var helpers = new TypeReference(module, imports.Il2CppStructArray.ToTypeDefOrRef().Scope, "Il2CppInterop.Runtime.InteropTypes", "Il2CppSpans");
            var returnShape = new GenericInstanceTypeSignature(((GenericInstanceTypeSignature)clrSpanType).GenericType, true,
                [new GenericParameterSignature(GenericParameterType.Method, 0)]);
            var convert = new MemberReference(helpers, spanType.GenericType.Name == "Span`1" ? "ToSpan" : "ToReadOnlySpan",
                MethodSignature.CreateStatic(returnShape, 1, [imports.Il2CppObjectBase]));

            var local = new CilLocalVariable(clrSpanType);
            target.CilMethodBody!.LocalVariables.Add(local);
            var instructions = target.CilMethodBody.Instructions;
            instructions.Add(OpCodes.Ldarg, target.Parameters.GetBySignatureIndex(parameter.MethodSignatureIndex));
            instructions.Add(OpCodes.Call, module.DefaultImporter.ImportMethod(convert.MakeGenericInstanceMethod([elementType])));
            instructions.Add(OpCodes.Stloc, local);
            locals.Add(parameter.MethodSignatureIndex, local);
        }

        return locals;
    }

    /// <summary>
    ///     Emit a CLR span over an array for a span constructor or AsSpan call. A rebuilt array is an il2cpp struct
    ///     array, whose memory il2cpp never moves.
    /// </summary>
    /// <param name="target">Generated method being filled</param>
    /// <param name="instruction">Original call instruction</param>
    /// <param name="method">Called method in the Unity assemblies</param>
    /// <param name="globalContext">Rewrite context</param>
    /// <param name="imports">Runtime references of the target module</param>
    /// <returns>First emitted instruction, or null when the call is not a span over a struct array</returns>
    private static CilInstruction? EmitSpanFromArray(MethodDefinition target, CilInstruction instruction, IMethodDescriptor method,
        RewriteGlobalContext globalContext, RuntimeAssemblyReferences imports)
    {
        // A generic instance's full name carries its arguments, the definition's name is what identifies it
        var declaringSignature = method.DeclaringType?.ToTypeSignature();
        var declaringName = declaringSignature is GenericInstanceTypeSignature instance ? instance.GenericType.FullName : declaringSignature?.FullName;
        var signature = method.Signature;
        if (signature == null || signature.ParameterTypes.Count != 1 || signature.ParameterTypes[0] is not SzArrayTypeSignature)
            return null;

        TypeSignature? elementSource;
        bool readOnly;
        var isConstructor = method.Name == ".ctor";
        if (declaringName is "System.Span`1" or "System.ReadOnlySpan`1" && (isConstructor || method.Name == "op_Implicit"))
        {
            if (method.DeclaringType!.ToTypeSignature() is not GenericInstanceTypeSignature spanType)
                return null;
            elementSource = spanType.TypeArguments[0];
            readOnly = declaringName == "System.ReadOnlySpan`1";
        }
        else if (declaringName == "System.MemoryExtensions" && method.Name == "AsSpan" && method is MethodSpecification specification &&
                 specification.Signature?.TypeArguments.Count == 1)
        {
            elementSource = specification.Signature.TypeArguments[0];
            readOnly = false;
        }
        else
        {
            return null;
        }

        var elementType = Pass80UnstripMethods.ResolveTypeInNewAssemblies(globalContext, elementSource, imports);
        if (elementType == null || !elementType.IsValueType)
            return null;

        // A call to the constructor initializes a span at an address already on the stack, newobj pushes a new one
        if (isConstructor && instruction.OpCode != OpCodes.Newobj && instruction.OpCode != OpCodes.Call)
            return null;

        var module = imports.Module;
        var structArray = imports.Il2CppStructArray.MakeGenericInstanceType(elementType);
        var span = new TypeReference(module, module.CorLibTypeFactory.CorLibScope, "System", "Span`1");
        var spanOfElement = new GenericInstanceTypeSignature(span, true, [elementType]);
        var spanOfParameter = new GenericInstanceTypeSignature(span, true, [new GenericParameterSignature(GenericParameterType.Type, 0)]);
        var asSpan = new MemberReference(structArray.ToTypeDefOrRef(), "AsSpan", MethodSignature.CreateInstance(spanOfParameter));

        var instructions = target.CilMethodBody!.Instructions;
        var first = instructions.Add(OpCodes.Castclass, module.DefaultImporter.ImportType(structArray.ToTypeDefOrRef()));
        instructions.Add(OpCodes.Call, module.DefaultImporter.ImportMethod(asSpan));

        TypeSignature result = spanOfElement;
        if (readOnly)
        {
            var readOnlySpan = new TypeReference(module, module.CorLibTypeFactory.CorLibScope, "System", "ReadOnlySpan`1");
            var readOnlyOfParameter = new GenericInstanceTypeSignature(readOnlySpan, true, [new GenericParameterSignature(GenericParameterType.Type, 0)]);
            var toReadOnly = new MemberReference(spanOfElement.ToTypeDefOrRef(), "op_Implicit",
                MethodSignature.CreateStatic(readOnlyOfParameter, [spanOfParameter]));
            instructions.Add(OpCodes.Call, module.DefaultImporter.ImportMethod(toReadOnly));
            result = new GenericInstanceTypeSignature(readOnlySpan, true, [elementType]);
        }

        if (isConstructor && instruction.OpCode == OpCodes.Call)
            instructions.Add(OpCodes.Stobj, module.DefaultImporter.ImportType(result.ToTypeDefOrRef()));

        return first;
    }

    private static bool HasIl2CppClass(TypeDefinition type, RewriteGlobalContext globalContext)
    {
        return TryGetIl2CppContext(type, globalContext) != null;
    }

    /// <summary>
    /// Get the rewrite context of a generated type that il2cpp still has
    /// </summary>
    /// <param name="type">Type in the generated assemblies</param>
    /// <param name="globalContext">Rewrite context</param>
    /// <returns>Its context, or null for an unstripped type or one outside the generated assemblies</returns>
    private static TypeRewriteContext? TryGetIl2CppContext(TypeDefinition type, RewriteGlobalContext globalContext)
    {
        if (type.DeclaringModule?.Assembly == null)
            return null;

        try
        {
            var context = globalContext.GetContextForNewType(type);
            return context.OriginalType != null ? context : null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private static TypeSignature? ProducedType(CilInstruction instruction)
    {
        return instruction.OpCode.Code switch
        {
            CilCode.Ldarg => (instruction.Operand as Parameter)?.ParameterType,
            CilCode.Ldloc => (instruction.Operand as CilLocalVariable)?.VariableType,
            CilCode.Ldfld or CilCode.Ldsfld => (instruction.Operand as IFieldDescriptor)?.Signature?.FieldType,
            CilCode.Call or CilCode.Callvirt => (instruction.Operand as IMethodDescriptor)?.Signature?.ReturnType,
            CilCode.Newarr => (instruction.Operand as ITypeDefOrRef)?.ToTypeSignature().MakeSzArrayType(),
            CilCode.Castclass or CilCode.Isinst or CilCode.Ldelem => (instruction.Operand as ITypeDefOrRef)?.ToTypeSignature(),
            _ => null,
        };
    }

    private static HashSet<CilInstruction> CollectBranchTargets(CilMethodBody body)
    {
        HashSet<CilInstruction> targets = new();
        foreach (var instruction in body.Instructions)
        {
            switch (instruction.Operand)
            {
                case CilInstructionLabel { Instruction: not null } label:
                    targets.Add(label.Instruction);
                    break;
                case IReadOnlyList<ICilLabel> labels:
                    foreach (var label in labels.OfType<CilInstructionLabel>())
                    {
                        if (label.Instruction != null)
                            targets.Add(label.Instruction);
                    }
                    break;
            }
        }

        foreach (var handler in body.ExceptionHandlers)
        {
            foreach (var label in new[] { handler.TryStart, handler.HandlerStart, handler.FilterStart })
            {
                if (label is CilInstructionLabel { Instruction: not null } start)
                    targets.Add(start.Instruction);
            }
        }

        return targets;
    }

    public static void ReplaceBodyWithException(MethodDefinition newMethod, RuntimeAssemblyReferences imports)
    {
        newMethod.CilMethodBody = new();
        var processor = newMethod.CilMethodBody.Instructions;

        processor.Add(OpCodes.Ldstr, "Method unstripping failed");
        processor.Add(OpCodes.Newobj, imports.Module.NotSupportedExceptionCtor());
        processor.Add(OpCodes.Throw);
        processor.Add(OpCodes.Ret);
    }

    //Required for deconstruction on net472
    private static void Deconstruct(this KeyValuePair<CilInstructionLabel, CilInstructionLabel> pair, out CilInstructionLabel key, out CilInstructionLabel value)
    {
        key = pair.Key;
        value = pair.Value;
    }
}
