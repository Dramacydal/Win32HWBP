﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using DirtyMagic.WinAPI;
using DirtyMagic.WinAPI.Input;
using DirtyMagic.WinAPI.Structures;

namespace DirtyMagic.Input
{
    public class GlobalKeyboardInput : IKeyboardInput
    {
        public override void KeyPress(Keys key, Modifiers modifiers = Modifiers.None,
            TimeSpan keyPressTime = default(TimeSpan), int extraInfo = 0)
        {
            SendKey(key, modifiers, false, extraInfo);
            if (!DefaultKeypressTime.IsEmpty())
                Thread.Sleep((int) DefaultKeypressTime.TotalMilliseconds);
            SendKey(key, modifiers, true, extraInfo);
        }

        public override void SendChar(char c)
        {
            var inp = new INPUT {Type = InputType.KEYBOARD};
            inp.Union.ki.dwFlags = KeyEventFlags.UNICODE;
            inp.Union.ki.wVk = 0;
            inp.Union.ki.wScan = Convert.ToInt16(c);
            inp.Union.ki.time = 0;
            inp.Union.ki.dwExtraInfo = IntPtr.Zero;

            if (User32.SendInput(1, new INPUT[] {inp}, INPUT.Size) != 1)
                throw new Win32Exception();
        }

        public override void SendKey(Keys key, Modifiers modifiers, bool up, int extraInfo = 0)
        {
            var keyMod = KeyToModifier(key);
            if (keyMod != Modifiers.None)
                modifiers &= ~keyMod;

            var inputs = BuildModifiersInput(modifiers, up, extraInfo).ToList();

            if (key != Keys.None)
            {
                var inp = new INPUT {Type = InputType.KEYBOARD};
                inp.Union.ki.dwFlags = up ? KeyEventFlags.KEYUP : KeyEventFlags.NONE;
                inp.Union.ki.wVk = (short) key;
                inp.Union.ki.wScan = 0;
                inp.Union.ki.time = 0;
                inp.Union.ki.dwExtraInfo = new IntPtr(extraInfo);

                inputs.Add(inp);
            }

            if (inputs.Count == 0)
                return;

            if (up)
                inputs.Reverse();

            if (User32.SendInput(inputs.Count, inputs.ToArray(), INPUT.Size) != inputs.Count)
                throw new Win32Exception();
        }

        private Modifiers KeyToModifier(Keys key)
        {
            switch (key)
            {
                case Keys.LMenu:
                case Keys.RMenu:
                    return Modifiers.Alt;
                case Keys.LControlKey:
                case Keys.RControlKey:
                    return Modifiers.Ctrl;
                case Keys.LShiftKey:
                case Keys.RShiftKey:
                    return Modifiers.Shift;
            }

            return Modifiers.None;
        }

        private IEnumerable<INPUT> BuildModifiersInput(Modifiers modifiers, bool up, int extraInfo)
        {
            var keys = new List<Keys>();
            if (modifiers.CtrlPressed())
                keys.Add(Keys.ControlKey);
            if (modifiers.AltPressed())
                keys.Add(Keys.Menu);
            if (modifiers.ShiftPressed())
                keys.Add(Keys.ShiftKey);

            return keys.Select(key =>
            {
                var input = new INPUT();
                input.Type = InputType.KEYBOARD;
                input.Union.ki.dwFlags = up ? KeyEventFlags.KEYUP : KeyEventFlags.NONE;
                input.Union.ki.wVk = (short) key;
                input.Union.ki.wScan = 0;
                input.Union.ki.time = 0;
                input.Union.ki.dwExtraInfo = new IntPtr(extraInfo);

                return input;
            });
        }

        public void SendScanCode(ScanCodeShort scanCode, bool up = false)
        {
            var inp = new INPUT {Type = InputType.KEYBOARD};
            inp.Union.ki.dwFlags = (up ? KeyEventFlags.KEYUP : KeyEventFlags.NONE) | KeyEventFlags.SCANCODE;
            inp.Union.ki.wVk = 0;
            inp.Union.ki.wScan = (short) scanCode;
            inp.Union.ki.time = 0;
            inp.Union.ki.dwExtraInfo = IntPtr.Zero;

            if (User32.SendInput(1, new[] {inp}, INPUT.Size) != 1)
                throw new Win32Exception();
        }
    }
}
