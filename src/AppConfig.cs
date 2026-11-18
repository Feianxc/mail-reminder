using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace OfficeSignal
{
    public sealed class TeamConfig
    {
        public string RoomName { get; set; }
        public string RoomKey { get; set; }
        public string BroadcastAddress { get; set; }
        public int Port { get; set; }
        public string RelayUrl { get; set; }

        public byte[] KeyBytes { get { return Convert.FromBase64String(RoomKey); } }
        public string TeamId
        {
            get
            {
                using (var hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(KeyBytes), 0, 8).Replace("-", "");
            }
        }

        public static TeamConfig Load(string path)
        {
            var config = new JavaScriptSerializer().Deserialize<TeamConfig>(File.ReadAllText(path, Encoding.UTF8));
            IPAddress address;
            if (config == null || string.IsNullOrWhiteSpace(config.RoomName) || config.RoomName.Length > 24 ||
                !IPAddress.TryParse(config.BroadcastAddress, out address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                config.Port < 1024 || config.Port > 65535 || config.KeyBytes.Length != 32)
                throw new InvalidDataException("team.json 的团队名称、IPv4 地址、端口或团队密钥不正确。请重新解压完整分发包。");
            return config;
        }
    }

    public sealed class Preferences
    {
        public string DisplayName { get; set; }
        public bool Sound { get; set; }
        public bool FloatingButton { get; set; }
        private string themeKey = "tech";
        public string ThemeKey
        {
            get { return themeKey; }
            set { themeKey = value == "cosmos" ? "cosmos" : "tech"; }
        }
        public bool ReduceMotion { get; set; }

        public static Preferences Load(string path)
        {
            if (!File.Exists(path))
                return new Preferences { DisplayName = Environment.MachineName, Sound = false, FloatingButton = true };
            var result = new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(path, Encoding.UTF8));
            if (result == null || string.IsNullOrWhiteSpace(result.DisplayName) || result.DisplayName.Length > 24)
                throw new InvalidDataException("个人设置损坏，请从设置文件中恢复或移走 settings.json 后重启。");
            return result;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }

    internal static class StartupRegistration
    {
        private static string Name(string teamId) { return "OfficeSignal_" + teamId; }
        public static bool IsEnabled(string teamId)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key != null && string.Equals(key.GetValue(Name(teamId)) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
        private static string Command { get { return "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --background"; } }
        public static void SetEnabled(string teamId, bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled) key.SetValue(Name(teamId), Command, RegistryValueKind.String);
                else key.DeleteValue(Name(teamId), false);
            }
        }
    }

    internal static class Log
    {
        public static string FilePath;
        private static readonly object Gate = new object();
        public static void Write(string message)
        {
            if (FilePath == null) return;
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 262144)
                        File.Move(FilePath, FilePath + "." + DateTime.UtcNow.Ticks + ".old");
                    // Keep the last rollover only. Logging cannot interrupt receiving an alert.
                    foreach (var old in new DirectoryInfo(Path.GetDirectoryName(FilePath))
                        .GetFiles(Path.GetFileName(FilePath) + ".*.old").OrderByDescending(f => f.LastWriteTimeUtc).Skip(1)) old.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("s") + " " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
