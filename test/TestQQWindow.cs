using System;
using System.Runtime.InteropServices;

namespace TestQQWindow
{
    internal static class Program
    {
        private static string ClassName = "Chrome_WidgetWin_1";

        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_VISIBLE = 0x10000000;
        private const int SW_SHOW = 5;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_CLOSE = 0x0010;

        private const uint CS_HREDRAW = 0x0002;
        private const uint CS_VREDRAW = 0x0001;
        private const uint COLOR_WINDOW = 5;
        private const int IDC_ARROW = 32512;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
            IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowText(IntPtr hWnd, string text);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int code);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        private static WndProc _wndProc;

        [STAThread]
        private static int Main(string[] args)
        {
            string exeName = System.IO.Path.GetFileName(
                System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
            if (string.Equals(exeName, "Weixin.exe", StringComparison.OrdinalIgnoreCase))
            {
                ClassName = "Qt51514QWindowIcon";
            }

            bool lateTitle = args.Length > 0 && args[0] == "--late";
            string title = lateTitle
                ? (args.Length > 1 ? args[1] : "图片查看器")
                : (args.Length > 0 ? args[0] : "图片查看器");
            int width = 900;
            int height = 700;

            _wndProc = WindowProc;

            WNDCLASSEX wc = new WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
            wc.style = CS_HREDRAW | CS_VREDRAW;
            wc.lpfnWndProc = _wndProc;
            wc.hInstance = GetModuleHandle(null);
            wc.hCursor = LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW);
            wc.hbrBackground = (IntPtr)(COLOR_WINDOW + 1);
            wc.lpszClassName = ClassName;

            if (RegisterClassEx(ref wc) == 0)
            {
                return 2;
            }

            uint style = WS_OVERLAPPEDWINDOW | (lateTitle ? 0 : WS_VISIBLE);
            IntPtr hwnd = CreateWindowEx(0, ClassName, lateTitle ? "" : title,
                style, 120, 120, width, height,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (hwnd == IntPtr.Zero) return 3;

            if (lateTitle)
            {
                System.Threading.Thread.Sleep(1200);
                SetWindowText(hwnd, title);
            }

            ShowWindow(hwnd, SW_SHOW);
            UpdateWindow(hwnd);

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            return 0;
        }

        private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_CLOSE)
            {
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            }
            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }
}
