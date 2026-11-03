using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FitnessTimer.Core;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field,T value,[CallerMemberName] string? name=null)
    { if(EqualityComparer<T>.Default.Equals(field,value))return;field=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name)); }
}
public sealed class BatchCard : ObservableModel
{
    public Guid Id { get; set; }=Guid.NewGuid();
    private string name="",lane1="",lane2="",lane3="",lane4="";
    private int order;
    private bool current;
    private bool selected;
    public string Name { get=>name;set=>Set(ref name,value); }
    public string Lane1 { get=>lane1;set=>Set(ref lane1,value); }
    public string Lane2 { get=>lane2;set=>Set(ref lane2,value); }
    public string Lane3 { get=>lane3;set=>Set(ref lane3,value); }
    public string Lane4 { get=>lane4;set=>Set(ref lane4,value); }
    [JsonIgnore] public int Order { get=>order;set=>Set(ref order,value); }
    [JsonIgnore] public bool IsCurrent { get=>current;set=>Set(ref current,value); }
    [JsonIgnore] public bool IsSelected { get=>selected;set=>Set(ref selected,value); }
    public string[] Names()=>[Lane1,Lane2,Lane3,Lane4];
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(Name)||Name.Length>80)throw new InvalidDataException("批次名称不能为空，且不能超过 80 个字。");
        if(Names().Any(n=>n==null||n.Length>40))throw new InvalidDataException($"批次“{Name}”的姓名不能超过 40 个字。");
    }
}
public enum SoundEvent { Start, Finish, Penalty, Pause, Resume }
public sealed class SoundCue : ObservableModel
{
    public SoundEvent Event { get; set; }
    private string path="";
    private bool enabled=true;
    public string FilePath { get=>path;set=>Set(ref path,value); }
    public bool Enabled { get=>enabled;set=>Set(ref enabled,value); }
    [JsonIgnore] public string Label => Event switch { SoundEvent.Start=>"开始声音",SoundEvent.Finish=>"完成声音",SoundEvent.Penalty=>"罚时声音",SoundEvent.Pause=>"暂停声音",_=>"继续声音" };
}
public sealed class PreparationSettings : ObservableModel
{
    private string activityName="体能测试计时系统",organizer="";
    public string ActivityName { get=>activityName;set=>Set(ref activityName,value); }
    public string Organizer { get=>organizer;set=>Set(ref organizer,value); }
    private bool soundEnabled=true;
    public bool SoundEnabled { get=>soundEnabled;set=>Set(ref soundEnabled,value); }
    public ObservableCollection<BatchCard> Batches { get;set; }=[];
    public ObservableCollection<SoundCue> Sounds { get;set; }=new(Enum.GetValues<SoundEvent>().Select(e=>new SoundCue { Event=e }));
}
public static class PreparationStore
{
    public static PreparationSettings Load(string path)
    {
        if(!File.Exists(path))return new();
        var settings=JsonSerializer.Deserialize<PreparationSettings>(File.ReadAllText(path))??throw new InvalidDataException("赛前配置为空。");
        if(settings.ActivityName==null||settings.Organizer==null||settings.ActivityName.Length>80||settings.Organizer.Length>100)
            throw new InvalidDataException("活动名称或主办单位配置损坏。");
        if(settings.Batches==null||settings.Sounds==null||settings.Batches.Count>5000||settings.Batches.Any(b=>b==null||b.Id==Guid.Empty)||settings.Batches.Select(b=>b.Id).Distinct().Count()!=settings.Batches.Count)
            throw new InvalidDataException("批次配置损坏或存在重复标识。");
        if(settings.Sounds.Count!=5||settings.Sounds.Any(s=>s==null||s.FilePath==null)||!settings.Sounds.Select(s=>s.Event).Order().SequenceEqual(Enum.GetValues<SoundEvent>()))
            throw new InvalidDataException("声音配置不完整。");
        foreach(var batch in settings.Batches)
            if(batch.Name==null||batch.Names().Any(n=>n==null))throw new InvalidDataException("批次名称或姓名数据损坏。");
        return settings;
    }
    public static void Save(string path,PreparationSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(settings,new JsonSerializerOptions { WriteIndented=true }));
        File.Move(path+".tmp",path,true);
    }
}
