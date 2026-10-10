using System;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2CppInterop.Runtime.Attributes;

/// <summary>
///     Base of the C# attributes the generator mirrors from il2cpp attribute classes, so they can be applied to injected
///     classes. The class injector builds the il2cpp attribute from it and reports it to the engine
/// </summary>
public abstract class Il2CppMirroredAttribute : Attribute
{
    private readonly Type[]? _constructorParameters;
    private readonly string[]? _fieldNames;
    private readonly object?[] _constructorArguments;

    /// <summary>
    ///     Mirror an il2cpp attribute built through one of its wrapper constructors
    /// </summary>
    /// <param name="il2CppAttributeType">Generated wrapper of the il2cpp attribute class</param>
    /// <param name="constructorParameters">Parameter types of the wrapper constructor to build it with</param>
    /// <param name="constructorArguments">Arguments as written on the attribute, a System.Type is converted to its il2cpp type</param>
    protected Il2CppMirroredAttribute(Type il2CppAttributeType, Type[] constructorParameters, object?[] constructorArguments)
    {
        Il2CppAttributeType = il2CppAttributeType;
        _constructorParameters = constructorParameters;
        _constructorArguments = constructorArguments;
    }

    /// <summary>
    ///     Mirror an il2cpp attribute whose constructor the game stripped and that only stores its arguments in fields.
    ///     It is allocated without a constructor and the fields are set through the wrapper
    /// </summary>
    /// <param name="il2CppAttributeType">Generated wrapper of the il2cpp attribute class</param>
    /// <param name="fieldNames">Fields the constructor stores its arguments in</param>
    /// <param name="fieldValues">Arguments as written on the attribute, a System.Type is converted to its il2cpp type</param>
    protected Il2CppMirroredAttribute(Type il2CppAttributeType, string[] fieldNames, object?[] fieldValues)
    {
        Il2CppAttributeType = il2CppAttributeType;
        _fieldNames = fieldNames;
        _constructorArguments = fieldValues;
    }

    /// <summary>
    ///     Generated wrapper of the il2cpp attribute class this attribute stands for
    /// </summary>
    public Type Il2CppAttributeType { get; }

    internal object?[] ConstructorArguments => _constructorArguments;

    /// <summary>
    ///     Create the il2cpp attribute object
    /// </summary>
    /// <returns>Wrapper of the new il2cpp attribute</returns>
    public Il2CppObjectBase CreateIl2CppAttribute()
    {
        if (_fieldNames != null)
            return CreateFromFields(_fieldNames);

        var constructor = Il2CppAttributeType.GetConstructor(_constructorParameters!)
                          ?? throw new MissingMethodException($"{Il2CppAttributeType} has no constructor taking ({string.Join<Type>(", ", _constructorParameters)})");

        var arguments = new object?[_constructorArguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            arguments[i] = _constructorArguments[i] is Type type && _constructorParameters[i] == typeof(Il2CppSystem.Type)
                ? Il2CppType.From(type)
                : _constructorArguments[i];
        }

        try
        {
            return (Il2CppObjectBase)constructor.Invoke(arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw exception.InnerException;
        }
    }

    /// <summary>
    ///     Allocate the il2cpp attribute without a constructor and store the arguments in its fields
    /// </summary>
    /// <param name="fieldNames">Field set by each argument</param>
    /// <returns>Wrapper of the new il2cpp attribute</returns>
    private Il2CppObjectBase CreateFromFields(string[] fieldNames)
    {
        var klass = Il2CppClassPointerStore.GetNativeClassPointer(Il2CppAttributeType);
        if (klass == IntPtr.Zero)
            throw new TypeLoadException($"{Il2CppAttributeType} has no il2cpp class");

        var attribute = (Il2CppObjectBase)Activator.CreateInstance(Il2CppAttributeType, IL2CPP.il2cpp_object_new(klass))!;
        for (var i = 0; i < fieldNames.Length; i++)
        {
            var field = Il2CppAttributeType.GetProperty(fieldNames[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? throw new MissingMemberException(Il2CppAttributeType.FullName, fieldNames[i]);
            var value = _constructorArguments[i] is Type type && field.PropertyType == typeof(Il2CppSystem.Type)
                ? Il2CppType.From(type)
                : _constructorArguments[i];
            field.SetValue(attribute, value);
        }

        return attribute;
    }
}
