using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.File;
using Iced.Intel;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Generator.Passes.SPT;

// SPT addition. A method's init flag or icall pointer is a function local static, so every function that writes it carries
// an inlined copy of the method. A stub with no static is found by having no direct calls and only forwarding to another method.
public static class PassSptInlineMarks
{
    private const int MaxNamedCallers = 8;

    private const int MaxStubSize = 0x80;

    private const int UnwindFlagChainInfo = 4;

    public static void DoPass(RewriteGlobalContext context)
    {
        var options = context.Options;
        if (!options.InlineMarks || string.IsNullOrEmpty(options.GameAssemblyPath))
            return;

        var pe = PEFile.FromFile(options.GameAssemblyPath);
        using var mappedFile = MemoryMappedFile.CreateFromFile(options.GameAssemblyPath, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        unsafe
        {
            byte* basePtr = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
            try
            {
                Mark(context, new Image(pe, (nint)basePtr));
            }
            finally
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    private static void Mark(RewriteGlobalContext context, Image image)
    {
        var methodsByRva = new Dictionary<uint, List<MethodRewriteContext>>();
        foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
                foreach (var method in type.Methods)
                    if (method.Rva != 0)
                    {
                        if (!methodsByRva.TryGetValue((uint)method.Rva, out var list))
                            methodsByRva[(uint)method.Rva] = list = [];
                        list.Add(method);
                    }

        var chunks = ReadFunctionTable(image);
        var functions = new HashSet<uint>(chunks.Where(chunk => chunk.Primary == chunk.Begin).Select(chunk => chunk.Begin));
        var scan = new Scan();

        Parallel.ForEach(chunks, chunk => ScanChunk(image, chunk, false, methodsByRva, scan));

        // A leaf has no unwind data, so its own calls and its size come from the method table
        var starts = methodsByRva.Keys.OrderBy(rva => rva).ToList();
        Parallel.For(0, starts.Count, i =>
        {
            var rva = starts[i];
            if (functions.Contains(rva))
                return;

            var end = i + 1 < starts.Count ? Math.Min(starts[i + 1], rva + MaxStubSize) : rva + MaxStubSize;
            ScanChunk(image, new FunctionChunk(rva, (uint)end, rva), true, methodsByRva, scan);
        });

        var owned = AssignStatics(scan);

        int byStatic = 0, byStub = 0;
        foreach (var pair in methodsByRva)
        {
            var rva = pair.Key;
            var methods = pair.Value;
            scan.DirectCalls.TryGetValue(rva, out var calls);

            if (owned.TryGetValue(rva, out var statics))
            {
                var hosts = statics.SelectMany(address => scan.Writes[address]).Distinct()
                    .Where(function => function != rva)
                    .OrderBy(function => function)
                    .ToList();
                if (hosts.Count > 0)
                {
                    var names = hosts.Take(MaxNamedCallers).Select(function => Describe(function, methodsByRva)).ToArray();
                    foreach (var method in methods)
                        AddAttribute(method, names, hosts.Count, calls, null);
                    byStatic += methods.Count;
                    continue;
                }
            }

            // Virtual methods and lambdas are reached through a slot or a delegate, so no direct call proves nothing there.
            // A target body shared by several methods is a folded throw helper
            if (calls != 0 || !scan.StubTargets.TryGetValue(rva, out var target) ||
                !scan.Sizes.TryGetValue(rva, out var size) || size > MaxStubSize ||
                target != 0 && methodsByRva[target].Count > 1 ||
                methods.Any(method => method.OriginalMethod.IsVirtual || method.OriginalMethod.Name.Contains('<')))
                continue;

            foreach (var method in methods)
                AddAttribute(method, [], 0, 0, target != 0 ? Describe(target, methodsByRva) : null);
            byStub += methods.Count;
        }

        Logger.Instance.LogInformation("{Static} methods marked as inlined into named functions and {Stub} small methods as never called directly, from {Functions} functions",
            byStatic, byStub, functions.Count);
    }

    private sealed class Scan
    {
        public readonly ConcurrentDictionary<uint, ConcurrentBag<uint>> Writes = new();
        public readonly ConcurrentDictionary<uint, List<uint>> PrologueReads = new();
        public readonly ConcurrentDictionary<uint, int> Sizes = new();
        public readonly ConcurrentDictionary<uint, int> DirectCalls = new();
        public readonly ConcurrentDictionary<uint, uint> StubTargets = new();
    }

    private static unsafe void ScanChunk(Image image, FunctionChunk chunk, bool isLeaf,
        Dictionary<uint, List<MethodRewriteContext>> methodsByRva, Scan scan)
    {
        var isPrimary = chunk.Primary == chunk.Begin;
        var length = (int)(chunk.End - chunk.Begin);
        var stream = new UnmanagedMemoryStream((byte*)image.At(chunk.Begin), length);
        var decoder = Decoder.Create(64, new StreamCodeReader(stream));
        decoder.IP = chunk.Begin;

        var prologue = isPrimary;
        var prologueReads = new List<uint>();
        var chunkWrites = new HashSet<uint>();
        var callees = new HashSet<uint>();

        while (decoder.IP < chunk.End)
        {
            decoder.Decode(out var instruction);
            if (instruction.IsInvalid)
                continue;

            if (instruction.Mnemonic is Mnemonic.Call or Mnemonic.Jmp && instruction.Op0Kind == OpKind.NearBranch64)
            {
                var target = (uint)instruction.NearBranch64;
                var isMethod = target != chunk.Primary && methodsByRva.ContainsKey(target);
                if (isMethod)
                {
                    scan.DirectCalls.AddOrUpdate(target, 1, (_, count) => count + 1);
                    callees.Add(target);
                }

                if (instruction.Mnemonic == Mnemonic.Call)
                    prologue = false;

                // A leaf has no recorded end, it stops at its first unconditional jump or return
                if (isLeaf && instruction.Mnemonic == Mnemonic.Jmp)
                    break;
                continue;
            }

            if (isLeaf && instruction.FlowControl == FlowControl.Return)
                break;

            if (instruction.Mnemonic == Mnemonic.Call)
                prologue = false;

            if (!instruction.IsIPRelativeMemoryOperand || instruction.Mnemonic == Mnemonic.Lea)
                continue;

            var address = (uint)instruction.IPRelativeMemoryAddress;
            if (prologue)
                prologueReads.Add(address);

            if (instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Memory)
                chunkWrites.Add(address);
        }

        var scanned = (int)(decoder.IP - chunk.Begin);
        scan.Sizes.AddOrUpdate(chunk.Primary, scanned, (_, size) => size + scanned);

        foreach (var address in chunkWrites)
            scan.Writes.GetOrAdd(address, _ => []).Add(chunk.Primary);

        if (isPrimary && prologueReads.Count > 0)
            scan.PrologueReads[chunk.Primary] = prologueReads;

        // A stub works on its arguments and fields and calls one method at most, without statics of its own
        if (isPrimary && callees.Count <= 1 && chunkWrites.Count == 0)
            scan.StubTargets[chunk.Primary] = callees.FirstOrDefault();
    }

    // Statics are laid out in the order of the functions that own them. Uncontested prologue claims that keep that order
    // anchor it, and a shared static belongs to the one writer that falls between its neighbouring anchors
    private static Dictionary<uint, List<uint>> AssignStatics(Scan scan)
    {
        // The first prologue read the function also writes is its static. A stack cookie is read first but never written
        var claims = new Dictionary<uint, List<uint>>();
        foreach (var pair in scan.PrologueReads)
        {
            foreach (var address in pair.Value)
            {
                if (!scan.Writes.TryGetValue(address, out var writers) || !writers.Contains(pair.Key))
                    continue;

                if (!claims.TryGetValue(address, out var claimants))
                    claims[address] = claimants = [];
                claimants.Add(pair.Key);
                break;
            }
        }

        var anchors = MonotonicAnchors(claims
            .Where(pair => pair.Value.Count == 1)
            .Select(pair => (Function: pair.Value[0], Static: pair.Key))
            .OrderBy(anchor => anchor.Function)
            .ToList());
        var anchorStatics = anchors.Select(anchor => anchor.Static).ToList();

        var owned = new Dictionary<uint, List<uint>>();
        foreach (var pair in scan.Writes)
        {
            var writers = pair.Value.Distinct().ToList();
            if (writers.Count < 2)
                continue;

            var index = anchorStatics.BinarySearch(pair.Key);
            List<uint> owners;
            if (index >= 0)
            {
                owners = writers.Where(function => function == anchors[index].Function).ToList();
            }
            else
            {
                index = ~index;
                var low = index > 0 ? anchors[index - 1].Function : 0;
                var high = index < anchors.Count ? anchors[index].Function : uint.MaxValue;
                owners = writers.Where(function => function > low && function < high).ToList();
            }

            if (owners.Count != 1)
                continue;

            if (!owned.TryGetValue(owners[0], out var statics))
                owned[owners[0]] = statics = [];
            statics.Add(pair.Key);
        }

        return owned;
    }

    // Longest run of anchors whose statics rise with their functions, a few claims made by inlined prologues break it
    private static List<(uint Function, uint Static)> MonotonicAnchors(List<(uint Function, uint Static)> anchors)
    {
        var tails = new List<int>();
        var previous = new int[anchors.Count];
        for (var i = 0; i < anchors.Count; i++)
        {
            int low = 0, high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (anchors[tails[middle]].Static < anchors[i].Static)
                    low = middle + 1;
                else
                    high = middle;
            }

            previous[i] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count)
                tails.Add(i);
            else
                tails[low] = i;
        }

        var result = new List<(uint Function, uint Static)>(tails.Count);
        for (var i = tails.Count > 0 ? tails[tails.Count - 1] : -1; i >= 0; i = previous[i])
            result.Add(anchors[i]);
        result.Reverse();
        return result;
    }

    private static string Describe(uint rva, Dictionary<uint, List<MethodRewriteContext>> methodsByRva)
    {
        if (!methodsByRva.TryGetValue(rva, out var methods))
            return $"sub_{rva:X}";

        // Overloads share an interop name, so the parameters tell them apart
        var method = methods[0];
        var parameters = string.Join(", ", method.NewMethod.Parameters.Select(parameter => parameter.ParameterType.Name));
        return $"{method.DeclaringType.NewType.FullName}.{method.NewMethod.Name}({parameters})";
    }

    private static void AddAttribute(MethodRewriteContext method, string[] names, int count, int directCalls, string? forwardsTo)
    {
        var imports = method.DeclaringType.AssemblyContext.Imports;
        var module = imports.Module;
        method.NewMethod.CustomAttributes.Add(new CustomAttribute(
            (ICustomAttributeType)imports.InlinedIntoAttributector.Value,
            new CustomAttributeSignature(
                new CustomAttributeArgument(module.String().MakeSzArrayType(), names.Cast<object?>()),
                new CustomAttributeArgument(module.Int(), count),
                new CustomAttributeArgument(module.Int(), directCalls),
                new CustomAttributeArgument(module.String(), forwardsTo))));
    }

    private readonly record struct FunctionChunk(uint Begin, uint End, uint Primary);

    private sealed class Image(PEFile pe, nint basePtr)
    {
        public readonly PEFile Pe = pe;

        public nint At(uint rva)
        {
            return basePtr + (nint)Pe.RvaToFileOffset(rva);
        }
    }

    // Every non leaf function has an entry in the exception directory. Cold parts chain back to the entry of their function
    private static unsafe List<FunctionChunk> ReadFunctionTable(Image image)
    {
        var directory = image.Pe.OptionalHeader.GetDataDirectory(DataDirectoryIndex.ExceptionDirectory);
        var table = (uint*)image.At(directory.VirtualAddress);
        var count = directory.Size / 12;

        var chunks = new List<FunctionChunk>((int)count);
        for (var i = 0; i < count; i++)
            chunks.Add(new FunctionChunk(table[i * 3], table[i * 3 + 1], PrimaryBegin(image, table[i * 3], table[i * 3 + 2])));

        return chunks;
    }

    private static unsafe uint PrimaryBegin(Image image, uint begin, uint unwindRva)
    {
        for (var depth = 0; depth < 32; depth++)
        {
            var info = (byte*)image.At(unwindRva & ~1u);
            if ((info[0] >> 3 & UnwindFlagChainInfo) == 0)
                return begin;

            var codes = (info[2] + 1) & ~1;
            var parent = (uint*)(info + 4 + codes * 2);
            begin = parent[0];
            unwindRva = parent[2];
        }

        return begin;
    }
}
