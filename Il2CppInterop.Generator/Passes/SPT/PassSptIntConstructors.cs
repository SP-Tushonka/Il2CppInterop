using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Generator.Passes.SPT;

// Every wrapper has a public (IntPtr pointer) constructor, and an int converts implicitly to IntPtr since it became nint.
// That conversion beats int to long, float or double, so new WaitForSeconds(10) wrapped address 10 instead of waiting.
// A wrapper whose single argument constructors take such a type gets an exact (int) constructor forwarding to the one
// an int argument was meant for. An IntPtr argument still binds the pointer constructor.
public static class PassSptIntConstructors
{
    // In the order C# prefers them for an int argument
    private static readonly (string Type, CilOpCode Conversion)[] Widenings =
    [
        ("System.Int64", CilOpCodes.Conv_I8),
        ("System.Single", CilOpCodes.Conv_R4),
        ("System.Double", CilOpCodes.Conv_R8),
    ];

    public static void DoPass(RewriteGlobalContext context)
    {
        if (!context.Options.IntConstructors)
            return;

        var added = 0;
        foreach (var assemblyContext in context.Assemblies)
        {
            foreach (var typeContext in assemblyContext.Types)
            {
                var type = typeContext.NewType;
                if (type.IsValueType || type.IsInterface || type.GenericParameters.Count > 0)
                    continue;

                var singles = type.Methods
                    .Where(m => m.IsConstructor && !m.IsStatic && m.IsPublic && m.Parameters.Count == 1)
                    .ToList();
                if (singles.All(m => m.Parameters[0].ParameterType.FullName != "System.IntPtr") ||
                    singles.Any(m => m.Parameters[0].ParameterType.FullName == "System.Int32"))
                    continue;

                var target = Widenings
                    .Select(w => (w.Conversion, Constructor: singles.FirstOrDefault(m => m.Parameters[0].ParameterType.FullName == w.Type)))
                    .FirstOrDefault(c => c.Constructor != null);
                if (target.Constructor == null)
                    continue;

                type.Methods.Add(CreateForwarder(assemblyContext.NewAssembly.ManifestModule!, target.Constructor, target.Conversion));
                added++;
            }
        }

        Logger.Instance.LogInformation("{Count} int constructors added", added);
    }

    private static MethodDefinition CreateForwarder(ModuleDefinition module, MethodDefinition target, CilOpCode conversion)
    {
        var constructor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
        constructor.AddParameter(module.CorLibTypeFactory.Int32, target.ParameterDefinitions.FirstOrDefault()?.Name?.Value ?? "value");

        constructor.CilMethodBody = new();
        var body = constructor.CilMethodBody.Instructions;
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Ldarg_1);
        body.Add(conversion);
        body.Add(CilOpCodes.Call, target);
        body.Add(CilOpCodes.Ret);
        return constructor;
    }
}
