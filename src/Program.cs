using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Reflection;
using System.Runtime.Versioning;

[assembly: AssemblyTitle("邮件提醒助手")]
[assembly: AssemblyDescription("局域网与跨设备邮件提醒工具")]
[assembly: AssemblyCompany("OfficeSignal")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.7.2", FrameworkDisplayName = ".NET Framework 4.7.2")]

namespace OfficeSignal
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            int updateArgument = Array.IndexOf(args, "--apply-update");
            if (updateArgument >= 0)
            {
                if (updateArgument + 1 >= args.Length || string.IsNullOrWhiteSpace(args[updateArgument + 1]))
                {
                    Console.Error.WriteLine("更新助手缺少更新计划文件路径。");
                    return 2;
                }
                int updateResult = OnlineUpdate.ApplyStagedPlan(args[updateArgument + 1]);
                if (updateResult != 0)
                    Console.Error.WriteLine(OnlineUpdate.LastError);
                return updateResult;
            }
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                string basePath = AppDomain.CurrentDomain.BaseDirectory;
                var team = TeamConfig.Load(Path.Combine(basePath, "team.json"));
                if (Array.IndexOf(args, "--local-only") >= 0)
                    team.RelayUrl = null;
                else if (string.IsNullOrWhiteSpace(team.RelayUrl))
                    team.RelayUrl = "https://blog.feian.online/mail-reminder/";
                bool created;
                using (var mutex = new Mutex(true, @"Local\OfficeSignal_" + team.TeamId, out created))
                {
                    if (!created)
                    {
                        MessageBox.Show("邮件提醒助手已经在运行。\r\n请在任务栏右下角找到提醒图标，双击打开面板。", "邮件提醒助手");
                        return 0;
                    }
                    string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfficeSignal", team.TeamId);
                    string settingsPath = Path.Combine(profile, "settings.json");
                    Log.FilePath = Path.Combine(profile, "app.log");
                    var prefs = Preferences.Load(settingsPath);
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    app.DispatcherUnhandledException += (sender, e) => {
                        Log.Write(e.Exception.ToString());
                        MessageBox.Show("操作未完成。\r\n" + e.Exception.Message, "邮件提醒助手", MessageBoxButton.OK, MessageBoxImage.Warning);
                        e.Handled = true;
                    };
                    using (var node = new LanNode(new NetworkOptions {
                        NodeId = Guid.NewGuid().ToString("N"), DisplayName = prefs.DisplayName, RoomKey = team.KeyBytes,
                        Port = team.Port, BroadcastTargets = new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Parse(team.BroadcastAddress), team.Port) }
                    }))
                    using (var window = new MainWindow(team, prefs, settingsPath, node, Array.IndexOf(args, "--background") >= 0))
                    {
                        app.Run(window);
                    }
                    mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write(ex.ToString());
                MessageBox.Show("邮件提醒助手未能启动。\r\n" + ex.Message + "\r\n\r\n请确认已完整解压软件，且没有其他程序占用通信端口。", "邮件提醒助手", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
