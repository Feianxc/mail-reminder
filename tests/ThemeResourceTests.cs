using System;
using System.Windows;

namespace OfficeSignal
{
    internal static class ThemeResourceTests
    {
        [STAThread]
        private static int Main()
        {
            var application = new Application();
            string[,] views = {
                { "TechMain.xaml", "tech" },
                { "CosmosMain.xaml", "cosmos" },
                { "TechAlert.xaml", "tech" },
                { "CosmosAlert.xaml", "cosmos" },
                { "TechFloating.xaml", "tech" },
                { "CosmosFloating.xaml", "cosmos" },
                { "Settings.xaml", "tech" },
                { "Settings.xaml", "cosmos" }
            };
            int failures = 0;
            for (int index = 0; index < views.GetLength(0); index++)
            {
                string file = views[index, 0];
                string theme = views[index, 1];
                try
                {
                    var view = ThemeView.Load(file, theme);
                    if (double.IsNaN(view.Width) || double.IsNaN(view.Height) ||
                        view.Width <= 0 || view.Height <= 0)
                        throw new InvalidOperationException("View dimensions must be positive finite values.");
                    view.Measure(new Size(view.Width, view.Height));
                    view.Arrange(new Rect(0, 0, view.Width, view.Height));
                    view.UpdateLayout();
                    Console.WriteLine("PASS resource load and layout " + file + " / " + theme);
                }
                catch (Exception exception)
                {
                    failures++;
                    Console.WriteLine("FAIL resource load and layout " + file + " / " + theme);
                    Console.WriteLine(exception.ToString());
                }
            }
            application.Shutdown();
            Console.WriteLine("RESULT " + (failures == 0 ? "pass" : "fail") +
                " 8 theme resource/layout cases, failures=" + failures);
            return failures == 0 ? 0 : 1;
        }
    }
}
