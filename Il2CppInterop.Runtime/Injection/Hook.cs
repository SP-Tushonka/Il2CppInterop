using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Injection
{
    internal abstract class Hook<T> where T : Delegate
    {
        private bool _isApplied;
        private T _detour;
        private T _method;
        private T _original;

        public T Original => _original;

        public abstract string TargetMethodName { get; }
        public abstract T GetDetour();
        public abstract IntPtr FindTargetMethod();

        public virtual void TargetMethodNotFound()
        {
            throw new Exception($"Required target method {TargetMethodName} not found");
        }

        public void ApplyHook()
        {
            if (_isApplied) return;

            var methodPtr = FindTargetMethod();

            if (methodPtr == IntPtr.Zero)
            {
                TargetMethodNotFound();
                return;
            }

            // As an offset into GameAssembly, so a report can be checked against the build's symbols
            Logger.Instance.LogInformation("{MethodName} found at GameAssembly+0x{Rva}", TargetMethodName,
                (methodPtr - InjectorHelpers.Il2CppModule.BaseAddress).ToString("X"));

            _detour = GetDetour();
            Detour.Apply(methodPtr, _detour, out _original);
            _method = Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
            _isApplied = true;
        }
    }
}
