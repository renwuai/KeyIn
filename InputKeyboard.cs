using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KeyLend;

internal sealed class KeyboardCapture : IMessageFilter
{
    private readonly List<byte> pressedKeys = new();
    private byte modifiers;

    public bool Enabled { get; set; }
    public Func<Keys, bool>? IsShortcut { get; set; }
    public event Action<byte[], bool>? Report;
    public event Action? Pause;

    public void Clear()
    {
        modifiers = 0;
        pressedKeys.Clear();
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (!Enabled || m.Msg is not (>= 0x0100 and <= 0x0109 or 0x0302))
            return false;
        if (m.HWnd == 0 || GetAncestor(m.HWnd, 2) != GetForegroundWindow())
            return false;

        // 只转发物理键，字符、死键、Unicode 注入及本地粘贴没有对应的键盘报告。
        bool down = m.Msg is 0x0100 or 0x0104;
        if (!down && m.Msg is not (0x0101 or 0x0105))
            return true;
        var key = (Keys)(int)m.WParam;
        if (key == Keys.Packet)
            return true;
        if (key == Keys.ProcessKey)
            key = (Keys)ImmGetVirtualKey(m.HWnd);

        long flags = m.LParam.ToInt64();
        // 长按的重复由接收端生成。启用转发前已经按下的键也不应中途进入报告。
        if (down && (flags & (1L << 30)) != 0)
            return true;
        if (key == Keys.Escape)
        {
            if (down)
                Pause?.Invoke();
            return true;
        }

        int scan = (int)((flags >> 16) & 0xff);
        if ((flags & (1L << 24)) != 0)
            scan |= 0xe000;
        if (scan == 0)
            scan = (int)MapVirtualKey((uint)key, 4);
        byte usage = ToUsage(scan, key);
        if (usage == 0)
            return true;

        if (down)
        {
            Keys chord = key | Control.ModifierKeys;
            if (IsShortcut?.Invoke(chord) == true || chord == (Keys.Control | Keys.V) ||
                chord == (Keys.Shift | Keys.Insert))
                return true;
        }

        if (usage is >= 0xe0 and <= 0xe7)
        {
            int bit = 1 << (usage - 0xe0);
            byte updated = (byte)(down ? modifiers | bit : modifiers & ~bit);
            if (updated == modifiers)
                return true;
            modifiers = updated;
        }
        else if (down)
        {
            if (pressedKeys.Contains(usage))
                return true;
            pressedKeys.Add(usage);
        }
        else if (!pressedKeys.Remove(usage))
        {
            return true;
        }

        var report = new byte[8];
        report[0] = modifiers;
        if (pressedKeys.Count > 6)
            Array.Fill(report, (byte)1, 2, 6); // USB HID ErrorRollOver，抬键后恢复完整状态。
        else
            pressedKeys.CopyTo(report, 2);
        bool textKey = down && pressedKeys.Count <= 6 && (modifiers & 0xdd) == 0 &&
                       IsTextKey(usage, key);
        Report?.Invoke(report, textKey);
        return true;
    }

    private static bool IsTextKey(byte usage, Keys key) =>
        usage is >= 0x04 and <= 0x27 or >= 0x2c and <= 0x38 or
                 >= 0x54 and <= 0x57 or 0x64 or 0x67 or 0x85 or 0x87 or 0x89 ||
        ((int)key >= (int)Keys.NumPad0 && (int)key <= (int)Keys.NumPad9) || key == Keys.Decimal;

    // Windows Scan 1 对应 USB HID 键盘页 0x07；按物理键位匹配，使拼音和非美式布局不改写按键。
    // 两行共用同一扫描码的传统键，用 Windows 提供的虚拟键消除歧义。
    internal static byte ToUsage(int scan, Keys key) => key switch
    {
        Keys.Pause or Keys.Cancel => 0x48,
        Keys.NumLock => 0x53,
        Keys.Snapshot => 0x46,
        _ => scan switch
        {
            0x01 => 0x29,
            >= 0x02 and <= 0x0b => (byte)(scan + 0x1c),
            0x0c => 0x2d, 0x0d => 0x2e, 0x0e => 0x2a, 0x0f => 0x2b,
            0x10 => 0x14, 0x11 => 0x1a, 0x12 => 0x08, 0x13 => 0x15,
            0x14 => 0x17, 0x15 => 0x1c, 0x16 => 0x18, 0x17 => 0x0c,
            0x18 => 0x12, 0x19 => 0x13, 0x1a => 0x2f, 0x1b => 0x30,
            0x1c => 0x28, 0x1d => 0xe0, 0x1e => 0x04, 0x1f => 0x16,
            0x20 => 0x07, 0x21 => 0x09, 0x22 => 0x0a, 0x23 => 0x0b,
            0x24 => 0x0d, 0x25 => 0x0e, 0x26 => 0x0f, 0x27 => 0x33,
            0x28 => 0x34, 0x29 => 0x35, 0x2a => 0xe1, 0x2b => 0x31,
            0x2c => 0x1d, 0x2d => 0x1b, 0x2e => 0x06, 0x2f => 0x19,
            0x30 => 0x05, 0x31 => 0x11, 0x32 => 0x10, 0x33 => 0x36,
            0x34 => 0x37, 0x35 => 0x38, 0x36 or 0xe036 => 0xe5, 0x37 => 0x55,
            0x38 => 0xe2, 0x39 => 0x2c, 0x3a => 0x39,
            >= 0x3b and <= 0x44 => (byte)(scan - 1),
            0x45 or 0xe045 => 0x53, 0x46 => 0x47, 0x47 => 0x5f,
            0x48 => 0x60, 0x49 => 0x61, 0x4a => 0x56, 0x4b => 0x5c,
            0x4c => 0x5d, 0x4d => 0x5e, 0x4e => 0x57, 0x4f => 0x59,
            0x50 => 0x5a, 0x51 => 0x5b, 0x52 => 0x62, 0x53 => 0x63,
            0x54 or 0xe037 => 0x46, 0x56 => 0x64, 0x57 => 0x44,
            0x58 => 0x45, 0x59 => 0x67, 0x5c => 0x8c,
            >= 0x64 and <= 0x6e => (byte)(scan + 4),
            0x70 => 0x88, 0x71 or 0xf1 => 0x91, 0x72 or 0xf2 => 0x90,
            0x73 => 0x87, 0x76 => 0x73, 0x77 => 0x93, 0x78 => 0x92,
            0x79 => 0x8a, 0x7b => 0x8b, 0x7d => 0x89, 0x7e => 0x85,
            0xe01c => 0x58, 0xe01d => 0xe4, 0xe035 => 0x54, 0xe038 => 0xe6,
            0xe046 => 0x48, 0xe047 => 0x4a, 0xe048 => 0x52, 0xe049 => 0x4b,
            0xe04b => 0x50, 0xe04d => 0x4f, 0xe04f => 0x4d, 0xe050 => 0x51,
            0xe051 => 0x4e, 0xe052 => 0x49, 0xe053 => 0x4c, 0xe05b => 0xe3,
            0xe05c => 0xe7, 0xe05d => 0x65,
            _ => 0
        }
    };

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("imm32.dll")] private static extern uint ImmGetVirtualKey(nint window);
}
