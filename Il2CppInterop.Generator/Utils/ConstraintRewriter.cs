using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Il2CppInterop.Generator.Extensions;

namespace Il2CppInterop.Generator.Utils;

// System.ValueType is dropped because non blittable structs are wrapped by classes. Interface constraints are
// dropped because primitives and blittable structs never implement the generated interfaces.
public static class ConstraintRewriter
{
    public static void Rewrite(GenericParameter original, GenericParameter target, RuntimeAssemblyReferences imports,
        Func<TypeSignature, TypeSignature?> resolve)
    {
        foreach (var constraint in original.Constraints)
        {
            if (constraint.IsSystemValueType() || constraint.IsInterface())
                continue;

            // Unmanaged is System.ValueType marked with UnmanagedType. Both stay CLR types.
            if (constraint.Constraint is TypeSpecification { Signature: CustomModifierTypeSignature { BaseType.FullName: "System.ValueType" } })
            {
                var unmanagedType = imports.Module.DefaultImporter.ImportType(typeof(System.Runtime.InteropServices.UnmanagedType));
                var unmanaged = new CustomModifierTypeSignature(unmanagedType, true, imports.Module.ValueType());
                target.Constraints.Add(new GenericParameterConstraint(unmanaged.ToTypeDefOrRef()));
                continue;
            }

            if (constraint.IsSystemEnum())
            {
                target.Constraints.Add(new GenericParameterConstraint(imports.Module.Enum().ToTypeDefOrRef()));
                continue;
            }

            var constraintType = constraint.Constraint?.ToTypeSignature();
            if (constraintType == null)
                continue;

            // A constraint from the Cpp2IL dummies may not resolve, the generated type it maps to does
            var rewritten = resolve(constraintType);
            var rewrittenDefinition = rewritten is GenericInstanceTypeSignature generic ? generic.GenericType.Resolve() : rewritten?.Resolve();
            if (rewritten != null && rewrittenDefinition?.IsInterface != true)
                target.Constraints.Add(new GenericParameterConstraint(rewritten.ToTypeDefOrRef()));
        }
    }
}
