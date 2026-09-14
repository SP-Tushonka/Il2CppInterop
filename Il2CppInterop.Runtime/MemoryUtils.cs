using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Il2CppInterop.Common.XrefScans;

namespace Il2CppInterop.Runtime;

internal class MemoryUtils
{
    public static unsafe nint FindSignatureInModule(ProcessModule module, SignatureDefinition sigDef)
    {
        // On newer Unity (6000.x) the loaded GameAssembly maps some pages PAGE_NOACCESS / guard pages; the raw
        // linear byte walk in FindSignatureInBlock dereferences them and throws a fatal AccessViolationException.
        // Use VirtualQuery to enumerate the module's regions and scan only the readable committed ones, skipping
        // the rest -- without ever modifying page protections. VirtualQuery is Windows-only; elsewhere (where this
        // guard-page issue does not arise) fall back to the plain whole-module scan.
        nint ptr = 0;
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            const uint pageReadable = PageReadOnly | PageReadWrite | PageWriteCopy |
                                      PageExecuteRead | PageExecuteReadWrite | PageExecuteWriteCopy;
            var regions = GetModuleRegions(module);
            foreach (var region in regions)
            {
                if (region.State != MemCommit || (region.Protect & PageGuard) != 0 ||
                    (region.Protect & pageReadable) == 0)
                    continue;
                ptr = FindSignatureInBlock((nint)region.BaseAddress, (long)region.RegionSize,
                    sigDef.pattern, sigDef.mask, sigDef.offset);
                if (ptr != 0)
                    break;
            }
        }
        else
        {
            ptr = FindSignatureInBlock(module.BaseAddress, module.ModuleMemorySize,
                sigDef.pattern, sigDef.mask, sigDef.offset);
        }

        if (ptr != 0 && sigDef.xref)
            ptr = XrefScannerLowLevel.JumpTargets(ptr).FirstOrDefault();
        return ptr;
    }

    public static nint FindSignatureInBlock(nint block, long blockSize, string pattern, string mask, long sigOffset = 0)
    {
        return FindSignatureInBlock(block, blockSize, pattern.ToCharArray(), mask.ToCharArray(), sigOffset);
    }

    public static unsafe nint FindSignatureInBlock(nint block, long blockSize, char[] pattern, char[] mask,
        long sigOffset = 0)
    {
        // Stop at blockSize - mask.Length so the inner read (block + address + mask.Length - 1) never passes the
        // end of this block. When the caller scans per-region (Unity 6 readable regions interleaved with guard /
        // PAGE_NOACCESS pages), an overread off the tail would fault the adjacent page -> a fatal
        // AccessViolationException that aborts the chainloader. If the block is smaller than the mask, scan nothing.
        for (long address = 0; address <= blockSize - mask.Length; address++)
        {
            var found = true;
            for (uint offset = 0; offset < mask.Length; offset++)
                if (*(byte*)(address + block + offset) != (byte)pattern[offset] && mask[offset] != '?')
                {
                    found = false;
                    break;
                }

            if (found)
                return (nint)(address + block + sigOffset);
        }

        return 0;
    }

    /// <summary>
    /// Walks the module's address space via <c>VirtualQuery</c>, collecting each memory region so the scan can pick
    /// the readable committed ones. Stops at the first <c>VirtualQuery</c> failure or once the module end is reached.
    /// </summary>
    [SupportedOSPlatform("windows6.1")]
    internal static unsafe List<MemoryBasicInformation> GetModuleRegions(ProcessModule module)
    {
        var regions = new List<MemoryBasicInformation>();
        var moduleEndAddress = (long)module.BaseAddress + module.ModuleMemorySize;
        var currentAddress = (long)module.BaseAddress;
        while (currentAddress < moduleEndAddress)
        {
            MemoryBasicInformation memoryInfo = default;
            var result = VirtualQuery((void*)currentAddress, &memoryInfo, (nuint)sizeof(MemoryBasicInformation));
            if (result == 0)
                break; // error, or reached the end of the module's mapped memory

            regions.Add(memoryInfo);
            currentAddress = (long)memoryInfo.BaseAddress + (long)memoryInfo.RegionSize;
        }

        return regions;
    }

    internal enum FunctionEntry
    {
        /// <summary>The exception directory lists this address as a function's first instruction.</summary>
        Yes,

        /// <summary>The address falls inside a listed function, so it cannot be an entry point.</summary>
        Interior,

        /// <summary>No entry covers the address. Leaf functions get none, so this proves nothing either way.</summary>
        Unknown
    }

    /// <summary>
    /// Answers whether an address is a function's first instruction using the PE exception directory, which lists
    /// the bounds of every function that needs unwind data. Exact where the table has an entry, which the usual
    /// alignment guess is not, because a loop head inside a function can be aligned too.
    /// </summary>
    internal static unsafe FunctionEntry ClassifyFunctionEntry(ProcessModule module, nint address)
    {
        var moduleBase = (byte*)module.BaseAddress;
        if (moduleBase == null || address < (nint)moduleBase ||
            address >= (nint)moduleBase + module.ModuleMemorySize)
            return FunctionEntry.Unknown;

        var peOffset = *(int*)(moduleBase + 0x3C);
        if (peOffset <= 0 || peOffset > module.ModuleMemorySize - 0x100)
            return FunctionEntry.Unknown;

        var ntHeader = moduleBase + peOffset;
        if (*(uint*)ntHeader != 0x00004550) // "PE\0\0"
            return FunctionEntry.Unknown;

        const ushort pe32Plus = 0x20B;
        var optionalHeader = ntHeader + 24;
        if (*(ushort*)optionalHeader != pe32Plus)
            return FunctionEntry.Unknown;

        // Data directory 3 is IMAGE_DIRECTORY_ENTRY_EXCEPTION, at a fixed offset in the PE32+ optional header.
        var exceptionDirectory = optionalHeader + 112;
        var tableRva = *(uint*)exceptionDirectory;
        var tableSize = *(uint*)(exceptionDirectory + 4);
        if (tableRva == 0 || tableSize < RuntimeFunctionSize)
            return FunctionEntry.Unknown;

        var count = (int)(tableSize / RuntimeFunctionSize);
        var table = moduleBase + tableRva;
        var target = (uint)(address - (nint)moduleBase);

        // The table is sorted by BeginAddress, so bisect it.
        var low = 0;
        var high = count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var record = table + middle * RuntimeFunctionSize;
            var begin = *(uint*)record;
            var end = *(uint*)(record + 4);

            if (target < begin)
                high = middle - 1;
            else if (target >= end)
                low = middle + 1;
            else
                return target == begin ? FunctionEntry.Yes : FunctionEntry.Interior;
        }

        return FunctionEntry.Unknown;
    }

    private const int RuntimeFunctionSize = 12;

    private const uint MemCommit = 0x1000;
    private const uint PageReadOnly = 0x02;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;
    private const uint PageGuard = 0x100;

    // MEMORY_BASIC_INFORMATION. Sequential layout reproduces the native padding around PartitionId on 32 and 64 bit
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MemoryBasicInformation
    {
        public void* BaseAddress;
        public void* AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe nuint VirtualQuery(void* lpAddress, MemoryBasicInformation* lpBuffer, nuint dwLength);

    public struct SignatureDefinition
    {
        public string pattern;
        public string mask;
        public int offset;
        public bool xref;
    }
}
