using System.ComponentModel;
using System.IO;
using System.Media;
using System.Windows.Media;
using FitnessTimer.Core;

namespace FitnessTimer;

public sealed class EventAudioPlayer : IDisposable
{
    private sealed class CachedAudio
    {
        public required string Path {get;init;}
        public required MediaPlayer Player {get;init;}
        public bool Ready,Failed;
    }
    private readonly PreparationSettings settings;
    private readonly Dictionary<SoundEvent,CachedAudio> cache=[];
    private readonly List<SoundCue> watched;
    private MediaPlayer? active;
    private readonly Dictionary<SoundEvent,SoundPlayer> tones=[];
    private bool disposed;
    public event Action<string>? PlaybackError;
    public EventAudioPlayer(PreparationSettings settings)
    {
        this.settings=settings;watched=settings.Sounds.ToList();
        foreach(var sound in Enum.GetValues<SoundEvent>())tones[sound]=CreateTone(sound);
        foreach(var cue in watched){cue.PropertyChanged+=CueChanged;Preload(cue);}
    }
    private void CueChanged(object? sender,PropertyChangedEventArgs e)
    {if(!disposed&&sender is SoundCue cue&&e.PropertyName==nameof(SoundCue.FilePath))Preload(cue);}
    private void Preload(SoundCue cue)
    {
        if(cache.Remove(cue.Event,out var old)){if(ReferenceEquals(active,old.Player))Stop();old.Player.Close();}
        if(string.IsNullOrWhiteSpace(cue.FilePath))return;
        try
        {
            var item=new CachedAudio{Path=cue.FilePath,Player=new MediaPlayer{Volume=1}};
            cache[cue.Event]=item;
            item.Player.MediaOpened+=(_,_)=>{if(!disposed&&cache.GetValueOrDefault(cue.Event)==item)item.Ready=true;};
            item.Player.MediaEnded+=(_,_)=>{if(ReferenceEquals(active,item.Player))Stop();};
            item.Player.MediaFailed+=(_,args)=>
            {
                item.Ready=false;item.Failed=true;
                if(disposed||cache.GetValueOrDefault(cue.Event)!=item)return;
                if(ReferenceEquals(active,item.Player)){Stop();Fallback(cue.Event);}
                PlaybackError?.Invoke($"{cue.Label}预加载失败：{args.ErrorException.Message}");
            };
            item.Player.Open(new Uri(System.IO.Path.GetFullPath(cue.FilePath),UriKind.Absolute));
        }
        catch(Exception ex)when(ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
        {PlaybackError?.Invoke($"{cue.Label}预加载失败：{ex.Message}");}
    }
    public void Play(SoundEvent sound,bool preview=false)
    {
        if(disposed)return;
        var cue=settings.Sounds.First(s=>s.Event==sound);
        if(!preview&&(!settings.SoundEnabled||!cue.Enabled))return;
        Stop();
        if(string.IsNullOrWhiteSpace(cue.FilePath)){Fallback(sound);return;}
        if(!cache.TryGetValue(sound,out var item)||!item.Ready||item.Path!=cue.FilePath)
        {
            // Never play an old start cue late when asynchronous opening finishes.
            Fallback(sound);
            PlaybackError?.Invoke($"{cue.Label}尚未加载完成或文件不可用，本次使用系统提示音。建议提前试听，并使用无前导静音的短WAV文件。");return;
        }
        try{active=item.Player;active.Position=TimeSpan.Zero;active.Play();}
        catch(Exception ex)when(ex is InvalidOperationException or IOException)
        {Stop();Fallback(sound);PlaybackError?.Invoke($"{cue.Label}播放失败：{ex.Message}");}
    }
    private static SoundPlayer CreateTone(SoundEvent sound)
    {
        const int rate=22050,count=2646;
        var stream=new MemoryStream();
        using(var writer=new BinaryWriter(stream,System.Text.Encoding.ASCII,true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+count*2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));writer.Write(16);
            writer.Write((short)1);writer.Write((short)1);writer.Write(rate);writer.Write(rate*2);
            writer.Write((short)2);writer.Write((short)16);writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));writer.Write(count*2);
            var frequency=sound is SoundEvent.Start or SoundEvent.Resume?1000:sound==SoundEvent.Finish?1400:750;
            for(var i=0;i<count;i++)
            {
                var envelope=Math.Min(1.0,Math.Min(i,count-1-i)/110.0);
                writer.Write((short)(7000*envelope*Math.Sin(2*Math.PI*frequency*i/rate)));
            }
        }
        stream.Position=0;var tone=new SoundPlayer(stream);tone.Load();return tone;
    }
    private void Fallback(SoundEvent sound)
    {
        try{tones[sound].Play();}
        catch(Exception ex)when(ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {PlaybackError?.Invoke($"提示音播放失败：{ex.Message}");}
    }
    public void Stop(){foreach(var tone in tones.Values)tone.Stop();var old=active;active=null;if(old!=null){old.Pause();old.Position=TimeSpan.Zero;}}
    public void Dispose()
    {
        if(disposed)return;disposed=true;Stop();
        foreach(var cue in watched)cue.PropertyChanged-=CueChanged;
        foreach(var item in cache.Values)item.Player.Close();cache.Clear();
        foreach(var tone in tones.Values){var stream=tone.Stream;tone.Dispose();stream?.Dispose();}tones.Clear();
    }
}
