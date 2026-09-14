using Iced.Intel;

namespace Il2CppInterop.Common.XrefScans;

public static class XrefScannerLowLevel
{
    public static IEnumerable<IntPtr> JumpTargets(IntPtr codeStart, bool ignoreRetn = false)
    {
        return JumpTargetsImpl(XrefScanner.DecoderForAddress(codeStart), ignoreRetn);
    }

    private static IEnumerable<IntPtr> JumpTargetsImpl(Decoder myDecoder, bool ignoreRetn)
    {
        var firstFlowControl = true;

        while (true)
        {
            myDecoder.Decode(out var instruction);
            if (myDecoder.LastError == DecoderError.NoMoreBytes) yield break;

            // 0xcc - padding after most functions
            if (instruction.Mnemonic == Mnemonic.Int3)
                yield break;

            if (instruction.FlowControl == FlowControl.Return && !ignoreRetn)
                yield break;

            if (instruction.FlowControl == FlowControl.UnconditionalBranch ||
                instruction.FlowControl == FlowControl.Call)
            {
                // We hope and pray that the compiler didn't use short jumps for any function calls
                if (!instruction.IsJmpShort)
                {
                    yield return (IntPtr)ExtractTargetAddress(in instruction);
                    if (firstFlowControl && instruction.FlowControl == FlowControl.UnconditionalBranch) yield break;
                }
            }

            if (instruction.FlowControl != FlowControl.Next)
            {
                firstFlowControl = false;
            }
        }
    }

    /// <summary>
    /// Direct near call targets only, so a result is always a callee rather than a jump table entry or a
    /// rip-relative data reference. A function starting with an unconditional jmp is a thunk, so step through it.
    /// </summary>
    public static IEnumerable<IntPtr> CallTargets(IntPtr codeStart)
    {
        for (var hops = 0; hops < 8; hops++)
        {
            var thunkDecoder = XrefScanner.DecoderForAddress(codeStart, 16);
            thunkDecoder.Decode(out var first);
            if (thunkDecoder.LastError != DecoderError.None) break;
            if (first.Mnemonic != Mnemonic.Jmp || first.Op0Kind != OpKind.NearBranch64) break;
            codeStart = (IntPtr)first.NearBranch64;
        }

        return CallTargetsImpl(XrefScanner.DecoderForAddress(codeStart));
    }

    private static IEnumerable<IntPtr> CallTargetsImpl(Decoder decoder)
    {
        while (true)
        {
            decoder.Decode(out var instruction);
            if (decoder.LastError == DecoderError.NoMoreBytes) yield break;

            // 0xcc - padding after most functions
            if (instruction.Mnemonic == Mnemonic.Int3) yield break;
            if (instruction.FlowControl == FlowControl.Return) yield break;

            if (instruction.Mnemonic == Mnemonic.Call && instruction.Op0Kind == OpKind.NearBranch64)
                yield return (IntPtr)instruction.NearBranch64;
        }
    }

    public static IEnumerable<IntPtr> CallAndIndirectTargets(IntPtr pointer)
    {
        return CallAndIndirectTargetsImpl(XrefScanner.DecoderForAddress(pointer, 1024 * 1024));
    }

    private static IEnumerable<IntPtr> CallAndIndirectTargetsImpl(Decoder decoder)
    {
        while (true)
        {
            decoder.Decode(out var instruction);
            if (decoder.LastError == DecoderError.NoMoreBytes) yield break;

            if (instruction.FlowControl == FlowControl.Return)
                yield break;

            if (instruction.Mnemonic == Mnemonic.Int || instruction.Mnemonic == Mnemonic.Int1)
                yield break;

            if (instruction.Mnemonic == Mnemonic.Call || instruction.Mnemonic == Mnemonic.Jmp)
            {
                var targetAddress = XrefScanner.ExtractTargetAddress(instruction);
                if (targetAddress != 0)
                    yield return (IntPtr)targetAddress;
                continue;
            }

            if (instruction.Mnemonic == Mnemonic.Lea)
                if (instruction.MemoryBase == Register.RIP)
                {
                    var targetAddress = instruction.IPRelativeMemoryAddress;
                    if (targetAddress != 0)
                        yield return (IntPtr)targetAddress;
                }
        }
    }

    private static ulong ExtractTargetAddress(in Instruction instruction)
    {
        switch (instruction.Op0Kind)
        {
            case OpKind.NearBranch16:
                return instruction.NearBranch16;
            case OpKind.NearBranch32:
                return instruction.NearBranch32;
            case OpKind.NearBranch64:
                return instruction.NearBranch64;
            case OpKind.FarBranch16:
                return instruction.FarBranch16;
            case OpKind.FarBranch32:
                return instruction.FarBranch32;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}
