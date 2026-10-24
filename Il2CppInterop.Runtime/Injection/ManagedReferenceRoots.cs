using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime.Runtime;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Injection;

/// <summary>
///     Makes Resources.UnloadUnusedAssets keep what managed code still references. The engine only counts objects
///     reachable from il2cpp statics and scripts, so an asset held by a mod's static or an injected component's field
///     was unloaded. Live wrappers are gathered before the engine stops the il2cpp world and handed to it as one more root
/// </summary>
internal static unsafe class ManagedReferenceRoots
{
    private const string StopWorldExport = "il2cpp_stop_gc_world";
    private const string FromStaticsExport = "il2cpp_unity_liveness_calculation_from_statics";
    private const string FromRootExport = "il2cpp_unity_liveness_calculation_from_root";
    private const uint MemCommitReserve = 0x3000;
    private const uint PageExecuteReadWrite = 0x40;

    private static readonly List<IntPtr> s_Live = [];
    private static IntPtr s_OriginalStopWorld;
    private static nint s_RootHandle;

    // Native stub data, read by the stub while the world is stopped
    private static IntPtr* s_RootSlot;

    /// <summary>
    ///     Redirect the engine's stop world and statics liveness imports
    /// </summary>
    internal static void Install()
    {
        try
        {
            if (!InjectorHelpers.TryGetIl2CppExport(FromRootExport, out var fromRoot)
                || !EngineImports.TryFindSlot(StopWorldExport, out var stopSlot, out var stopWorld)
                || !EngineImports.TryFindSlot(FromStaticsExport, out var staticsSlot, out var fromStatics))
            {
                Logger.Instance.LogWarning("Assets referenced only by managed code can be unloaded, the engine imports were not found");
                return;
            }

            var stub = BuildFromStaticsStub(fromStatics, fromRoot);
            s_OriginalStopWorld = stopWorld;
            EngineImports.Swap(FromStaticsExport, staticsSlot, fromStatics, stub);
            EngineImports.Swap(StopWorldExport, stopSlot, stopWorld, (IntPtr)(delegate* unmanaged<void>)&StopWorld);
        }
        catch (Exception exception)
        {
            Logger.Instance.LogWarning("Assets referenced only by managed code can be unloaded: {Exception}", exception);
        }
    }

    /// <summary>
    ///     Gather the live wrappers into a rooted il2cpp array, then stop the world as asked
    /// </summary>
    [UnmanagedCallersOnly]
    private static void StopWorld()
    {
        try
        {
            PrepareRoot();
        }
        catch (Exception exception)
        {
            Logger.Instance.LogError("Could not gather managed references for asset unloading: {Exception}", exception);
        }

        ((delegate* unmanaged<void>)s_OriginalStopWorld)();
    }

    private static void PrepareRoot()
    {
        if (s_RootHandle != 0)
        {
            IL2CPP.il2cpp_gchandle_free(s_RootHandle);
            s_RootHandle = 0;
        }

        var started = Stopwatch.GetTimestamp();

        // Wrappers nothing references any more drop their handles, like the full collection Mono runs here
        GC.Collect();

        s_Live.Clear();
        Il2CppObjectPool.CollectLive(s_Live);
        var count = s_Live.Count;
        if (count == 0)
            return;

        var array = IL2CPP.il2cpp_array_new(Il2CppClassPointerStore<Il2CppSystem.Object>.NativeClassPtr, (ulong)count);
        var elements = (IntPtr*)(array + 4 * IntPtr.Size);
        for (var i = 0; i < count; i++)
            IL2CPP.WriteReference(array, (IntPtr)(elements + i), s_Live[i]);

        s_RootHandle = IL2CPP.il2cpp_gchandle_new(array, false);
        *s_RootSlot = array;
        Logger.Instance.LogDebug("Asset unloading keeps {Count} objects managed code references, gathered in {Milliseconds:F1} ms",
            count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    ///     Write the native replacement for the statics liveness import. It runs while the world is stopped, so it must
    ///     not enter managed code. It walks the statics, then the prepared root once, then clears the root
    /// </summary>
    /// <param name="fromStatics">Original statics liveness export</param>
    /// <param name="fromRoot">Root liveness export</param>
    /// <returns>Address of the stub</returns>
    private static IntPtr BuildFromStaticsStub(IntPtr fromStatics, IntPtr fromRoot)
    {
        byte[] code =
        [
            0x53, // push rbx
            0x48, 0x83, 0xEC, 0x20, // sub rsp, 20h
            0x48, 0x89, 0xCB, // mov rbx, rcx
            0x48, 0x8B, 0x05, 0, 0, 0, 0, // mov rax, [rip+fromStatics]
            0xFF, 0xD0, // call rax
            0x48, 0x8B, 0x0D, 0, 0, 0, 0, // mov rcx, [rip+root]
            0x48, 0x85, 0xC9, // test rcx, rcx
            0x74, 0x17, // je done
            0x48, 0x89, 0xDA, // mov rdx, rbx
            0x48, 0x8B, 0x05, 0, 0, 0, 0, // mov rax, [rip+fromRoot]
            0xFF, 0xD0, // call rax
            0x48, 0xC7, 0x05, 0, 0, 0, 0, 0, 0, 0, 0, // mov qword [rip+root], 0
            0x48, 0x83, 0xC4, 0x20, // done: add rsp, 20h
            0x5B, // pop rbx
            0xC3 // ret
        ];

        var memory = VirtualAlloc(IntPtr.Zero, 4096, MemCommitReserve, PageExecuteReadWrite);
        if (memory == IntPtr.Zero)
            throw new OutOfMemoryException("No memory for the liveness stub");

        var data = (IntPtr*)(memory + 128);
        data[0] = fromStatics;
        data[1] = fromRoot;
        data[2] = IntPtr.Zero;
        s_RootSlot = data + 2;

        // Each displacement is relative to the end of its instruction, the root store also carries a 4 byte immediate
        Patch(code, 11, 15, (IntPtr)data, memory);
        Patch(code, 20, 24, (IntPtr)(data + 2), memory);
        Patch(code, 35, 39, (IntPtr)(data + 1), memory);
        Patch(code, 44, 52, (IntPtr)(data + 2), memory);
        Marshal.Copy(code, 0, memory, code.Length);
        return memory;
    }

    private static void Patch(byte[] code, int displacementAt, int instructionEnd, IntPtr target, IntPtr codeBase)
    {
        var displacement = checked((int)((long)target - ((long)codeBase + instructionEnd)));
        BitConverter.TryWriteBytes(code.AsSpan(displacementAt), displacement);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint allocationType, uint protect);
}
