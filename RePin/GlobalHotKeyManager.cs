using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReBuffer
{
    /// <summary>
    /// Instance-based low-level keyboard hook manager for global hotkeys.
    /// </summary>
    public sealed class GlobalHotKeyManager : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;

        // Instance fields (not static) for thread safety
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private Action? _hotkeyCallback;
        private int _targetKeyCode = 0x77; // F8 by default
        private bool _disposed;

        public GlobalHotKeyManager()
        {
            // Pin the delegate to prevent GC from collecting it while the hook is active
            _proc = HookCallback;
            _hookId = SetHook(_proc);

            if (_hookId == IntPtr.Zero)
            {
                Console.WriteLine("⚠ Failed to set keyboard hook");
            }
        }

        /// <summary>
        /// Registers a hotkey with the specified key code and callback.
        /// </summary>
        /// <param name="keyCode">The virtual key code (use Keys enum)</param>
        /// <param name="callback">The action to invoke when the hotkey is pressed</param>
        public void RegisterHotKey(int keyCode, Action callback)
        {
            _targetKeyCode = keyCode;
            _hotkeyCallback = callback;
        }

        /// <summary>
        /// Gets or sets the current target key code.
        /// </summary>
        public int TargetKeyCode
        {
            get => _targetKeyCode;
            set => _targetKeyCode = value;
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;

            if (curModule != null)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
                    GetModuleHandle(curModule.ModuleName), 0);
            }

            return IntPtr.Zero;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                if (vkCode == _targetKeyCode)
                {
                    try
                    {
                        _hotkeyCallback?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠ Hotkey callback error: {ex.Message}");
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }

            _hotkeyCallback = null;
        }

        #region Native Methods

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook,
            LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        #endregion
    }

    /// <summary>
    /// Virtual key codes for function keys.
    /// </summary>
    public enum Keys
    {
        F1 = 0x70,
        F2 = 0x71,
        F3 = 0x72,
        F4 = 0x73,
        F5 = 0x74,
        F6 = 0x75,
        F7 = 0x76,
        F8 = 0x77,
        F9 = 0x78,
        F10 = 0x79,
        F11 = 0x7A,
        F12 = 0x7B
    }
}
