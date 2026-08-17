using System.Runtime.InteropServices;
using System.Windows.Threading;
using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>
/// Observes non-keyboard shortcut inputs. It never swallows the input: middle click and controller
/// buttons continue to reach the foreground application/game after OverTranslate reacts to them.
/// </summary>
internal sealed class GlobalAuxiliaryHotkeys : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMButtonDown = 0x0207;

    private readonly Dictionary<ShortcutTrigger, HotkeyAction> _bindings = new();
    private readonly ushort[] _previousGamepadButtons = new ushort[4];
    private readonly DispatcherTimer _gamepadTimer = new() { Interval = TimeSpan.FromMilliseconds(35) };

    private LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook;

    public event Action<HotkeyAction>? ShortcutPressed;

    public GlobalAuxiliaryHotkeys()
    {
        _gamepadTimer.Tick += (_, _) => PollGamepads();
    }

    public void Register(IEnumerable<HotkeyBinding> bindings)
    {
        ClearRuntimeHooks();
        _bindings.Clear();

        foreach (var binding in bindings)
        {
            if (!binding.IsActive || binding.InputKind == ShortcutInputKind.Keyboard) continue;
            _bindings[binding.Trigger] = binding.Action;
        }

        if (_bindings.Keys.Any(t => t.Kind == ShortcutInputKind.MouseMiddle))
            InstallMouseHook();

        if (_bindings.Keys.Any(t => t.Kind == ShortcutInputKind.Gamepad))
        {
            SnapshotGamepads();
            _gamepadTimer.Start();
        }
    }

    private void InstallMouseHook()
    {
        _mouseProc = MouseHookCallback;
        IntPtr moduleHandle = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, moduleHandle, 0);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WmMButtonDown &&
            _bindings.TryGetValue(ShortcutTrigger.MouseMiddle(), out var action))
        {
            ShortcutPressed?.Invoke(action);
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void SnapshotGamepads()
    {
        for (int i = 0; i < _previousGamepadButtons.Length; i++)
            _previousGamepadButtons[i] = GamepadInput.TryGetButtons(i, out var buttons) ? buttons : (ushort)0;
    }

    private void PollGamepads()
    {
        for (int i = 0; i < _previousGamepadButtons.Length; i++)
        {
            if (!GamepadInput.TryGetButtons(i, out var current))
            {
                _previousGamepadButtons[i] = 0;
                continue;
            }

            ushort pressed = (ushort)(current & ~_previousGamepadButtons[i]);
            _previousGamepadButtons[i] = current;
            if (pressed == 0) continue;

            // More than one physical button may be pressed in one poll. Fire each supported edge;
            // conflict resolution ensures one button maps to at most one app action.
            foreach (var button in GamepadInput.SupportedButtons)
            {
                if ((pressed & (ushort)button) == 0) continue;
                if (_bindings.TryGetValue(ShortcutTrigger.Gamepad(button), out var action))
                    ShortcutPressed?.Invoke(action);
            }
        }
    }

    private void ClearRuntimeHooks()
    {
        _gamepadTimer.Stop();
        Array.Clear(_previousGamepadButtons, 0, _previousGamepadButtons.Length);

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _mouseProc = null;
    }

    public void Dispose()
    {
        ClearRuntimeHooks();
        _bindings.Clear();
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
