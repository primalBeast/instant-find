using System.Windows;
using InstantFind.Services;

namespace InstantFind;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        SingleInstance.TryRegister();
        base.OnStartup(e);
    }
}
