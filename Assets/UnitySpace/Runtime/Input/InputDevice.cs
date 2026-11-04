using System.Collections.Generic;
using UnityEngine;

namespace Unity.SpaceGod.Input
{
    /// <summary>
    /// The keys and buttons the game reads, by the names the original's Unity Input System bindings use
    /// (<c>kb.wKey</c> is <see cref="Key.W"/>, <c>kb.backquoteKey</c> is <see cref="Key.Backquote"/>...).
    /// <see cref="UnityInputDevice"/> reads each from the Input System (US-T39: the seam the Unreal ports added, so the
    /// co-op smoke run can script the keys).
    /// </summary>
    public enum Key
    {
        None,
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        Digit0, Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        Space, Enter, NumpadEnter, Escape, Tab, Backspace, Backquote, Quote, Semicolon, Comma, Period, Slash, Backslash,
        LeftBracket, RightBracket, Minus, Equals, NumpadPlus, NumpadMinus, NumpadMultiply, NumpadDivide,
        LeftShift, RightShift, LeftCtrl, RightCtrl, LeftAlt, RightAlt,
        Insert, Delete, Home, End, PageUp, PageDown, Pause,
        UpArrow, DownArrow, LeftArrow, RightArrow,
    }

    public enum MouseButton
    {
        Left,
        Right,
        Middle,
    }

    /// <summary>
    /// One frame of keyboard and mouse as the Unity Input System reported it: held, pressed this frame, released
    /// this frame; the pointer in Unity's screen space (pixels, origin bottom-left); its delta (pixels, y up); and
    /// the wheel in Unity's raw units (120 a notch on a standard mouse).
    /// </summary>
    public interface IInputDevice
    {
        /// <summary>False when there is no keyboard and mouse to read (Unity's <c>Keyboard.current == null</c>).</summary>
        bool Present { get; }

        bool IsPressed(Key key);
        bool WasPressedThisFrame(Key key);
        bool IsPressed(MouseButton button);
        bool WasPressedThisFrame(MouseButton button);
        bool WasReleasedThisFrame(MouseButton button);
        Vector2 MousePosition { get; }
        Vector2 MouseDelta { get; }
        float ScrollY { get; }
    }

    /// <summary>
    /// A device driven by script (the headless smoke run, a tour, a test): keys held and pressed are set before a
    /// frame's poll and the edges cleared after it, as a real device's are.
    /// </summary>
    public sealed class ScriptedInputDevice : IInputDevice
    {
        readonly HashSet<Key> held = new HashSet<Key>();
        readonly HashSet<Key> pressed = new HashSet<Key>();
        readonly HashSet<MouseButton> buttons = new HashSet<MouseButton>();
        readonly HashSet<MouseButton> buttonsPressed = new HashSet<MouseButton>();
        readonly HashSet<MouseButton> buttonsReleased = new HashSet<MouseButton>();

        public bool Present => true;
        public Vector2 MousePosition { get; set; } = new Vector2(Screen.width / 2f, Screen.height / 2f);
        public Vector2 MouseDelta { get; set; }
        public float ScrollY { get; set; }

        public void Hold(Key k)
        {
            if (held.Add(k)) pressed.Add(k);
        }

        public void Release(Key k) => held.Remove(k);

        /// <summary>Taps a key: pressed (and held) for the next frame only.</summary>
        public void Tap(Key k)
        {
            pressed.Add(k);
            held.Add(k);
            tapped.Add(k);
        }

        readonly HashSet<Key> tapped = new HashSet<Key>();

        public void HoldButton(MouseButton b)
        {
            if (buttons.Add(b)) buttonsPressed.Add(b);
        }

        public void ReleaseButton(MouseButton b)
        {
            if (buttons.Remove(b)) buttonsReleased.Add(b);
        }

        /// <summary>Clears this frame's edges (after the game has polled).</summary>
        public void EndFrame()
        {
            pressed.Clear();
            buttonsPressed.Clear();
            buttonsReleased.Clear();
            foreach (var k in tapped) held.Remove(k);
            tapped.Clear();
            MouseDelta = Vector2.zero;
            ScrollY = 0;
        }

        public bool IsPressed(Key key) => held.Contains(key);
        public bool WasPressedThisFrame(Key key) => pressed.Contains(key);
        public bool IsPressed(MouseButton button) => buttons.Contains(button);
        public bool WasPressedThisFrame(MouseButton button) => buttonsPressed.Contains(button);
        public bool WasReleasedThisFrame(MouseButton button) => buttonsReleased.Contains(button);
    }

    /// <summary>The Input System's current keyboard and mouse, as the original's InputState read them.</summary>
    public sealed class UnityInputDevice : IInputDevice
    {
        public static readonly UnityInputDevice Instance = new UnityInputDevice();

        static readonly UnityEngine.InputSystem.Key[] Map = BuildMap();

        static UnityEngine.InputSystem.Key[] BuildMap()
        {
            var values = (Key[])System.Enum.GetValues(typeof(Key));
            var map = new UnityEngine.InputSystem.Key[values.Length];
            foreach (var k in values)
                map[(int)k] = (UnityEngine.InputSystem.Key)System.Enum.Parse(typeof(UnityEngine.InputSystem.Key), k.ToString());
            return map;
        }

        static UnityEngine.InputSystem.Keyboard Kb => UnityEngine.InputSystem.Keyboard.current;
        static UnityEngine.InputSystem.Mouse Ms => UnityEngine.InputSystem.Mouse.current;

        static UnityEngine.InputSystem.Controls.ButtonControl Button(MouseButton b)
        {
            var m = Ms;
            return b == MouseButton.Left ? m.leftButton : b == MouseButton.Right ? m.rightButton : m.middleButton;
        }

        public bool Present => Kb != null && Ms != null;
        public bool IsPressed(Key key) => key != Key.None && Kb[Map[(int)key]].isPressed;
        public bool WasPressedThisFrame(Key key) => key != Key.None && Kb[Map[(int)key]].wasPressedThisFrame;
        public bool IsPressed(MouseButton button) => Button(button).isPressed;
        public bool WasPressedThisFrame(MouseButton button) => Button(button).wasPressedThisFrame;
        public bool WasReleasedThisFrame(MouseButton button) => Button(button).wasReleasedThisFrame;
        public Vector2 MousePosition => Ms.position.ReadValue();
        public Vector2 MouseDelta => Ms.delta.ReadValue();
        public float ScrollY => Ms.scroll.ReadValue().y;
    }

    /// <summary>No device: everything idle (Unity's null Keyboard.current).</summary>
    public sealed class NoInputDevice : IInputDevice
    {
        public static readonly NoInputDevice Instance = new NoInputDevice();
        public bool Present => false;
        public bool IsPressed(Key key) => false;
        public bool WasPressedThisFrame(Key key) => false;
        public bool IsPressed(MouseButton button) => false;
        public bool WasPressedThisFrame(MouseButton button) => false;
        public bool WasReleasedThisFrame(MouseButton button) => false;
        public Vector2 MousePosition => Vector2.zero;
        public Vector2 MouseDelta => Vector2.zero;
        public float ScrollY => 0;
    }
}
