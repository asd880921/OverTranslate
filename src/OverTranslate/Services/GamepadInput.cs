using System.Runtime.InteropServices;
using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>
/// Minimal XInput reader used by shortcut recording and the global gamepad shortcut listener.
/// No package is required, and no vibration/input is sent back to the controller.
/// </summary>
internal static class GamepadInput
{
    private const uint ErrorSuccess = 0;
    private static int _backend; // 0 unknown, 14 xinput1_4, 91 xinput9_1_0, -1 unavailable

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState14(uint userIndex, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState91(uint userIndex, out XInputState state);

    public static bool TryGetButtons(int controllerIndex, out ushort buttons)
    {
        buttons = 0;
        if (controllerIndex is < 0 or > 3 || _backend == -1) return false;

        if (_backend is 0 or 14)
        {
            try
            {
                if (XInputGetState14((uint)controllerIndex, out var state) == ErrorSuccess)
                {
                    _backend = 14;
                    buttons = state.Gamepad.Buttons;
                    return true;
                }
                if (_backend == 14) return false;
            }
            catch (DllNotFoundException)
            {
                _backend = 91;
            }
            catch (EntryPointNotFoundException)
            {
                _backend = 91;
            }
        }

        if (_backend is 0 or 91)
        {
            try
            {
                var result = XInputGetState91((uint)controllerIndex, out var state);
                _backend = 91;
                if (result != ErrorSuccess) return false;
                buttons = state.Gamepad.Buttons;
                return true;
            }
            catch (DllNotFoundException)
            {
                _backend = -1;
            }
            catch (EntryPointNotFoundException)
            {
                _backend = -1;
            }
        }

        return false;
    }

    /// <summary>Returns the first supported button newly present in a button mask.</summary>
    public static GamepadShortcutButton FirstButton(ushort pressedMask)
    {
        foreach (var button in SupportedButtons)
            if ((pressedMask & (ushort)button) != 0)
                return button;
        return GamepadShortcutButton.None;
    }

    public static IReadOnlyList<GamepadShortcutButton> SupportedButtons { get; } =
    new[]
    {
        GamepadShortcutButton.A,
        GamepadShortcutButton.B,
        GamepadShortcutButton.X,
        GamepadShortcutButton.Y,
        GamepadShortcutButton.LeftShoulder,
        GamepadShortcutButton.RightShoulder,
        GamepadShortcutButton.LeftThumb,
        GamepadShortcutButton.RightThumb,
        GamepadShortcutButton.DPadUp,
        GamepadShortcutButton.DPadDown,
        GamepadShortcutButton.DPadLeft,
        GamepadShortcutButton.DPadRight,
        GamepadShortcutButton.Start,
        GamepadShortcutButton.Back,
    };

    public static string ButtonName(GamepadShortcutButton button) => button switch
    {
        GamepadShortcutButton.LeftShoulder => "LB",
        GamepadShortcutButton.RightShoulder => "RB",
        GamepadShortcutButton.LeftThumb => "L3",
        GamepadShortcutButton.RightThumb => "R3",
        GamepadShortcutButton.DPadUp => "D-Pad Up",
        GamepadShortcutButton.DPadDown => "D-Pad Down",
        GamepadShortcutButton.DPadLeft => "D-Pad Left",
        GamepadShortcutButton.DPadRight => "D-Pad Right",
        GamepadShortcutButton.Start => "Start",
        GamepadShortcutButton.Back => "Back",
        GamepadShortcutButton.A => "A",
        GamepadShortcutButton.B => "B",
        GamepadShortcutButton.X => "X",
        GamepadShortcutButton.Y => "Y",
        _ => button.ToString(),
    };
}
