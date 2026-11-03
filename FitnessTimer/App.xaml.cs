using System.Windows;
using System.Threading;
namespace FitnessTimer;
public partial class App : Application
{
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(true, "Local\\FitnessTimer.Desktop", out var created);
        if (!created)
        {
            MessageBox.Show("体能计时软件已经在运行，请切换到已打开的窗口。", "体能计时");
            instanceMutex.Dispose(); instanceMutex = null; Shutdown(); return;
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (instanceMutex != null) { instanceMutex.ReleaseMutex(); instanceMutex.Dispose(); }
        base.OnExit(e);
    }
}
