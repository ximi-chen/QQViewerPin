using System;
using System.Text;

namespace QQViewerPin
{
    internal static class HotkeyUtil
    {
        public static string Format(uint modifiers, uint key)
        {
            if (key == 0) return "未设置";

            StringBuilder sb = new StringBuilder();
            if ((modifiers & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((modifiers & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((modifiers & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((modifiers & Native.MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(KeyName(key));
            return sb.ToString();
        }

        public static string KeyName(uint vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F).ToString();

            switch (vk)
            {
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x13: return "Pause";
                case 0x14: return "CapsLock";
                case 0x1B: return "Esc";
                case 0x20: return "Space";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "Left";
                case 0x26: return "Up";
                case 0x27: return "Right";
                case 0x28: return "Down";
                case 0x2C: return "PrintScreen";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x5B: return "Win";
                case 0x5C: return "Win";
                case 0x90: return "NumLock";
                case 0x91: return "ScrollLock";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
                default: return "VK" + vk.ToString("X2");
            }
        }
    }
}
