using FitnessTimer.Core;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++; Console.WriteLine($"PASS: {message}");
}
long tick = 0;
var lane = new LaneTimer(1, () => tick, 1000);
lane.Start(); tick = 1250;
Check(lane.BaseDisplay == "00:01.25", "显示实际经过时间");
lane.Pause(); tick = 9500;
Check(lane.BaseDisplay == "00:01.25", "暂停期间不累计");
lane.Start(); tick = 10000; lane.Finish(); tick = 20000;
Check(lane.BaseDisplay == "00:01.75", "继续后累计，完成后冻结");
lane.Start(); Check(lane.State == LaneState.Finished, "完成的赛道不会重新开始");
lane.AdjustPenalty(5, 1); lane.AdjustPenalty(15, 1);
Check(lane.TotalDisplay == "00:21.75", "两档罚时正确相加");
lane.AdjustPenalty(5, -1); lane.AdjustPenalty(5, -1);
Check(lane.FiveCount == 0 && lane.PenaltySeconds == 15, "罚时次数不会为负数");
lane.Reset(); Check(lane.State == LaneState.Ready && lane.TotalDisplay == "00:00.00", "重置清除计时与罚时");
var second = new LaneTimer(2, () => tick, 1000);
lane.Start(tick); second.Start(tick); tick += 500;
Check(lane.Elapsed == second.Elapsed, "统一起点的两条赛道时间一致");
lane.Pause(); tick += 250;
Check(second.Elapsed > lane.Elapsed, "独立暂停不会影响其他赛道");
Check(LaneTimer.Format(TimeSpan.FromMinutes(65) + TimeSpan.FromSeconds(2)) == "65:02.00", "超过一小时不回绕");
var directory = Path.Combine(Path.GetTempPath(), "FitnessTimerChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var json = Path.Combine(directory, "results.json");
var csv = Path.Combine(directory, "results.csv");
var result = new RaceResult("测试,批次", DateTimeOffset.Now, 1, "=1+1\"\n姓名", TimeSpan.FromMilliseconds(1750).Ticks, 1, 1);
ResultStore.Save(json, [result]);
Check(ResultStore.Load(json).Single() == result, "成绩持久化往返保留完整数据");
ResultStore.Save(json, [result, result]);
Check(ResultStore.Load(json).Count == 2, "重复保存可原子替换成绩文件");
ResultStore.Export(csv, [result]);
var bytes = File.ReadAllBytes(csv);
var text = File.ReadAllText(csv);
Check(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "CSV 携带中文 UTF-8 BOM");
Check(text.Contains("\"测试,批次\"") && text.Contains("\"'=1+1\"\"\n姓名\""), "CSV 正确转义并防止公式注入");
File.WriteAllText(json, "invalid json");
try { ResultStore.Load(json); throw new Exception("损坏数据未被发现"); }
catch (System.Text.Json.JsonException) { Check(true, "损坏的历史文件会报错，避免静默覆盖"); }
File.Delete(json); File.Delete(csv); Directory.Delete(directory);
Check(ButtonPacket.ParseHex("AA 01:ff-02").SequenceEqual(new byte[]{0xAA,1,0xFF,2}), "蓝牙十六进制解析支持空格、冒号和短横线");
foreach (var invalid in new[]{"", "1", "0G",new string('A',514)})
{
    try { ButtonPacket.ParseHex(invalid); throw new Exception("不合法的按键数据未被拒绝"); }
    catch (FormatException) { Check(true,"拒绝空、奇数位、非十六进制或过长按键数据"); }
}
Check(ButtonPacket.ParseUuid("FFE0") == Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb"), "支持蓝牙16位短UUID");
Check(ButtonPacket.ParseUuid("12345678") == Guid.Parse("12345678-0000-1000-8000-00805f9b34fb"), "支持蓝牙32位短UUID");
try { ButtonPacket.ParseUuid("bad-uuid"); throw new Exception("非法UUID未拒绝"); }
catch(FormatException) { Check(true,"非法UUID在连接前拒绝"); }
Check(ButtonPacket.Matches(new byte[]{1},new byte[]{1},false),"精确匹配按下数据");
Check(!ButtonPacket.Matches(new byte[]{1,0},new byte[]{1},false),"精确匹配拒绝多余字节");
Check(ButtonPacket.Matches(new byte[]{0xAA,1,7},new byte[]{0xAA,1},true),"前缀匹配保留可变序号字节");
Check(!ButtonPacket.Matches(new byte[]{0},new byte[]{1},false) && !ButtonPacket.Matches(new byte[]{1},Array.Empty<byte>(),true),"松开和空匹配规则不触发");
var gate = new ButtonPacketGate([1],[0],false);
Check(gate.TryPress([1]) && !gate.TryPress([1]),"长按重复通知只触发一次");
Check(!gate.TryPress([0]) && gate.TryPress([1]),"松开后允许下一次按下");
try { _=new ButtonPacketGate([1],[1,2],true); throw new Exception("按下与松开前缀重叠未拒绝"); }
catch(ArgumentException) { Check(true,"拒绝按下与松开规则重叠"); }
long radioClock=1000;
var radioLanes=Enumerable.Range(1,4).Select(n=>new LaneTimer(n,()=>radioClock,1000)).ToArray();
var buttons=new FinishButtonController(radioLanes,1000);
buttons.SetEnabled(true);
Check(!buttons.TryFinish(1,radioClock,buttons.Generation),"未开始时按按钮不能完成赛道");
foreach(var item in radioLanes) item.Start(radioClock);
var round=buttons.Generation;
radioClock=1800;
Check(buttons.TryFinish(1,1250,round) && radioLanes[0].BaseDisplay=="00:00.25", "按接收时刻完成，排除界面队列延迟");
Check(radioLanes[1].State==LaneState.Running && !buttons.TryFinish(1,1400,round),"只完成对应赛道，重复事件不覆盖成绩");
radioLanes[1].Pause();
Check(!buttons.TryFinish(2,1800,round),"暂停期间忽略终点按钮");
radioClock=2000;radioLanes[1].Start();radioClock=2200;
Check(!buttons.TryFinish(2,1900,round) && buttons.TryFinish(2,2100,round),"继续后拒绝暂停期间的延迟事件");
Check(!buttons.TryFinish(3,2300,round),"拒绝未来时间戳");
Check(!buttons.TryFinish(0,2200,round) && !buttons.TryFinish(5,2200,round),"拒绝无效赛道编号");
buttons.InvalidatePending();
Check(!buttons.TryFinish(3,2150,round),"换组或重新启用后丢弃旧队列事件");
buttons.SetEnabled(false);
Check(!buttons.TryFinish(3,2200,buttons.Generation),"停用蓝牙后按键不影响计时");
radioLanes[0].Reset();radioClock=1400;radioLanes[0].Start();radioClock=1500;buttons.SetEnabled(true);
Check(!buttons.TryFinish(1,1450,buttons.Generation),"500毫秒防抖跨组保留，阻止快速重复信号");
radioClock=2000;
Check(buttons.TryFinish(1,1900,buttons.Generation),"防抖窗口过后允许新组正常按下");
var cards=new System.Collections.ObjectModel.ObservableCollection<BatchCard>();
var queue=new BatchQueue(cards);
Check(queue.PeekNext()==null,"空名单没有下一组");
var a=new BatchCard {Name="甲组",Lane1="张三",Lane2="李四",Lane3="王五",Lane4="赵六"};
var b=new BatchCard {Name="乙组",Lane1="乙一",Lane2="乙二",Lane3="乙三",Lane4="乙四"};
var c=new BatchCard {Name="丙组",Lane1="丙一"};
cards.Add(a);cards.Add(b);cards.Add(c);queue.Reindex();
Check(queue.PeekNext()==a&&a.Order==1,"未载入时从第一张卡片开始");
queue.Load(a);Check(queue.PeekNext()==b&&a.IsCurrent&&!b.IsCurrent,"载入后按顺序进入下一卡片");
Check(queue.Current!.Names().SequenceEqual(new[]{"张三","李四","王五","赵六"}),"批次按四条赛道保留姓名对应关系");
cards.Move(1,2);queue.Reindex();Check(queue.PeekNext()==c,"调整卡片顺序后下一组随之更新");
queue.Load(b);Check(queue.PeekNext()==null,"最后一组不自动循环覆盖第一组");
queue.Remove(b);Check(queue.PeekNext()==null,"移除末尾当前组仍保持队列结束");
queue.Load(a);queue.Remove(a);Check(queue.PeekNext()==c,"移除当前卡片后继续原位置下一张");
queue.Replace(new[]{a,b});Check(queue.Current==null&&queue.PeekNext()==a,"替换名单后从新名单首组开始");
queue.Replace(new[]{a,b,c});queue.Load(b);queue.RemoveMany(new[]{a,b});
Check(cards.Count==1&&cards[0]==c&&c.Order==1&&queue.PeekNext()==c,"批量删除包含当前组时保留剩余卡片顺序和下一组位置");
queue.RemoveMany(cards);
Check(cards.Count==0&&queue.Current==null&&queue.PeekNext()==null,"全选删除支持空名单且不会修改枚举中的集合");
cards.Add(a);queue.Reindex();Check(queue.PeekNext()==a,"全部删除后添加新批次仍从首组开始");
var prepFolder=Path.Combine(Path.GetTempPath(),"FitnessPreparation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(prepFolder);
var prepPath=Path.Combine(prepFolder,"preparation.json");
var settings=new PreparationSettings {SoundEnabled=false,ActivityName="测试活动",Organizer="测试主办单位"};settings.Batches.Add(a);settings.Batches.Add(b);
settings.Sounds[0].FilePath=@"C:\声音\开始.mp3";settings.Sounds[2].Enabled=false;
a.IsSelected=true;PreparationStore.Save(prepPath,settings);var loaded=PreparationStore.Load(prepPath);
Check(loaded.ActivityName==settings.ActivityName&&loaded.Organizer==settings.Organizer,"活动信息随赛前配置持久化");
Check(!loaded.Batches[0].IsSelected,"勾选状态不持久化，重启后避免误删旧选择");
Check(!loaded.SoundEnabled&&loaded.Batches[0].Id==a.Id&&loaded.Batches[1].Name=="乙组","赛前配置持久化保留顺序和稳定卡片标识");
Check(loaded.Sounds[0].FilePath==@"C:\声音\开始.mp3"&&!loaded.Sounds[2].Enabled,"保存与恢复声音路径及独立启用状态");
Check(!loaded.Batches[0].IsCurrent&&loaded.Batches[0].Order==0,"当前测试标记不写入永久名单");
var workbookPath=Path.Combine(prepFolder,"名单.xlsx");
var literal=new BatchCard {Name="测试,第一组",Lane1="=1+1",Lane2="王\"小明",Lane3="中文姓名",Lane4=""};
BatchExcel.Export(workbookPath,new[]{literal,b});var imported=BatchExcel.Import(workbookPath);
Check(imported.Count==2&&imported[0].Names().SequenceEqual(literal.Names())&&imported[1].Name==b.Name,"Excel往返保留中文、引号、空姓名和批次顺序");
using(var book=new ClosedXML.Excel.XLWorkbook(workbookPath))
{
    Check(!book.Worksheet(1).Cell(2,3).HasFormula&&book.Worksheet(1).Cell(2,3).GetString()=="=1+1","Excel将等号开头的姓名写为文本而非公式");
    book.Worksheet(1).Cell(2,1).Value=2;book.Worksheet(1).Cell(3,1).Value=1;book.Save();
}
Check(BatchExcel.Import(workbookPath)[0].Name==b.Name,"Excel按顺序列排序导入，支持表内调整组次");
using(var book=new ClosedXML.Excel.XLWorkbook(workbookPath)){book.Worksheet(1).Cell(2,1).Value=1;book.Save();}
try{BatchExcel.Import(workbookPath);throw new Exception("重复组次未被拒绝");}catch(InvalidDataException){Check(true,"Excel拒绝重复顺序，导入失败不返回部分名单");}
BatchExcel.Export(workbookPath,new[]{a});
using(var book=new ClosedXML.Excel.XLWorkbook(workbookPath)){book.Worksheet(1).Cell(2,3).FormulaA1="1+1";book.Save();}
try{BatchExcel.Import(workbookPath);throw new Exception("名单公式未被拒绝");}catch(InvalidDataException){Check(true,"Excel拒绝公式单元格，不执行名单中的公式");}
BatchExcel.Export(workbookPath,Array.Empty<BatchCard>());
using(var book=new ClosedXML.Excel.XLWorkbook(workbookPath)){Check(book.Worksheet(1).Cell(1,6).GetString()=="4道姓名","空白模板保留全部六列表头");}
try{BatchExcel.Import(workbookPath);throw new Exception("空模板未被拒绝");}catch(InvalidDataException){Check(true,"未填写的模板不会覆盖现有卡片");}
File.WriteAllText(prepPath,"{\"Batches\":[null]}");
try{PreparationStore.Load(prepPath);throw new Exception("损坏配置未被拒绝");}catch(InvalidDataException){Check(true,"损坏的赛前名单不会静默加载");}
File.Delete(prepPath);File.Delete(workbookPath);Directory.Delete(prepFolder);
var buttonEndpoint=new BleEndpoint(Guid.Parse("A1234567-0000-0000-0000-000000000001"),Guid.Parse("A1234567-0000-0000-0000-000000000002"));
Check(BleEndpointSelection.Choose(new[]{buttonEndpoint})==buttonEndpoint,"自动连接选择唯一可用的按钮通道");
var batteryEndpoint=new BleEndpoint(ButtonPacket.ParseUuid("180F"),ButtonPacket.ParseUuid("2A19"));
Check(BleEndpointSelection.Choose(new[]{batteryEndpoint,buttonEndpoint})==buttonEndpoint,"自动连接排除电池通知，避免误作终点信号");
Check(!BleEndpointSelection.CanAutoInspect(ButtonPacket.ParseUuid("1812")),"键盘式HID设备不被默认当成普通按钮通道");
try{BleEndpointSelection.Choose(new[]{batteryEndpoint});throw new Exception("不应选择电池服务");}catch(InvalidOperationException){Check(true,"无可用按钮通道时明确拒绝自动连接");}
try{BleEndpointSelection.Choose(new[]{buttonEndpoint,buttonEndpoint with{Characteristic=Guid.NewGuid()}});throw new Exception("不应猜测多个通道");}catch(InvalidOperationException){Check(true,"多个通知通道时不随意选择");}
try{BleEndpointSelection.Choose(new[]{buttonEndpoint,buttonEndpoint});throw new Exception("相同UUID不同实例不应合并");}catch(InvalidOperationException){Check(true,"同UUID的多个服务实例也视为有歧义");}
var autoProfile=new BluetoothButtonProfile(1,123,"按钮","","","01",false,Automatic:true,ProtocolConfirmed:false);
var profileRoundTrip=System.Text.Json.JsonSerializer.Deserialize<BluetoothButtonProfile>(System.Text.Json.JsonSerializer.Serialize(autoProfile))!;
Check(profileRoundTrip.Automatic&&!profileRoundTrip.ProtocolConfirmed&&profileRoundTrip.ServiceUuid=="","自动模式可保存无UUID、待识别按键的配置");
var rankTime=DateTimeOffset.UtcNow;
var rankInputs=new[]{
    new RaceResult("同名批次",rankTime,1,"甲",TimeSpan.FromSeconds(10).Ticks,1,0),
    new RaceResult("同名批次",rankTime,2,"乙",TimeSpan.FromSeconds(12).Ticks,0,0),
    new RaceResult("同名批次",rankTime,3,"丙",TimeSpan.FromSeconds(13).Ticks,0,0),
    new RaceResult("同名批次",rankTime,4,"丁",TimeSpan.FromSeconds(14).Ticks,0,0),
    new RaceResult("同名批次",rankTime.AddSeconds(1),1,"戊",TimeSpan.FromSeconds(11).Ticks,0,0)};
var rankedRows=ResultRanking.Calculate(rankInputs);
Check(rankedRows.Select(r=>r.BatchRank).SequenceEqual(new[]{4,1,2,3,1}),"本批次排名包含罚时且同名不同测试批次分别排名");
Check(rankedRows.Select(r=>r.OverallRank).SequenceEqual(new[]{5,2,3,4,1}),"所有历史选手按总用时计算总排名");
var tied=ResultRanking.Calculate(new[]{rankInputs[1],rankInputs[1] with{Lane=3,ElapsedTicks=rankInputs[1].ElapsedTicks+1},rankInputs[2]});
Check(tied.Select(r=>r.BatchRank).SequenceEqual(new[]{1,1,3}),"显示到百分秒的相同总用时并列排名，下一名跳位");
Check(ResultRanking.Calculate(Array.Empty<RaceResult>()).Count==0,"空历史可正常计算排名");
Check(rankedRows.Select(r=>r.Result).SequenceEqual(rankInputs),"排名计算保留历史记录展示顺序及原始成绩");
var startGate=new StartButtonGate(1000);
Check(!startGate.TryAccept(100,0,100),"开始键未就绪时不接受发令");
startGate.SetArmed(true,1000);var startRevision=startGate.Generation;
Check(!startGate.TryAccept(999,startRevision,1100)&&!startGate.TryAccept(1200,startRevision,1100),"开始键拒绝就绪前和未来时间的信号");
Check(startGate.TryAccept(1050,startRevision,1100)&&!startGate.TryAccept(1060,startRevision,1100),"开始键一次发令后不接受重复事件");
startGate.SetArmed(true,1200);var rearmed=startGate.Generation;
Check(!startGate.TryAccept(1250,startRevision,1300)&&!startGate.TryAccept(1300,rearmed,1300),"重置或切页后旧事件失效且保留500毫秒防抖");
Check(startGate.TryAccept(1600,rearmed,1700),"防抖时间之后新一组可正常发令");
Check(QuizzerProtocol.Matches(ButtonPacket.ParseUuid("FFE0"),ButtonPacket.ParseUuid("FFE2")),"抽签固件按FFE0服务及FFE2通知识别");
Check(!QuizzerProtocol.Matches(ButtonPacket.ParseUuid("FFE0"),ButtonPacket.ParseUuid("FFE1")),"写命令通道FFE1不会被误认为按下通知");
Check(QuizzerProtocol.Command==ButtonPacket.ParseUuid("FFE1")&&QuizzerProtocol.ArmCommand().SequenceEqual(new byte[]{1}),"抽签按钮准备命令为FFE1写入单字节01");
Console.WriteLine($"全部 {checks} 项检查通过。");
