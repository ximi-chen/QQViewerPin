using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QQViewerPin
{
    internal enum PreviewApp
    {
        None,
        QQ,
        WeChat
    }

    internal enum PreviewType
    {
        None,
        Image,
        Video,
        Media
    }

    internal sealed class AlphaChangedEventArgs : EventArgs
    {
        public readonly PreviewApp App;
        public readonly int Percent;

        public AlphaChangedEventArgs(PreviewApp app, int percent)
        {
            App = app;
            Percent = percent;
        }
    }

    internal sealed class PreviewManager
    {
        private const string ChromeClass = "Chrome_WidgetWin_1";
        private const string WeChatTitle = "图片和视频";

        private readonly AppConfig _cfg;
        private readonly PreviewApp _app;

        private Native.WinEventDelegate _winEventProc;
        private Native.LowLevelMouseProc _mouseProc;
        private IntPtr _winEventHook = IntPtr.Zero;
        private IntPtr _mouseHook = IntPtr.Zero;

        private IntPtr _active = IntPtr.Zero;
        private PreviewType _activeType = PreviewType.None;
        private int _originalEx;
        private bool _haveOriginalEx;
        private RectI _target;
        private RectI _lastRect;

        private readonly Timer _saveTimer;
        private readonly Timer _guardTimer;
        private readonly Timer _maintenanceTimer;
        private readonly Timer _retryTimer;
        private readonly Timer _cloakTimer;
        private readonly Timer _fastTimer;
        private int _guardTicks;
        private IntPtr _fastHwnd = IntPtr.Zero;
        private int _fastPercent = 50;
        private DateTime _fastUntil = DateTime.MinValue;
        private readonly List<IntPtr> _pending = new List<IntPtr>();
        private readonly Dictionary<IntPtr, DateTime> _cloaked = new Dictionary<IntPtr, DateTime>();
        private readonly Dictionary<IntPtr, int> _candidateOriginalEx = new Dictionary<IntPtr, int>();
        private readonly HashSet<IntPtr> _dwmCloaked = new HashSet<IntPtr>();
        private readonly HashSet<IntPtr> _hiddenCandidates = new HashSet<IntPtr>();
        private bool _revealing;

        private readonly Dictionary<uint, PreviewApp> _procCache = new Dictionary<uint, PreviewApp>();
        private bool _paused;
        private bool _started;
        private bool _adopting;

        public event EventHandler<AlphaChangedEventArgs> AlphaChanged;

        public PreviewManager(AppConfig cfg, PreviewApp app)
        {
            _cfg = cfg;
            _app = app;

            _saveTimer = new Timer();
            _saveTimer.Interval = 600;
            _saveTimer.Tick += SaveTimerTick;

            _guardTimer = new Timer();
            _guardTimer.Interval = 100;
            _guardTimer.Tick += GuardTimerTick;

            _maintenanceTimer = new Timer();
            _maintenanceTimer.Interval = 1000;
            _maintenanceTimer.Tick += MaintenanceTimerTick;

            _retryTimer = new Timer();
            _retryTimer.Interval = 150;
            _retryTimer.Tick += RetryTimerTick;

            _cloakTimer = new Timer();
            _cloakTimer.Interval = 500;
            _cloakTimer.Tick += CloakTimerTick;

            _fastTimer = new Timer();
            _fastTimer.Interval = 10;
            _fastTimer.Tick += FastTimerTick;
        }

        public PreviewApp App
        {
            get { return _app; }
        }

        public AppConfig Config
        {
            get { return _cfg; }
        }

        public bool Enabled
        {
            get { return _app == PreviewApp.QQ ? _cfg.QQEnabled : _cfg.WeChatEnabled; }
        }

        public bool Paused
        {
            get { return _paused; }
            set
            {
                _paused = value;
                if (_paused)
                {
                    _guardTimer.Stop();
                    _pending.Clear();
                    StopFast();
                    UncloakAll();
                }
            }
        }

        public IntPtr ActiveWindow
        {
            get { return _active; }
        }

        public PreviewType ActiveType
        {
            get { return _activeType; }
        }

        public int Alpha
        {
            get { return GetAlpha(); }
        }

        public void Start()
        {
            if (_started) return;
            _started = true;

            _winEventProc = OnWinEvent;
            _winEventHook = Native.SetWinEventHook(
                Native.EVENT_OBJECT_CREATE, Native.EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero, _winEventProc, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            if (_winEventHook == IntPtr.Zero)
            {
                ConfigStore.Log(AppName + "：SetWinEventHook 失败，错误码 " + Marshal.GetLastWin32Error());
            }

            _mouseProc = MouseHookProc;
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc,
                Native.GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                ConfigStore.Log(AppName + "：鼠标钩子安装失败，错误码 " + Marshal.GetLastWin32Error());
            }

            _maintenanceTimer.Start();
            _cloakTimer.Start();
            ConfigStore.Log(AppName + "：已启动。");
        }

        public void Shutdown()
        {
            if (!_started) return;
            _started = false;

            if (_winEventHook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            _saveTimer.Stop();
            _guardTimer.Stop();
            _maintenanceTimer.Stop();
            _retryTimer.Stop();
            _cloakTimer.Stop();
            StopFast();
            UncloakAll();

            RestoreActiveStyle();
            ConfigStore.Save(_cfg);
            ConfigStore.Log(AppName + "：已退出。");
        }

        public void SaveNow()
        {
            CaptureActiveRect();
            ConfigStore.Save(_cfg);
        }

        public void SetAlpha(int percent)
        {
            if (percent < 10) percent = 10;
            if (percent > 100) percent = 100;

            if (_app == PreviewApp.QQ) _cfg.Alpha = percent;
            else _cfg.WeChatAlpha = percent;
            ConfigStore.Save(_cfg);

            if (_active != IntPtr.Zero && Native.IsWindow(_active))
            {
                ApplyAlphaInternal(_active, percent);
            }
            OnAlphaChanged(percent);
        }

        public void AdjustAlpha(int delta)
        {
            SetAlpha(GetAlpha() + delta);
        }

        public bool ToggleActiveMinimize()
        {
            if (_active == IntPtr.Zero || !Native.IsWindow(_active)) return false;
            if (Native.IsIconic(_active))
            {
                Native.ShowWindow(_active, Native.SW_RESTORE);
            }
            else
            {
                Native.ShowWindow(_active, Native.SW_MINIMIZE);
            }
            return true;
        }

        public void ResetPositions()
        {
            if (_app == PreviewApp.QQ)
            {
                _cfg.Image = null;
                _cfg.Video = null;
            }
            else
            {
                _cfg.WeChatMedia = null;
            }
            ConfigStore.Save(_cfg);
        }

        public void CollectManagedWindows(List<IntPtr> target)
        {
            if (!Enabled || _paused) return;

            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;

                PreviewType type;
                if (!LooksLikePreview(h, false, out type)) return true;
                target.Add(h);
                return true;
            }, IntPtr.Zero);
        }

        public int CloseAllPreviews()
        {
            List<IntPtr> all = new List<IntPtr>();
            CollectManagedWindows(all);

            foreach (IntPtr h in all)
            {
                Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            if (all.Count > 0)
            {
                ConfigStore.Log(AppName + "：关闭所有预览窗，共 " + all.Count + " 个。");
            }
            return all.Count;
        }

        private string AppName
        {
            get { return _app == PreviewApp.QQ ? "QQ" : "微信"; }
        }

        private void OnAlphaChanged(int percent)
        {
            EventHandler<AlphaChangedEventArgs> handler = AlphaChanged;
            if (handler != null) handler(this, new AlphaChangedEventArgs(_app, percent));
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint eventThread, uint eventTime)
        {
            if (idObject != Native.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;

            if (eventType == Native.EVENT_OBJECT_CREATE)
            {
                HandleCreate(hwnd);
            }
            else if (eventType == Native.EVENT_OBJECT_NAMECHANGE)
            {
                HandleNameChange(hwnd);
            }
            else if (eventType == Native.EVENT_OBJECT_SHOW)
            {
                HandleShow(hwnd);
            }
            else if (eventType == Native.EVENT_OBJECT_HIDE)
            {
                if (_hiddenCandidates.Contains(hwnd)) return;
                HandleGone(hwnd);
            }
            else if (eventType == Native.EVENT_OBJECT_DESTROY)
            {
                HandleGone(hwnd);
            }
            else if (eventType == Native.EVENT_OBJECT_LOCATIONCHANGE)
            {
                HandleLocation(hwnd);
            }
        }

        private void HandleCreate(IntPtr hwnd)
        {
            PreparePreview(hwnd, "创建时");
        }

        private void HandleNameChange(IntPtr hwnd)
        {
            PreparePreview(hwnd, "标题就绪时");

            PreviewType type;
            if (LooksLikePreview(hwnd, false, out type))
            {
                ApplyAlphaInternal(hwnd, GetAlpha());
                Adopt(hwnd, type);
            }
        }

        private void PreparePreview(IntPtr hwnd, string stage)
        {
            if (_paused || !Enabled || hwnd == IntPtr.Zero || hwnd == _active) return;

            PreviewType type;
            if (!LooksLikePreview(hwnd, false, out type)) return;

            RectData saved = GetSavedRect(type);
            if (saved != null && saved.W > 0 && saved.H > 0)
            {
                // Position before the window becomes visible, so QQ/WeChat do not
                // paint one frame in the middle of the screen first.
                RectI target = ClampToMonitor(new RectI(saved.X, saved.Y, saved.W, saved.H));
                Native.SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.W, target.H,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }

            if (_cfg.HideFromTaskbar)
            {
                ApplyToolWindow(hwnd, Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE));
            }
            ApplyAlphaInternal(hwnd, GetAlpha());
            ConfigStore.Log(AppName + "：预览窗" + stage + "预置透明度 hwnd=0x" +
                hwnd.ToInt64().ToString("X") + " 类型=" + type);
        }

        private void HandleShow(IntPtr hwnd)
        {
            if (_paused || !Enabled || _revealing) return;
            if (hwnd == _active) return;

            PreviewType type;
            if (!LooksLikePreview(hwnd, true, out type))
            {
                QueueRetry(hwnd);
                return;
            }

            ApplyAlphaInternal(hwnd, GetAlpha());
            Adopt(hwnd, type);        }

        private void HandleGone(IntPtr hwnd)
        {
            if (hwnd != _active)
            {
                Uncloak(hwnd);
                return;
            }

            Uncloak(hwnd);
            if (_fastHwnd == hwnd) StopFast();
            RectI now = Native.GetRect(_active);
            if (now != null && now.W > 0 && now.H > 0)
            {
                _lastRect = now;
            }
            if (_lastRect != null && _activeType != PreviewType.None)
            {
                SaveRect(_activeType, _lastRect);
            }
            ConfigStore.Log(AppName + "：预览窗已关闭 hwnd=0x" + hwnd.ToInt64().ToString("X"));

            _active = IntPtr.Zero;
            _activeType = PreviewType.None;
            _target = null;
            _lastRect = null;
            _haveOriginalEx = false;
            _saveTimer.Stop();
            _guardTimer.Stop();
        }

        private void HandleLocation(IntPtr hwnd)
        {
            if (hwnd != _active) return;
            RectI now = Native.GetRect(_active);
            if (now == null) return;
            _lastRect = now;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void QueueRetry(IntPtr hwnd)
        {
            if (_paused || !Enabled) return;
            if (hwnd == IntPtr.Zero) return;
            if (_pending.Contains(hwnd)) return;
            _pending.Add(hwnd);
            if (!_retryTimer.Enabled) _retryTimer.Start();
        }

        private void RetryTimerTick(object sender, EventArgs e)
        {
            _retryTimer.Stop();
            if (_pending.Count == 0) return;

            IntPtr[] candidates = _pending.ToArray();
            _pending.Clear();

            foreach (IntPtr hwnd in candidates)
            {
                if (_paused || !Enabled) return;
                if (hwnd == _active) continue;
                if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) continue;

                PreviewType type;
                if (!LooksLikePreview(hwnd, true, out type)) continue;
                Adopt(hwnd, type);
            }
        }

        private bool LooksLikePreview(IntPtr hwnd, bool requireVisible, out PreviewType type)
        {
            type = PreviewType.None;
            if (!Native.IsWindow(hwnd)) return false;
            if (requireVisible && !Native.IsWindowVisible(hwnd)) return false;
            if (!Native.IsTopLevel(hwnd)) return false;

            string title = Native.Title(hwnd);
            if (title.Length == 0) return false;

            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (GetProcessApp(pid) != _app) return false;

            if (_app == PreviewApp.QQ)
            {
                if (!string.Equals(Native.ClassName(hwnd), ChromeClass, StringComparison.Ordinal)) return false;
                if (title.IndexOf("图片查看器", StringComparison.Ordinal) >= 0)
                {
                    type = PreviewType.Image;
                }
                else if (title.IndexOf("视频播放器", StringComparison.Ordinal) >= 0 ||
                         title.IndexOf("视频查看器", StringComparison.Ordinal) >= 0)
                {
                    type = PreviewType.Video;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                if (!string.Equals(title, WeChatTitle, StringComparison.Ordinal)) return false;
                type = PreviewType.Media;
            }

            if (requireVisible)
            {
                RectI r = Native.GetRect(hwnd);
                if (r == null || r.W < 50 || r.H < 50) return false;
            }
            return true;
        }

        private void Adopt(IntPtr hwnd, PreviewType type)
        {
            if (_paused || !Enabled || _adopting) return;
            if (hwnd == IntPtr.Zero) return;

            RectI cur = Native.GetRect(hwnd);
            if (cur == null || cur.W < 50 || cur.H < 50) return;

            _adopting = true;
            try
            {
                AdoptCore(hwnd, type, cur);
            }
            finally
            {
                _adopting = false;
            }
        }

        private void AdoptCore(IntPtr hwnd, PreviewType type, RectI cur)
        {
            CaptureActiveRect();

            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            int originalEx = ex;
            int candidateEx;
            if (_candidateOriginalEx.TryGetValue(hwnd, out candidateEx))
            {
                originalEx = candidateEx;
            }

            RectData saved = GetSavedRect(type);
            RectI target;
            if (saved != null && saved.W > 0 && saved.H > 0)
            {
                target = ClampToMonitor(new RectI(saved.X, saved.Y, saved.W, saved.H));
            }
            else
            {
                target = DefaultRect(cur);
            }

            Native.SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.W, target.H,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

            if (_cfg.HideFromTaskbar) ApplyToolWindow(hwnd, ex);
            ApplyAlphaInternal(hwnd, GetAlpha());

            _active = hwnd;
            _activeType = type;
            _originalEx = originalEx;
            _haveOriginalEx = true;
            _target = target;
            _lastRect = target.Clone();

            SaveRect(type, target);
            ConfigStore.Log(AppName + "：接管预览窗 hwnd=0x" + hwnd.ToInt64().ToString("X") +
                " 类型=" + type + " 目标=" + target.X + "," + target.Y + " " +
                target.W + "x" + target.H + " alpha=" + GetAlpha());
            ForgetCandidate(hwnd);
            StartFast(hwnd, GetAlpha(), 1000);
            CloseOtherPreviews(hwnd);
            StartGuard();
        }

        private RectData GetSavedRect(PreviewType type)
        {
            if (_app == PreviewApp.QQ)
            {
                return type == PreviewType.Video ? _cfg.Video : _cfg.Image;
            }
            return _cfg.WeChatMedia;
        }

        private int GetAlpha()
        {
            return _app == PreviewApp.QQ ? _cfg.Alpha : _cfg.WeChatAlpha;
        }

        private void CaptureActiveRect()
        {
            if (_active == IntPtr.Zero || !Native.IsWindow(_active)) return;
            if (_activeType == PreviewType.None) return;

            RectI r = Native.GetRect(_active);
            if (r == null || r.W <= 0 || r.H <= 0) return;
            _lastRect = r;
            SaveRect(_activeType, r);
        }

        private RectI DefaultRect(RectI current)
        {
            Native.POINT pt;
            Native.GetCursorPos(out pt);
            Native.MONITORINFO info;
            if (!Native.TryGetMonitorInfoFromPoint(pt.X, pt.Y, out info))
            {
                return ClampToMonitor(new RectI(current.X, current.Y, current.W, current.H));
            }

            int left = info.rcWork.Left;
            int top = info.rcWork.Top;
            int width = info.rcWork.Right - info.rcWork.Left;
            int height = info.rcWork.Bottom - info.rcWork.Top;

            int w = Math.Min(current.W, width);
            int h = Math.Min(current.H, height);
            int x = info.rcWork.Right - w - 40;
            int y = info.rcWork.Bottom - h - 40;

            if (x < left) x = left;
            if (y < top) y = top;

            return ClampToMonitor(new RectI(x, y, w, h));
        }

        private RectI ClampToMonitor(RectI rect)
        {
            Native.MONITORINFO info;
            if (!Native.TryGetMonitorInfoFromRect(rect, out info)) return rect;

            int left = info.rcWork.Left;
            int top = info.rcWork.Top;
            int width = info.rcWork.Right - info.rcWork.Left;
            int height = info.rcWork.Bottom - info.rcWork.Top;

            int w = Math.Min(rect.W, width);
            int h = Math.Min(rect.H, height);

            int x = rect.X;
            int y = rect.Y;
            if (x < left) x = left;
            if (y < top) y = top;
            if (x + w > info.rcWork.Right) x = info.rcWork.Right - w;
            if (y + h > info.rcWork.Bottom) y = info.rcWork.Bottom - h;

            return new RectI(x, y, w, h);
        }

        private void CloakCandidate(IntPtr hwnd)
        {
            if (_paused || !Enabled || hwnd == IntPtr.Zero) return;
            if (!Native.IsWindow(hwnd) || !Native.IsTopLevel(hwnd)) return;

            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (GetProcessApp(pid) != _app) return;

            string title = Native.Title(hwnd);
            string cls = Native.ClassName(hwnd);
            if (_app == PreviewApp.QQ)
            {
                if (!string.Equals(cls, ChromeClass, StringComparison.Ordinal)) return;
            }
            else
            {
                if (!cls.StartsWith("Qt", StringComparison.Ordinal)) return;
            }

            bool candidateTitle = title.Length == 0 ||
                (_app == PreviewApp.WeChat &&
                 string.Equals(title, "Weixin", StringComparison.OrdinalIgnoreCase));
            if (!candidateTitle) return;

            // Only the real preview window: independent top-level, resizable,
            // no owner, not a popup/menu/IME, and roughly the size we saved last time.
            if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return;
            int style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
            if ((style & Native.WS_THICKFRAME) == 0) return;
            int styleEx = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            int transient = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW |
                            Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            if ((styleEx & transient) != 0) return;
            if (!MatchesSavedSize(hwnd)) return;

            if (_cloaked.ContainsKey(hwnd))
            {
                HideCandidate(hwnd);
                return;
            }

            HideCandidate(hwnd);

            // Keep alpha=0 as a second guard in case Qt shows the window again
            // before we reveal it with the final position/alpha.
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            _candidateOriginalEx[hwnd] = ex;

            if ((ex & Native.WS_EX_LAYERED) == 0)
            {
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_LAYERED);
                Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                    Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            }
            Native.SetLayeredWindowAttributes(hwnd, 0, 0, Native.LWA_ALPHA);
            _cloaked[hwnd] = DateTime.Now;
            StartFast(hwnd, 0, 1500);
            ConfigStore.Log(AppName + "：按上次尺寸锁定候选窗口 hwnd=0x" +
                hwnd.ToInt64().ToString("X"));
        }

        private bool MatchesSavedSize(IntPtr hwnd)
        {
            RectI r = Native.GetRect(hwnd);
            if (r == null || r.W < 100 || r.H < 100) return false;

            if (_app == PreviewApp.WeChat)
            {
                return MatchesRect(_cfg.WeChatMedia, r);
            }
            return MatchesRect(_cfg.Image, r) || MatchesRect(_cfg.Video, r);
        }

        private static bool MatchesRect(RectData saved, RectI r)
        {
            if (saved == null || saved.W <= 0 || saved.H <= 0) return false;

            int toleranceW = Math.Max(24, saved.W / 10);
            int toleranceH = Math.Max(24, saved.H / 10);
            return Math.Abs(r.W - saved.W) <= toleranceW &&
                   Math.Abs(r.H - saved.H) <= toleranceH;
        }

        private void HideCandidate(IntPtr hwnd)
        {
            if (!Native.IsWindow(hwnd)) return;
            if (!_hiddenCandidates.Add(hwnd)) return;

            Native.ShowWindow(hwnd, Native.SW_HIDE);
            ConfigStore.Log(AppName + "：候选窗口先隐藏 hwnd=0x" +
                hwnd.ToInt64().ToString("X"));
        }

        private void RevealCandidate(IntPtr hwnd)
        {
            if (!_hiddenCandidates.Remove(hwnd)) return;
            if (Native.IsWindow(hwnd))
            {
                _revealing = true;
                Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
                _revealing = false;
            }
            ConfigStore.Log(AppName + "：候选窗口恢复显示 hwnd=0x" +
                hwnd.ToInt64().ToString("X"));
        }

        private void Uncloak(IntPtr hwnd)
        {
            if (!_cloaked.Remove(hwnd)) return;
            StopFast();
            UncloakDwm(hwnd);

            int originalEx;
            if (_candidateOriginalEx.TryGetValue(hwnd, out originalEx))
            {
                _candidateOriginalEx.Remove(hwnd);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, originalEx);
                Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                    Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
                if ((originalEx & Native.WS_EX_LAYERED) != 0)
                {
                    Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA);
                }
            }
            else
            {
                Native.SetCloaked(hwnd, false);
            }

            ConfigStore.Log(AppName + "：恢复显示 hwnd=0x" + hwnd.ToInt64().ToString("X"));
        }

        private bool TryDwmCloak(IntPtr hwnd)
        {
            if (_dwmCloaked.Contains(hwnd)) return true;
            if (!Native.SetCloaked(hwnd, true)) return false;

            _dwmCloaked.Add(hwnd);
            ConfigStore.Log(AppName + "：DWM 隐藏成功 hwnd=0x" + hwnd.ToInt64().ToString("X"));
            return true;
        }

        private void UncloakDwm(IntPtr hwnd)
        {
            if (_dwmCloaked.Remove(hwnd))
            {
                Native.SetCloaked(hwnd, false);
            }
        }

        private void ForgetCandidate(IntPtr hwnd)
        {
            UncloakDwm(hwnd);
            if (_candidateOriginalEx.Remove(hwnd))
            {
                _cloaked.Remove(hwnd);
                return;
            }
            if (_cloaked.Remove(hwnd))
            {
                Native.SetCloaked(hwnd, false);
            }
        }

        private void UncloakAll()
        {
            if (_cloaked.Count == 0) return;
            IntPtr[] keys = new IntPtr[_cloaked.Count];
            _cloaked.Keys.CopyTo(keys, 0);
            foreach (IntPtr h in keys)
            {
                Uncloak(h);
            }
        }

        private void CloakTimerTick(object sender, EventArgs e)
        {
            if (_cloaked.Count == 0) return;
            IntPtr[] keys = new IntPtr[_cloaked.Count];
            _cloaked.Keys.CopyTo(keys, 0);
            DateTime now = DateTime.Now;
            foreach (IntPtr h in keys)
            {
                DateTime since;
                if (!_cloaked.TryGetValue(h, out since)) continue;
                if (!Native.IsWindow(h) || (now - since).TotalSeconds > 2.0)
                {
                    Uncloak(h);
                }
            }
        }

        private void StartFast(IntPtr hwnd, int percent, int milliseconds)
        {
            if (hwnd == IntPtr.Zero) return;
            _fastHwnd = hwnd;
            _fastPercent = percent;
            _fastUntil = DateTime.Now.AddMilliseconds(milliseconds);
            _fastTimer.Stop();
            _fastTimer.Start();
        }

        private void StopFast()
        {
            _fastTimer.Stop();
            _fastHwnd = IntPtr.Zero;
        }

        private void FastTimerTick(object sender, EventArgs e)
        {
            if (_fastHwnd == IntPtr.Zero || DateTime.Now > _fastUntil || !Native.IsWindow(_fastHwnd))
            {
                StopFast();
                return;
            }

            int desired = _fastPercent * 255 / 100;
            byte readAlpha;
            uint readKey;
            uint readFlags;
            bool hasAlpha = Native.GetLayeredWindowAttributes(_fastHwnd, out readKey, out readAlpha, out readFlags);
            if (!hasAlpha || readAlpha != (byte)desired)
            {
                ApplyAlphaInternal(_fastHwnd, _fastPercent);
            }
        }

        private void ApplyAlphaInternal(IntPtr hwnd, int percent)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_LAYERED) == 0)
            {
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_LAYERED);
                Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                    Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            }

            int alpha = percent * 255 / 100;
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            Native.SetLayeredWindowAttributes(hwnd, 0, (byte)alpha, Native.LWA_ALPHA);
        }

        private void ApplyToolWindow(IntPtr hwnd, int originalEx)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            int updated = (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
            if (updated == ex) return;

            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, updated);
            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }

        private void CloseOtherPreviews(IntPtr keep)
        {
            List<IntPtr> others = new List<IntPtr>();

            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (h == keep) return true;
                if (!Native.IsWindowVisible(h)) return true;

                PreviewType type;
                if (!LooksLikePreview(h, false, out type)) return true;
                others.Add(h);
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr h in others)
            {
                ConfigStore.Log(AppName + "：关闭旧预览窗 hwnd=0x" + h.ToInt64().ToString("X"));
                Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private void StartGuard()
        {
            _guardTicks = 6;
            _guardTimer.Stop();
            _guardTimer.Start();
        }

        private void GuardTimerTick(object sender, EventArgs e)
        {
            if (_paused || !Enabled)
            {
                _guardTimer.Stop();
                return;
            }
            if (_active == IntPtr.Zero || !Native.IsWindow(_active) || !Native.IsWindowVisible(_active))
            {
                _guardTimer.Stop();
                return;
            }
            if (Native.IsLeftMouseDown())
            {
                _guardTimer.Stop();
                return;
            }
            if (_target == null)
            {
                _guardTimer.Stop();
                return;
            }

            RectI now = Native.GetRect(_active);
            if (now == null)
            {
                _guardTimer.Stop();
                return;
            }

            if (!now.SameAs(_target, 3))
            {
                Native.SetWindowPos(_active, IntPtr.Zero, _target.X, _target.Y, _target.W, _target.H,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                _lastRect = _target.Clone();
            }

            int ex = Native.GetWindowLong(_active, Native.GWL_EXSTYLE);
            if (_cfg.HideFromTaskbar && (ex & Native.WS_EX_TOOLWINDOW) == 0)
            {
                ApplyToolWindow(_active, _originalEx);
            }
            ApplyAlphaInternal(_active, GetAlpha());

            _guardTicks--;
            if (_guardTicks <= 0) _guardTimer.Stop();
        }

        private void MaintenanceTimerTick(object sender, EventArgs e)
        {
            if (_paused || !Enabled) return;
            if (_active == IntPtr.Zero || !Native.IsWindow(_active) || !Native.IsWindowVisible(_active))
            {
                return;
            }

            int ex = Native.GetWindowLong(_active, Native.GWL_EXSTYLE);
            if (_cfg.HideFromTaskbar && (ex & Native.WS_EX_TOOLWINDOW) == 0)
            {
                ApplyToolWindow(_active, _originalEx);
            }

            int desired = GetAlpha() * 255 / 100;
            byte readAlpha;
            uint readKey;
            uint readFlags;
            bool hasAlpha = Native.GetLayeredWindowAttributes(_active, out readKey, out readAlpha, out readFlags);
            if ((ex & Native.WS_EX_LAYERED) == 0 || !hasAlpha || readAlpha != (byte)desired)
            {
                ApplyAlphaInternal(_active, GetAlpha());
            }
        }

        private void SaveTimerTick(object sender, EventArgs e)
        {
            _saveTimer.Stop();
            if (_activeType == PreviewType.None || _lastRect == null) return;
            SaveRect(_activeType, _lastRect);
        }

        private void SaveRect(PreviewType type, RectI r)
        {
            if (r == null) return;
            RectData data = new RectData();
            data.X = r.X;
            data.Y = r.Y;
            data.W = r.W;
            data.H = r.H;

            if (_app == PreviewApp.QQ)
            {
                if (type == PreviewType.Image) _cfg.Image = data;
                else if (type == PreviewType.Video) _cfg.Video = data;
                else return;
            }
            else
            {
                _cfg.WeChatMedia = data;
            }

            ConfigStore.Save(_cfg);
        }

        private void RestoreActiveStyle()
        {
            if (_active == IntPtr.Zero || !_haveOriginalEx) return;
            if (!Native.IsWindow(_active)) return;

            Native.SetWindowLong(_active, Native.GWL_EXSTYLE, _originalEx);
            Native.SetWindowPos(_active, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

            if ((_originalEx & Native.WS_EX_LAYERED) != 0)
            {
                Native.SetLayeredWindowAttributes(_active, 0, 255, Native.LWA_ALPHA);
            }
        }

        private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt32() == (int)Native.WM_MOUSEWHEEL && !_paused && Enabled)
            {
                if (Native.IsAltDown() && IsCursorOverActive())
                {
                    Native.MSLLHOOKSTRUCT data = (Native.MSLLHOOKSTRUCT)
                        Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    int delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    AdjustAlpha(delta > 0 ? 5 : -5);
                    return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        public bool IsCursorOverActive()
        {
            if (_active == IntPtr.Zero || !Native.IsWindow(_active)) return false;

            Native.POINT pt;
            Native.GetCursorPos(out pt);
            IntPtr under = Native.WindowFromPoint(pt);
            if (under == IntPtr.Zero) return false;
            return Native.GetAncestor(under, Native.GA_ROOT) == _active;
        }

        private PreviewApp GetProcessApp(uint pid)
        {
            PreviewApp cached;
            if (_procCache.TryGetValue(pid, out cached)) return cached;

            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return PreviewApp.None;

            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!Native.QueryFullProcessImageName(h, 0, sb, ref size)) return PreviewApp.None;

                string path = sb.ToString();
                PreviewApp app = PreviewApp.None;
                if (path.EndsWith("QQ.exe", StringComparison.OrdinalIgnoreCase)) app = PreviewApp.QQ;
                else if (path.EndsWith("Weixin.exe", StringComparison.OrdinalIgnoreCase)) app = PreviewApp.WeChat;

                if (app != PreviewApp.None) _procCache[pid] = app;
                return app;
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }
    }
}
