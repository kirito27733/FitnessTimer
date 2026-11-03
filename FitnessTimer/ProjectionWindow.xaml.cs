using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FitnessTimer.Core;

namespace FitnessTimer;

public partial class ProjectionWindow : Window
{
    private bool fullScreen;
    private Rect previousBounds;
    private WindowState previousState;

    public ProjectionWindow(IEnumerable<LaneTimer> lanes, string batch,PreparationSettings? preparation=null)
    {
        InitializeComponent();
        DataContext = lanes; UpdateBatch(batch);
        preparation??=new PreparationSettings();
        ActivityTitleText.SetBinding(System.Windows.Controls.TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(PreparationSettings.ActivityName)){Source=preparation});
        OrganizerTitleText.SetBinding(System.Windows.Controls.TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(PreparationSettings.Organizer)){Source=preparation});
        RefreshMonitors();
        Loaded += (_, _) =>
        {
            // Leave a preview window on a single monitor so the operator retains the control panel.
            if (MonitorBox.SelectedItem is DisplayMonitor monitor && !monitor.IsPrimary) EnterFullScreen();
        };
    }
    public void UpdateBatch(string batch) => BatchText.Text = "比赛批次 · " + (string.IsNullOrWhiteSpace(batch) ? "未命名批次" : batch.Trim());
    private void RefreshMonitors()
    {
        var old = MonitorBox.SelectedItem as DisplayMonitor;
        var monitors = DisplayMonitor.GetAll();
        MonitorBox.ItemsSource = monitors;
        MonitorBox.SelectedItem = monitors.FirstOrDefault(m => m.Name == old?.Name)
            ?? monitors.FirstOrDefault(m => !m.IsPrimary) ?? monitors.FirstOrDefault();
    }
    private void EnterFullScreen()
    {
        if (fullScreen) return;
        // Re-enumerate to avoid using a screen that was disconnected since opening the preview.
        RefreshMonitors();
        if (MonitorBox.SelectedItem is not DisplayMonitor monitor) return;
        previousState = WindowState;
        previousBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Toolbar.Visibility = Visibility.Collapsed;
        fullScreen = true;
        try { monitor.PlaceFullScreen(new WindowInteropHelper(this).Handle); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ExitFullScreen();
            MessageBox.Show(this, $"无法切换到所选屏幕，请刷新屏幕后重试。\n{ex.Message}", "外屏投屏");
        }
    }
    private void ExitFullScreen()
    {
        if (!fullScreen) return;
        fullScreen = false;
        WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize;
        Toolbar.Visibility = Visibility.Visible;
        Left = previousBounds.Left; Top = previousBounds.Top; Width = previousBounds.Width; Height = previousBounds.Height;
        WindowState = previousState;
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => EnterFullScreen();
    private void RefreshScreens_Click(object sender, RoutedEventArgs e) => RefreshMonitors();
    private void MonitorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (fullScreen && MonitorBox.SelectedItem is DisplayMonitor monitor)
            monitor.PlaceFullScreen(new WindowInteropHelper(this).Handle);
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && fullScreen) { ExitFullScreen(); e.Handled = true; }
    }
    private void Window_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only the display content toggles full screen, leaving the title bar and controls alone.
        if (e.OriginalSource is not DependencyObject source || !IsInDisplay(source)) return;
        ToggleFullScreen(); e.Handled = true;
    }
    private bool IsInDisplay(DependencyObject source)
    {
        for (DependencyObject? item = source; item != null;)
        {
            if (ReferenceEquals(item, DisplayContent)) return true;
            item = item is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        return false;
    }
    private void ToggleFullScreen() { if (fullScreen) ExitFullScreen(); else EnterFullScreen(); }
}
