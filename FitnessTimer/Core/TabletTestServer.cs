using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FitnessTimer.Core;

public sealed record TabletLaneSnapshot(string Activity,string Organizer,string Batch,string Participant,string State,string BaseTime,int PenaltySeconds,string TotalTime);

// Read-only, token-scoped LAN endpoint. No remote timer controls or file access.
public sealed class TabletTestServer : IDisposable
{
    private readonly object sync=new();
    private readonly SemaphoreSlim capacity=new(16);
    private TcpListener? listener;
    private CancellationTokenSource? shutdown;
    private string token="",snapshot="{}";
    private DateTime lastTablet,lastLocal;
    private string tabletAddress="";
    public bool IsRunning {get{lock(sync)return listener!=null;}}
    public int Port {get;private set;}
    public bool TabletOnline {get{lock(sync)return listener!=null&&DateTime.UtcNow-lastTablet<TimeSpan.FromSeconds(8);}}
    public string ClientStatus {get{lock(sync)return listener==null?"服务已停止":DateTime.UtcNow-lastTablet<TimeSpan.FromSeconds(8)?$"1道平板已联通 · {tabletAddress}":DateTime.UtcNow-lastLocal<TimeSpan.FromSeconds(8)?"本机测试通过，等待平板连接":"等待1道平板连接";}}
    public string LocalUrl {get{lock(sync)return listener==null?"":$"http://127.0.0.1:{Port}/lane/1/{token}/";}}
    public IReadOnlyList<string> TabletUrls
    {
        get
        {
            lock(sync)
            {
                if(listener==null)return [];
                return NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback)
                    .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a.Address))
                    .Select(a=>$"http://{a.Address}:{Port}/lane/1/{token}/").Distinct().ToArray();
            }
        }
    }
    public void SetSnapshot(TabletLaneSnapshot value)=>Volatile.Write(ref snapshot,JsonSerializer.Serialize(value,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
    public void Start(int port=18760)
    {
        lock(sync)
        {
            if(listener!=null)return;
            var candidate=new TcpListener(IPAddress.Any,port);
            try{candidate.Start();}catch{candidate.Stop();throw;}
            token=RandomNumberGenerator.GetHexString(32);lastTablet=lastLocal=default;tabletAddress="";
            Port=((IPEndPoint)candidate.LocalEndpoint).Port;listener=candidate;shutdown=new();
            _=AcceptLoop(candidate,shutdown.Token,token);
        }
    }
    public void Stop()
    {
        lock(sync){shutdown?.Cancel();listener?.Stop();listener=null;shutdown?.Dispose();shutdown=null;lastTablet=lastLocal=default;}
    }
    private async Task AcceptLoop(TcpListener active,CancellationToken stop,string session)
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var client=await active.AcceptTcpClientAsync(stop);
                if(!capacity.Wait(0)){client.Dispose();continue;}
                _=Serve(client,stop,session);
            }
        }
        catch(Exception ex)when(ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {lock(sync){if(ReferenceEquals(listener,active)){listener=null;active.Stop();}}}
    }
    private async Task Serve(TcpClient client,CancellationToken stop,string session)
    {
        using(client)
        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var stream=client.GetStream();var bytes=new byte[8192];var used=0;string request="";
                while(used<bytes.Length)
                {
                    var count=await stream.ReadAsync(bytes.AsMemory(used),timeout.Token);if(count==0)return;used+=count;
                    request=Encoding.ASCII.GetString(bytes,0,used);if(request.Contains("\r\n\r\n"))break;
                }
                if(!request.Contains("\r\n\r\n")){await Reply(stream,431,"text/plain","Request too large",timeout.Token);return;}
                var line=request.Split("\r\n",2)[0].Split(' ');
                if(line.Length!=3){await Reply(stream,400,"text/plain","Bad request",timeout.Token);return;}
                if(line[0]!="GET"){await Reply(stream,405,"text/plain","Read only",timeout.Token);return;}
                var path=line[1].Split('?')[0];var root=$"/lane/1/{session}/";
                if(path!=root&&path!=root+"api"){await Reply(stream,404,"text/plain","Not found",timeout.Token);return;}
                lock(sync)
                {
                    if(listener==null||token!=session)return;
                    var remote=((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                    if(IPAddress.IsLoopback(remote))lastLocal=DateTime.UtcNow;
                    else{lastTablet=DateTime.UtcNow;tabletAddress=remote.ToString();}
                }
                await Reply(stream,200,path.EndsWith("api")?"application/json":"text/html",path.EndsWith("api")?Volatile.Read(ref snapshot):Page,timeout.Token);
            }
            catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException){ }
            finally{capacity.Release();}
        }
    }
    private static async Task Reply(NetworkStream stream,int status,string type,string body,CancellationToken stop)
    {
        var content=Encoding.UTF8.GetBytes(body);
        var header=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {content.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; frame-ancestors 'none'\r\n\r\n");
        await stream.WriteAsync(header,stop);await stream.WriteAsync(content,stop);
    }
    public void Dispose()=>Stop();
    private const string Page="""
<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>1道平板联通测试</title>
<style>body{margin:0;background:#0b1220;color:#f1f5f9;font-family:system-ui,sans-serif;padding:28px}main{max-width:850px;margin:auto}h1{font-size:30px}#link{color:#ffce73}.card{background:#172237;border-radius:18px;padding:24px;margin-top:24px}#total{font:700 clamp(44px,10vw,90px) monospace;color:#36dabc;margin:24px 0}p{line-height:1.7;color:#b5c5da}button{padding:14px 24px;border:0;border-radius:10px;background:#25d3b0;font-size:18px}strong{color:white}</style>
<main><h1 id="activity">1道平板联通测试</h1><p id="organizer"></p><div id="link">正在连接电脑服务…</div><div class="card"><h2>1号赛道</h2><p>批次 <strong id="batch">—</strong></p><p>选手 <strong id="name">—</strong> · <strong id="state">—</strong></p><div id="total">00:00.00</div><p>基础用时 <strong id="base">—</strong> · 罚时 <strong id="penalty">0</strong> 秒</p></div><p id="time"></p><button onclick="refresh()">测试连接</button><p>当前为只读联通测试页面，数据每秒刷新。计时操作仍在电脑端完成。</p></main>
<script>let busy=false;async function refresh(){if(busy)return;busy=true;const controller=new AbortController();const timeout=setTimeout(()=>controller.abort(),3500);try{const r=await fetch(new URL('api',location.href),{cache:'no-store',signal:controller.signal});if(!r.ok)throw Error();const s=await r.json();for(const [id,key] of Object.entries({activity:'activity',organizer:'organizer',batch:'batch',name:'participant',state:'state',total:'totalTime',base:'baseTime',penalty:'penaltySeconds'}))document.getElementById(id).textContent=s[key]??'—';document.getElementById('link').textContent='● 已联通电脑服务';document.getElementById('link').style.color='#36dabc';document.getElementById('time').textContent='最近同步 '+new Date().toLocaleTimeString();}catch{document.getElementById('link').textContent='● 连接中断，正在重试';document.getElementById('link').style.color='#ffce73';}finally{clearTimeout(timeout);busy=false;}}refresh();setInterval(refresh,1000);</script></html>
""";
}
