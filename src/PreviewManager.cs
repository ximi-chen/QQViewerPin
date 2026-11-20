using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QQViewerPin
{
    internal enum PreviewType
    {
        None,
        Image,
        Video
    }

    internal sealed class AlphaChangedEventArgs : EventArgs
    {
        public readonly int Percent;

        public AlphaChangedEventArgs(int percent)
        {
            Percent = percent;
        }
    }

    internal sealed class PreviewManager
    {
        private const string ChromeClass = "Chrome_WidgetWin_1";

        private readonly AppConfig _cfg;

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
        private readonly Timer _retryTimer;
        private int _guardTicks;
        private readonly List<IntPtr> _pending = new List<IntPtr>();

        private readonly Dictionary<uint, bool> _procCache = new Dictionary<uint, bool>();
        private bool _paused;
        private bool _started;

        public event EventHandler<AlphaChangedEventArgs> AlphaChanged;

        public PreviewManager(AppConfig cfg)
        {
            _cfg = cfg;

            _saveTimer = new Timer();
            _saveTimer.Interval = 600;
            _saveTimer.Tick += SaveTimerTick;

            _guardTimer = new Timer();
            _guardTimer.Interval = 100;
            _guardTimer.Tick += GuardTimerTick;

            _retryTimer = new Timer();
            _retryTimer.Interval = 150;
            _retryTimer.Tick += RetryTimerTick;
        }

        public AppConfig Config
        {
            get { return _cfg; }
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
                ConfigStore.Log("SetWinEventHook 失败，错误码 " + Marshal.GetLastWin32Error());
            }

            _mouseProc = MouseHookProc;
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc,
                Native.GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                ConfigStore.Log("鼠标钩子安装失败，错误码 " + Marshal.GetLastWin32Error());
            }

            ConfigStore.Log("已启动。");
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
            _retryTimer.Stop();

            RestoreActiveStyle();
            ConfigStore.Save(_cfg);
            ConfigStore.Log("已退出。");
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
            _cfg.Alpha = percent;
            ConfigStore.Save(_cfg);

            if (_active != IntPtr.Zero && Native.IsWindow(_active))
            {
                ApplyAlphaInternal(_active, percent);
            }
            OnAlphaChanged(percent);
        }

        public void AdjustAlpha(int delta)
        {
            SetAlpha(_cfg.Alpha + delta);
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
            _cfg.Image = null;
            _cfg.Video = null;
            ConfigStore.Save(_cfg);
        }

        public int CloseAllPreviews()
        {
            List<IntPtr> all = new List<IntPtr>();

            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindow(h)) return true;
                if (!Native.IsTopLevel(h)) return true;
                if (!string.Equals(Native.ClassName(h), ChromeClass, StringComparison.Ordinal)) return true;
                if (ClassifyTitle(Native.Title(h)) == PreviewType.None) return true;

                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!IsQQProcess(pid)) return true;

                all.Add(h);
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr h in all)
            {
                Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            if (all.Count > 0)
            {
                ConfigStore.Log("关闭所有预览窗：共 " + all.Count + " 个。");
            }
            return all.Count;
        }

        private void OnAlphaChanged(int percent)
        {
            EventHandler<AlphaChangedEventArgs> handler = AlphaChanged;
            if (handler != null) handler(this, new AlphaChangedEventArgs(percent));
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
            else if (eventType == Native.EVENT_OBJECT_HIDE || eventType == Native.EVENT_OBJECT_DESTROY)
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
        }

        private void PreparePreview(IntPtr hwnd, string stage)
        {
            if (_paused || hwnd == IntPtr.Zero || hwnd == _active) return;

            PreviewType type = ClassifyTitle(Native.Title(hwnd));
            if (type == PreviewType.None) return;
            if (!LooksLikePreview(hwnd, type, false)) return;

            // Apply the layered style while the window is still hidden/being created,
            // so QQ cannot paint one opaque frame before the show hook runs.
            if (_cfg.HideFromTaskbar)
            {
                ApplyToolWindow(hwnd, Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE));
            }
            ApplyAlphaInternal(hwnd, _cfg.Alpha);
            ConfigStore.Log("预览窗" + stage + "预置透明度 hwnd=0x" + hwnd.ToInt64().ToString("X") +
                " 类型=" + type);
        }

        private void HandleShow(IntPtr hwnd)
        {
            if (_paused) return;
            if (hwnd == _active) return;

            PreviewType type = ClassifyTitle(Native.Title(hwnd));
            if (type == PreviewType.None)
            {
                QueueRetry(hwnd);
                return;
            }
            if (!LooksLikePreview(hwnd, type)) return;

            // Close the gap between the show event and Adopt's alpha application.
            ApplyAlphaInternal(hwnd, _cfg.Alpha);
            Adopt(hwnd, type);
        }

        private void HandleGone(IntPtr hwnd)
        {
            if (hwnd != _active) return;

            RectI now = Native.GetRect(_active);
            if (now != null && now.W > 0 && now.H > 0)
            {
                _lastRect = now;
            }
            if (_lastRect != null && _activeType != PreviewType.None)
            {
                SaveRect(_activeType, _lastRect);
            }
            ConfigStore.Log("预览窗已关闭 hwnd=0x" + hwnd.ToInt64().ToString("X"));

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
            if (_paused) return;
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
                if (_paused) return;
                if (hwnd == _active) continue;
                if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) continue;

                PreviewType type = ClassifyTitle(Native.Title(hwnd));
                if (type == PreviewType.None) continue;
                if (!LooksLikePreview(hwnd, type)) continue;
                Adopt(hwnd, type);
            }
        }

        private bool LooksLikePreview(IntPtr hwnd, PreviewType type)
        {
            return LooksLikePreview(hwnd, type, true);
        }

        private bool LooksLikePreview(IntPtr hwnd, PreviewType type, bool requireVisible)
        {
            if (!Native.IsWindow(hwnd)) return false;
            if (requireVisible && !Native.IsWindowVisible(hwnd)) return false;
            if (!Native.IsTopLevel(hwnd)) return false;
            if (!string.Equals(Native.ClassName(hwnd), ChromeClass, StringComparison.Ordinal)) return false;
            if (ClassifyTitle(Native.Title(hwnd)) != type) return false;

            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (!IsQQProcess(pid)) return false;

            if (requireVisible)
            {
                RectI r = Native.GetRect(hwnd);
                if (r == null || r.W < 50 || r.H < 50) return false;
            }
            return true;
        }

        private void Adopt(IntPtr hwnd, PreviewType type)
        {
            if (_paused) return;
            if (hwnd == IntPtr.Zero) return;

            RectI cur = Native.GetRect(hwnd);
            if (cur == null || cur.W < 50 || cur.H < 50) return;

            CaptureActiveRect();

            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);

            RectData saved = type == PreviewType.Image ? _cfg.Image : _cfg.Video;
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
            ApplyAlphaInternal(hwnd, _cfg.Alpha);

            _active = hwnd;
            _activeType = type;
            _originalEx = ex;
            _haveOriginalEx = true;
            _target = target;
            _lastRect = target.Clone();

            SaveRect(type, target);
            ConfigStore.Log("接管预览窗 hwnd=0x" + hwnd.ToInt64().ToString("X") + " 类型=" + type +
                " 目标=" + target.X + "," + target.Y + " " + target.W + "x" + target.H +
                " alpha=" + _cfg.Alpha);
            CloseOtherPreviews(hwnd);
            StartGuard();
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
                if (!Native.IsTopLevel(h)) return true;
                if (!string.Equals(Native.ClassName(h), ChromeClass, StringComparison.Ordinal)) return true;

                PreviewType type = ClassifyTitle(Native.Title(h));
                if (type == PreviewType.None) return true;

                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!IsQQProcess(pid)) return true;

                others.Add(h);
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr h in others)
            {
                ConfigStore.Log("关闭旧预览窗 hwnd=0x" + h.ToInt64().ToString("X"));
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
            if (_paused)
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
            ApplyAlphaInternal(_active, _cfg.Alpha);

            _guardTicks--;
            if (_guardTicks <= 0) _guardTimer.Stop();
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

            if (type == PreviewType.Image) _cfg.Image = data;
            else if (type == PreviewType.Video) _cfg.Video = data;
            else return;

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
            if (nCode >= 0 && wParam.ToInt32() == (int)Native.WM_MOUSEWHEEL && !_paused)
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

        private bool IsQQProcess(uint pid)
        {
            bool known;
            if (_procCache.TryGetValue(pid, out known)) return known;

            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;

            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!Native.QueryFullProcessImageName(h, 0, sb, ref size)) return false;

                string path = sb.ToString();
                bool isQQ = path.EndsWith("QQ.exe", StringComparison.OrdinalIgnoreCase);
                if (isQQ) _procCache[pid] = true;
                return isQQ;
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        public static PreviewType ClassifyTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return PreviewType.None;
            if (title.IndexOf("图片查看器", StringComparison.Ordinal) >= 0) return PreviewType.Image;
            if (title.IndexOf("视频播放器", StringComparison.Ordinal) >= 0) return PreviewType.Video;
            if (title.IndexOf("视频查看器", StringComparison.Ordinal) >= 0) return PreviewType.Video;
            return PreviewType.None;
        }
    }
}
