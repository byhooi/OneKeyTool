// InputSender.cs — 按键注入（SendInput 封装）
// 注入的事件会带 LLKHF_INJECTED 标志，钩子侧据此忽略，不会造成递归

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace OneKeyTool
{
    internal static class InputSender
    {
        /// <summary>
        /// 本程序注入事件时写入 dwExtraInfo 的魔数标记。
        /// 钩子侧据此无条件忽略，避免“放弃拦截 → 重放 → 又被自己处理”的死循环。
        /// </summary>
        public static readonly IntPtr Magic = new IntPtr(0x4F4B5459);   // "OKTY"

        /// <summary>按下或抬起一个虚拟键。extended 表示是否为扩展键（如方向键、Win 键）。</summary>
        public static void Key(ushort vk, bool isDown, bool extended)
        {
            Send(new Native.INPUT[] { CreateVirtualKey(vk, isDown, extended) });
        }

        /// <summary>完整敲击一个键（按下再抬起）。</summary>
        public static void Tap(ushort vk, bool extended)
        {
            Key(vk, true, extended);
            Key(vk, false, extended);
        }

        /// <summary>按顺序重放一批被暂扣的按键事件，保持原始按下/抬起顺序。</summary>
        public static void Replay(List<KeyEvent> events)
        {
            Native.INPUT[] inputs = new Native.INPUT[events.Count];
            for (int i = 0; i < events.Count; i++)
            {
                KeyEvent e = events[i];
                inputs[i] = e.Scan == 0
                    ? CreateVirtualKey(e.Vk, e.IsDown, e.IsExtended)
                    : CreateScanCode(e.Scan, e.IsDown, e.IsExtended);
            }
            Send(inputs);
        }

        /// <summary>按住修饰键，敲击主键，再松开修饰键。</summary>
        public static void Chord(ushort[] modifiers, ushort key, bool keyExtended)
        {
            List<Native.INPUT> inputs = new List<Native.INPUT>();
            for (int i = 0; i < modifiers.Length; i++) inputs.Add(CreateVirtualKey(modifiers[i], true, IsExtendedVk(modifiers[i])));
            inputs.Add(CreateVirtualKey(key, true, keyExtended));
            inputs.Add(CreateVirtualKey(key, false, keyExtended));
            for (int i = modifiers.Length - 1; i >= 0; i--) inputs.Add(CreateVirtualKey(modifiers[i], false, IsExtendedVk(modifiers[i])));
            Send(inputs.ToArray());
        }

        private static Native.INPUT CreateVirtualKey(ushort vk, bool isDown, bool extended)
        {
            Native.INPUT input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.U.ki.wVk = vk;
            input.U.ki.wScan = (ushort)Native.MapVirtualKey(vk, Native.MAPVK_VK_TO_VSC);
            input.U.ki.dwFlags = (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0) |
                (isDown ? 0 : Native.KEYEVENTF_KEYUP);
            input.U.ki.dwExtraInfo = Magic;
            return input;
        }

        private static Native.INPUT CreateScanCode(ushort scan, bool isDown, bool extended)
        {
            Native.INPUT input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.U.ki.wScan = scan;
            input.U.ki.dwFlags = Native.KEYEVENTF_SCANCODE |
                (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0) |
                (isDown ? 0 : Native.KEYEVENTF_KEYUP);
            input.U.ki.dwExtraInfo = Magic;
            return input;
        }

        private static void Send(Native.INPUT[] inputs)
        {
            if (inputs.Length == 0) return;
            uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
            if (sent != inputs.Length)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "SendInput 仅注入了 " + sent + "/" + inputs.Length + " 个按键事件（可能受 UIPI 权限限制）");
            }
        }

        public static bool IsExtendedVk(ushort vk)
        {
            switch (vk)
            {
                case 0x5B: // LWin
                case 0x5C: // RWin
                case 0x21: // PageUp
                case 0x22: // PageDown
                case 0x23: // End
                case 0x24: // Home
                case 0x25: // Left
                case 0x26: // Up
                case 0x27: // Right
                case 0x28: // Down
                case 0x2D: // Insert
                case 0x2E: // Delete
                case 0x90: // NumLock
                case 0x6F: // Numpad /
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>一条键盘事件的不可变记录，用于暂扣与重放。</summary>
    internal struct KeyEvent
    {
        public readonly ushort Vk;
        public readonly ushort Scan;
        public readonly bool IsDown;
        public readonly bool IsExtended;

        public KeyEvent(ushort vk, ushort scan, bool isDown, bool isExtended)
        {
            Vk = vk;
            Scan = scan;
            IsDown = isDown;
            IsExtended = isExtended;
        }
    }
}
