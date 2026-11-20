using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace QQViewerPin
{
    [DataContract]
    public sealed class RectData
    {
        [DataMember(Name = "x", Order = 1)]
        public int X;

        [DataMember(Name = "y", Order = 2)]
        public int Y;

        [DataMember(Name = "w", Order = 3)]
        public int W;

        [DataMember(Name = "h", Order = 4)]
        public int H;
    }

    [DataContract]
    public sealed class AppConfig
    {
        [DataMember(Name = "image", Order = 1)]
        public RectData Image;

        [DataMember(Name = "video", Order = 2)]
        public RectData Video;

        [DataMember(Name = "alpha", Order = 3)]
        public int Alpha;

        [DataMember(Name = "hideFromTaskbar", Order = 4)]
        public bool HideFromTaskbar;

        [DataMember(Name = "autoStart", Order = 5)]
        public bool AutoStart;

        [DataMember(Name = "hotkeyEnabled", Order = 6)]
        public bool HotkeyEnabled;

        [DataMember(Name = "hotkeyModifiers", Order = 7)]
        public uint HotkeyModifiers;

        [DataMember(Name = "hotkeyKey", Order = 8)]
        public uint HotkeyKey;

        [DataMember(Name = "closeHotkeyEnabled", Order = 9)]
        public bool CloseHotkeyEnabled;

        [DataMember(Name = "closeHotkeyModifiers", Order = 10)]
        public uint CloseHotkeyModifiers;

        [DataMember(Name = "closeHotkeyKey", Order = 11)]
        public uint CloseHotkeyKey;

        [DataMember(Name = "weChatMedia", Order = 12)]
        public RectData WeChatMedia;

        [DataMember(Name = "qqEnabled", Order = 13)]
        public bool QQEnabled;

        [DataMember(Name = "weChatEnabled", Order = 14)]
        public bool WeChatEnabled;

        [DataMember(Name = "weChatAlpha", Order = 15)]
        public int WeChatAlpha;

        public AppConfig()
        {
            Alpha = 50;
            HideFromTaskbar = true;
            AutoStart = false;
            HotkeyEnabled = true;
            CloseHotkeyEnabled = true;
            QQEnabled = true;
            WeChatEnabled = true;
            WeChatAlpha = 50;
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            Alpha = 50;
            HideFromTaskbar = true;
            HotkeyEnabled = true;
            CloseHotkeyEnabled = true;
            QQEnabled = true;
            WeChatEnabled = true;
            WeChatAlpha = 50;
        }
    }

    internal static class ConfigStore
    {
        public static readonly string Dir = ResolveDir();

        public static readonly string ConfigPath = Path.Combine(Dir, "config.json");

        private static readonly object Sync = new object();

        private static string ResolveDir()
        {
            string custom = Environment.GetEnvironmentVariable("QQVIEWERPIN_DATA");
            if (!string.IsNullOrEmpty(custom)) return custom;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QQViewerPin");
        }

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return new AppConfig();
                DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(AppConfig));
                using (FileStream fs = File.OpenRead(ConfigPath))
                {
                    AppConfig cfg = (AppConfig)ser.ReadObject(fs);
                    if (cfg == null) return new AppConfig();
                    if (cfg.Alpha < 10 || cfg.Alpha > 100) cfg.Alpha = 50;
                    cfg.Image = Validate(cfg.Image);
                    cfg.Video = Validate(cfg.Video);
                    cfg.WeChatMedia = Validate(cfg.WeChatMedia);
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                Log("加载配置失败：" + ex.Message);
                return new AppConfig();
            }
        }

        public static void Save(AppConfig cfg)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    string tmp = ConfigPath + ".tmp";
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(AppConfig));
                    using (FileStream fs = File.Create(tmp))
                    {
                        ser.WriteObject(fs, cfg);
                    }
                    File.Copy(tmp, ConfigPath, true);
                    try { File.Delete(tmp); } catch { }
                }
                catch (Exception ex)
                {
                    Log("保存配置失败：" + ex.Message);
                }
            }
        }

        public static void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
            if (TryAppend(Path.Combine(Dir, "log.txt"), line)) return;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "QQViewerPin.log"), line);
            }
            catch { }
        }

        private static bool TryAppend(string path, string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, line);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static RectData Validate(RectData r)
        {
            if (r == null) return null;
            if (r.W <= 0 || r.H <= 0) return null;
            if (r.W > 20000 || r.H > 20000) return null;
            if (r.X < -100000 || r.X > 100000) return null;
            if (r.Y < -100000 || r.Y > 100000) return null;
            return r;
        }
    }
}
