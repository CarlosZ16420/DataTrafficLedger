using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public string CoreIdentity="";
    public bool CoreIdentityVerified;
    private readonly string configured;
    private readonly CancellationToken cancellation;
    public MihomoClient(string configuredPipe="",CancellationToken cancellation=default(CancellationToken)) { configured=configuredPipe;this.cancellation=cancellation; }
    public static IEnumerable<string> Discover() {
        return Directory.GetFiles(@"\\.\pipe\").Select(System.IO.Path.GetFileName).Where(x=>x.StartsWith("verge-mihomo",StringComparison.OrdinalIgnoreCase)||x.Equals("mihomo",StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.IndexOf("production",StringComparison.OrdinalIgnoreCase)>=0).ThenBy(x=>x);
    }
    public Dictionary<string,object> Get(string endpoint) {
        if(!endpoint.StartsWith("/",StringComparison.Ordinal)||endpoint.Contains("\r")||endpoint.Contains("\n"))throw new ArgumentException("无效接口");
        cancellation.ThrowIfCancellationRequested();
        if(PipeName!="")try{return Request(PipeName,endpoint);}catch(OperationCanceledException){throw;}catch{PipeName="";}
        Exception last=null;
        if(configured!=""){string name=configured.Replace(@"\\.\pipe\", "");try{var r=Request(name,endpoint);if(endpoint=="/connections")Snapshot.Parse(r);PipeName=name;return r;}catch(OperationCanceledException){throw;}catch(Exception ex){throw new IOException("指定的 Mihomo 管道暂时不可用。",ex);}}
        foreach(string name in Discover().Distinct())try{var r=Request(name,endpoint);if(endpoint=="/connections")Snapshot.Parse(r);PipeName=name;return r;}catch(OperationCanceledException){throw;}catch(Exception ex){last=ex;}
        throw new IOException("未连接到 Mihomo，请确认 Clash Verge 正在运行。",last);
    }
    private Dictionary<string,object> Request(string name,string endpoint) {
        using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous)) {
            cancellation.ThrowIfCancellationRequested();
            pipe.Connect(500);uint pid;GetNamedPipeServerProcessId(pipe.SafePipeHandle,out pid);byte[] request=Encoding.ASCII.GetBytes("GET "+endpoint+" HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            var operation=Task.Run(async()=>{await pipe.WriteAsync(request,0,request.Length).ConfigureAwait(false);using(var buffer=new MemoryStream()){byte[] chunk=new byte[16384];int n;while((n=await pipe.ReadAsync(chunk,0,chunk.Length).ConfigureAwait(false))>0){if(buffer.Length+n>16*1024*1024)throw new IOException("连接响应超过安全上限");buffer.Write(chunk,0,n);}return buffer.ToArray();}});
            try{
                if(!operation.Wait(3500,cancellation))throw new TimeoutException("Mihomo 读取超时");
                var response=Json.Read(DecodeHttp(operation.GetAwaiter().GetResult()));
                ServerPid=pid;CoreIdentity=name+"|"+pid;CoreIdentityVerified=false;
                if(pid!=0)try{using(var process=Process.GetProcessById((int)pid))CoreIdentity+="|"+process.StartTime.ToUniversalTime().Ticks;CoreIdentityVerified=true;}catch{}
                return response;
            }catch{pipe.Dispose();operation.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);throw;}
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
// Keep core totals even when a prolonged storage failure exhausts the detail cache.
// The cache limit is applied only after a failed transaction, never before a write.
internal sealed class CaptureBuffer {
    private readonly List<Usage> pending=new List<Usage>();
    private readonly Dictionary<long,TimedDelta> totals=new Dictionary<long,TimedDelta>();
    private CaptureCheckpoint checkpoint;
    private readonly int limit;
    public int Count {get{return pending.Count;}}
    public long DroppedRecords {get;private set;}
    public CaptureBuffer(int limit=50000){this.limit=limit;}
    public void Add(DeltaResult delta,long now,CaptureCheckpoint checkpoint=null){
        if(checkpoint!=null)this.checkpoint=checkpoint;
        pending.AddRange(delta.Usage);if(delta.Baseline)return;
        long bucket=Clock.Minute(now);TimedDelta value;
        if(!totals.TryGetValue(bucket,out value))totals[bucket]=value=new TimedDelta{Bucket=bucket};
        value.Upload+=delta.Upload;value.Download+=delta.Download;value.AttributedUpload+=delta.AttributedUpload;value.AttributedDownload+=delta.AttributedDownload;
    }
    public void Flush(Store store){
        string saved=checkpoint==null?null:checkpoint.Serialize();
        try{store.Write(pending,totals.Values,"collector_heartbeat",saved);}
        catch{
            int count=Math.Max(0,pending.Count-limit);
            for(int i=0;i<count;i++){
                var usage=pending[i];TimedDelta value;
                if(totals.TryGetValue(usage.Bucket,out value)){value.AttributedUpload-=usage.Upload;value.AttributedDownload-=usage.Download;}
            }
            pending.RemoveRange(0,count);DroppedRecords+=count;throw;
        }
        pending.Clear();totals.Clear();checkpoint=null;
    }
}
internal sealed class Collector : IDisposable {
    private readonly Store store;private readonly string data;private readonly LedgerConfig config;private readonly CancellationTokenSource cancel=new CancellationTokenSource();private Task worker,nodeWorker;
    private readonly CaptureBuffer buffer=new CaptureBuffer();private long reportedDrops;
    public volatile bool Connected;public volatile bool Tun,TunKnown;public string Version="",Error="正在连接 Clash Verge…",Pipe="";public long LastSample;public int ConnectionCount;public bool StorageHealthy=true;private bool disposed;
    public Collector(Store store,LedgerConfig config,string data){this.store=store;this.config=config;this.data=data;}
    public void Start(){worker=Task.Run((Action)Run);}
    private void Run(){
        var tracker=new DeltaTracker();var client=new MihomoClient(config.Pipe,cancel.Token);long lastFlush=0,lastConfig=0,lastPrune=0,lastGap=0,lastState=0,lastNodes=0;bool wasConnected=false,interrupted=false;
        try{long previousHeartbeat;if(long.TryParse(store.GetMeta("collector_heartbeat"),out previousHeartbeat)&&Clock.Now-previousHeartbeat>8)TryEvent("gap","上次记录后有未监控时段："+DateTimeOffset.FromUnixTimeSeconds(previousHeartbeat).ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss")+" 至本次启动");}catch{StorageHealthy=false;}
        try{var checkpoint=CaptureCheckpoint.Parse(store.GetMeta(CaptureCheckpoint.Key));if(checkpoint!=null){tracker.Restore(checkpoint);interrupted=true;}}catch{StorageHealthy=false;}
        TryEvent("start",interrupted?"后台采集已启动；正在核实已保存的内核身份与计数，尝试恢复累计增量。":"后台采集已启动；已有连接建立基线，从现在起记录。");
        while(!cancel.IsCancellationRequested){long now=Clock.Now;var watch=System.Diagnostics.Stopwatch.StartNew();
            try {
                long requestStarted=now;
                var snapshot=Snapshot.Parse(client.Get("/connections"));
                now=Clock.Now;
                var delta=tracker.Observe(snapshot,now,client.CoreIdentity,interrupted,client.CoreIdentityVerified,requestStarted);
                if(!wasConnected||delta.Reset){TunKnown=false;lastConfig=0;}
                if(delta.Reset)TryEvent("reset","内核实例更换、身份无法确认或计数重置，已重新建立基线；中断期间流量无法完整恢复。");
                if(!wasConnected)TryEvent("connected",delta.Recovered?"已恢复同一 Mihomo 内核的累计增量；差额记在恢复分钟，短连接与末段仍可能无法归属。":"已连接 Clash Verge，本次连接先建立流量基线。");
                foreach(var usage in delta.Usage)usage.Rate=store.Catalog.Resolve(usage.Dimension);
                buffer.Add(delta,now,new CaptureCheckpoint{At=requestStarted,CoreIdentity=client.CoreIdentity,IdentityVerified=client.CoreIdentityVerified,Snapshot=snapshot});interrupted=false;
                Connected=true;wasConnected=true;Error="";LastSample=now;ConnectionCount=snapshot.Connections.Count;Pipe=client.PipeName;
                if(now-lastConfig>=30){try{var tun=Json.Obj(client.Get("/configs"),"tun");TunKnown=tun!=null&&tun.ContainsKey("enable");Tun=Json.Bool(tun,"enable");}catch{TunKnown=false;}try{Version=Json.Str(client.Get("/version"),"version");}catch{}lastConfig=now;}
                // Directory reads have their own client and worker; a slow provider API must
                // never extend the one-second connection sampling interval.
                if(now-lastNodes>=60&&(nodeWorker==null||nodeWorker.IsCompleted)){nodeWorker=Task.Run(()=>{try{store.Catalog.Refresh(config.Pipe,cancel.Token);}catch{}});lastNodes=now;}
            } catch(OperationCanceledException)when(cancel.IsCancellationRequested){break;}
            catch(Exception ex){Connected=false;Error=ex.GetBaseException().Message;if(wasConnected||now-lastGap>300){TryEvent("gap","Mihomo 采样暂时中断；恢复后仅在确认同一内核且累计计数未回退时保留增量。 "+Error);lastGap=now;}wasConnected=false;interrupted=true;}
            if(now-lastFlush>=5||buffer.Count>50000){try{Flush();StorageHealthy=true;if(buffer.DroppedRecords>reportedDrops&&TryEvent("gap","磁盘写入失败期间明细缓存已满，累计 "+buffer.DroppedRecords+" 条明细未保存；对应字节保留在内核总量的未归属差额中。"))reportedDrops=buffer.DroppedRecords;}catch{StorageHealthy=false;}lastFlush=now;}
            if(now-lastPrune>=3600){try{store.Prune(config.RetentionDays);}catch{}lastPrune=now;}
            if(now-lastState>=5)try{AtomicFile.Write(Path.Combine(data,"capture-state.json"),Json.Write(new{at=now,tun=Tun,tunKnown=TunKnown,connected=Connected}));lastState=now;}catch{}
            int delay=Connected?1000:5000;cancel.Token.WaitHandle.WaitOne(Math.Max(50,delay-(int)watch.ElapsedMilliseconds));
        }
        try{Flush();TryEvent("stop","后台采集正常停止。");}catch{StorageHealthy=false;}
    }
    private bool TryEvent(string kind,string detail){try{store.Event(kind,detail);return true;}catch{StorageHealthy=false;return false;}}
    public void Flush(){buffer.Flush(store);}
    public object Status(){return new{connected=Connected,version=Version,pipe=Pipe,lastSample=LastSample,connections=ConnectionCount,tun=Tun,tunKnown=TunKnown,error=Error,storageHealthy=StorageHealthy,droppedRecords=buffer.DroppedRecords};}
    public void Dispose(){if(disposed)return;disposed=true;cancel.Cancel();try{if(worker!=null)worker.GetAwaiter().GetResult();if(nodeWorker!=null)nodeWorker.GetAwaiter().GetResult();}finally{cancel.Dispose();}}
}
}
