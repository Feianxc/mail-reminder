using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace OfficeSignal
{
    internal static class ThemeView
    {
        private static BitmapImage cosmicImage;
        public static Grid Load(string file, string key)
        {
            Grid root;
            using (var stream = OpenResource("OfficeSignal.UI." + file))
                root = (Grid)XamlReader.Load(stream);
            bool cosmos = key == "cosmos";
            root.Resources.MergedDictionaries.Add(Palette(cosmos));
            using (var stream = OpenResource("OfficeSignal.UI.Common.xaml"))
                root.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
            root.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            var image = root.FindName("CosmicImage") as Image;
            if (image != null && cosmos)
                image.Source = CosmicImage();
            return root;
        }
        public static T Find<T>(FrameworkElement root, string name) where T : class
        {
            var result = root.FindName(name) as T;
            if (result == null)
                throw new InvalidDataException("界面组件缺失 · " + name);
            return result;
        }
        private static Stream OpenResource(string name)
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null)
                throw new InvalidDataException("界面资源缺失 · " + name);
            return stream;
        }
        private static BitmapImage CosmicImage()
        {
            if (cosmicImage != null)
                return cosmicImage;
            using (var stream = OpenResource("OfficeSignal.Assets.cosmos.png"))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                cosmicImage = image;
            }
            return cosmicImage;
        }
        public static SolidColorBrush Brush(string hex)
        {
            return new SolidColorBrush(Color(hex));
        }
        private static Color Color(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        public static bool CanAnimate(bool reduce)
        {
            return !reduce && SystemParameters.ClientAreaAnimation;
        }
        // 两套主题的全部色彩与数字字体。视图只引用这些键，不各自定义颜色。
        // 高科技：石墨底 + 冰蓝数据色 + 琥珀信号色；宇宙星空：深空靛 + 薰衣草 + 日冕金。
        private static ResourceDictionary Palette(bool cosmos)
        {
            var palette = new ResourceDictionary();
            Action<string, string, string> set = (name, tech, star) => palette[name + "Brush"] = Brush(cosmos ? star : tech);
            set("Background", "#0A0C10", "#060714");
            set("Surface", "#101319", "#B00C0D24");
            set("Soft", "#171B23", "#24B9B2FF");
            set("Button", "#1C212B", "#2EB9B2FF");
            set("Menu", "#151920", "#14153A");
            set("Line", "#262C37", "#30D8D5FF");
            set("Text", "#EEF1F6", "#F4F2FF");
            set("Muted", "#8B95A7", "#A6A7CF");
            set("Faint", "#586273", "#6E7099");
            set("Accent", "#7FD8FF", "#CDC4FF");
            set("Signal", "#FF8A3D", "#FFD29A");
            set("OnAccent", "#06121A", "#14102E");
            set("Ok", "#57E3B0", "#8DE8D2");
            set("Danger", "#FF6B6B", "#FF8FA6");
            palette["ActionBrush"] = new LinearGradientBrush(
                Color(cosmos ? "#FFE9C2" : "#FFB45A"), Color(cosmos ? "#FFB07A" : "#FF6A2C"), new Point(0, 0), new Point(1, 1));
            palette["NumFont"] = new FontFamily(cosmos ? "Segoe UI Light, Segoe UI, Microsoft YaHei UI" : "Bahnschrift, Segoe UI, Microsoft YaHei UI");
            return palette;
        }
        // 右键菜单与视图共用同一套资源，保证弹出层里的颜色与样式一致
        public static ContextMenu CreateMenu(FrameworkElement view)
        {
            var menu = new ContextMenu();
            menu.Resources.MergedDictionaries.Add(view.Resources);
            menu.Style = (Style)view.FindResource("MenuStyle");
            return menu;
        }
        public static void AddMenuItem(ContextMenu menu, string header, Action action)
        {
            var item = new MenuItem { Header = header, Style = (Style)menu.FindResource("MenuItemStyle") };
            item.Click += (s, e) => action();
            menu.Items.Add(item);
        }
        public static System.Drawing.Icon Icon(string key)
        {
            bool cosmos = key == "cosmos";
            using (var bitmap = new System.Drawing.Bitmap(32, 32))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var back = new System.Drawing.SolidBrush(cosmos ? System.Drawing.Color.FromArgb(8, 8, 26) : System.Drawing.Color.FromArgb(20, 23, 30)))
            using (var core = new System.Drawing.SolidBrush(cosmos ? System.Drawing.Color.FromArgb(255, 246, 228) : System.Drawing.Color.FromArgb(255, 138, 61)))
            using (var pen = new System.Drawing.Pen(cosmos ? System.Drawing.Color.FromArgb(255, 210, 154) : System.Drawing.Color.FromArgb(127, 216, 255), 2.6f))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.FillEllipse(back, 1, 1, 30, 30);
                if (cosmos)
                {
                    // 日食：金色日冕环 + 右上角钻石光点
                    graphics.DrawEllipse(pen, 4, 4, 24, 24);
                    graphics.FillEllipse(core, 20, 3, 7, 7);
                }
                else
                {
                    // 信号：琥珀核心 + 两侧冰蓝弧线
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    graphics.FillEllipse(core, 12, 12, 8, 8);
                    graphics.DrawArc(pen, 6, 6, 20, 20, 135, 90);
                    graphics.DrawArc(pen, 6, 6, 20, 20, -45, 90);
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    }

    internal sealed class ThemeMotion : IDisposable
    {
        private readonly FrameworkElement root;
        private readonly Func<bool> enabled;
        // 视图通过约定的元素名接入动效，缺少某个名字时对应动效自动跳过：
        //   AmbientOrbit / AmbientOrbitB  持续旋转（B 反向且更慢），需带 RotateTransform
        //   AmbientDot / AmbientGlow      呼吸明暗
        //   SignalPulse / SignalPulseB    发送时向外扩散的光环，需带 ScaleTransform
        //   CooldownGauge                 冷却进度，StrokeDashArray 第一项为整圈长度
        //   模板内的 Hover / Knob         悬停淡入淡出、开关滑块过渡
        // 根元素 Tag="side" 表示入场时从右侧滑入（桌面通知）。
        private readonly List<ScaleTransform> buttonTransforms = new List<ScaleTransform>();
        private readonly RotateTransform[] orbits;
        private readonly UIElement[] lights;
        private readonly UIElement[] signals;
        private readonly Shape gauge;
        private readonly IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        private DateTime gaugeEndUtc = DateTime.MinValue;
        private double gaugeTotal = 1;
        public bool IsRunning
        {
            get; private set;
        }
        public ThemeMotion(FrameworkElement root, Func<bool> enabled)
        {
            this.root = root;
            this.enabled = enabled;
            foreach (var control in LogicalDescendants(root).OfType<System.Windows.Controls.Primitives.ButtonBase>())
            {
                control.MouseEnter += (s, e) => Glow(control, true);
                control.MouseLeave += (s, e) => Glow(control, false);
                var box = control as CheckBox;
                if (box != null)
                {
                    box.Checked += (s, e) => Slide(box, -18);
                    box.Unchecked += (s, e) => Slide(box, 18);
                    continue;
                }
                var scale = new ScaleTransform(1, 1);
                control.RenderTransform = scale;
                control.RenderTransformOrigin = new Point(0.5, 0.5);
                buttonTransforms.Add(scale);
                control.MouseLeave += (s, e) => Scale(scale, 1);
                control.PreviewMouseLeftButtonDown += (s, e) => Scale(scale, 0.97);
                control.PreviewMouseLeftButtonUp += (s, e) => Scale(scale, 1);
            }
            orbits = new[] { Spinner("AmbientOrbit"), Spinner("AmbientOrbitB") };
            lights = new[] { root.FindName("AmbientDot") as UIElement, root.FindName("AmbientGlow") as UIElement };
            signals = new[] { root.FindName("SignalPulse") as UIElement, root.FindName("SignalPulseB") as UIElement };
            gauge = root.FindName("CooldownGauge") as Shape;
        }
        private RotateTransform Spinner(string name)
        {
            var element = root.FindName(name) as UIElement;
            return element == null ? null : element.RenderTransform as RotateTransform;
        }
        private void Scale(ScaleTransform scale, double target)
        {
            double from = scale.ScaleX;
            scale.ScaleX = target;
            scale.ScaleY = target;
            if (!enabled())
                return;
            var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
        // 悬停层的最终状态由模板触发器决定，这里只叠加一段过渡，结束后自动让位
        private void Glow(Control control, bool entering)
        {
            if (!enabled() || !control.IsLoaded || control.Template == null)
                return;
            var layer = control.Template.FindName("Hover", control) as UIElement;
            if (layer == null)
                return;
            double from = layer.HasAnimatedProperties ? layer.Opacity : entering ? 0 : 1;
            var fade = new DoubleAnimation(from, entering ? 1 : 0, TimeSpan.FromMilliseconds(entering ? 140 : 240)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            layer.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        // 开关滑块：触发器已把滑块放到新位置，这里从旧位置滑过去
        private void Slide(CheckBox box, double from)
        {
            if (!enabled() || !box.IsLoaded || box.Template == null)
                return;
            var knob = box.Template.FindName("Knob", box) as UIElement;
            if (knob == null)
                return;
            var shift = new TranslateTransform();
            knob.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }
        public void Update(bool visible, bool cosmos)
        {
            Stop();
            if (!visible || !enabled())
                return;
            IsRunning = orbits.Any(item => item != null) || lights.Any(item => item != null);
            for (int i = 0; i < orbits.Length; i++)
            {
                var orbit = orbits[i];
                if (orbit == null)
                    continue;
                double seconds = (cosmos ? 40 : 18) * (i == 0 ? 1 : 2.5);
                var spin = new DoubleAnimation(orbit.Angle, orbit.Angle + (i == 0 ? 360 : -360), TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(spin, 30);
                orbit.BeginAnimation(RotateTransform.AngleProperty, spin);
            }
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] == null)
                    continue;
                double seconds = i == 0 ? (cosmos ? 2.6 : 1.5) : 3.2;
                var pulse = new DoubleAnimation(i == 0 ? 0.35 : 0.55, 1, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Timeline.SetDesiredFrameRate(pulse, 24);
                lights[i].BeginAnimation(UIElement.OpacityProperty, pulse);
            }
            StartGauge();
        }
        // 发送后的冷却进度。remaining 为剩余秒数，total 为整段冷却时长。
        public void Cooldown(double remaining, double total)
        {
            gaugeEndUtc = DateTime.UtcNow.AddSeconds(remaining);
            gaugeTotal = Math.Max(Math.Max(total, remaining), 0.1);
            StartGauge();
        }
        private void StartGauge()
        {
            double remaining = (gaugeEndUtc - DateTime.UtcNow).TotalSeconds;
            if (gauge == null || gauge.StrokeDashArray == null || gauge.StrokeDashArray.Count == 0 || remaining <= 0.05 || !enabled())
                return;
            double length = gauge.StrokeDashArray[0];
            var span = TimeSpan.FromSeconds(remaining);
            gauge.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(length * (1 - remaining / gaugeTotal), length, span) { FillBehavior = FillBehavior.Stop });
            gauge.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 1, span) { FillBehavior = FillBehavior.Stop });
        }
        public void Enter(double offset = 10)
        {
            if (!enabled())
                return;
            bool side = Equals(root.Tag, "side");
            root.Opacity = 1;
            var translate = new TranslateTransform();
            root.RenderTransform = translate;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(side ? 260 : 220)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            root.BeginAnimation(UIElement.OpacityProperty, fade);
            var glide = new DoubleAnimation(side ? 24 : offset, 0, TimeSpan.FromMilliseconds(side ? 420 : 300)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            translate.BeginAnimation(side ? TranslateTransform.XProperty : TranslateTransform.YProperty, glide);
        }
        public void Signal()
        {
            if (!enabled())
                return;
            for (int i = 0; i < signals.Length; i++)
            {
                var ring = signals[i];
                var scale = ring == null ? null : ring.RenderTransform as ScaleTransform;
                if (scale == null)
                    continue;
                var begin = TimeSpan.FromMilliseconds(i * 160);
                var span = TimeSpan.FromMilliseconds(820);
                var spread = new DoubleAnimation(0.92, 1.42, span) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.Stop };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, spread);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, spread);
                ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.9, 0, span) { BeginTime = begin, FillBehavior = FillBehavior.Stop });
            }
        }
        public void Stop()
        {
            IsRunning = false;
            foreach (var orbit in orbits)
                if (orbit != null)
                    orbit.BeginAnimation(RotateTransform.AngleProperty, null);
            foreach (var light in lights)
                if (light != null)
                    light.BeginAnimation(UIElement.OpacityProperty, null);
            foreach (var ring in signals)
            {
                if (ring == null)
                    continue;
                ring.BeginAnimation(UIElement.OpacityProperty, null);
                var spread = ring.RenderTransform as ScaleTransform;
                if (spread == null)
                    continue;
                spread.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                spread.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            if (gauge != null)
            {
                gauge.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                gauge.BeginAnimation(UIElement.OpacityProperty, null);
            }
            root.BeginAnimation(UIElement.OpacityProperty, null);
            root.Opacity = 1;
            var translate = root.RenderTransform as TranslateTransform;
            if (translate != null)
            {
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
            }
            foreach (var scale in buttonTransforms)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
        }
        public void Dispose()
        {
            Stop();
        }
        private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
        {
            foreach (var item in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                yield return item;
                foreach (var child in LogicalDescendants(item))
                    yield return child;
            }
        }
    }
}
