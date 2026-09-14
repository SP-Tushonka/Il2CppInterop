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

            if (constraint.IsSystemEnum())
            {
                target.Constraints.Add(new GenericParameterConstraint(imports.Module.Enum().ToTypeDefOrRef()));
                continue;
            }

            var constraintType = constraint.Constraint?.ToTypeSignature();
            if (constraintType == null)
                continue;

            var rewritten = resolve(constraintType);
            if (rewritten != null)
                target.Constraints.Add(new GenericParameterConstraint(rewritten.ToTypeDefOrRef()));
        }
    }
}
