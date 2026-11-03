using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FitnessTimer.Core;
using FitnessTimer.Bluetooth;
using Microsoft.Win32;

namespace FitnessTimer;

public partial class MainWindow : Window
{
    public ObservableCollection<LaneTimer> Lanes { get; } = new(Enumerable.Range(1, 4).Select(n => new LaneTimer(n)));
    public ObservableCollection<RaceResult> Results { get; } = [];
    public ObservableCollection<RankedRaceResult> HistoryResults { get; } = [];
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly string dataPath;
    private bool saved;
    private bool storageAvailable = true;
    private ProjectionWindow? projectionWindow;
    private TabletConnectionWindow? tabletWindow;
    private readonly TabletTestServer tabletServer=new();
    private readonly DispatcherTimer deviceTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly BluetoothButtonManager bluetooth;
    private readonly FinishButtonController finishButtons;
    private readonly StartButtonGate startButtonGate=new(Stopwatch.Frequency);
    private bool closing;
    public PreparationSettings Preparation { get;private set; }=new();
    private readonly BatchQueue batchQueue;
    private readonly EventAudioPlayer audio;
    private readonly string preparationPath;
    private readonly DispatcherTimer preparationSaveTimer=new(){Interval=TimeSpan.FromMilliseconds(600)};
    private readonly HashSet<BatchCard> watchedCards=[];
    private bool preparationDirty,preparationReady,preparationStorageAvailable=true;

    public MainWindow():this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FitnessTimer")) { }
    public MainWindow(string dataDirectory):this(dataDirectory,()=>new BleButtonConnection()){}
    internal MainWindow(string dataDirectory,Func<IBleButtonConnection> connectionFactory)
    {
        dataPath=Path.Combine(dataDirectory,"results.json");preparationPath=Path.Combine(dataDirectory,"preparation.json");
        InitializeComponent();
        try{Preparation=PreparationStore.Load(preparationPath);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            preparationStorageAvailable=false;
            Loaded+=(_,_)=>MessageBox.Show(this,$"赛前配置读取失败，原文件已保留。可重新配置后点击“保存赛前配置”，备份原文件后保存新配置。\n{ex.Message}","赛前配置");
        }
        batchQueue=new BatchQueue(Preparation.Batches);audio=new EventAudioPlayer(Preparation);
        audio.PlaybackError+=message=>StatusText.Text=message;
        DataContext = this;
        finishButtons = new FinishButtonController(Lanes, Stopwatch.Frequency);
        finishButtons.SetEnabled(true);
        bluetooth = new BluetoothButtonManager(Dispatcher,dataDirectory,connectionFactory);
        BluetoothIndicators.ItemsSource=bluetooth.Bindings;
        bluetooth.StateChanged += UpdateBluetoothStatus;
        bluetooth.ConnectionLost += lane =>
        {
            StatusText.Text = lane==0?"蓝牙开始按钮已断开，可点击界面“统一开始”；已开始的计时不受影响。":$"{lane} 道蓝牙按钮断开，计时继续；可点击该赛道“完成”作为备用。";
            if (finishButtons.Enabled) SystemSounds.Exclamation.Play();
        };
        bluetooth.PressReceived += (lane, timestamp) =>
        {
            var generation = finishButtons.Generation;
            var startGeneration=startButtonGate.Generation;
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(async () =>
            {
                if (closing) return;
                if(lane==0)
                {
                    if(CanStartFromBluetooth()&&startButtonGate.TryAccept(timestamp,startGeneration,Stopwatch.GetTimestamp()))
                    {if(await StartTimingAsync(timestamp))bluetooth.AddLog("蓝牙开始按钮已触发，四条赛道使用同一起始时刻。");}
                    else bluetooth.AddLog("开始按键信号已忽略：请进入现场计时、载入批次并等待四道全部待开始；运行、暂停和赛前试按不会重新发令。");
                    return;
                }
                if(lane<1||lane>4)return;
                var connected = bluetooth.Lanes[lane-1].IsReadyForTiming;
                if (connected && finishButtons.TryFinish(lane,timestamp,generation))
                {
                    StatusText.Text = $"{lane} 道蓝牙终点按钮已触发，基础用时 {Lanes[lane-1].BaseDisplay}。";
                    bluetooth.AddLog($"{lane} 道已完成计时，基础用时 {Lanes[lane-1].BaseDisplay}。");
                    audio.Play(SoundEvent.Finish);
                    UpdateButtons();
                }
                else bluetooth.AddLog($"{lane} 道按键信号未用于完成计时（按钮未就绪、非计时状态、过期或重复事件）。");
            }));
        };
        Closed += (_, _) => projectionWindow?.Close();
        Closed += (_, _) => { closing=true; bluetooth.Dispose();audio.Dispose(); };
        Closed += (_,_)=>{deviceTimer.Stop();tabletServer.Dispose();tabletWindow?.Close();};
        PrepPanel.Initialize(Preparation,batchQueue,bluetooth,CanConfigureBluetooth);
        PrepPanel.LoadRequested+=card=>{if(LoadPreparedBatch(card))MainTabs.SelectedItem=TimingTab;};
        PrepPanel.PreviewRequested+=sound=>audio.Play(sound,true);
        PrepPanel.StopPreviewRequested+=audio.Stop;
        PrepPanel.SaveRequested+=()=>SavePreparation(true);
        PrepPanel.QueueChanged+=()=>{UpdateQueueStatus();MarkPreparationDirty();};
        Preparation.PropertyChanged+=PreparationEdited;
        foreach(var cue in Preparation.Sounds)cue.PropertyChanged+=PreparationEdited;
        Preparation.Batches.CollectionChanged+=RosterChanged;
        WatchCards();batchQueue.Reindex();
        preparationSaveTimer.Tick+=(_,_)=>{preparationSaveTimer.Stop();SavePreparation();};
        preparationReady=true;
        MainTabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,MainTabs))UpdateButtons();};
        try { foreach (var result in ResultStore.Load(dataPath).OrderByDescending(r => r.RecordedAt)) Results.Add(result); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            storageAvailable = false;
            Loaded += (_, _) => MessageBox.Show(this, $"无法读取历史成绩，原文件将保留。请检查后重启软件。\n{dataPath}\n{ex.Message}", "历史成绩读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshHistoryRankings();
        refreshTimer.Tick += (_, _) => { foreach (var lane in Lanes) if (lane.State == LaneState.Running) lane.Refresh(); };
        refreshTimer.Start(); UpdateButtons(); UpdateBluetoothStatus();UpdateQueueStatus();
        if(Preparation.Batches.Count>0)LoadPreparedBatch(Preparation.Batches[0],false);
        deviceTimer.Tick+=(_,_)=>{foreach(var binding in bluetooth.Bindings)binding.RefreshBattery();RefreshTabletState();};deviceTimer.Start();RefreshTabletState();
    }
    private void RefreshTabletState()
    {
        var lane=Lanes[0];
        tabletServer.SetSnapshot(new TabletLaneSnapshot(Preparation.ActivityName,Preparation.Organizer,BatchNameText.Text,lane.Participant,lane.Status,lane.BaseDisplay,lane.PenaltySeconds,lane.TotalDisplay));
        TabletServiceText.Text=!tabletServer.IsRunning?"平板服务 · 已停止":tabletServer.TabletOnline?"平板服务 · 1道已联通":"平板服务 · 已启动，等待平板";
        TabletServiceButton.ToolTip=tabletServer.ClientStatus;
        TabletServiceText.Foreground=new System.Windows.Media.SolidColorBrush(tabletServer.IsRunning?System.Windows.Media.Color.FromRgb(8,127,114):System.Windows.Media.Color.FromRgb(113,128,150));
    }
    private void TabletService_Click(object sender,RoutedEventArgs e)
    {
        if(tabletWindow!=null){tabletWindow.Activate();return;}
        tabletWindow=new TabletConnectionWindow(tabletServer,RefreshTabletState){Owner=this};
        tabletWindow.Closed+=(_,_)=>tabletWindow=null;tabletWindow.Show();
    }
    private bool CanConfigureBluetooth() => !Lanes.Any(l => l.State is LaneState.Running or LaneState.Paused);
    private void UpdateBluetoothStatus()
    {
        var connected=bluetooth.Lanes.Count(l=>l.IsConnected);
        var ready=bluetooth.Lanes.Count(l=>l.IsReadyForTiming);
        BluetoothStatusText.Text=$"开始键{(bluetooth.StartButton.IsReadyForTiming?"已就绪":bluetooth.StartButton.IsConnected?"待识别":"未连接")} · 终点 {connected}/4 已连接 · {ready} 道自动完成";
        BluetoothStatusText.Foreground = new System.Windows.Media.SolidColorBrush(ready>0 ? System.Windows.Media.Color.FromRgb(8,127,114) : System.Windows.Media.Color.FromRgb(166,93,20));
        UpdateButtons();
    }
    private void Projection_Click(object sender, RoutedEventArgs e)
    {
        if (projectionWindow != null)
        {
            if (projectionWindow.WindowState == WindowState.Minimized) projectionWindow.WindowState = WindowState.Normal;
            projectionWindow.Activate(); return;
        }
        projectionWindow = new ProjectionWindow(Lanes, BatchNameText.Text,Preparation);
        projectionWindow.Closed += (_, _) => projectionWindow = null;
        projectionWindow.Show();
        StatusText.Text = "外屏已打开：连接多屏时请在 Windows 中选择“扩展”，外屏按 F11 全屏、Esc 返回。";
    }
    private void UpdateButtons()
    {
        StartButton.IsEnabled = !startingTiming && !bluetooth.IsPreparing && Lanes.Any(l => l.State is LaneState.Ready or LaneState.Paused);
        StartButton.Content = Lanes.Any(l => l.State == LaneState.Paused) ? "▶  开始 / 继续" : "▶  统一开始";
        PauseAllButton.IsEnabled = Lanes.Any(l => l.State == LaneState.Running);
        FinishAllButton.IsEnabled = Lanes.Any(l => l.CanFinish);
        LaneList.IsEnabled = !saved;
        PrepPanel.UpdateControls(saved||Lanes.All(l=>l.State==LaneState.Ready&&l.PenaltySeconds==0));
        NextBatchButton.IsEnabled=!startingTiming;
        UpdateStartButtonArming();
    }
    private bool CanStartFromBluetooth()=>!closing&&!bluetooth.IsPreparing&&!saved&&TimingTab.IsSelected&&
        bluetooth.StartButton.IsReadyForTiming&&Lanes.All(l=>l.State==LaneState.Ready)&&
        batchQueue.Current is { } card&&!string.IsNullOrWhiteSpace(card.Name)&&card.Name.Length<=80;
    private bool startingTiming;
    private void UpdateStartButtonArming()
    {
        var available=!startingTiming&&CanStartFromBluetooth();
        startButtonGate.SetArmed(available,Stopwatch.GetTimestamp());
        bluetooth.SetStartButtonAvailable(available);
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
        =>await StartTimingAsync();
    private async Task<bool> StartTimingAsync(long? receivedAt=null)
    {
        if (startingTiming) return false;
        if(Lanes.All(l=>l.State==LaneState.Ready))
        {
            var card=batchQueue.Current??batchQueue.PeekNext();
            if(card==null){MainTabs.SelectedIndex=0;StatusText.Text="请先在赛前准备添加批次卡片并填写批次名称和选手姓名。";return false;}
            if(batchQueue.Current==null){if(!LoadPreparedBatch(card))return false;}
            else
            {
                try{card.Validate();}catch(InvalidDataException ex){MainTabs.SelectedIndex=0;MessageBox.Show(this,ex.Message,"请完善赛前批次信息");return false;}
                ApplyBatchNames(card);
            }
        }
        startingTiming=true;UpdateButtons();
        try
        {
        bluetooth.StopScan();
        var pendingGeneration=finishButtons.Generation;
        var pendingStates=Lanes.Select(l=>l.State).ToArray();
        try{await bluetooth.PrepareFinishButtonsAsync(Lanes.Where(l=>l.State is LaneState.Ready or LaneState.Paused).Select(l=>l.Number));}
        catch(Exception ex){StatusText.Text=$"未开始计时：{ex.Message} 请重连按钮，或断开后使用手动完成。";return false;}
        if(closing||pendingGeneration!=finishButtons.Generation||!Lanes.Select(l=>l.State).SequenceEqual(pendingStates))
        {StatusText.Text="准备期间本组状态发生变化，本次开始已取消，请核对后重试。";return false;}
        if (Lanes.All(l=>l.State==LaneState.Ready)) finishButtons.InvalidatePending();
        var resumed=Lanes.Any(l=>l.State==LaneState.Paused);
        var timestamp = receivedAt??Stopwatch.GetTimestamp();
        foreach (var lane in Lanes) lane.Start(timestamp);
        audio.Play(resumed?SoundEvent.Resume:SoundEvent.Start);SavePreparation();
        StatusText.Text = bluetooth.Lanes.Any(l=>l.IsReadyForTiming) ? "计时已开始；已连接的蓝牙终点按钮自动完成对应赛道，其他赛道请点击“完成”。" : "计时已开始；各赛道到达终点后，请点击对应的“完成”。"; UpdateButtons();return true;
        }
        finally{startingTiming=false;UpdateButtons();}
    }
    private void PauseAll_Click(object sender, RoutedEventArgs e)
    {
        var timestamp = Stopwatch.GetTimestamp();
        foreach (var lane in Lanes) lane.Pause(timestamp);
        audio.Play(SoundEvent.Pause);
        StatusText.Text = "全部运行中的赛道已暂停。"; UpdateButtons();
    }
    private void FinishAll_Click(object sender, RoutedEventArgs e)
    {
        var timestamp = Stopwatch.GetTimestamp();
        foreach (var lane in Lanes) lane.Finish(timestamp);
        audio.Play(SoundEvent.Finish);
        StatusText.Text = "本组计时结束，请核对罚时，点击“下一组”自动保存成绩。"; UpdateButtons();
    }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        var lane = (LaneTimer)((Button)sender).DataContext;
        if (lane.State == LaneState.Paused)
        {
            var generation=finishButtons.Generation;
            try{await bluetooth.PrepareFinishButtonsAsync(new[]{lane.Number});}
            catch(Exception ex){StatusText.Text=$"未继续计时：{ex.Message}";return;}
            if(closing||generation!=finishButtons.Generation||lane.State!=LaneState.Paused)return;
            lane.Start();audio.Play(SoundEvent.Resume);
        }
        else{lane.Pause();audio.Play(SoundEvent.Pause);} UpdateButtons();
    }
    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        var lane=(LaneTimer)((Button)sender).DataContext;
        if(lane.CanFinish){lane.Finish();audio.Play(SoundEvent.Finish);} UpdateButtons();
    }
    private void Penalty_Click(object sender, RoutedEventArgs e)
    {
        if (saved) { StatusText.Text = "本组成绩已保存，请重置后开始下一组。"; return; }
        var button = (Button)sender; var parts = ((string)button.Tag).Split(',');
        ((LaneTimer)button.DataContext).AdjustPenalty(int.Parse(parts[0]), int.Parse(parts[1]));
        if(int.Parse(parts[1])>0)audio.Play(SoundEvent.Penalty);
        UpdateButtons();
    }
    private bool SaveCurrentResults()
    {
        if(saved)return true;
        if(!storageAvailable){StatusText.Text="历史成绩文件不可用，当前成绩未保存，不能进入下一组。";return false;}
        if(Lanes.Any(l=>l.State!=LaneState.Finished))return false;
        if (string.IsNullOrWhiteSpace(BatchNameText.Text)) { MessageBox.Show(this, "请在赛前准备的批次卡片中填写批次名称。", "提示"); return false; }
        var now = DateTimeOffset.Now;
        var current = Lanes.Select(l => new RaceResult(BatchNameText.Text.Trim(), now, l.Number,
            string.IsNullOrWhiteSpace(l.Participant) ? $"选手 {l.Number}" : l.Participant.Trim(), l.Elapsed.Ticks, l.FiveCount, l.FifteenCount)).ToList();
        try
        {
            ResultStore.Save(dataPath, current.Concat(Results));
            for (var i = current.Count - 1; i >= 0; i--) Results.Insert(0, current[i]);
            RefreshHistoryRankings();
            saved = true; StatusText.Text = "本组 4 条成绩已保存，可在“历史成绩”查看和导出。"; UpdateButtons();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowFileError(ex);return false; }
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if(MessageBox.Show(this,"确定重置本组成绩吗？当前计时和罚时将清零，批次与选手姓名保留。已保存的历史成绩不会删除。","确认重置本组",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        ResetTimers();StatusText.Text="本组计时和罚时已重置，批次与选手姓名保留。";UpdateButtons();
    }
    private bool ConfirmReplaceGroup(string action)=>saved||Lanes.All(l=>l.State==LaneState.Ready&&l.PenaltySeconds==0)||MessageBox.Show(this,$"本组成绩尚未保存，{action}将清除当前计时和罚时。是否继续？","未保存的成绩",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes;
    private void ResetTimers()
    {
        startButtonGate.SetArmed(false,Stopwatch.GetTimestamp());
        audio.Stop();foreach(var lane in Lanes)lane.Reset();finishButtons.InvalidatePending();saved=false;
    }
    private bool LoadPreparedBatch(BatchCard card,bool confirm=true)
    {
        try{card.Validate();}catch(InvalidDataException ex){MessageBox.Show(this,ex.Message,"批次信息不完整");return false;}
        if(confirm&&!ConfirmReplaceGroup("载入其他批次"))return false;
        ResetTimers();batchQueue.Load(card);ApplyBatchNames(card);UpdateQueueStatus();UpdateButtons();SavePreparation();
        StatusText.Text=$"已载入第 {card.Order} 组“{card.Name}”，请核对四条赛道姓名后开始。";return true;
    }
    private void ApplyBatchNames(BatchCard card)
    {
        BatchNameText.Text=card.Name;projectionWindow?.UpdateBatch(card.Name);var names=card.Names();for(var i=0;i<4;i++)Lanes[i].Participant=names[i];
    }
    private void NextBatch_Click(object sender,RoutedEventArgs e)
    {
        if(startingTiming)return;
        if(!saved&&Lanes.Any(l=>l.State!=LaneState.Ready||l.PenaltySeconds>0))
        {
            if(Lanes.Any(l=>l.State!=LaneState.Finished))
            {StatusText.Text="请先完成本组全部赛道，再点击“下一组”自动保存成绩；如需放弃本组，请点击“重置本组”。";return;}
            if(!SaveCurrentResults())return;
        }
        if(batchQueue.Cards.Count==0)
        {
            MainTabs.SelectedIndex=0;
            StatusText.Text="请先在赛前准备增加批次卡片或导入人员名单。";return;
        }
        var next=batchQueue.PeekNext();
        if(next==null){MessageBox.Show(this,saved?"最后一组成绩已保存，当前成绩保留。可在赛前准备增加批次，或载入指定卡片重新测试。":"已经是最后一组，当前信息保留。可在赛前准备增加批次，或载入指定卡片重新测试。","批次队列结束");return;}
        LoadPreparedBatch(next);
    }
    private void UpdateQueueStatus()
    {
        var next=batchQueue.PeekNext();var current=batchQueue.Current;
        QueueStatusText.Text=batchQueue.Cards.Count==0?"请在赛前准备配置批次":$"{(current==null?"待载入批次":$"当前第 {current.Order}/{batchQueue.Cards.Count} 组")} · {(next==null?"已到最后一组":$"下一组：{next.Name}")}";
        QueueStatusText.MaxWidth=370;QueueStatusText.TextTrimming=TextTrimming.CharacterEllipsis;QueueStatusText.ToolTip=QueueStatusText.Text;
    }
    private void WatchCards()
    {
        foreach(var old in watchedCards.Where(c=>!Preparation.Batches.Contains(c)).ToList()){old.PropertyChanged-=PreparationEdited;watchedCards.Remove(old);}
        foreach(var card in Preparation.Batches)if(watchedCards.Add(card))card.PropertyChanged+=PreparationEdited;
    }
    private void RosterChanged(object? sender,NotifyCollectionChangedEventArgs e)
    {WatchCards();batchQueue.Reindex();UpdateQueueStatus();UpdateButtons();MarkPreparationDirty();}
    private void PreparationEdited(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(BatchCard.Order) or nameof(BatchCard.IsCurrent) or nameof(BatchCard.IsSelected))return;
        if(sender is BatchCard card&&batchQueue.CurrentId==card.Id&&!saved&&Lanes.All(l=>l.State==LaneState.Ready&&l.PenaltySeconds==0))ApplyBatchNames(card);
        UpdateStartButtonArming();
        UpdateQueueStatus();MarkPreparationDirty();
    }
    private void MarkPreparationDirty()
    {
        if(!preparationReady)return;preparationDirty=true;PrepPanel.ShowSaveStatus("赛前配置已修改，正在自动保存…");preparationSaveTimer.Stop();preparationSaveTimer.Start();
    }
    private bool SavePreparation(bool manual=false)
    {
        if(!manual&&!preparationDirty)return true;
        if(!preparationStorageAvailable&&!manual){PrepPanel.ShowSaveStatus("原配置读取失败，自动保存已停用；请点击保存赛前配置备份原文件后保存。");return false;}
        try
        {
            if(!preparationStorageAvailable&&File.Exists(preparationPath))File.Copy(preparationPath,preparationPath+".backup-"+Guid.NewGuid().ToString("N"));
            PreparationStore.Save(preparationPath,Preparation);preparationStorageAvailable=true;preparationDirty=false;
            PrepPanel.ShowSaveStatus($"赛前配置已保存 · {DateTime.Now:HH:mm:ss}");return true;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
        {
            PrepPanel.ShowSaveStatus($"赛前配置保存失败：{ex.Message}");if(manual)ShowFileError(ex);return false;
        }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Results.Count == 0) { MessageBox.Show(this, "暂无已保存成绩。", "导出成绩"); return; }
        var dialog = new SaveFileDialog { Title = "导出成绩", Filter = "CSV 成绩表 (*.csv)|*.csv", FileName = $"体能测试成绩_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { ResultStore.Export(dialog.FileName, Results); StatusText.Text = $"已导出 {Results.Count} 条成绩。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowFileError(ex); }
    }
    private void RefreshHistoryRankings()
    {
        HistoryResults.Clear();
        foreach(var row in ResultRanking.Calculate(Results))HistoryResults.Add(row);
    }
    private bool updatingHistorySelection;
    private void HistoryCheck_MouseDown(object sender,MouseButtonEventArgs e)
    {
        if(ItemsControl.ContainerFromElement(HistoryGrid,(DependencyObject)sender) is DataGridRow row)
        {row.IsSelected=!row.IsSelected;e.Handled=true;}
    }
    private void SelectAllHistory_Click(object sender,RoutedEventArgs e)
    {
        if(updatingHistorySelection||HistoryGrid==null)return;
        if(SelectAllHistory.IsChecked==true)HistoryGrid.SelectAll();else HistoryGrid.UnselectAll();
    }
    private void HistorySelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(DeleteHistoryButton==null||SelectAllHistory==null)return;
        var count=HistoryGrid.SelectedItems.Count;
        DeleteHistoryButton.IsEnabled=count>0;
        DeleteHistoryButton.Content=count>0?$"删除选中成绩（{count}）":"删除选中成绩";
        updatingHistorySelection=true;
        try{SelectAllHistory.IsChecked=count==0?false:count==HistoryResults.Count?true:null;}
        finally{updatingHistorySelection=false;}
    }
    private void DeleteHistory_Click(object sender,RoutedEventArgs e)
    {
        var selected=HistoryGrid.SelectedItems.Cast<RankedRaceResult>().ToArray();
        if(selected.Length==0)return;
        if(MessageBox.Show(this,$"确定删除选中的 {selected.Length} 条历史成绩吗？删除后无法恢复，剩余成绩的名次将重新计算。","确认删除历史成绩",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        try{DeleteHistoryRecords(selected);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or InvalidOperationException){ShowFileError(ex);}
    }
    private void DeleteHistoryRecords(IEnumerable<RankedRaceResult> selected)
    {
        if(!storageAvailable)throw new InvalidOperationException("历史成绩文件不可用，不能删除。请检查文件后重启软件。");
        var removed=new HashSet<RaceResult>(selected.Select(row=>row.Result),ReferenceEqualityComparer.Instance);
        var remaining=Results.Where(result=>!removed.Contains(result)).ToArray();
        var count=Results.Count-remaining.Length;
        if(count==0)return;
        // Persist first: a failed write must leave the visible history untouched.
        ResultStore.Save(dataPath,remaining);
        Results.Clear();foreach(var result in remaining)Results.Add(result);
        RefreshHistoryRankings();
        StatusText.Text=$"已删除 {count} 条历史成绩，剩余 {Results.Count} 条，名次已更新。";
    }
    private void ShowFileError(Exception ex) => MessageBox.Show(this, $"文件操作失败，请检查路径和文件占用情况。\n{ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!saved && Lanes.Any(l => l.State != LaneState.Ready || l.PenaltySeconds > 0) && MessageBox.Show(this,
            "当前计时或成绩尚未保存，确定退出吗？", "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        if(preparationDirty&&!SavePreparation()&&MessageBox.Show(this,"赛前配置尚未成功保存。是否仍然退出？","赛前配置未保存",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes){e.Cancel=true;return;}
        closing=true; finishButtons.InvalidatePending(); refreshTimer.Stop();preparationSaveTimer.Stop();
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if(MainTabs.SelectedItem!=TimingTab)return;
        if (e.Key == Key.Enter && StartButton.IsEnabled) { Start_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.P && PauseAllButton.IsEnabled) { PauseAll_Click(sender, e); e.Handled = true; }
    }
}
