using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QQViewerPin
{
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly AppConfig _cfg;
        private readonly PreviewManager _mgr;
        private readonly HudForm _hud;
        private readonly NotifyIcon _tray;
        private readonly MessageWindow _msg;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _alphaMenu;
        private readonly ToolStripMenuItem _pauseItem;
        private readonly ToolStripMenuItem _hotkeyItem;
        private readonly ToolStripMenuItem _closeHotkeyItem;
        private readonly ToolStripMenuItem _closeAllItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private bool _hotkeyRegistered;
        private bool _hotkeyEnabled;
        private uint _hotkeyMods;
        private uint _hotkeyKey;
        private string _hotkeyNotice;
        private bool _hotkeyNoticeWarning;
        private bool _closeHotkeyRegistered;
        private bool _closeHotkeyEnabled;
        private uint _closeHotkeyMods;
        private uint _closeHotkeyKey;
        private string _closeHotkeyNotice;
        private bool _closeHotkeyNoticeWarning;
        private bool _cleaned;

        public TrayApp(AppConfig cfg, bool firstRun, bool selftest)
        {
            _cfg = cfg;

            _hud = new HudForm();
            _mgr = new PreviewManager(cfg);
            _mgr.AlphaChanged += OnAlphaChanged;
            _mgr.Start();

            _msg = new MessageWindow();
            _msg.HotkeyPressed += OnHotkey;
            _msg.CloseAllPressed += OnCloseAllHotkey;

            _cfg.AutoStart = AutoStartHelper.IsEnabled();

            _menu = new ContextMenuStrip();

            _alphaMenu = new ToolStripMenuItem("透明度");
            int[] presets = new int[] { 100, 70, 50, 30, 10 };
            foreach (int preset in presets)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(preset + "%");
                item.Tag = preset;
                item.Click += AlphaPresetClick;
                _alphaMenu.DropDownItems.Add(item);
            }
            _alphaMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem hint = new ToolStripMenuItem("Alt+滚轮微调（每格 5%）");
            hint.Enabled = false;
            _alphaMenu.DropDownItems.Add(hint);

            _pauseItem = new ToolStripMenuItem("暂停接管");
            _pauseItem.Click += PauseClick;

            _hotkeyItem = new ToolStripMenuItem("设置快捷键...");
            _hotkeyItem.Click += HotkeyClick;

            _closeAllItem = new ToolStripMenuItem("关闭所有预览窗");
            _closeAllItem.Click += CloseAllClick;

            _closeHotkeyItem = new ToolStripMenuItem("设置关闭快捷键...");
            _closeHotkeyItem.Click += CloseHotkeyClick;

            _autoStartItem = new ToolStripMenuItem("开机自启");
            _autoStartItem.Click += AutoStartClick;

            ToolStripMenuItem resetItem = new ToolStripMenuItem("重置位置");
            resetItem.Click += ResetClick;

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += ExitClick;

            _menu.Items.Add(_alphaMenu);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_pauseItem);
            _menu.Items.Add(_hotkeyItem);
            _menu.Items.Add(_closeHotkeyItem);
            _menu.Items.Add(_closeAllItem);
            _menu.Items.Add(_autoStartItem);
            _menu.Items.Add(resetItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);
            _menu.Opening += MenuOpening;

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "QQ 预览窗固定器";
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.DoubleClick += TrayDoubleClick;

            InitializeHotkey();
            InitializeCloseHotkey();

            string startupNotice = _hotkeyNotice;
            if (_closeHotkeyNotice != null)
            {
                startupNotice = startupNotice == null
                    ? _closeHotkeyNotice
                    : startupNotice + " " + _closeHotkeyNotice;
            }
            bool startupWarning = _hotkeyNoticeWarning || _closeHotkeyNoticeWarning;

            if (selftest)
            {
                Timer timer = new Timer();
                timer.Interval = 1200;
                timer.Tick += delegate
                {
                    timer.Stop();
                    timer.Dispose();
                    ConfigStore.Log("自检：托盘、钩子、热键均已初始化。");
                    Cleanup();
                    ExitThread();
                };
                timer.Start();
            }
            else if (startupNotice != null)
            {
                _tray.ShowBalloonTip(8000, "QQ 预览窗固定器", startupNotice,
                    startupWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            else if (firstRun)
            {
                string tip = "已启动。若任务栏右下角没有图标，点 ^ 展开隐藏图标。";
                if (_hotkeyRegistered)
                {
                    tip += "快速隐藏：" + HotkeyUtil.Format(_hotkeyMods, _hotkeyKey) + "；";
                }
                if (_closeHotkeyRegistered)
                {
                    tip += "关闭全部预览窗：" + HotkeyUtil.Format(_closeHotkeyMods, _closeHotkeyKey) + "；";
                }
                tip += "Alt+滚轮调透明度。右键托盘图标可改快捷键。";
                _tray.ShowBalloonTip(5000, "QQ 预览窗固定器", tip, ToolTipIcon.Info);
            }
        }

        private void InitializeHotkey()
        {
            _hotkeyEnabled = _cfg.HotkeyEnabled;
            _hotkeyMods = _cfg.HotkeyModifiers;
            _hotkeyKey = _cfg.HotkeyKey;
            _hotkeyRegistered = false;

            if (!_hotkeyEnabled)
            {
                ConfigStore.Log("快速隐藏快捷键已禁用。");
                UpdateHotkeyMenu();
                return;
            }

            if (_hotkeyMods != 0 && _hotkeyKey != 0)
            {
                string name = HotkeyUtil.Format(_hotkeyMods, _hotkeyKey);
                if (TryRegisterHotkey(_hotkeyMods, _hotkeyKey))
                {
                    ConfigStore.Log("已注册快速隐藏快捷键 " + name + "。");
                }
                else
                {
                    _hotkeyNotice = "快捷键 " + name + " 被其他程序占用，快速隐藏暂不可用。请右键托盘图标 → 设置快捷键。";
                    _hotkeyNoticeWarning = true;
                    ConfigStore.Log("快捷键 " + name + " 注册失败，错误码 " + Marshal.GetLastWin32Error());
                }
                UpdateHotkeyMenu();
                return;
            }

            uint[] mods = new uint[] { Native.MOD_ALT, Native.MOD_CONTROL, Native.MOD_CONTROL | Native.MOD_ALT };
            uint[] keys = new uint[] { Native.VK_OEM_3, Native.VK_OEM_3, 0x51 };
            string[] names = new string[] { "Alt+`", "Ctrl+`", "Ctrl+Alt+Q" };
            for (int i = 0; i < mods.Length; i++)
            {
                if (TryRegisterHotkey(mods[i], keys[i]))
                {
                    _hotkeyEnabled = true;
                    _cfg.HotkeyEnabled = true;
                    _cfg.HotkeyModifiers = mods[i];
                    _cfg.HotkeyKey = keys[i];
                    ConfigStore.Save(_cfg);

                    if (i > 0)
                    {
                        _hotkeyNotice = "Alt+` 被其他程序占用，已改用 " + names[i] + "。";
                        ConfigStore.Log("Alt+` 被占用，改用 " + names[i] + " 作为快速隐藏热键。");
                    }
                    else
                    {
                        ConfigStore.Log("已注册快速隐藏快捷键 " + names[i] + "。");
                    }
                    UpdateHotkeyMenu();
                    return;
                }
            }

            _hotkeyNotice = "没有可用的默认快捷键（Alt+`、Ctrl+`、Ctrl+Alt+Q 均被占用）。请右键托盘图标 → 设置快捷键。";
            _hotkeyNoticeWarning = true;
            ConfigStore.Log("注册热键失败：Alt+`、Ctrl+`、Ctrl+Alt+Q 均被占用，错误码 " + Marshal.GetLastWin32Error());
            UpdateHotkeyMenu();
        }

        private void InitializeCloseHotkey()
        {
            _closeHotkeyEnabled = _cfg.CloseHotkeyEnabled;
            _closeHotkeyMods = _cfg.CloseHotkeyModifiers;
            _closeHotkeyKey = _cfg.CloseHotkeyKey;
            _closeHotkeyRegistered = false;

            if (!_closeHotkeyEnabled)
            {
                ConfigStore.Log("关闭所有预览窗快捷键已禁用。");
                UpdateHotkeyMenu();
                return;
            }

            if (_closeHotkeyMods != 0 && _closeHotkeyKey != 0)
            {
                string name = HotkeyUtil.Format(_closeHotkeyMods, _closeHotkeyKey);
                if (TryRegisterCloseHotkey(_closeHotkeyMods, _closeHotkeyKey))
                {
                    ConfigStore.Log("已注册关闭所有预览窗快捷键 " + name + "。");
                }
                else
                {
                    _closeHotkeyNotice = "快捷键 " + name + " 被其他程序占用，关闭所有预览窗暂不可用。请右键托盘图标 → 设置关闭快捷键。";
                    _closeHotkeyNoticeWarning = true;
                    ConfigStore.Log("关闭快捷键 " + name + " 注册失败，错误码 " + Marshal.GetLastWin32Error());
                }
                UpdateHotkeyMenu();
                return;
            }

            uint[] mods = new uint[]
            {
                Native.MOD_CONTROL | Native.MOD_ALT,
                Native.MOD_CONTROL | Native.MOD_SHIFT,
                Native.MOD_ALT | Native.MOD_SHIFT
            };
            uint[] keys = new uint[] { Native.VK_W, Native.VK_W, Native.VK_W };
            string[] names = new string[] { "Ctrl+Alt+W", "Ctrl+Shift+W", "Alt+Shift+W" };
            for (int i = 0; i < mods.Length; i++)
            {
                if (TryRegisterCloseHotkey(mods[i], keys[i]))
                {
                    _closeHotkeyEnabled = true;
                    _cfg.CloseHotkeyEnabled = true;
                    _cfg.CloseHotkeyModifiers = mods[i];
                    _cfg.CloseHotkeyKey = keys[i];
                    ConfigStore.Save(_cfg);

                    if (i > 0)
                    {
                        _closeHotkeyNotice = "关闭快捷键默认组合被占用，已改用 " + names[i] + "。";
                        ConfigStore.Log(names[0] + " 被占用，改用 " + names[i] + " 作为关闭所有预览窗热键。");
                    }
                    else
                    {
                        ConfigStore.Log("已注册关闭所有预览窗快捷键 " + names[i] + "。");
                    }
                    UpdateHotkeyMenu();
                    return;
                }
            }

            _closeHotkeyNotice = "没有可用的关闭快捷键（Ctrl+Alt+W、Ctrl+Shift+W、Alt+Shift+W 均被占用）。请右键托盘图标 → 设置关闭快捷键。";
            _closeHotkeyNoticeWarning = true;
            ConfigStore.Log("注册关闭快捷键失败：Ctrl+Alt+W、Ctrl+Shift+W、Alt+Shift+W 均被占用，错误码 "
                + Marshal.GetLastWin32Error());
            UpdateHotkeyMenu();
        }

        private bool TryRegisterCloseHotkey(uint mods, uint vk)
        {
            if (!Native.RegisterHotKey(_msg.Handle, Native.HOTKEY_ID_CLOSE, mods, vk)) return false;

            _closeHotkeyRegistered = true;
            _closeHotkeyMods = mods;
            _closeHotkeyKey = vk;
            UpdateHotkeyMenu();
            return true;
        }

        private void UnregisterCloseHotkey()
        {
            if (_closeHotkeyRegistered)
            {
                Native.UnregisterHotKey(_msg.Handle, Native.HOTKEY_ID_CLOSE);
                _closeHotkeyRegistered = false;
            }
        }


        private bool TryRegisterHotkey(uint mods, uint vk)
        {
            if (!Native.RegisterHotKey(_msg.Handle, Native.HOTKEY_ID, mods, vk)) return false;

            _hotkeyRegistered = true;
            _hotkeyMods = mods;
            _hotkeyKey = vk;
            UpdateHotkeyMenu();
            return true;
        }

        private void UnregisterCurrent()
        {
            if (_hotkeyRegistered)
            {
                Native.UnregisterHotKey(_msg.Handle, Native.HOTKEY_ID);
                _hotkeyRegistered = false;
            }
        }

        private void UpdateHotkeyMenu()
        {
            if (_hotkeyItem != null)
            {
                string state = HotkeyState(_hotkeyEnabled, _hotkeyRegistered, _hotkeyMods, _hotkeyKey);
                _hotkeyItem.Text = "设置快速隐藏快捷键（当前 " + state + "）...";
            }

            if (_closeHotkeyItem != null)
            {
                string closeState = HotkeyState(_closeHotkeyEnabled, _closeHotkeyRegistered,
                    _closeHotkeyMods, _closeHotkeyKey);
                _closeHotkeyItem.Text = "设置关闭快捷键（当前 " + closeState + "）...";
            }
        }

        private static string HotkeyState(bool enabled, bool registered, uint mods, uint key)
        {
            if (!enabled) return "已禁用";
            if (registered) return HotkeyUtil.Format(mods, key);
            if (mods != 0 && key != 0) return "冲突";
            return "未设置";
        }

        private void HotkeyClick(object sender, EventArgs e)
        {
            uint oldMods = _hotkeyMods;
            uint oldKey = _hotkeyKey;
            bool wasEnabled = _hotkeyEnabled;

            UnregisterCurrent();

            try
            {
                using (HotkeyForm form = new HotkeyForm(wasEnabled, oldMods, oldKey, ApplyHotkey))
                {
                    form.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                ConfigStore.Log("打开快捷键设置失败：" + ex.Message);
            }

            if (!_hotkeyRegistered && _hotkeyEnabled && _hotkeyMods != 0 && _hotkeyKey != 0)
            {
                if (!TryRegisterHotkey(_hotkeyMods, _hotkeyKey))
                {
                    ConfigStore.Log("恢复原快捷键失败：" + HotkeyUtil.Format(_hotkeyMods, _hotkeyKey));
                }
            }
            UpdateHotkeyMenu();
        }

        private bool ApplyHotkey(bool enabled, uint mods, uint vk, out string error)
        {
            error = null;

            if (!enabled)
            {
                UnregisterCurrent();
                _hotkeyEnabled = false;
                _hotkeyMods = 0;
                _hotkeyKey = 0;
                _cfg.HotkeyEnabled = false;
                _cfg.HotkeyModifiers = 0;
                _cfg.HotkeyKey = 0;
                ConfigStore.Save(_cfg);
                ConfigStore.Log("快速隐藏快捷键已禁用。");
                UpdateHotkeyMenu();
                return true;
            }

            UnregisterCurrent();
            if (Native.RegisterHotKey(_msg.Handle, Native.HOTKEY_ID, mods, vk))
            {
                _hotkeyRegistered = true;
                _hotkeyEnabled = true;
                _hotkeyMods = mods;
                _hotkeyKey = vk;
                _cfg.HotkeyEnabled = true;
                _cfg.HotkeyModifiers = mods;
                _cfg.HotkeyKey = vk;
                ConfigStore.Save(_cfg);
                ConfigStore.Log("快速隐藏快捷键已设为 " + HotkeyUtil.Format(mods, vk) + "。");
                UpdateHotkeyMenu();
                return true;
            }

            int err = Marshal.GetLastWin32Error();
            error = "快捷键 " + HotkeyUtil.Format(mods, vk) + " 已被其他程序占用（错误码 " + err + "），请换一个组合。";
            UpdateHotkeyMenu();
            return false;
        }

        private void CloseHotkeyClick(object sender, EventArgs e)
        {
            uint oldMods = _closeHotkeyMods;
            uint oldKey = _closeHotkeyKey;
            bool wasEnabled = _closeHotkeyEnabled;

            UnregisterCloseHotkey();

            try
            {
                using (HotkeyForm form = new HotkeyForm(wasEnabled, oldMods, oldKey,
                    ApplyCloseHotkey, "关闭所有预览窗"))
                {
                    form.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                ConfigStore.Log("打开关闭快捷键设置失败：" + ex.Message);
            }

            if (!_closeHotkeyRegistered && _closeHotkeyEnabled &&
                _closeHotkeyMods != 0 && _closeHotkeyKey != 0)
            {
                if (!TryRegisterCloseHotkey(_closeHotkeyMods, _closeHotkeyKey))
                {
                    ConfigStore.Log("恢复关闭快捷键失败：" +
                        HotkeyUtil.Format(_closeHotkeyMods, _closeHotkeyKey));
                }
            }
            UpdateHotkeyMenu();
        }

        private bool ApplyCloseHotkey(bool enabled, uint mods, uint vk, out string error)
        {
            error = null;

            if (!enabled)
            {
                UnregisterCloseHotkey();
                _closeHotkeyEnabled = false;
                _closeHotkeyMods = 0;
                _closeHotkeyKey = 0;
                _cfg.CloseHotkeyEnabled = false;
                _cfg.CloseHotkeyModifiers = 0;
                _cfg.CloseHotkeyKey = 0;
                ConfigStore.Save(_cfg);
                ConfigStore.Log("关闭所有预览窗快捷键已禁用。");
                UpdateHotkeyMenu();
                return true;
            }

            UnregisterCloseHotkey();
            if (Native.RegisterHotKey(_msg.Handle, Native.HOTKEY_ID_CLOSE, mods, vk))
            {
                _closeHotkeyRegistered = true;
                _closeHotkeyEnabled = true;
                _closeHotkeyMods = mods;
                _closeHotkeyKey = vk;
                _cfg.CloseHotkeyEnabled = true;
                _cfg.CloseHotkeyModifiers = mods;
                _cfg.CloseHotkeyKey = vk;
                ConfigStore.Save(_cfg);
                ConfigStore.Log("关闭所有预览窗快捷键已设为 " +
                    HotkeyUtil.Format(mods, vk) + "。");
                UpdateHotkeyMenu();
                return true;
            }

            int err = Marshal.GetLastWin32Error();
            error = "快捷键 " + HotkeyUtil.Format(mods, vk) + " 已被其他程序占用（错误码 " + err + "），请换一个组合。";
            UpdateHotkeyMenu();
            return false;
        }

        private void OnAlphaChanged(object sender, AlphaChangedEventArgs e)
        {
            _hud.ShowText("透明度 " + e.Percent + "%", AnchorRect());
        }

        private Rectangle AnchorRect()
        {
            IntPtr h = _mgr.ActiveWindow;
            if (h != IntPtr.Zero)
            {
                Native.RECT r;
                if (Native.GetWindowRect(h, out r))
                {
                    return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                }
            }

            Native.POINT pt;
            Native.GetCursorPos(out pt);
            return new Rectangle(pt.X, pt.Y, 1, 1);
        }

        private void AlphaPresetClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null || !(item.Tag is int)) return;
            _mgr.SetAlpha((int)item.Tag);
        }

        private void PauseClick(object sender, EventArgs e)
        {
            _mgr.Paused = !_mgr.Paused;
            _pauseItem.Checked = _mgr.Paused;
        }

        private void TrayDoubleClick(object sender, EventArgs e)
        {
            _mgr.Paused = !_mgr.Paused;
            _pauseItem.Checked = _mgr.Paused;
        }

        private void AutoStartClick(object sender, EventArgs e)
        {
            bool enable = !AutoStartHelper.IsEnabled();
            AutoStartHelper.Set(enable);
            _cfg.AutoStart = enable;
            ConfigStore.Save(_cfg);
            _autoStartItem.Checked = enable;
        }

        private void ResetClick(object sender, EventArgs e)
        {
            _mgr.ResetPositions();
            _hud.ShowText("位置已重置", AnchorRect());
        }

        private void MenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _pauseItem.Checked = _mgr.Paused;
            _autoStartItem.Checked = AutoStartHelper.IsEnabled();
            UpdateHotkeyMenu();

            foreach (ToolStripItem item in _alphaMenu.DropDownItems)
            {
                ToolStripMenuItem mi = item as ToolStripMenuItem;
                if (mi == null || !(mi.Tag is int)) continue;
                mi.Checked = ((int)mi.Tag == _cfg.Alpha);
            }
        }

        private void OnHotkey(object sender, EventArgs e)
        {
            if (!_hotkeyRegistered) return;
            _mgr.ToggleActiveMinimize();
        }

        private void OnCloseAllHotkey(object sender, EventArgs e)
        {
            if (!_closeHotkeyRegistered) return;
            CloseAllClick(sender, e);
        }

        private void CloseAllClick(object sender, EventArgs e)
        {
            int count = _mgr.CloseAllPreviews();
            _hud.ShowText(count > 0 ? "已关闭 " + count + " 个预览窗" : "没有打开的预览窗",
                AnchorRect());
        }

        private void ExitClick(object sender, EventArgs e)
        {
            Cleanup();
            ExitThread();
        }

        private void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;

            try { _mgr.SaveNow(); }
            catch (Exception ex) { ConfigStore.Log("退出保存失败：" + ex.Message); }

            try { _mgr.Shutdown(); }
            catch (Exception ex) { ConfigStore.Log("退出清理失败：" + ex.Message); }

            UnregisterCurrent();
            UnregisterCloseHotkey();
            _msg.Destroy();

            _tray.Visible = false;
            _tray.Dispose();
            _hud.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Cleanup();
            base.Dispose(disposing);
        }
    }

    internal sealed class MessageWindow : NativeWindow
    {
        public event EventHandler HotkeyPressed;
        public event EventHandler CloseAllPressed;

        public MessageWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "QQViewerPin.Message";
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == Native.HOTKEY_ID)
                {
                    EventHandler handler = HotkeyPressed;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
                else if (id == Native.HOTKEY_ID_CLOSE)
                {
                    EventHandler handler = CloseAllPressed;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
                return;
            }
            base.WndProc(ref m);
        }

        public void Destroy()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    internal static class AutoStartHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "QQViewerPin";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    object value = key.GetValue(ValueName);
                    return value != null && value.ToString().Length > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                ConfigStore.Log("设置开机自启失败：" + ex.Message);
            }
        }
    }
}
