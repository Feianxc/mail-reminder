using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace OfficeSignal
{
    internal static class UiTests
    {
        private static int checks;
        private const string UrgentMessage = "收到一封加急邮件，请立刻处理";
        private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[1] == "--native-smoke")
                return RunNativeSmoke(args[0]);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            string output = args[0];
            int result = 1;
            Directory.CreateDirectory(output);
            string profile = Path.Combine(output, "test-profile", "settings.json");
            VerifyPreferences(profile);
            var roomKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
            int first = FreePort();
            int second = FreePort();
            while (second == first) second = FreePort();
            var config = new TeamConfig {
                RoomName = "办公室", RoomKey = Convert.ToBase64String(roomKey), BroadcastAddress = "127.0.0.1", Port = first
            };
            var preferences = new Preferences {
                DisplayName = "测试电脑 A", Sound = false, FloatingButton = false, ThemeKey = "tech", ReduceMotion = false
            };
            preferences.Save(profile);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (var remote = new LanNode(new NetworkOptions {
                NodeId = Guid.NewGuid().ToString("N"), DisplayName = "测试电脑 B", RoomKey = roomKey,
                ListenAddress = IPAddress.Loopback, Port = second,
                BroadcastTargets = new[] { new IPEndPoint(IPAddress.Loopback, first) }
            }))
            using (var local = new LanNode(new NetworkOptions {
                NodeId = Guid.NewGuid().ToString("N"), DisplayName = "测试电脑 A", RoomKey = roomKey,
                ListenAddress = IPAddress.Loopback, Port = first,
                BroadcastTargets = new[] { new IPEndPoint(IPAddress.Loopback, second) }
            }))
            using (var window = new MainWindow(config, preferences, profile, local, false))
            {
                remote.Start();
                bool testing = false;
                window.ContentRendered += async (sender, eventArgs) => {
                    if (testing) return;
                    testing = true;
                    try
                    {
                        await WaitUntil(() => local.GetPeers().Length == 1 && remote.GetPeers().Length == 1,
                            "WPF application discovers other real UDP node");
                        Check(config.RelayUrl == null, "UI fixture has no FEIAN relay endpoint");
                        await VerifyLiveNames(window, local, remote, preferences, profile);
                        foreach (string theme in new[] { "tech", "cosmos" })
                            await VerifyTheme(window, local, remote, config, preferences, profile, theme, output);
                        await VerifyNotificationTimerReset(window, remote);
                        await SetMotionThroughSettings(window, true);
                        Check(!window.MotionEnabled && !window.IsMotionRunning, "reduce motion stops the main ambient animation");
                        var stoppedOrbit = Descendants<FrameworkElement>(window).Single(element => element.Name == "AmbientOrbit").RenderTransform as RotateTransform;
                        double stoppedAngle = stoppedOrbit.Angle;
                        await Task.Delay(250);
                        Check(Math.Abs(stoppedOrbit.Angle - stoppedAngle) < 0.001,
                            "reduce motion keeps the actual orbit angle stable over time");
                        Check(Preferences.Load(profile).ReduceMotion, "reduce motion preference survives disk readback");
                        Check(CountAnimatedObjects(window) == 0, "reduce motion removes active animation clocks from the main visual tree");
                        Capture(window, Path.Combine(output, "main-cosmos-reduced.png"));
                        remote.SendAlert(true);
                        await WaitUntil(() => window.AlertView != null && !window.AlertView.IsClosed && window.AlertView.IsVisible,
                            "reduced-motion test alert opens");
                        Check(!window.AlertView.MotionEnabled, "reduced-motion preference applies to notifications");
                        Check(Descendants<TextBlock>(window.AlertView).Any(text => text.Text == "测试提醒"),
                            "network test alert has an explicit test title");
                        Click(window.AlertView.SeenButton);
                        await WaitUntil(() => remote.GetLastSent().SeenIds.Length == 1, "reduced-motion notification returns human confirmation");
                        await SetMotionThroughSettings(window, false);
                        Check(window.MotionEnabled == SystemParameters.ClientAreaAnimation, "motion preference respects the Windows animation setting");
                        string productionExe = Path.Combine(Path.GetDirectoryName(output), "release", "MailReminder-1.3.0", "邮件提醒助手.exe");
                        await VerifyUpdateEntryWithoutTeam(productionExe, output);
                        await VerifyPortableEntry(productionExe);
                        Console.WriteLine("RESULT pass " + checks + " WPF UI checks. Cross-PC delivery and colleagues' Windows 10 machines not run.");
                        result = 0;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("RESULT fail " + ex);
                        result = 1;
                    }
                    finally
                    {
                        window.Quit();
                        app.Shutdown(result);
                    }
                };
                app.DispatcherUnhandledException += (sender, eventArgs) => {
                    Console.WriteLine("RESULT fail unhandled WPF exception " + eventArgs.Exception);
                    eventArgs.Handled = true;
                    result = 1;
                    window.Quit();
                    app.Shutdown(result);
                };
                app.Run(window);
            }
            return result;
        }

        private static int RunNativeSmoke(string output)
        {
            try
            {
                string exePath = Path.Combine(Path.GetDirectoryName(output), "release", "MailReminder-1.3.0", "邮件提醒助手.exe");
                string productionTeamPath = Path.Combine(Path.GetDirectoryName(exePath), "team.json");
                byte[] productionTeam = File.ReadAllBytes(productionTeamPath);
                VerifyPortableEntry(exePath).GetAwaiter().GetResult();
                Check(productionTeam.SequenceEqual(File.ReadAllBytes(productionTeamPath)), "local-only startup preserves the production relay configuration bytes");
                string folder = Path.Combine(output, "legacy-native-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string copy = Path.Combine(folder, Path.GetFileName(exePath));
                File.Copy(exePath, copy);
                File.Copy(exePath + ".config", copy + ".config");
                var key = Enumerable.Range(1, 32).Select(index => (byte)index).ToArray();
                string teamPath = Path.Combine(folder, "team.json");
                string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {
                    RoomName = "旧版本机验收", RoomKey = Convert.ToBase64String(key), BroadcastAddress = "127.0.0.1", Port = 45820
                });
                File.WriteAllText(teamPath, json, new UTF8Encoding(false));
                byte[] original = File.ReadAllBytes(teamPath);
                Check(TeamConfig.Load(teamPath).RelayUrl == null, "legacy team configuration keeps its optional relay URL unset outside Program startup");
                VerifyPortableEntry(copy).GetAwaiter().GetResult();
                Check(original.SequenceEqual(File.ReadAllBytes(teamPath)), "local-only startup preserves legacy team configuration bytes");
                Console.WriteLine("RESULT pass " + checks + " native startup/legacy configuration checks. FEIAN relay registration not run.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("RESULT fail " + ex);
                return 1;
            }
        }

        private static void VerifyPreferences(string profile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profile));
            var defaults = Preferences.Load(Path.Combine(Path.GetDirectoryName(profile), Guid.NewGuid().ToString("N") + ".json"));
            Check(defaults.FloatingButton, "new installations enable the warning ball by default");
            File.WriteAllText(profile, "{\"DisplayName\":\"旧版电脑\",\"Sound\":true,\"FloatingButton\":false}", Encoding.UTF8);
            var old = Preferences.Load(profile);
            Check(old.DisplayName == "旧版电脑" && old.Sound && !old.FloatingButton && old.ThemeKey == "tech" && !old.ReduceMotion,
                "1.0 settings retain user choices and receive default theme and motion values");
            foreach (string themeValue in new[] { "null", "\"unknown\"", "\"\"" })
            {
                File.WriteAllText(profile, "{\"DisplayName\":\"电脑\",\"ThemeKey\":" + themeValue + "}", Encoding.UTF8);
                Check(Preferences.Load(profile).ThemeKey == "tech", "invalid or null stored theme safely normalizes to tech: " + themeValue);
            }
            var updated = new Preferences {
                DisplayName = "星空电脑", Sound = false, FloatingButton = true, ThemeKey = "cosmos", ReduceMotion = true
            };
            updated.Save(profile);
            var readback = Preferences.Load(profile);
            Check(readback.DisplayName == updated.DisplayName && readback.ThemeKey == "cosmos" && readback.ReduceMotion && readback.FloatingButton,
                "new theme and motion settings save atomically and read back");
        }

        private static async Task VerifyTheme(MainWindow window, LanNode local, LanNode remote, TeamConfig config,
            Preferences preferences, string profile, string theme, string output)
        {
            window.Show();
            SelectTheme(window, theme);
            await WaitUntil(() => window.SendButton.IsEnabled, theme + " primary action becomes available");
            await Task.Delay(400);
            Check(window.ThemeKey == theme && Preferences.Load(profile).ThemeKey == theme,
                theme + " theme applies immediately and persists (memory=" + window.ThemeKey +
                ", stored=" + Preferences.Load(profile).ThemeKey + ")");
            var dpi = VisualTreeHelper.GetDpi(window);
            Console.WriteLine(theme + " DPI " + dpi.PixelsPerInchX + ", window " + window.ActualWidth + " x " + window.ActualHeight);
            Check(window.ActualWidth > 0 && window.ActualHeight > 0 && window.SendButton.ActualWidth > 0,
                theme + " main layout and primary action measure at the display DPI");
            Check(Descendants<TextBlock>(window).Any(text => text.Name == "ActionTitle" && text.Text == UrgentMessage),
                theme + " main action uses the requested urgent-mail message");
            AssertUrgentMessageFits(Descendants<TextBlock>(window).Single(text => text.Name == "ActionTitle"),
                theme + " main urgent-mail message is visible without clipping");
            AssertSelfAndPeerListed(window, local.DisplayName, remote.DisplayName, theme);
            var orbit = Descendants<FrameworkElement>(window).Single(element => element.Name == "AmbientOrbit").RenderTransform as RotateTransform;
            Check(orbit != null, theme + " ambient orbit exposes the actual rotation transform");
            if (window.MotionEnabled)
            {
                double before = orbit.Angle;
                await Task.Delay(250);
                double after = orbit.Angle;
                Check(Math.Abs(after - before) > 0.01, theme + " enabled orbit advances over time");
                Console.WriteLine(theme + " orbit angle " + before.ToString("F3") + " -> " + after.ToString("F3"));
            }
            else Console.WriteLine("NOT RUN " + theme + " orbit advance because Windows animations are disabled");
            Capture(window, Path.Combine(output, "main-" + theme + ".png"));

            Click(window.SendButton);
            Check(local.GetLastSent() != null && !local.GetLastSent().IsTest, theme + " primary button sends a real alert");
            string sentId = local.GetLastSent().Id;
            await WaitUntil(() => local.GetLastSent().ReceivedIds.Length == 1, theme + " sent alert receives software ACK");
            await WaitUntil(() => window.ReceiptText.Contains("1 / 1") && window.ReceiptText.Contains("0 人"),
                theme + " UI separates software delivery from human confirmation");
            Click(window.SendButton);
            Check(local.GetLastSent().Id == sentId, theme + " quick repeat click respects cooldown");
            remote.MarkSeen(sentId);
            await WaitUntil(() => local.GetLastSent().SeenIds.Length == 1, theme + " sender records remote human confirmation");

            window.Close();
            Check(!window.IsVisible, theme + " closing the main panel keeps the application running");
            Check(!window.IsMotionRunning, theme + " hidden main panel pauses ambient animation");
            int displayCount = window.AlertDisplayCount;
            remote.SendAlert(false);
            await WaitUntil(() => window.AlertView != null && !window.AlertView.IsClosed && window.AlertView.IsVisible,
                theme + " hidden application displays its themed notification");
            await Task.Delay(350);
            Check(window.AlertView.Topmost, theme + " notification is topmost");
            IntPtr alertHandle = new System.Windows.Interop.WindowInteropHelper(window.AlertView).Handle;
            Check(!window.AlertView.ShowActivated && !window.AlertView.IsActive &&
                GetForegroundWindow() != alertHandle && (GetWindowLong(alertHandle, -20) & 0x08000000) != 0,
                theme + " notification remains non-activating and does not own the input window");
            Check(Descendants<TextBlock>(window.AlertView).Any(text => text.Name == "AlertTitle" && text.Text == UrgentMessage),
                theme + " received alert uses the requested urgent-mail message");
            AssertUrgentMessageFits(Descendants<TextBlock>(window.AlertView).Single(text => text.Name == "AlertTitle"),
                theme + " notification urgent-mail message is visible without clipping");
            Capture(window.AlertView, Path.Combine(output, "alert-" + theme + ".png"));

            if (theme == "tech")
            {
                string alertId = remote.GetLastSent().Id;
                window.Show();
                SelectTheme(window, "cosmos");
                await Task.Delay(350);
                Check(window.AlertView != null && !window.AlertView.IsClosed && window.AlertView.IsVisible,
                    "switching theme retains the pending notification");
                Check(remote.GetLastSent().Id == alertId && remote.GetLastSent().SeenIds.Length == 0,
                    "theme switch does not acknowledge a pending notification");
                SelectTheme(window, "tech");
                window.Hide();
            }
            Click(window.AlertView.SeenButton);
            await WaitUntil(() => remote.GetLastSent().SeenIds.Length == 1, theme + " notification button returns human confirmation");
            await WaitUntil(() => window.AlertView.IsClosed, theme + " confirmed notification closes after its exit transition");
            await Task.Delay(1800);
            Check(window.AlertDisplayCount == displayCount + 1, theme + " retried packets do not produce duplicate notifications");
            await VerifyAutomaticDismiss(window, remote, theme);
            window.Show();
            await Task.Delay(250);

            var settings = new SettingsWindow(config, preferences, profile, () => {});
            try
            {
                settings.Show();
                await Task.Delay(200);
                var initialName = Descendants<TextBox>(settings).Single(item => item.Name == "DisplayName");
                Check(initialName.Text == preferences.DisplayName, theme + " settings initializes the current display name");
                Rect nameCharacter = initialName.GetRectFromCharacterIndex(0);
                Console.WriteLine(theme + " name field text length=" + initialName.Text.Length +
                    ", control=" + initialName.ActualWidth + "x" + initialName.ActualHeight +
                    ", first character=" + nameCharacter);
                Check(!nameCharacter.IsEmpty && nameCharacter.Height > 0 &&
                    nameCharacter.Bottom <= initialName.ActualHeight + 1,
                    theme + " display name has a visible character layout within its field");
                Capture(settings, Path.Combine(output, "settings-" + theme + ".png"));
                Check(settings.ActualWidth > 0 && settings.ActualHeight > 0, theme + " settings view renders");
                string beforePreview = File.ReadAllText(profile);
                string unsavedName = "未保存的测试名称";
                Descendants<TextBox>(settings).Single(item => item.Name == "DisplayName").Text = unsavedName;
                var sound = Descendants<CheckBox>(settings).Single(item => item.Name == "SoundOption");
                bool changedSound = sound.IsChecked != true;
                sound.IsChecked = changedSound;
                string alternateTheme = theme == "tech" ? "CosmosTheme" : "TechTheme";
                Click(Descendants<Button>(settings).Single(item => item.Name == alternateTheme));
                Check(Descendants<TextBox>(settings).Single(item => item.Name == "DisplayName").Text == unsavedName,
                    theme + " theme preview retains the unsaved display name");
                Check(Descendants<CheckBox>(settings).Single(item => item.Name == "SoundOption").IsChecked == changedSound,
                    theme + " theme preview retains the unsaved sound option");
                Check((string)Descendants<Button>(settings).Single(item => item.Name == alternateTheme).Tag == "selected",
                    theme + " settings preview selects the requested theme");
                Check(File.ReadAllText(profile) == beforePreview && window.ThemeKey == theme,
                    theme + " unsaved preview leaves persisted and main-window preferences intact");
            }
            finally { settings.Close(); }
            var bindingTeam = new TeamConfig {
                RoomName = config.RoomName, RoomKey = config.RoomKey, BroadcastAddress = "192.168.20.255",
                Port = config.Port, RelayUrl = "http://127.0.0.1:1/mail-reminder/"
            };
            using (var bindingRelay = new RelayClient(local, bindingTeam))
            {
                var binding = new PhoneBindingWindow(bindingRelay, theme, true) { Owner = window };
                try
                {
                    binding.Show();
                    await Task.Delay(150);
                    var subnet = Descendants<TextBlock>(binding).Single(text => text.Name == "SubnetText");
                    var installUrl = Descendants<TextBox>(binding).Single(text => text.Name == "InstallUrl");
                    Check(subnet.IsVisible && !string.IsNullOrWhiteSpace(subnet.Text) && subnet.Text.Contains(bindingRelay.Subnet),
                        theme + " phone-binding window renders the desktop subnet");
                    Check(installUrl.IsVisible && installUrl.Text == bindingRelay.PublicUrl,
                        theme + " phone-binding window renders its installation URL without starting relay traffic");
                    Capture(binding, Path.Combine(output, "phone-binding-" + theme + ".png"));
                }
                finally { binding.Close(); }
                Check(binding.IsClosed, theme + " phone-binding window closes normally");
            }
            var floating = new FloatingWindow(() => window.Send(false), () => {}, theme, true);
            try
            {
                floating.Show();
                floating.SetState(true, 0);
                await Task.Delay(100);
                Capture(floating, Path.Combine(output, "floating-" + theme + ".png"));
                Check(!floating.MotionEnabled, theme + " floating control accepts reduced motion");
                Check(!Descendants<TextBlock>(floating).Any(text => text.IsVisible && text.Text.Contains("双击")),
                    theme + " idle ball surface has no double-click instruction text");
                var ball = Descendants<Button>(floating).Single(item => item.Name == "BallSurface");
                var menu = ball.ContextMenu;
                menu.PlacementTarget = ball;
                menu.IsOpen = true;
                await Task.Delay(100);
                Check(menu.IsOpen && menu.Items.OfType<MenuItem>().Count() == 3,
                    theme + " floating-ball context menu opens with its three actions");
                menu.ApplyTemplate();
                menu.UpdateLayout();
                var menuBrush = menu.FindResource("MenuBrush") as SolidColorBrush;
                Check(menuBrush != null && menuBrush.Color.A == 255 && Descendants<Border>(menu).Any(border =>
                    border.ActualWidth > 0 && border.Background is SolidColorBrush &&
                    ((SolidColorBrush)border.Background).Color == menuBrush.Color),
                    theme + " context menu renders its opaque theme surface");
                menu.IsOpen = false;
                await VerifyDocking(floating, theme, output);
                await WaitUntil(() => window.SendButton.IsEnabled, theme + " floating-ball test waits for the shared send cooldown");
                string priorAlertId = local.GetLastSent().Id;
                floating.TriggerBallClick(1);
                await Task.Delay(150);
                Check(local.GetLastSent().Id == priorAlertId, theme + " floating ball single click does not send a warning");
                floating.TriggerBallClick(2);
                Check(local.GetLastSent().Id != priorAlertId, theme + " floating ball double click sends through the main warning action");
                await WaitUntil(() => local.GetLastSent().ReceivedIds.Length == 1, theme + " floating-ball warning reaches the other node and returns ACK");
            }
            finally { floating.Close(); }
        }

        private static async Task VerifyDocking(FloatingWindow floating, string theme, string output)
        {
            floating.DockToRight();
            Check(floating.IsDocked && !floating.IsDockExpanded, theme + " dock enters the collapsed state");
            AssertDockPose(floating, false, theme + " reduced-motion dock lands at the half-hidden position immediately");
            Check(CountAnimatedObjects(floating) == 0, theme + " reduced-motion dock has no animation clocks");
            floating.ExpandFromDock();
            AssertDockPose(floating, true, theme + " reduced-motion expansion lands fully inside the work area immediately");
            floating.CollapseToDock();
            AssertDockPose(floating, false, theme + " reduced-motion collapse restores the half-hidden position immediately");
            floating.ReleaseFromDock();
            Check(!floating.IsDocked && !floating.IsDockExpanded,
                theme + " releasing the dock clears both docking states");

            floating.ApplyTheme(theme, false);
            floating.Left = SystemParameters.WorkArea.Right - floating.Width - 50;
            double fromLeft = floating.Left;
            var scale = (ScaleTransform)((UIElement)floating.Content).RenderTransform;
            if (floating.MotionEnabled)
                await ObserveDockTransition(floating, scale, theme, output);
            else
            {
                Console.WriteLine("NOT RUN " + theme + " dock animation because Windows animations are disabled");
                floating.DockToRight();
            }
            await Task.Delay(340);
            AssertDockPose(floating, false, theme + " animated dock settles at the half-hidden position");
            Check(floating.Left > fromLeft && !floating.IsMotionRunning,
                theme + " collapsed dock moves right and pauses ambient effects");
            Capture(floating, Path.Combine(output, "floating-docked-" + theme + ".png"));

            floating.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            Check(floating.IsDockExpanded, theme + " mouse-enter event expands the dock");
            await Task.Delay(340);
            AssertDockPose(floating, true, theme + " hover expansion restores full size inside the work area");
            Point pointer = Mouse.GetPosition(floating);
            bool pointerInside = pointer.X >= 0 && pointer.X <= floating.Width && pointer.Y >= 0 && pointer.Y <= floating.Height;
            if (!pointerInside)
            {
                floating.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                await Task.Delay(400);
                Check(floating.IsDockExpanded, theme + " mouse leave keeps the ball expanded during the collapse delay");
                await WaitUntil(() => !floating.IsDockExpanded, theme + " mouse leave collapses the ball after the scheduled delay");
                await Task.Delay(320);
            }
            else
            {
                Console.WriteLine("NOT RUN " + theme + " leave-delay check because the real pointer is inside the ball; desktop input remains untouched");
                floating.CollapseToDock();
                await Task.Delay(340);
            }
            AssertDockPose(floating, false, theme + " delayed collapse returns to the half-hidden pose");
            floating.ReleaseFromDock();
            Check(!floating.IsDocked && !floating.IsDockExpanded && Math.Abs(scale.ScaleX - 1) < 0.001,
                theme + " detaching from the dock restores full scale");
            floating.Left = SystemParameters.WorkArea.Right - floating.Width - 18;
            floating.ApplyTheme(theme, true);
        }

        private static void AssertDockPose(FloatingWindow floating, bool expanded, string name)
        {
            var scale = (ScaleTransform)((UIElement)floating.Content).RenderTransform;
            double expectedLeft = SystemParameters.WorkArea.Right - floating.Width * (expanded ? 1 : 0.5);
            double expectedScale = expanded ? 1 : 0.82;
            Check(Math.Abs(floating.Left - expectedLeft) < 1 && Math.Abs(scale.ScaleX - expectedScale) < 0.001 &&
                Math.Abs(scale.ScaleY - expectedScale) < 0.001 && floating.IsDockExpanded == expanded, name);
        }

        private sealed class DockSample
        {
            public string Phase;
            public long ElapsedMs;
            public double Left;
            public double BaseLeft;
            public double Scale;
            public double BaseScale;
            public bool LeftAnimated;
            public bool ScaleAnimated;
            public bool Docked;
            public bool Expanded;

            public override string ToString()
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0} t={1}ms Left={2:F6} baseLeft={3:F6} scale={4:F6} baseScale={5:F6} leftClock={6} scaleClock={7} docked={8} expanded={9}",
                    Phase, ElapsedMs, Left, BaseLeft, Scale, BaseScale, LeftAnimated, ScaleAnimated, Docked, Expanded);
            }
        }

        private static async Task ObserveDockTransition(FloatingWindow floating, ScaleTransform scale, string theme, string output)
        {
            var samples = new List<DockSample>();
            var clock = Stopwatch.StartNew();
            Action<string> record = phase => samples.Add(new DockSample {
                Phase = phase, ElapsedMs = clock.ElapsedMilliseconds, Left = floating.Left,
                BaseLeft = (double)floating.GetAnimationBaseValue(Window.LeftProperty), Scale = scale.ScaleX,
                BaseScale = (double)scale.GetAnimationBaseValue(ScaleTransform.ScaleXProperty),
                LeftAnimated = floating.HasAnimatedProperties, ScaleAnimated = scale.HasAnimatedProperties,
                Docked = floating.IsDocked, Expanded = floating.IsDockExpanded
            });
            EventHandler frame = (sender, args) => record("frame");
            record("before");
            CompositionTarget.Rendering += frame;
            try
            {
                floating.DockToRight();
                record("after-call");
                await Task.Delay(450);
                record("settled");
            }
            finally
            {
                CompositionTarget.Rendering -= frame;
                File.WriteAllLines(Path.Combine(output, "dock-trace-" + theme + ".txt"), samples.Select(sample => sample.ToString()), Encoding.UTF8);
            }
            Console.WriteLine(theme + " dock trace samples=" + samples.Count);
            Console.WriteLine(samples.First().ToString());
            Console.WriteLine(samples[1].ToString());
            Console.WriteLine(samples.Last().ToString());
            var before = samples.First();
            var afterCall = samples[1];
            var settled = samples.Last();
            double targetLeft = SystemParameters.WorkArea.Right - floating.Width * 0.5;
            Check(afterCall.LeftAnimated && afterCall.ScaleAnimated, theme + " dock starts real position and scale animations");
            Check(Math.Abs(afterCall.Left - before.Left) < 1 && Math.Abs(afterCall.Scale - before.Scale) < 0.000001 &&
                Math.Abs(afterCall.BaseLeft - before.Left) < 1 && Math.Abs(afterCall.BaseScale - before.Scale) < 0.000001,
                theme + " dock retains its starting pose before the first animation frame");
            Check(samples.Any(sample => sample.Phase == "frame" && sample.Left > before.Left + 0.01 && sample.Left < targetLeft - 0.01),
                theme + " rendered dock frames include an intermediate position");
            Check(samples.Any(sample => sample.Phase == "frame" && sample.Scale < before.Scale - 0.000001 && sample.Scale > 0.820001),
                theme + " rendered dock frames include an intermediate scale");
            Check(Math.Abs(settled.Left - targetLeft) < 1 && Math.Abs(settled.Scale - 0.82) < 0.000001,
                theme + " dock reaches its position and scale targets independently of frame sampling");
            Check(!settled.LeftAnimated && !settled.ScaleAnimated,
                theme + " completed dock transition removes its animation clocks");
        }

        private static void Click(Button button)
        {
            if (button.IsEnabled) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }

        private static async Task VerifyLiveNames(MainWindow window, LanNode local, LanNode remote, Preferences prefs, string profile)
        {
            const string ownName = "张三-研发";
            var completion = new TaskCompletionSource<bool>();
            _ = window.Dispatcher.BeginInvoke(new Action(async () => {
                SettingsWindow dialog = null;
                try
                {
                    await WaitUntil(() => Application.Current.Windows.OfType<SettingsWindow>().Any(item => item.IsVisible),
                        "rename opens the real modal settings window");
                    dialog = Application.Current.Windows.OfType<SettingsWindow>().Single(item => item.IsVisible);
                    var startup = Descendants<CheckBox>(dialog).Single(item => item.Name == "StartupOption");
                    bool? existingStartup = startup.IsChecked;
                    Descendants<TextBox>(dialog).Single(item => item.Name == "DisplayName").Text = ownName;
                    Check(startup.IsChecked == existingStartup, "rename preserves the startup option");
                    Click(Descendants<Button>(dialog).Single(item => item.Name == "SaveButton"));
                    completion.SetResult(true);
                }
                catch (Exception ex)
                {
                    if (dialog != null && !dialog.IsClosed) dialog.Close();
                    completion.TrySetException(ex);
                }
            }));
            Click(Descendants<Button>(window).Single(item => item.Name == "SettingsButton"));
            await completion.Task;
            await WaitUntil(() => local.DisplayName == ownName && remote.GetPeers().Any(peer => peer.Name == ownName),
                "Chinese rename through settings reaches the other UDP node immediately");
            Check(prefs.DisplayName == ownName && Preferences.Load(profile).DisplayName == ownName,
                "Chinese display name persists without restarting");
            Check(local.GetPeers().Length == 1, "transport peer snapshots continue to exclude the local node");
            remote.UpdateDisplayName("李四-办公室");
            await WaitUntil(() => local.GetPeers().Any(peer => peer.Name == "李四-办公室") &&
                Descendants<ItemsControl>(window).Single(item => item.Name == "PeerList").Items.OfType<PeerRow>().Any(row => row.Name.Contains("李四-办公室")),
                "remote Chinese rename appears in the live main-window list");
            AssertSelfAndPeerListed(window, ownName, remote.DisplayName, "live rename");
        }

        private static void AssertSelfAndPeerListed(MainWindow window, string ownName, string peerName, string context)
        {
            var rows = Descendants<ItemsControl>(window).Single(item => item.Name == "PeerList").Items.OfType<PeerRow>().ToArray();
            string online = Descendants<TextBlock>(window).Single(text => text.Name == "OnlineCount").Text;
            Check(rows.Any(row => row.Name.Contains(ownName)) && rows.Any(row => row.Name.Contains(peerName)) &&
                rows.Length == 2 && online == "2", context + " online list and count include self plus the other node");
        }

        private static void AssertUrgentMessageFits(TextBlock text, string name)
        {
            double availableWidth = text.ActualWidth - text.Padding.Left - text.Padding.Right;
            double availableHeight = text.ActualHeight - text.Padding.Top - text.Padding.Bottom;
            var dpi = VisualTreeHelper.GetDpi(text);
            var formatted = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture,
                text.FlowDirection, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                text.FontSize, Brushes.White, dpi.PixelsPerDip);
            if (text.TextWrapping != TextWrapping.NoWrap) formatted.MaxTextWidth = Math.Max(1, availableWidth);
            if (!double.IsNaN(text.LineHeight)) formatted.LineHeight = text.LineHeight;
            bool withinVisibleBounds = true;
            DependencyObject ancestor = VisualTreeHelper.GetParent(text);
            while (ancestor != null)
            {
                var element = ancestor as FrameworkElement;
                if (element != null && (element.ClipToBounds || element is Window))
                {
                    Rect bounds = text.TransformToAncestor(element).TransformBounds(new Rect(text.RenderSize));
                    if (bounds.Left < -2 || bounds.Top < -2 || bounds.Right > element.ActualWidth + 2 || bounds.Bottom > element.ActualHeight + 2)
                        withinVisibleBounds = false;
                }
                ancestor = VisualTreeHelper.GetParent(ancestor);
            }
            Console.WriteLine(name + " allocated=" + availableWidth.ToString("F2") + "x" + availableHeight.ToString("F2") +
                ", text=" + formatted.WidthIncludingTrailingWhitespace.ToString("F2") + "x" + formatted.Height.ToString("F2"));
            Check(text.IsVisible && withinVisibleBounds && availableWidth > 0 && availableHeight > 0 &&
                formatted.WidthIncludingTrailingWhitespace <= availableWidth + 2 && formatted.Height <= availableHeight + 2, name);
        }

        private static async Task VerifyAutomaticDismiss(MainWindow window, LanNode remote, string theme)
        {
            int before = window.AlertDisplayCount;
            remote.SendAlert(false);
            await WaitUntil(() => window.AlertView != null && !window.AlertView.IsClosed && window.AlertView.IsVisible,
                theme + " timed urgent-mail notification opens");
            var popup = window.AlertView;
            var elapsed = Stopwatch.StartNew();
            await Task.Delay(4500);
            Check(!popup.IsClosed && popup.IsVisible, theme + " notification remains visible before its five-second deadline");
            await WaitUntil(() => popup.IsClosed, theme + " notification dismisses automatically after five seconds");
            Check(elapsed.ElapsedMilliseconds >= 4800 && elapsed.ElapsedMilliseconds <= 6500,
                theme + " automatic dismissal stays within the five-second timing tolerance");
            Check(remote.GetLastSent().ReceivedIds.Length == 1 && remote.GetLastSent().SeenIds.Length == 0,
                theme + " automatic dismissal preserves delivery ACK without a human seen receipt");
            await Task.Delay(800);
            Check(window.AlertDisplayCount == before + 1 && window.AlertView.IsClosed,
                theme + " network retransmissions do not reopen the automatically closed notification");
        }

        private static async Task VerifyNotificationTimerReset(MainWindow window, LanNode remote)
        {
            int before = window.AlertDisplayCount;
            remote.SendAlert(false);
            await WaitUntil(() => window.AlertView != null && !window.AlertView.IsClosed && window.AlertView.IsVisible,
                "reset-timer notification opens");
            var popup = window.AlertView;
            await Task.Delay(2800);
            remote.SendAlert(false);
            await WaitUntil(() => window.AlertDisplayCount == before + 2, "a new reminder ID arrives in the existing notification");
            var elapsed = Stopwatch.StartNew();
            Check(ReferenceEquals(window.AlertView, popup), "a new reminder reuses the visible notification");
            await Task.Delay(2800);
            Check(!popup.IsClosed && popup.IsVisible, "a new reminder resets the five-second timer beyond the first reminder deadline");
            string opposite = window.ThemeKey == "tech" ? "cosmos" : "tech";
            window.Show();
            SelectTheme(window, opposite);
            await WaitUntil(() => popup.IsClosed, "theme switching preserves the remaining five-second deadline");
            Check(elapsed.ElapsedMilliseconds >= 4800 && elapsed.ElapsedMilliseconds <= 6500,
                "theme switching does not restart the five-second timer");
            Check(remote.GetLastSent().SeenIds.Length == 0, "reset-timer automatic closure sends no human acknowledgement");
            await Task.Delay(800);
            Check(window.AlertDisplayCount == before + 2 && window.AlertView.IsClosed,
                "duplicate UDP retries neither extend nor reopen the closed notification");
            SelectTheme(window, "cosmos");
        }

        private static void SelectTheme(MainWindow window, string theme)
        {
            string name = theme == "cosmos" ? "CosmosTheme" : "TechTheme";
            var button = Descendants<Button>(window).Single(item => item.Name == name);
            Check(button.IsVisible && button.IsEnabled, theme + " theme button is available through the real main-window entry");
            Click(button);
        }

        private static async Task SetMotionThroughSettings(MainWindow window, bool reduce)
        {
            var completion = new TaskCompletionSource<bool>();
            _ = window.Dispatcher.BeginInvoke(new Action(async () => {
                SettingsWindow dialog = null;
                try
                {
                    await WaitUntil(() => Application.Current.Windows.OfType<SettingsWindow>().Any(item => item.IsVisible),
                        "main settings button opens the real modal settings window");
                    dialog = Application.Current.Windows.OfType<SettingsWindow>().Single(item => item.IsVisible);
                    var startup = Descendants<CheckBox>(dialog).Single(item => item.Name == "StartupOption");
                    bool? originalStartup = startup.IsChecked;
                    Descendants<CheckBox>(dialog).Single(item => item.Name == "MotionOption").IsChecked = reduce;
                    Check(startup.IsChecked == originalStartup, "motion edit preserves the existing startup checkbox value");
                    Click(Descendants<Button>(dialog).Single(item => item.Name == "SaveButton"));
                    completion.SetResult(true);
                }
                catch (Exception ex)
                {
                    if (dialog != null && !dialog.IsClosed) dialog.Close();
                    completion.TrySetException(ex);
                }
            }));
            Click(Descendants<Button>(window).Single(item => item.Name == "SettingsButton"));
            await completion.Task;
            Check(window.MotionEnabled == (!reduce && SystemParameters.ClientAreaAnimation),
                "saving through the real settings entry applies the motion preference to the main window");
        }

        private static int FreePort()
        {
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        private static async Task WaitUntil(Func<bool> condition, string name)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition() && deadline.ElapsedMilliseconds < 8000) await Task.Delay(50);
            Check(condition(), name);
        }

        private static void Capture(Window window, string path)
        {
            window.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(window);
            int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
            int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T) yield return (T)root;
            if (!(root is Visual) && !(root is System.Windows.Media.Media3D.Visual3D)) yield break;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
        }

        private static int CountAnimatedObjects(DependencyObject root)
        {
            return CountAnimatedObjects(root, new HashSet<DependencyObject>());
        }

        private static int CountAnimatedObjects(DependencyObject root, HashSet<DependencyObject> visited)
        {
            if (root == null || !visited.Add(root)) return 0;
            int count = 0;
            var element = root as UIElement;
            var animatable = root as Animatable;
            if ((element != null && element.HasAnimatedProperties) || (animatable != null && animatable.HasAnimatedProperties)) count++;
            var values = root.GetLocalValueEnumerator();
            while (values.MoveNext())
            {
                var child = root.GetValue(values.Current.Property) as DependencyObject;
                if (child != null) count += CountAnimatedObjects(child, visited);
            }
            if (root is Visual || root is System.Windows.Media.Media3D.Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                    count += CountAnimatedObjects(VisualTreeHelper.GetChild(root, i), visited);
            return count;
        }

        private static async Task VerifyUpdateEntryWithoutTeam(string exePath, string output)
        {
            string folder = Path.Combine(output, "update-entry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string copy = Path.Combine(folder, Path.GetFileName(exePath));
            File.Copy(exePath, copy);
            File.Copy(exePath + ".config", copy + ".config");
            Check(!File.Exists(Path.Combine(folder, "team.json")), "update-entry fixture has no team configuration");
            foreach (string arguments in new[] {
                "--apply-update", "--apply-update \"" + Path.Combine(folder, "missing-plan.json") + "\""
            })
            {
                using (var process = Process.Start(new ProcessStartInfo(copy, arguments) {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true, RedirectStandardOutput = true
                }))
                {
                    try
                    {
                        await WaitUntil(() => process.HasExited, "update entry exits without a team file or a WPF prompt");
                        int expected = arguments == "--apply-update" ? 2 : 1;
                        Check(process.ExitCode == expected, "update entry returns its own validation result before normal application startup");
                        Check(!string.IsNullOrWhiteSpace(process.StandardError.ReadToEnd()), "update-entry validation preserves a diagnostic message");
                    }
                    finally
                    {
                        if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
                    }
                }
            }
        }

        private static async Task VerifyPortableEntry(string exePath)
        {
            using (var process = Process.Start(new ProcessStartInfo(exePath, "--background --local-only") {
                UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden
            }))
            {
                uint uiThread = 0;
                try
                {
                    await WaitUntil(() => {
                        EnumWindows((hwnd, unused) => {
                            uint owner;
                            uint thread = GetWindowThreadProcessId(hwnd, out owner);
                            if (owner != (uint)process.Id) return true;
                            var title = new StringBuilder(100);
                            GetWindowText(hwnd, title, title.Capacity);
                            if (title.ToString() == "邮件提醒助手") { uiThread = thread; return false; }
                            return true;
                        }, IntPtr.Zero);
                        return uiThread != 0;
                    }, "1.3 portable EXE starts through the normal WPF entry point in local-only mode");
                    await Task.Delay(600);
                    bool occupied = false;
                    using (var probe = new UdpClient(AddressFamily.InterNetwork))
                    {
                        probe.ExclusiveAddressUse = true;
                        try { probe.Client.Bind(new IPEndPoint(IPAddress.Any, 45820)); }
                        catch (SocketException ex) { occupied = ex.SocketErrorCode == SocketError.AddressAlreadyInUse; }
                    }
                    Check(occupied, "portable EXE loads bundled team configuration and listens");
                    Check(PostThreadMessage(uiThread, 0x0012, IntPtr.Zero, IntPtr.Zero), "portable EXE receives graceful message-loop shutdown");
                    await WaitUntil(() => process.HasExited, "portable EXE releases windows and transport on shutdown");
                    Check(process.ExitCode == 0, "portable EXE normal startup and disposal return exit 0");
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        if (uiThread != 0) PostThreadMessage(uiThread, 0x0012, IntPtr.Zero, IntPtr.Zero);
                        if (!process.WaitForExit(3000)) process.Kill();
                    }
                }
            }
        }
    }
}
