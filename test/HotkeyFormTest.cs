using System;
using System.Windows.Forms;
using QQViewerPin;

internal static class HotkeyFormTest
{
    private static HotkeyForm _form;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        _form = new HotkeyForm(true, Native.MOD_CONTROL, Native.VK_OEM_3, Apply);

        Timer send = new Timer();
        send.Interval = 800;
        send.Tick += delegate
        {
            send.Stop();
            SendKeys.SendWait("^%q");
            SendKeys.SendWait("{ENTER}");
        };
        send.Start();

        Timer guard = new Timer();
        guard.Interval = 5000;
        guard.Tick += delegate
        {
            guard.Stop();
            if (_form != null && !_form.IsDisposed) _form.Close();
        };
        guard.Start();

        Application.Run(_form);
        Console.WriteLine("result enabled=" + _form.EnabledHotkey
            + " mods=" + _form.Modifiers + " key=" + _form.Key
            + " text=" + _form.Text);
    }

    private static bool Apply(bool enabled, uint mods, uint key, out string error)
    {
        error = null;
        Console.WriteLine("apply enabled=" + enabled + " hotkey=" + HotkeyUtil.Format(mods, key));
        return true;
    }
}
