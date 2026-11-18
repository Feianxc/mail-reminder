using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace OfficeSignal
{
    internal sealed class PeerRow
    {
        public string Name
        {
            get; set;
        }
        public string State
        {
            get; set;
        }
        // 头像首字，供列表模板显示
        public string Initial
        {
            get
            {
                string text = (Name ?? "").Trim();
                return text.Length == 0 ? "·" : System.Globalization.StringInfo.GetNextTextElement(text).ToUpperInvariant();
            }
        }
        // 回执状态的视觉分级：idle / wait / got / seen / miss / off
        public string Tone
        {
            get
            {
                string text = State ?? "";
                if (text.Contains("已离线"))
                    return "off";
                if (text.StartsWith("已知晓", StringComparison.Ordinal))
                    return "seen";
                if (text.StartsWith("软件已收到", StringComparison.Ordinal))
                    return "got";
                if (text.StartsWith("未收到", StringComparison.Ordinal))
                    return "miss";
                return text.StartsWith("等待", StringComparison.Ordinal) ? "wait" : "idle";
            }
        }
    }

    internal class ThemedWindow : Window
    {
        protected Grid view;
        protected ThemeMotion motion;
        protected string currentTheme;
        protected bool reduceMotion;
        internal bool MotionEnabled
        {
            get
            {
                return ThemeView.CanAnimate(reduceMotion);
            }
        }
        internal bool IsMotionRunning
        {
            get
            {
                return motion != null && motion.IsRunning;
            }
        }
        internal bool IsClosed
        {
            get; private set;
        }
        public ThemedWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // 所有窗口自绘圆角与投影，由视图本身负责底色
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            IsVisibleChanged += (s, e) => UpdateMotion();
            StateChanged += (s, e) => UpdateMotion();
            Closed += (s, e) => { IsClosed = true; if (motion != null) motion.Dispose(); };
        }
        protected void SetView(string file, string key, bool reduced)
        {
            if (motion != null)
                motion.Dispose();
            currentTheme = key;
            reduceMotion = reduced;
            view = ThemeView.Load(file, key);
            Content = new Viewbox { Child = view, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
            Rect work = SystemParameters.WorkArea;
            double fit = Math.Min(1, Math.Min((work.Width - 32) / view.Width, (work.Height - 32) / view.Height));
            Width = Math.Round(view.Width * fit);
            Height = Math.Round(view.Height * fit);
            if (IsLoaded)
            {
                Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
                Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
            }
            motion = new ThemeMotion(view, () => MotionEnabled);
            var drag = view.FindName("DragBar") as FrameworkElement;
            if (drag != null)
                drag.MouseLeftButtonDown += (s, e) =>
                {
                    var source = e.OriginalSource as DependencyObject;
                    while (source != null && source != drag)
                    {
                        if (source is Button)
                            return;
                        source = VisualTreeHelper.GetParent(source);
                    }
                    if (e.LeftButton == MouseButtonState.Pressed)
                        DragMove();
                };
            if (IsLoaded)
            {
                view.UpdateLayout();
                UpdateMotion();
                motion.Enter();
            }
            else
                view.Loaded += (s, e) => { UpdateMotion(); motion.Enter(); };
        }
        protected virtual void UpdateMotion()
        {
            if (motion != null)
                motion.Update(IsVisible && WindowState != WindowState.Minimized && !IsClosed, currentTheme == "cosmos");
        }
        protected T Find<T>(string name) where T : class
        {
            return ThemeView.Find<T>(view, name);
        }
        protected void Text(string name, string value)
        {
            Find<TextBlock>(name).Text = value;
        }
        protected void Click(string name, Action action)
        {
            Find<Button>(name).Click += (s, e) => action();
        }
        protected void ThemeSelection(string key)
        {
            var tech = view.FindName("TechTheme") as Button;
            var cosmos = view.FindName("CosmosTheme") as Button;
            if (tech != null)
                tech.Tag = key == "tech" ? "selected" : null;
            if (cosmos != null)
                cosmos.Tag = key == "cosmos" ? "selected" : null;
        }
    }

    internal sealed class MainWindow : ThemedWindow, IDisposable
    {
        private readonly TeamConfig team;
        private readonly Preferences prefs;
        private readonly string settingsPath;
        private readonly LanNode node;
        private readonly bool background;
        private readonly Forms.NotifyIcon tray;
        private readonly DispatcherTimer timer;
        private readonly FloatingWindow floating;
        private System.Drawing.Icon trayIcon;
        private HwndSource nativeSource;
        private bool registered;
        private bool started;
        private bool quitting;
        private bool disposed;
        private string lastError;
        private string lastPeerSignature;
        private RelayClient relay;
        private DateTime nextSendUtc = DateTime.MinValue;
        internal Button SendButton
        {
            get
            {
                return Find<Button>("SendButton");
            }
        }
        internal AlertWindow AlertView;
        internal int AlertDisplayCount;
        internal string ReceiptText
        {
            get
            {
                return Find<TextBlock>("ReceiptSummary").Text;
            }
        }
        internal string ThemeKey
        {
            get
            {
                return prefs.ThemeKey;
            }
        }

        public MainWindow(TeamConfig team, Preferences prefs, string settingsPath, LanNode node, bool background)
        {
            this.team = team;
            this.prefs = prefs;
            this.settingsPath = settingsPath;
            this.node = node;
            this.background = background;
            Title = "邮件提醒助手";
            trayIcon = ThemeView.Icon(prefs.ThemeKey);
            Icon = Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "邮件提醒助手", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("收到一封加急邮件，请立刻处理 · 一键提醒", null, (s, e) => Send(false));
            menu.Items.Add("打开面板", null, (s, e) => OpenPanel());
            menu.Items.Add("设置", null, (s, e) => OpenSettings());
            menu.Items.Add("手机绑定", null, (s, e) => OpenPhoneBinding());
            menu.Items.Add("检查更新", null, (s, e) => CheckOnlineUpdate());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Quit());
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => OpenPanel();
            tray.BalloonTipClicked += (s, e) => { if (AlertView != null && !AlertView.IsClosed) AlertView.Show(); else OpenPanel(); };
            floating = new FloatingWindow(() => Send(false), OpenPanel, prefs.ThemeKey, prefs.ReduceMotion, Quit);
            LoadTheme();
            node.AlertReceived += OnAlert;
            node.Changed += OnChanged;
            node.Error += OnError;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (s, e) => RefreshState();
            SourceInitialized += InitializeNative;
            Loaded += (s, e) =>
            {
                try
                {
                    node.Start();
                    started = true;
                    if (!string.IsNullOrWhiteSpace(team.RelayUrl)) StartRelay();
                }
                catch (Exception ex) { lastError = "通信启动失败 · " + ex.Message; Log.Write(ex.ToString()); }
                timer.Start();
                RefreshState();
                if (prefs.FloatingButton)
                    floating.Show();
                if (background)
                    Hide();
            };
            Closed += (s, e) => Dispose();
        }
        private void LoadTheme()
        {
            lastPeerSignature = null;
            SetView(prefs.ThemeKey == "cosmos" ? "CosmosMain.xaml" : "TechMain.xaml", prefs.ThemeKey, prefs.ReduceMotion);
            Click("SendButton", () => Send(false));
            Click("TestButton", () => Send(true));
            Click("TechTheme", () => ApplyTheme("tech"));
            Click("CosmosTheme", () => ApplyTheme("cosmos"));
            Click("SettingsButton", OpenSettings);
            Click("CloseButton", Hide);
            Click("MinimizeButton", () => WindowState = WindowState.Minimized);
            Text("RoomName", team.RoomName);
            ThemeSelection(prefs.ThemeKey);
            RefreshState();
            var actionTitle = Find<TextBlock>("ActionTitle");
            actionTitle.FontSize = 20;
            actionTitle.TextWrapping = TextWrapping.Wrap;
            actionTitle.TextTrimming = TextTrimming.None;
            actionTitle.MaxWidth = 180;
            // 冷却中切换主题时，让新界面的冷却环接着走
            double cooling = (nextSendUtc - DateTime.UtcNow).TotalSeconds;
            if (cooling > 0)
                motion.Cooldown(cooling, 5);
        }
        internal void ApplyTheme(string key)
        {
            if (key != "tech" && key != "cosmos")
                throw new ArgumentException("主题不存在。");
            if (prefs.ThemeKey == key)
                return;
            string previous = prefs.ThemeKey;
            prefs.ThemeKey = key;
            try
            {
                prefs.Save(settingsPath);
            }
            catch { prefs.ThemeKey = previous; throw; }
            LoadTheme();
            floating.ApplyTheme(key, prefs.ReduceMotion);
            if (AlertView != null && !AlertView.IsClosed)
                AlertView.ApplyTheme(key, prefs.ReduceMotion);
            var previousIcon = trayIcon;
            trayIcon = ThemeView.Icon(key);
            tray.Icon = trayIcon;
            Icon = Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            previousIcon.Dispose();
        }
        internal void ApplyMotionPreference(bool reduced)
        {
            bool previous = prefs.ReduceMotion;
            prefs.ReduceMotion = reduced;
            try
            {
                prefs.Save(settingsPath);
            }
            catch { prefs.ReduceMotion = previous; throw; }
            reduceMotion = reduced;
            UpdateMotion();
            floating.ApplyTheme(prefs.ThemeKey, reduced);
            if (AlertView != null && !AlertView.IsClosed)
                AlertView.ApplyTheme(prefs.ThemeKey, reduced);
        }
        private void InitializeNative(object sender, EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            nativeSource = HwndSource.FromHwnd(handle);
            nativeSource.AddHook(WindowMessage);
            registered = RegisterHotKey(handle, 1, 0x4000 | 0x0002 | 0x0001, (int)Forms.Keys.F9);
        }
        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == 0x0312 && wparam.ToInt32() == 1)
            {
                Send(false);
                handled = true;
            }
            return IntPtr.Zero;
        }
        private void Dispatch(Action action)
        {
            if (disposed || Dispatcher.HasShutdownStarted)
                return;
            Dispatcher.BeginInvoke(new Action(() => { if (!disposed) action(); }));
        }
        private void OnAlert(IncomingAlert alert)
        {
            Dispatch(() => DisplayAlert(alert));
        }
        private void OnChanged()
        {
            Dispatch(RefreshState);
        }
        private void OnError(string error)
        {
            Log.Write(error);
            Dispatch(() => { lastError = "通信提示 · " + error; RefreshState(); });
        }
        internal void DisplayAlert(IncomingAlert alert)
        {
            AlertDisplayCount++;
            if (AlertView == null || AlertView.IsClosed || AlertView.IsClosing)
                AlertView = new AlertWindow(id => { if (id != null) node.MarkSeen(id); }, prefs.ThemeKey, prefs.ReduceMotion);
            AlertView.Add(alert);
            AlertView.Show();
            tray.ShowBalloonTip(5000, alert.IsTest ? "联机测试" : "收到一封加急邮件，请立刻处理", alert.SenderName + " 发来" + (alert.IsTest ? "测试提醒" : "邮件提醒助手"), Forms.ToolTipIcon.Info);
            if (prefs.Sound)
                SystemSounds.Exclamation.Play();
        }
        internal void Send(bool isTest)
        {
            if (!started || DateTime.UtcNow < nextSendUtc)
                return;
            try
            {
                node.SendAlert(isTest);
                nextSendUtc = DateTime.UtcNow.AddSeconds(5);
                lastError = null;
                RefreshState();
                motion.Signal();
                motion.Cooldown(5, 5);
            }
            catch (Exception ex) { lastError = "提醒发送失败 · " + ex.Message; Log.Write(ex.ToString()); RefreshState(); }
        }
        internal void RefreshState()
        {
            if (disposed || view == null)
                return;
            var peers = node.GetPeers();
            var sent = node.GetLastSent();
            Text("OnlineCount", (peers.Length + (started ? 1 : 0)).ToString());
            Text("ConnectionText", !started ? "通信准备中" : peers.Length == 0 ? "本机已在线，等待同事" : "已发现在线设备");
            Find<TextBlock>("EmptyPeers").Visibility = Visibility.Collapsed;
            int cooldown = (int)Math.Ceiling((nextSendUtc - DateTime.UtcNow).TotalSeconds);
            SendButton.IsEnabled = started && cooldown <= 0;
            Find<Button>("TestButton").IsEnabled = SendButton.IsEnabled;
            Text("ActionTitle", cooldown > 0 ? "已发出提醒" : "收到一封加急邮件，请立刻处理");
            Text("ActionHint", cooldown > 0 ? cooldown + " 秒后可再次发送" : "发送给全部在线同事");
            Text("HotkeyText", registered ? "Ctrl + Alt + F9" : "快捷键被占用，请使用按钮");
            if (floating != null)
                floating.SetState(started && cooldown <= 0, cooldown);
            if (sent == null)
            {
                Text("ReceiptSummary", "尚未发送提醒");
                Text("ReceivedCount", "—");
                Text("SeenCount", "—");
            }
            else
            {
                int expected = Math.Max(sent.ExpectedPeers.Length, sent.ReceivedIds.Length);
                string summary = (sent.IsTest ? "联机测试" : "收到一封加急邮件，请立刻处理") + "  " + sent.SentUtc.ToLocalTime().ToString("HH:mm:ss") + "\n";
                summary += expected == 0 ? "尚无同事回执，请检查网络。" : sent.ReceivedIds.Length + " / " + expected + " 台收到，" + sent.SeenIds.Length + " 人已知晓";
                Text("ReceiptSummary", summary);
                Text("ReceivedCount", sent.ReceivedIds.Length.ToString());
                Text("SeenCount", sent.SeenIds.Length.ToString());
            }
            var rows = peers.Select(p => new PeerRow { Name = p.Name, State = PeerState(p.Id, sent, true) }).ToList();
            rows.Insert(0, new PeerRow { Name = prefs.DisplayName, State = started ? "本机 · 在线" : "本机 · 通信准备中" });
            if (sent != null)
                rows.AddRange(sent.ExpectedPeers.Where(p => !peers.Any(peer => peer.Id == p.Id)).Select(p => new PeerRow { Name = p.Name, State = PeerState(p.Id, sent, false) }));
            string signature = string.Join("\n", rows.Select(row => row.Name + "\t" + row.State));
            if (signature != lastPeerSignature)
            {
                Find<ItemsControl>("PeerList").ItemsSource = rows;
                lastPeerSignature = signature;
            }
            Text("NetworkText", lastError ?? team.BroadcastAddress + "  ·  关闭窗口后继续接收");
        }
        private static string PeerState(string id, SentAlert sent, bool online)
        {
            if (sent == null || !sent.ExpectedPeers.Any(p => p.Id == id))
                return "在线";
            string result = sent.SeenIds.Contains(id) ? "已知晓" : sent.ReceivedIds.Contains(id) ? "软件已收到" : DateTime.UtcNow - sent.SentUtc > TimeSpan.FromSeconds(13) ? "未收到回执" : "等待回执";
            return online ? result : result + " · 已离线";
        }
        private void OpenPanel()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
        private void OpenSettings()
        {
            var dialog = new SettingsWindow(team, prefs, settingsPath, () => DisplayAlert(new IncomingAlert { SenderName = "本机预览", SentUtc = DateTime.UtcNow, IsTest = true }));
            dialog.Owner = this;
            if (dialog.ShowDialog() == true)
            {
                node.UpdateDisplayName(prefs.DisplayName);
                LoadTheme();
                floating.ApplyTheme(prefs.ThemeKey, prefs.ReduceMotion);
                if (prefs.FloatingButton)
                    floating.Show();
                else
                    floating.Hide();
                if (AlertView != null && !AlertView.IsClosed)
                    AlertView.ApplyTheme(prefs.ThemeKey, prefs.ReduceMotion);
                var old = trayIcon;
                trayIcon = ThemeView.Icon(prefs.ThemeKey);
                tray.Icon = trayIcon;
                Icon = Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                old.Dispose();
            }
        }
        private void StartRelay()
        {
            if (relay != null || !started) return;
            try
            {
                relay = new RelayClient(node, team);
                relay.Start();
            }
            catch (Exception ex)
            {
                Log.Write("手机同步启动失败 · " + ex.Message);
                if (relay != null) relay.Dispose();
                relay = null;
            }
        }
        private void OpenPhoneBinding()
        {
            if (!started) { MessageBox.Show(this, "请先恢复桌面局域网通信。", "手机绑定"); return; }
            if (string.IsNullOrWhiteSpace(team.RelayUrl)) team.RelayUrl = "https://blog.feian.online/mail-reminder/";
            StartRelay();
            if (relay == null) { MessageBox.Show(this, "手机同步服务暂不可用，请稍后重试。", "手机绑定"); return; }
            var dialog = new PhoneBindingWindow(relay, prefs.ThemeKey, prefs.ReduceMotion) { Owner = this };
            dialog.ShowDialog();
        }
        private async void CheckOnlineUpdate()
        {
            try
            {
                string url = string.IsNullOrWhiteSpace(team.RelayUrl) ? "https://blog.feian.online/mail-reminder/" : team.RelayUrl;
                var info = await OnlineUpdate.CheckAsync(url, CancellationToken.None);
                if (!info.IsNewer)
                {
                    MessageBox.Show(this, "当前已经是最新版本 " + OnlineUpdate.CurrentVersion, "在线更新");
                    return;
                }
                if (MessageBox.Show(this, "发现新版本 " + info.Version + "\n" + info.Notes + "\n\n现在下载并更新？", "在线更新", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                var staged = await OnlineUpdate.DownloadAndStageAsync(info, CancellationToken.None);
                OnlineUpdate.LaunchInstaller(staged);
                Quit();
            }
            catch (Exception ex) { Log.Write(ex.ToString()); MessageBox.Show(this, "在线更新未完成。\n" + ex.Message, "在线更新"); }
        }
        internal void Quit()
        {
            quitting = true;
            Close();
        }
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!quitting)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            timer.Stop();
            if (relay != null) relay.Dispose();
            if (motion != null)
                motion.Dispose();
            node.AlertReceived -= OnAlert;
            node.Changed -= OnChanged;
            node.Error -= OnError;
            if (nativeSource != null)
            {
                if (registered)
                    UnregisterHotKey(nativeSource.Handle, 1);
                registered = false;
                nativeSource.RemoveHook(WindowMessage);
            }
            tray.Visible = false;
            tray.ContextMenuStrip.Dispose();
            tray.Dispose();
            trayIcon.Dispose();
            floating.Close();
            if (AlertView != null && !AlertView.IsClosed)
                AlertView.Close();
        }
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, int key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }

    internal class PassiveWindow : ThemedWindow
    {
        public PassiveWindow()
        {
            ShowActivated = false;
            Topmost = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (s, e) => { var handle = new WindowInteropHelper(this).Handle; SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x00000080); };
        }
        protected void Place(int offset)
        {
            Rect area = SystemParameters.WorkArea;
            Left = area.Right - Width - 18;
            Top = area.Bottom - Height - offset;
        }
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    }

    internal sealed class FloatingWindow : PassiveWindow
    {
        private readonly Action send;
        private readonly Action open;
        private readonly Action quit;
        private bool ready;
        private int cooldown;
        private bool dragReady;
        private bool dragging;
        private Point dragStart;
        private double dragLeft;
        private double dragTop;
        private readonly Stopwatch gestureClock = Stopwatch.StartNew();
        private long lastClickMs = -1000000;
        private Point lastClickPosition;
        private ScaleTransform dockScale;
        private readonly DispatcherTimer collapseTimer;
        private int dockTransitionVersion;
        internal bool IsDocked
        {
            get; private set;
        }
        internal bool IsDockExpanded
        {
            get; private set;
        }
        public FloatingWindow(Action send, Action open, string themeKey = "tech", bool reduceMotion = false, Action quit = null)
        {
            this.send = send;
            this.open = open;
            this.quit = quit ?? Close;
            Title = "警告悬浮球";
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            collapseTimer.Tick += (s, e) =>
            {
                collapseTimer.Stop();
                if (!IsDocked || dragReady)
                    return;
                Point pointer = Mouse.GetPosition(this);
                bool insideWindow = pointer.X >= 0 && pointer.X <= Width && pointer.Y >= 0 && pointer.Y <= Height;
                if (insideWindow || Find<Button>("BallSurface").ContextMenu.IsOpen)
                {
                    collapseTimer.Start();
                    return;
                }
                CollapseToDock();
            };
            MouseEnter += (s, e) => ExpandFromDock();
            MouseLeave += (s, e) => ScheduleDockCollapse();
            Closed += (s, e) => { collapseTimer.Stop(); StopDockAnimations(); };
            ApplyTheme(themeKey, reduceMotion);
            Loaded += (s, e) => Place(18);
        }
        internal void ApplyTheme(string key, bool reduced)
        {
            lastClickMs = -1000000;
            dragReady = false;
            StopDockAnimations();
            SetView(key == "cosmos" ? "CosmosFloating.xaml" : "TechFloating.xaml", key, reduced);
            dockScale = new ScaleTransform(1, 1);
            ((UIElement)Content).RenderTransform = dockScale;
            ((UIElement)Content).RenderTransformOrigin = new Point(0.5, 0.5);
            var ball = Find<Button>("BallSurface");
            ball.PreviewMouseLeftButtonDown += BallDown;
            ball.PreviewMouseMove += BallMove;
            ball.PreviewMouseLeftButtonUp += BallUp;
            ball.LostMouseCapture += (s, e) => dragReady = false;
            var menu = ThemeView.CreateMenu(view);
            ThemeView.AddMenuItem(menu, "发出警告", () => TriggerBallClick(2));
            ThemeView.AddMenuItem(menu, "打开主面板", open);
            ThemeView.AddMenuItem(menu, "退出系统", quit);
            ball.ContextMenu = menu;
            SetState(ready, cooldown);
            if (IsDocked)
                SetDockPose(IsDockExpanded, false);
        }
        public void SetState(bool enabled, int seconds)
        {
            // 冷却开始的那一刻启动悬浮球上的环形倒计时
            if (seconds > 0 && cooldown <= 0)
                motion.Cooldown(seconds, seconds);
            ready = enabled;
            cooldown = seconds;
            Text("ActionTitle", seconds > 0 ? seconds + "s" : "");
            Find<TextBlock>("ActionTitle").Visibility = seconds > 0 ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("BallSurface").Opacity = enabled ? 1 : 0.7;
        }
        internal void TriggerBallClick(int clickCount)
        {
            if (clickCount != 2 || !ready)
                return;
            send();
            motion.Signal();
        }
        private void BallDown(object sender, MouseButtonEventArgs e)
        {
            collapseTimer.Stop();
            StopDockAnimations();
            dragStart = PointToScreen(e.GetPosition(this));
            dragLeft = Left;
            dragTop = Top;
            dragReady = true;
            dragging = false;
            ((Button)sender).CaptureMouse();
            e.Handled = true;
        }
        private void BallMove(object sender, MouseEventArgs e)
        {
            if (!dragReady || e.LeftButton != MouseButtonState.Pressed)
                return;
            Point position = PointToScreen(e.GetPosition(this));
            var dpi = VisualTreeHelper.GetDpi(this);
            double dx = (position.X - dragStart.X) / dpi.DpiScaleX;
            double dy = (position.Y - dragStart.Y) / dpi.DpiScaleY;
            if (!dragging && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance)
                return;
            dragging = true;
            lastClickMs = -1000000;
            if (IsDocked)
                ReleaseFromDock();
            Rect area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, Math.Min(area.Right - Width * 0.5, dragLeft + dx));
            Top = Math.Max(area.Top, Math.Min(area.Bottom - Height, dragTop + dy));
            e.Handled = true;
        }
        private void BallUp(object sender, MouseButtonEventArgs e)
        {
            if (!dragReady)
                return;
            if (!dragging)
            {
                Point position = PointToScreen(e.GetPosition(this));
                long now = gestureClock.ElapsedMilliseconds;
                var size = Forms.SystemInformation.DoubleClickSize;
                bool twice = now - lastClickMs <= Forms.SystemInformation.DoubleClickTime &&
                    Math.Abs(position.X - lastClickPosition.X) <= size.Width && Math.Abs(position.Y - lastClickPosition.Y) <= size.Height;
                TriggerBallClick(twice ? 2 : 1);
                lastClickMs = twice ? -1000000 : now;
                lastClickPosition = position;
                if (IsDocked)
                    SetDockPose(true, true);
            }
            else if (Left + Width >= SystemParameters.WorkArea.Right - 14)
            {
                DockToRight();
            }
            dragReady = false;
            ((Button)sender).ReleaseMouseCapture();
            e.Handled = true;
        }
        internal void DockToRight()
        {
            collapseTimer.Stop();
            IsDocked = true;
            SetDockPose(false, true);
        }
        internal void ExpandFromDock()
        {
            collapseTimer.Stop();
            if (IsDocked && !dragReady)
                SetDockPose(true, true);
        }
        internal void ScheduleDockCollapse()
        {
            if (IsDocked && IsDockExpanded && !dragReady)
            {
                collapseTimer.Stop();
                collapseTimer.Start();
            }
        }
        internal void CollapseToDock()
        {
            collapseTimer.Stop();
            if (IsDocked && !dragReady)
                SetDockPose(false, true);
        }
        internal void ReleaseFromDock()
        {
            collapseTimer.Stop();
            StopDockAnimations();
            IsDocked = false;
            IsDockExpanded = false;
            dockScale.ScaleX = 1;
            dockScale.ScaleY = 1;
            UpdateMotion();
        }
        private void SetDockPose(bool expanded, bool animate)
        {
            double fromLeft = Left;
            double fromScale = dockScale.ScaleX;
            StopDockAnimations();
            IsDockExpanded = expanded;
            double targetLeft = SystemParameters.WorkArea.Right - Width * (expanded ? 1 : 0.5);
            double targetScale = expanded ? 1 : 0.82;
            if (animate && MotionEnabled && IsVisible)
            {
                var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
                int version = dockTransitionVersion;
                var slide = new DoubleAnimation(fromLeft, targetLeft, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease };
                slide.Completed += (s, e) =>
                {
                    if (version != dockTransitionVersion || IsClosed)
                        return;
                    Left = targetLeft;
                    dockScale.ScaleX = targetScale;
                    dockScale.ScaleY = targetScale;
                    BeginAnimation(LeftProperty, null);
                    dockScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    dockScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                };
                BeginAnimation(LeftProperty, slide);
                var shrink = new DoubleAnimation(fromScale, targetScale, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease };
                dockScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
                dockScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            }
            else
            {
                Left = targetLeft;
                dockScale.ScaleX = targetScale;
                dockScale.ScaleY = targetScale;
            }
            UpdateMotion();
        }
        private void StopDockAnimations()
        {
            dockTransitionVersion++;
            double currentLeft = Left;
            if (!double.IsNaN(currentLeft))
                Left = currentLeft;
            BeginAnimation(LeftProperty, null);
            if (dockScale == null)
                return;
            double scale = dockScale.ScaleX;
            dockScale.ScaleX = scale;
            dockScale.ScaleY = scale;
            dockScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            dockScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        protected override void UpdateMotion()
        {
            base.UpdateMotion();
            if (IsDocked && !IsDockExpanded && motion != null)
                motion.Stop();
        }
    }

    internal sealed class AlertWindow : PassiveWindow
    {
        private readonly List<IncomingAlert> pending = new List<IncomingAlert>();
        private readonly Action<string> markSeen;
        private readonly DispatcherTimer autoClose;
        internal Button SeenButton
        {
            get
            {
                return Find<Button>("SeenButton");
            }
        }
        internal bool IsClosing
        {
            get; private set;
        }
        public AlertWindow(Action<string> markSeen, string key = "tech", bool reduced = false)
        {
            this.markSeen = markSeen;
            autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            autoClose.Tick += (s, e) => ClosePopup();
            Closed += (s, e) => autoClose.Stop();
            Title = "邮件提醒助手通知";
            ApplyTheme(key, reduced);
            Loaded += (s, e) => Place(98);
        }
        internal void ApplyTheme(string key, bool reduced)
        {
            if (IsClosing)
                return;
            SetView(key == "cosmos" ? "CosmosAlert.xaml" : "TechAlert.xaml", key, reduced);
            Click("SeenButton", Acknowledge);
            UpdateText();
            if (IsLoaded)
                Place(98);
        }
        public void Add(IncomingAlert alert)
        {
            pending.Add(alert);
            autoClose.Stop();
            autoClose.Start();
            UpdateText();
            // 通知已在屏幕上时又来一条：徽标再闪一次光环
            if (IsLoaded && pending.Count > 1)
                motion.Signal();
        }
        private void UpdateText()
        {
            if (pending.Count == 0)
                return;
            bool real = pending.Any(a => !a.IsTest);
            var latest = real ? pending.Last(a => !a.IsTest) : pending.Last();
            Text("AlertTitle", real ? "收到一封加急邮件，请立刻处理" : "测试提醒");
            var message = Find<TextBlock>("AlertTitle");
            message.FontSize = real ? 20 : 26;
            message.TextWrapping = TextWrapping.Wrap;
            message.TextTrimming = TextTrimming.None;
            Text("AlertKind", real ? "邮件提醒助手" : latest.Id == null ? "本机通知预览" : "联机测试");
            Text("AlertTime", latest.SentUtc.ToLocalTime().ToString("HH:mm:ss"));
            Text("AlertDetails", latest.SenderName + " 发来提醒\n" + (pending.Count > 1 ? "共有 " + pending.Count + " 条，5秒后自动关闭。" : "5秒后自动关闭，点击可确认知晓。"));
        }
        private void Acknowledge()
        {
            if (IsClosing || IsClosed) return;
            foreach (var alert in pending)
                markSeen(alert.Id);
            pending.Clear();
            ClosePopup();
        }
        private void ClosePopup()
        {
            if (IsClosing || IsClosed) return;
            autoClose.Stop();
            IsClosing = true;
            SeenButton.IsEnabled = false;
            motion.Stop();
            if (!MotionEnabled)
            {
                Close();
                return;
            }
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (s, e) => { if (!IsClosed) Close(); };
            view.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    internal sealed class SettingsWindow : ThemedWindow
    {
        private readonly TeamConfig team;
        private readonly Preferences prefs;
        private readonly string settingsPath;
        private readonly Action preview;
        private readonly bool originalStartup;
        private string selectedTheme;
        public SettingsWindow(TeamConfig team, Preferences prefs, string settingsPath, Action preview)
        {
            this.team = team;
            this.prefs = prefs;
            this.settingsPath = settingsPath;
            this.preview = preview;
            originalStartup = StartupRegistration.IsEnabled(team.TeamId);
            selectedTheme = prefs.ThemeKey;
            Title = "提醒设置";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            LoadSettingsView();
        }
        private void LoadSettingsView()
        {
            LoadSettingsView(prefs.DisplayName, prefs.FloatingButton, prefs.Sound, prefs.ReduceMotion, originalStartup);
        }
        private void LoadSettingsView(string name, bool floating, bool sound, bool reduced, bool startup)
        {
            SetView("Settings.xaml", selectedTheme, prefs.ReduceMotion);
            Find<TextBox>("DisplayName").Text = name;
            Find<CheckBox>("FloatingOption").IsChecked = floating;
            Find<CheckBox>("SoundOption").IsChecked = sound;
            Find<CheckBox>("MotionOption").IsChecked = reduced;
            Find<CheckBox>("StartupOption").IsChecked = startup;
            Click("TechTheme", () => SelectTheme("tech"));
            Click("CosmosTheme", () => SelectTheme("cosmos"));
            Click("CancelTop", Close);
            Click("PreviewButton", preview);
            Click("SaveButton", Save);
            ThemeSelection(selectedTheme);
        }
        // 点选主题卡片时，设置窗口立即换成该主题的外观作为预览；尚未保存的输入原样保留
        private void SelectTheme(string key)
        {
            selectedTheme = key;
            if (key == currentTheme)
            {
                ThemeSelection(key);
                return;
            }
            LoadSettingsView(
                Find<TextBox>("DisplayName").Text,
                Find<CheckBox>("FloatingOption").IsChecked == true,
                Find<CheckBox>("SoundOption").IsChecked == true,
                Find<CheckBox>("MotionOption").IsChecked == true,
                Find<CheckBox>("StartupOption").IsChecked == true);
        }
        private void Save()
        {
            string name = Find<TextBox>("DisplayName").Text.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            {
                MessageBox.Show(this, "请输入有效的显示名称。", "提醒设置");
                return;
            }
            var updated = new Preferences
            {
                DisplayName = name,
                FloatingButton = Find<CheckBox>("FloatingOption").IsChecked == true,
                Sound = Find<CheckBox>("SoundOption").IsChecked == true,
                ReduceMotion = Find<CheckBox>("MotionOption").IsChecked == true,
                ThemeKey = selectedTheme
            };
            bool startup = Find<CheckBox>("StartupOption").IsChecked == true;
            try
            {
                if (startup != originalStartup)
                    StartupRegistration.SetEnabled(team.TeamId, startup);
                try
                {
                    updated.Save(settingsPath);
                }
                catch { if (startup != originalStartup) StartupRegistration.SetEnabled(team.TeamId, originalStartup); throw; }
                prefs.DisplayName = updated.DisplayName;
                prefs.FloatingButton = updated.FloatingButton;
                prefs.Sound = updated.Sound;
                prefs.ReduceMotion = updated.ReduceMotion;
                prefs.ThemeKey = updated.ThemeKey;
                DialogResult = true;
                Close();
            }
            catch (Exception ex) { Log.Write(ex.ToString()); MessageBox.Show(this, "设置保存失败。\n" + ex.Message, "提醒设置"); }
        }
    }
}
