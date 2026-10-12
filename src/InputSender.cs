// InputSender.cs — 按键注入（SendInput 封装）
// 注入的事件会带 LLKHF_INJECTED 标志，钩子侧据此忽略，不会造成递归

using System;
using System.Collections.Generic;

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
            var inp = new Native.INPUT[1];
            inp[0].type = Native.INPUT_KEYBOARD;
            inp[0].U.ki.wVk = vk;
            inp[0].U.ki.wScan = (ushort)Native.MapVirtualKey(vk, Native.MAPVK_VK_TO_VSC);
            uint flags = 0;
            if (extended) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            if (!isDown) flags |= Native.KEYEVENTF_KEYUP;
            inp[0].U.ki.dwFlags = flags;
            inp[0].U.ki.time = 0;
            inp[0].U.ki.dwExtraInfo = Magic;
            Native.SendInput(1, inp, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.INPUT)));
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
            for (int i = 0; i < events.Count; i++)
            {
                KeyEvent e = events[i];
                Key(e.Vk, e.IsDown, e.IsExtended);
            }
        }

        /// <summary>按住修饰键，敲击主键，再松开修饰键。</summary>
        public static void Chord(ushort[] modifiers, ushort key, bool keyExtended)
        {
            for (int i = 0; i < modifiers.Length; i++) Key(modifiers[i], true, IsExtendedVk(modifiers[i]));
            Key(key, true, keyExtended);
            Key(key, false, keyExtended);
            for (int i = modifiers.Length - 1; i >= 0; i--) Key(modifiers[i], false, IsExtendedVk(modifiers[i]));
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
                case 0x0D: // Enter(小键盘)
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
