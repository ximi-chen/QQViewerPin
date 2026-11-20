using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace QQViewerPin
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool selftest = args != null && Array.IndexOf(args, "--selftest") >= 0;
            bool testMode = args != null && Array.IndexOf(args, "--test") >= 0;
            SelfLog(selftest || testMode, "start");

            bool createdNew;
            string mutexName = testMode ? "QQViewerPin_SingleInstance_Test" : "QQViewerPin_SingleInstance";
            using (Mutex mutex = new Mutex(true, mutexName, out createdNew))
            {
                SelfLog(selftest || testMode, "mutex createdNew=" + createdNew);
                if (!createdNew)
                {
                    if (!selftest && !testMode)
                    {
                        MessageBox.Show("QQ 预览窗固定器已经在运行。", "QQViewerPin",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }

                try
                {
                    Native.SetProcessDPIAware();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    SelfLog(selftest, "winforms ready");

                    if (!selftest && !testMode)
                    {
                        int rid = Native.GetCurrentIntegrityRid();
                        if (rid > 0 && rid < 0x2000)
                        {
                            ConfigStore.Log("检测到低完整性环境（0x" + rid.ToString("X") + "），已退出。");
                            MessageBox.Show(
                                "QQViewerPin 正在以低权限（沙箱）运行，无法操作 QQ 窗口，也无法显示托盘图标和保存配置。\n\n"
                                + "这通常是因为 exe 放在了被标记为 Low integrity 的文件夹里（例如当前工作区）。\n\n"
                                + "请：\n"
                                + "1. 在任务管理器结束当前的 QQViewerPin；\n"
                                + "2. 把 QQViewerPin-v1.zip 复制到桌面或 C:\\Tools；\n"
                                + "3. 解压后从资源管理器双击 QQViewerPin.exe。",
                                "QQViewerPin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }

                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                    {
                        ConfigStore.Log("UI 线程异常：" + e.Exception);
                    };
                    AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                    {
                        ConfigStore.Log("未处理异常：" + e.ExceptionObject);
                    };

                    bool firstRun = !File.Exists(ConfigStore.ConfigPath);
                    AppConfig cfg = ConfigStore.Load();
                    SelfLog(selftest, "config loaded, firstRun=" + firstRun);

                    using (TrayApp app = new TrayApp(cfg, firstRun, selftest))
                    {
                        SelfLog(selftest, "tray created");
                        Application.Run(app);
                        SelfLog(selftest, "message loop ended");
                    }

                    if (selftest) ConfigStore.Log("自检结束。");
                }
                catch (Exception ex)
                {
                    SelfLog(selftest, "exception: " + ex);
                    ConfigStore.Log("启动失败：" + ex);
                    if (!selftest && !testMode)
                    {
                        MessageBox.Show("启动失败：" + ex.Message, "QQViewerPin",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }

                GC.KeepAlive(mutex);
            }
        }

        private static void SelfLog(bool enabled, string message)
        {
            if (!enabled) return;
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log");
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
