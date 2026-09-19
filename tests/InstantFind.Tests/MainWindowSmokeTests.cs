using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Xunit;

namespace InstantFind.Tests;

public class MainWindowSmokeTests
{
    [Fact]
    public void MainWindow_can_construct_on_windows_sta()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Linux agents compile with EnableWindowsTargeting but lack WindowsDesktop runtime/display.
            return;
        }

        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                // Headless construct: supply the brushes/styles App.xaml normally provides.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SeedAppResources(app.Resources);

                var w = new InstantFind.MainWindow();
                Assert.False(string.IsNullOrWhiteSpace(w.AppVersion));
                w.Close();
                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(90)), "MainWindow construction timed out on STA thread.");
        if (failure is not null)
            Assert.Fail($"{failure.GetType().FullName}: {failure.Message}\n{failure.StackTrace}");
    }

    private static void SeedAppResources(ResourceDictionary resources)
    {
        resources["BgBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
        resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        resources["FgBrush"] = new SolidColorBrush(Color.FromRgb(0xF1, 0xF1, 0xF1));
        resources["MutedBrush"] = new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9D));
        resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C));
        resources["BoolToVis"] = new BooleanToVisibilityConverter();
        resources[typeof(ToggleButton)] = new Style(typeof(ToggleButton));
    }
}
