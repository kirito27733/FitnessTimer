using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FitnessTimer.Core;
using FitnessTimer.Bluetooth;
using Microsoft.Win32;

namespace FitnessTimer;

public partial class PreparationPanel : UserControl
{
    public PreparationSettings Settings { get;private set; }=null!;
    private BatchQueue queue=null!;
    private bool editable=true;
    private bool selectingAll;
    private readonly HashSet<BatchCard> selectionCards=[];
    public event Action<BatchCard>? LoadRequested;
    public event Action<SoundEvent>? PreviewRequested;
    public event Action? StopPreviewRequested;
    public event Action? SaveRequested;
    public event Action? QueueChanged;
    public PreparationPanel(){InitializeComponent();}
    public void Initialize(PreparationSettings settings,BatchQueue queue,BluetoothButtonManager bluetooth,Func<bool> canConfigureBluetooth)
    {
        Settings=settings;this.queue=queue;DataContext=this;
        Settings.Batches.CollectionChanged+=(_,_)=>WatchSelection();WatchSelection();
        BluetoothPanel.Initialize(bluetooth,canConfigureBluetooth);UpdateControls(true);
    }
    public void UpdateControls(bool canEdit)
    {
        editable=canEdit;EventInfoPanel.IsEnabled=canEdit;SoundsPanel.IsEnabled=canEdit;RosterPanel.IsEnabled=canEdit;
        EmptyText.Visibility=Settings?.Batches.Count==0?Visibility.Visible:Visibility.Collapsed;
        if(Settings!=null)UpdateSelection();
        BluetoothPanel.UpdateControls();
    }
    public void ShowSaveStatus(string text)=>SaveStatusText.Text=text;
    private void Save_Click(object sender,RoutedEventArgs e)=>SaveRequested?.Invoke();
    private void SoundConfiguration_Click(object sender,RoutedEventArgs e)
    {
        if(!editable)return;
        var dialog=new SoundConfigurationWindow(Settings,sound=>PreviewRequested?.Invoke(sound),()=>StopPreviewRequested?.Invoke()) {Owner=Window.GetWindow(this)};
        dialog.ShowDialog();SaveRequested?.Invoke();
    }
    private void AddBatch_Click(object sender,RoutedEventArgs e)
    {
        if(!editable)return;
        if(Settings.Batches.Count>=5000){Error(new InvalidDataException("最多配置 5000 个批次。"));return;}
        Settings.Batches.Add(new BatchCard {Name=$"第 {Settings.Batches.Count+1} 组"});Changed();
    }
    private void MoveUp_Click(object sender,RoutedEventArgs e)=>Move((BatchCard)((Button)sender).DataContext,-1);
    private void MoveDown_Click(object sender,RoutedEventArgs e)=>Move((BatchCard)((Button)sender).DataContext,1);
    private void Move(BatchCard card,int delta)
    {
        if(!editable)return;var i=Settings.Batches.IndexOf(card);var target=i+delta;
        if(i>=0&&target>=0&&target<Settings.Batches.Count){Settings.Batches.Move(i,target);Changed();}
    }
    private void RemoveBatch_Click(object sender,RoutedEventArgs e)
    {
        if(!editable)return;var card=(BatchCard)((Button)sender).DataContext;
        if(MessageBox.Show(Window.GetWindow(this),$"删除批次卡片“{card.Name}”？已保存的历史成绩不受影响。","删除批次",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        queue.Remove(card);Changed();
    }
    private void WatchSelection()
    {
        foreach(var card in selectionCards.Where(c=>!Settings.Batches.Contains(c)).ToList())
        {card.PropertyChanged-=SelectionChanged;selectionCards.Remove(card);}
        foreach(var card in Settings.Batches)
            if(selectionCards.Add(card))card.PropertyChanged+=SelectionChanged;
        UpdateSelection();
    }
    private void SelectionChanged(object? sender,PropertyChangedEventArgs e)
    {if(!selectingAll&&e.PropertyName==nameof(BatchCard.IsSelected))UpdateSelection();}
    private void UpdateSelection()
    {
        var count=Settings.Batches.Count(c=>c.IsSelected);
        SelectAllBatches.IsEnabled=Settings.Batches.Count>0;
        SelectAllBatches.IsChecked=count==0?false:count==Settings.Batches.Count?true:null;
        BatchSelectionText.Text=$"已选 {count} / {Settings.Batches.Count} 个批次";
        DeleteSelectedBatches.IsEnabled=editable&&count>0;
    }
    private void SelectAllBatches_Click(object sender,RoutedEventArgs e)
    {
        if(!editable)return;
        var select=!Settings.Batches.All(c=>c.IsSelected);
        selectingAll=true;
        try{foreach(var card in Settings.Batches)card.IsSelected=select;}
        finally{selectingAll=false;}
        UpdateSelection();
    }
    private void DeleteSelectedBatches_Click(object sender,RoutedEventArgs e)
    {
        if(!editable)return;
        var selected=Settings.Batches.Where(c=>c.IsSelected).ToList();
        if(selected.Count==0)return;
        if(MessageBox.Show(Window.GetWindow(this),$"确定删除选中的 {selected.Count} 个批次卡片？已保存的历史成绩不受影响。","批量删除批次",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        queue.RemoveMany(selected);Changed();
    }
    private void LoadBatch_Click(object sender,RoutedEventArgs e){if(editable)LoadRequested?.Invoke((BatchCard)((Button)sender).DataContext);}
    private void Import_Click(object sender,RoutedEventArgs e)
    {
        if(!editable)return;
        var dialog=new OpenFileDialog {Title="导入人员批次名单",Filter="Excel 工作簿 (*.xlsx)|*.xlsx"};
        if(dialog.ShowDialog(Window.GetWindow(this))!=true)return;
        try
        {
            var imported=BatchExcel.Import(dialog.FileName);
            var mode=Settings.Batches.Count==0?MessageBoxResult.No:MessageBox.Show(Window.GetWindow(this),$"已读取 {imported.Count} 个批次。是否追加到当前卡片末尾？\n是：追加；否：替换全部现有卡片；取消：不导入。","导入方式",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
            if(mode==MessageBoxResult.Cancel)return;
            if(mode==MessageBoxResult.Yes)
            {if(Settings.Batches.Count+imported.Count>5000)throw new InvalidDataException("追加后不能超过 5000 个批次。");foreach(var card in imported)Settings.Batches.Add(card);}
            else queue.Replace(imported);
            Changed();
        }
        catch(Exception ex){Error(ex);}
    }
    private void Export_Click(object sender,RoutedEventArgs e)=>Export(false);
    private void Template_Click(object sender,RoutedEventArgs e)=>Export(true);
    private void Export(bool template)
    {
        var dialog=new SaveFileDialog {Title=template?"导出名单模板":"导出人员名单",Filter="Excel 工作簿 (*.xlsx)|*.xlsx",FileName=template?"人员批次导入模板.xlsx":$"人员批次名单_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"};
        if(dialog.ShowDialog(Window.GetWindow(this))!=true)return;
        try{BatchExcel.Export(dialog.FileName,template?Array.Empty<BatchCard>():Settings.Batches);ShowSaveStatus(template?"空白模板已导出，请保留表头并填写顺序、批次和姓名。":"人员名单已导出。");}
        catch(Exception ex){Error(ex);}
    }
    private void Changed(){queue.Reindex();UpdateControls(editable);QueueChanged?.Invoke();}
    private void Error(Exception ex)=>MessageBox.Show(Window.GetWindow(this),ex.Message,"赛前准备",MessageBoxButton.OK,MessageBoxImage.Warning);
}
