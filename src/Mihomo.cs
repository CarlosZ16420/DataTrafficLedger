using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DataTrafficLedger {
internal sealed class MihomoClient {
    public string PipeName="";
    public uint ServerPid;
    private readonly string configured;
    public MihomoClient(string configuredPipe="") { configured=configuredPipe; }
    public static IEnumerable<string> Discover() {
        return Directory.GetFiles(@"\\.\pipe\").Select(System.IO.Path.GetFileName).Where(x=>x.StartsWith("verge-mihomo",StringComparison.OrdinalIgnoreCase)||x.Equals("mihomo",StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.IndexOf("production",StringComparison.OrdinalIgnoreCase)>=0).ThenBy(x=>x);
    }
    public Dictionary<string,object> Get(string endpoint) {
        if(!endpoint.StartsWith("/",StringComparison.Ordinal)||endpoint.Contains("\r")||endpoint.Contains("\n"))throw new ArgumentException("无效接口");
        if(PipeName!="")try{return Request(PipeName,endpoint);}catch{PipeName="";}
        var candidates=new List<string>();if(configured!="")candidates.Add(configured.Replace(@"\\.\pipe\", ""));
        candidates.AddRange(Discover());Exception last=null;
        foreach(string name in candidates.Distinct())try{var r=Request(name,endpoint);if(endpoint=="/connections")Snapshot.Parse(r);PipeName=name;return r;}catch(Exception ex){last=ex;}
        throw new IOException("未连接到 Mihomo，请确认 Clash Verge 正在运行。",last);
    }
    private Dictionary<string,object> Request(string name,string endpoint) {
        using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous)) {
            pipe.Connect(500);uint pid;if(GetNamedPipeServerProcessId(pipe.SafePipeHandle,out pid))ServerPid=pid;byte[] request=Encoding.ASCII.GetBytes("GET "+endpoint+" HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            var operation=Task.Run(async()=>{await pipe.WriteAsync(request,0,request.Length).ConfigureAwait(false);using(var buffer=new MemoryStream()){byte[] chunk=new byte[16384];int n;while((n=await pipe.ReadAsync(chunk,0,chunk.Length).ConfigureAwait(false))>0){if(buffer.Length+n>16*1024*1024)throw new IOException("连接响应超过安全上限");buffer.Write(chunk,0,n);}return buffer.ToArray();}});
            if(!operation.Wait(3500)){pipe.Dispose();operation.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);throw new TimeoutException("Mihomo 读取超时");}
            return Json.Read(DecodeHttp(operation.GetAwaiter().GetResult()));
        }
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
    internal static string DecodeHttp(byte[] bytes) {
        int end=-1;for(int i=0;i+3<bytes.Length;i++)if(bytes[i]==13&&bytes[i+1]==10&&bytes[i+2]==13&&bytes[i+3]==10){end=i;break;}
        if(end<0)throw new InvalidDataException("HTTP 头部不完整");
        string header=Encoding.ASCII.GetString(bytes,0,end);if(!header.StartsWith("HTTP/1.1 200 ",StringComparison.Ordinal)&&!header.StartsWith("HTTP/1.0 200 ",StringComparison.Ordinal))throw new IOException("Mihomo 接口未返回成功状态");
        int pos=end+4;if(header.IndexOf("transfer-encoding: chunked",StringComparison.OrdinalIgnoreCase)<0)return Encoding.UTF8.GetString(bytes,pos,bytes.Length-pos);
        using(var output=new MemoryStream()) {
            while(pos<bytes.Length){int e=pos;while(e+1<bytes.Length&&!(bytes[e]==13&&bytes[e+1]==10))e++;if(e+1>=bytes.Length)throw new InvalidDataException("分块头部不完整");
                int n=Convert.ToInt32(Encoding.ASCII.GetString(bytes,pos,e-pos).Split(';')[0],16);pos=e+2;if(n==0)return Encoding.UTF8.GetString(output.ToArray());
                if(n<0||pos+n+2>bytes.Length||bytes[pos+n]!=13||bytes[pos+n+1]!=10)throw new InvalidDataException("分块数据不完整");output.Write(bytes,pos,n);pos+=n+2;}
            throw new InvalidDataException("分块结束标记缺失");
        }
    }
}
internal sealed class Collector : IDisposable {
    private readonly Store store;private readonly string data;private readonly LedgerConfig config;private readonly CancellationTokenSource cancel=new CancellationTokenSource();private Task worker;
    private readonly List<Usage> pending=new List<Usage>();private readonly List<DeltaResult> totals=new List<DeltaResult>();
    public volatile bool Connected;public volatile bool Tun;public string Version="",Error="正在连接 Clash Verge…",Pipe="";public long LastSample;public int ConnectionCount;public bool StorageHealthy=true;private bool disposed;
    public Collector(Store store,LedgerConfig config,string data){this.store=store;this.config=config;this.data=data;}
    public void Start(){worker=Task.Run((Action)Run);}
    private void Run(){
        var tracker=new DeltaTracker();var client=new MihomoClient(config.Pipe);long lastFlush=0,lastConfig=0,lastPrune=0,lastGap=0,lastState=0;bool wasConnected=false;uint corePid=0;
        long previousHeartbeat; if(long.TryParse(store.GetMeta("collector_heartbeat"),out previousHeartbeat)&&Clock.Now-previousHeartbeat>8)store.Event("gap","上次记录后有未监控时段："+DateTimeOffset.FromUnixTimeSeconds(previousHeartbeat).ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss")+" 至本次启动");
        store.Event("start","后台采集已启动；已有连接建立基线，从现在起记录。");
        while(!cancel.IsCancellationRequested){long now=Clock.Now;var watch=System.Diagnostics.Stopwatch.StartNew();
            try {
                var snapshot=Snapshot.Parse(client.Get("/connections"));
                if(corePid!=0&&client.ServerPid!=0&&corePid!=client.ServerPid){tracker.Reset();store.Event("reset","检测到 Mihomo 进程更换，已重新建立基线。");}corePid=client.ServerPid;
                if(!wasConnected){tracker.Reset();store.Event("connected","已连接 Clash Verge，本次连接先建立流量基线。");}
                var delta=tracker.Observe(snapshot,now);if(delta.Reset)store.Event("reset","检测到内核计数重置，已重新建立基线。");
                pending.AddRange(delta.Usage);if(!delta.Baseline)totals.Add(TimedDelta.From(delta,now));
                Connected=true;wasConnected=true;Error="";LastSample=now;ConnectionCount=snapshot.Connections.Count;Pipe=client.PipeName;
                if(now-lastConfig>=30){try{var core=client.Get("/configs");Tun=Json.Bool(Json.Obj(core,"tun"),"enable");Version=Json.Str(client.Get("/version"),"version");}catch{}lastConfig=now;}
                if(pending.Count>50000) {store.Event("gap","磁盘写入长时间失败，内存缓存已到上限，部分记录无法保存。");pending.RemoveRange(0,pending.Count-50000);}
            } catch(Exception ex){Connected=false;Error=ex.GetBaseException().Message;if(wasConnected||now-lastGap>300){try{store.Event("gap","Mihomo 采集暂时中断；恢复后会重新建立基线，未监控时段不补记。 "+Error);}catch{}lastGap=now;}wasConnected=false;tracker.Reset();}
            if(now-lastFlush>=5){try{Flush();StorageHealthy=true;}catch{StorageHealthy=false;}lastFlush=now;}
            if(now-lastPrune>=3600){try{store.Prune(config.RetentionDays);}catch{}lastPrune=now;}
            if(now-lastState>=5)try{AtomicFile.Write(Path.Combine(data,"capture-state.json"),Json.Write(new{at=now,tun=Tun,connected=Connected}));lastState=now;}catch{}
            int delay=Connected?1000:5000;cancel.Token.WaitHandle.WaitOne(Math.Max(50,delay-(int)watch.ElapsedMilliseconds));
        }
        try{Flush();store.Event("stop","后台采集正常停止。");}catch{}
    }
    public void Flush(){store.Write(pending,totals,"collector_heartbeat");pending.Clear();totals.Clear();}
    public object Status(){return new{connected=Connected,version=Version,pipe=Pipe,lastSample=LastSample,connections=ConnectionCount,tun=Tun,error=Error,storageHealthy=StorageHealthy};}
    public void Dispose(){if(disposed)return;disposed=true;cancel.Cancel();if(worker!=null)worker.Wait(10000);cancel.Dispose();}
}
}
