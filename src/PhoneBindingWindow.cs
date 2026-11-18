using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace OfficeSignal
{
    internal sealed class PhoneBindingWindow : ThemedWindow
    {
        private readonly RelayClient relay;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        public PhoneBindingWindow(RelayClient relay, string theme, bool reduced)
        {
            this.relay = relay;
            Title = "手机绑定";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetView("PhoneBinding.xaml", theme, reduced);
            Text("SubnetText", "绑定桌面网段  " + relay.Subnet);
            Find<TextBox>("InstallUrl").Text = relay.PublicUrl;
            Click("CloseButton", Close);
            Click("CopyUrlButton", () => Clipboard.SetText(relay.PublicUrl));
            Click("CreateCodeButton", CreateCode);
            relay.Changed += RefreshStatus;
            Closed += (s, e) => { lifetime.Cancel(); relay.Changed -= RefreshStatus; };
            RefreshStatus();
        }
        private void RefreshStatus()
        {
            if (IsClosed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => { if (!IsClosed) Text("ConnectionText", relay.Status); }));
        }
        private async void CreateCode()
        {
            Find<Button>("CreateCodeButton").IsEnabled = false;
            Text("ExpiryText", "正在连接 FEIAN…");
            try
            {
                var result = await relay.RequestPairCodeAsync(lifetime.Token);
                Text("PairCodeText", result.Code);
                Text("ExpiryText", "一次性绑定码，" + Math.Ceiling(result.ExpiresIn / 60.0) + " 分钟内有效");
            }
            catch (Exception ex)
            {
                if (!lifetime.IsCancellationRequested) Text("ExpiryText", "生成失败，请检查网络后重试。");
                Log.Write("手机绑定码生成失败 · " + ex.Message);
            }
            finally { if (!IsClosed) Find<Button>("CreateCodeButton").IsEnabled = true; }
        }
    }
}
