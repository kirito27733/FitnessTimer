using System.Windows;
using System.Windows.Controls;
using FitnessTimer.Core;
using Microsoft.Win32;

namespace FitnessTimer;

public partial class SoundConfigurationWindow : Window
{
    private readonly Action<SoundEvent> preview;
    private readonly Action stopPreview;
    public SoundConfigurationWindow(PreparationSettings settings,Action<SoundEvent> preview,Action stopPreview)
    {
        InitializeComponent();DataContext=settings;this.preview=preview;this.stopPreview=stopPreview;
        Closed+=(_,_)=>stopPreview();
    }
    private void BrowseSound_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Title="选择事件声音文件",Filter="声音文件 (*.wav;*.mp3;*.wma;*.m4a)|*.wav;*.mp3;*.wma;*.m4a|所有文件 (*.*)|*.*"};
        if(dialog.ShowDialog(this)==true)((SoundCue)((Button)sender).DataContext).FilePath=dialog.FileName;
    }
    private void ClearSound_Click(object sender,RoutedEventArgs e)=>((SoundCue)((Button)sender).DataContext).FilePath="";
    private void PreviewSound_Click(object sender,RoutedEventArgs e)=>preview(((SoundCue)((Button)sender).DataContext).Event);
    private void StopSound_Click(object sender,RoutedEventArgs e)=>stopPreview();
    private void Done_Click(object sender,RoutedEventArgs e)=>Close();
}
