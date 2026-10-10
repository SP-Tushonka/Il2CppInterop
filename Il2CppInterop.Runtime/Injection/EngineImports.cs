using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Il2CppInterop.Common;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Injection;

/// <summary>
///     Swaps the engine's pointers to il2cpp exports. Unity resolves the il2cpp API by name into a table of its own, so
///     redirecting a slot there changes what the engine sees without touching il2cpp or anyone else calling the export
/// </summary>
internal static unsafe class EngineImports
{
    private const string EngineModuleName = "UnityPlayer.dll";
    private const uint ImageScnMemWrite = 0x80000000;

    private static readonly Lazy<State?> s_State = new(CreateState, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     Engine module, its writable sections and how many exports share each export address
    /// </summary>
    private sealed record State(ProcessModule Engine, List<(IntPtr Start, int Size)> Writable, Dictionary<IntPtr, int> ExportCounts);

    /// <summary>
    ///     Find the engine's import slot for an il2cpp export
    /// </summary>
    /// <param name="exportName">Name of the il2cpp export</param>
    /// <param name="slot">Address of the engine's pointer to it</param>
    /// <param name="export">Address of the export, which is what the slot holds</param>
    /// <returns>True when exactly one slot holds an export no other export shares</returns>
    public static bool TryFindSlot(string exportName, out IntPtr slot, out IntPtr export)
    {
        slot = IntPtr.Zero;
        var state = s_State.Value;
        // A folded export also serves another function, possibly one with more arguments than a handler forwards
        if (state == null || !InjectorHelpers.TryGetIl2CppExport(exportName, out export) || state.ExportCounts.GetValueOrDefault(export) != 1)
        {
            export = IntPtr.Zero;
            Logger.Instance.LogWarning("{Export} is missing or shared with another export", exportName);
            return false;
        }

        var found = FindPointerSlots(state.Writable, export);
        if (found.Count != 1)
        {
            Logger.Instance.LogWarning("{Module} holds {Count} pointers to {Export}, expected one", EngineModuleName, found.Count, exportName);
            return false;
        }

        slot = found[0];
        return true;
    }

    /// <summary>
    ///     Point an engine import slot at a handler
    /// </summary>
    /// <param name="exportName">Export the slot belongs to, for the log</param>
    /// <param name="slot">Slot from <see cref="TryFindSlot" /></param>
    /// <param name="export">Value the slot holds now</param>
    /// <param name="handler">Function the engine calls from now on</param>
    public static void Swap(string exportName, IntPtr slot, IntPtr export, IntPtr handler)
    {
        Interlocked.CompareExchange(ref *(IntPtr*)slot, handler, export);
        Logger.Instance.LogTrace("Engine import of {Export} at {Module}+0x{Rva} now reaches an injection handler", exportName,
            EngineModuleName, (slot - s_State.Value!.Engine.BaseAddress).ToString("X"));
    }

    private static State? CreateState()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var engine = Process.GetCurrentProcess().Modules.OfType<ProcessModule>()
            .FirstOrDefault(module => string.Equals(module.ModuleName, EngineModuleName, StringComparison.OrdinalIgnoreCase));
        if (engine == null)
        {
            Logger.Instance.LogWarning("{Module} is not loaded", EngineModuleName);
            return null;
        }

        var exportCounts = ExportAddresses(InjectorHelpers.Il2CppModule.BaseAddress)
            .GroupBy(address => address)
            .ToDictionary(group => group.Key, group => group.Count());
        return new State(engine, WritableSections(engine.BaseAddress), exportCounts);
    }

    /// <summary>
    ///     Read the export address table of a loaded PE32+ module
    /// </summary>
    /// <param name="moduleBase">Module base address</param>
    /// <returns>Address of every exported function</returns>
    private static List<IntPtr> ExportAddresses(IntPtr moduleBase)
    {
        var result = new List<IntPtr>();
        var ntHeaders = moduleBase + *(int*)(moduleBase + 0x3C);
        // The export directory is the first data directory, 112 bytes into the optional header that follows the
        // 4 byte signature and the 20 byte file header
        var exportDirectoryRva = *(int*)(ntHeaders + 24 + 112);
        if (exportDirectoryRva == 0)
            return result;

        var exportDirectory = moduleBase + exportDirectoryRva;
        var functionCount = *(int*)(exportDirectory + 20);
        var functions = (int*)(moduleBase + *(int*)(exportDirectory + 28));
        for (var i = 0; i < functionCount; i++)
        {
            if (functions[i] != 0)
                result.Add(moduleBase + functions[i]);
        }

        return result;
    }

    /// <summary>
    ///     List the writable sections of a loaded module
    /// </summary>
    /// <param name="moduleBase">Module base address</param>
    /// <returns>Start and size of each writable section</returns>
    private static List<(IntPtr Start, int Size)> WritableSections(IntPtr moduleBase)
    {
        var result = new List<(IntPtr Start, int Size)>();
        var ntHeaders = moduleBase + *(int*)(moduleBase + 0x3C);
        var sectionCount = *(ushort*)(ntHeaders + 6);
        var optionalHeaderSize = *(ushort*)(ntHeaders + 20);
        var sections = ntHeaders + 24 + optionalHeaderSize;
        for (var i = 0; i < sectionCount; i++)
        {
            var section = sections + i * 40;
            if ((*(uint*)(section + 36) & ImageScnMemWrite) != 0)
                result.Add((moduleBase + *(int*)(section + 12), *(int*)(section + 8)));
        }

        return result;
    }

    /// <summary>
    ///     Find every aligned pointer sized slot holding a value
    /// </summary>
    /// <param name="sections">Memory ranges to scan</param>
    /// <param name="value">Pointer to look for</param>
    /// <returns>Addresses of the matching slots</returns>
    private static List<IntPtr> FindPointerSlots(List<(IntPtr Start, int Size)> sections, IntPtr value)
    {
        var result = new List<IntPtr>();
        foreach (var (start, size) in sections)
        {
            var slots = (IntPtr*)start;
            var count = size / IntPtr.Size;
            for (var i = 0; i < count; i++)
            {
                if (slots[i] == value)
                    result.Add((IntPtr)(slots + i));
            }
        }

        return result;
    }
}
