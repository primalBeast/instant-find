using System.Reflection;
using System.Windows;
using Xunit;

namespace InstantFind.Tests;

/// <summary>
/// Full MainWindow InitializeComponent needs pack:// app resources + desktop session;
/// headless CI cannot reliably construct the Window. Smoke the type surface instead.
/// </summary>
public class MainWindowSmokeTests
{
    [Fact]
    public void MainWindow_type_exposes_AppVersion_and_is_Window()
    {
        var t = typeof(InstantFind.MainWindow);
        Assert.True(typeof(Window).IsAssignableFrom(t));
        Assert.NotNull(t.GetProperty("AppVersion", BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(t.GetConstructor(Type.EmptyTypes));
    }
}
