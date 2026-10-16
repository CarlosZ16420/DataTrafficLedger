using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace DataTrafficLedger {
internal struct FlowKey : IEquatable<FlowKey> {
    public int Pid;public byte Family,Protocol;public ulong Address1,Address2;public long Bucket;
    public bool Equals(FlowKey o){return Pid==o.Pid&&Family==o.Family&&Protocol==o.Protocol&&Address1==o.Address1&&Address2==o.Address2&&Bucket==o.Bucket;}
    public override bool Equals(object obj){return obj is FlowKey&&Equals((FlowKey)obj);}
    public override int GetHashCode(){unchecked{return Pid*397^(int)Address1^(int)(Address1>>32)^(int)Address2^(int)(Address2>>32)^(int)Bucket^Family^Protocol;}}
    public string Ip(){if(Family==4)return new IPAddress((long)(uint)Address1).ToString();var bytes=new byte[16];Buffer.BlockCopy(BitConverter.GetBytes(Address1),0,bytes,0,8);Buffer.BlockCopy(BitConverter.GetBytes(Address2),0,bytes,8,8);return new IPAddress(bytes).ToString();}
}
internal sealed class FlowBytes {public long Upload,Download;}
internal sealed class ProcessIdentity {public string Name,Path;public long Expires;}
internal sealed class EtwCapture : IDisposable {
    private static readonly Guid TcpGuid=new Guid("9a280ac0-c8e0-11d1-84e2-00c04fb998a2"),UdpGuid=new Guid("bf3a50c5-a9c9-4988-a005-2df0b7c80f80"),ProcessGuid=new Guid("3d6fa8d0-fe05-11d0-9dda-00c04fd7ba7c");
    private readonly object gate=new object();private Dictionary<FlowKey,FlowBytes> flows=new Dictionary<FlowKey,FlowBytes>();
    private readonly Dictionary<int,ProcessIdentity> processes=new Dictionary<int,ProcessIdentity>();
    private readonly object processGate=new object();private readonly string session;
    private ulong traceHandle,consumerHandle=ulong.MaxValue;private IntPtr properties,logfile,loggerName;private RecordCallback callback;private Task worker;private bool disposed;
    public long Malformed,DroppedFlows;public bool Running;public uint LastError;
    public EtwCapture(string session){this.session=session;}
    public uint Start(){
        EnableProfilePrivilege();
        properties=Marshal.AllocHGlobal(120+2048);for(int i=0;i<120+2048;i++)Marshal.WriteByte(properties,i,0);
        var p=new TraceProperties{TotalSize=120+2048,Guid=new Guid("f2b4a3c8-70ef-4608-a0d0-ce2687b99dd6"),ClientContext=1,WnodeFlags=0x00020000,BufferSize=64,MinimumBuffers=4,MaximumBuffers=32,LogFileMode=0x02000100|0x10000000,FlushTimer=2,EnableFlags=0x10010001,LoggerNameOffset=120};
        Marshal.StructureToPtr(p,properties,false);uint rc=StartTraceW(out traceHandle,session,properties);
        if(rc==183){ControlTraceW(0,session,properties,1);Marshal.StructureToPtr(p,properties,false);rc=StartTraceW(out traceHandle,session,properties);}
        if(rc!=0){LastError=rc;return rc;}
        loggerName=Marshal.StringToHGlobalUni(session);callback=OnRecord;
        logfile=Marshal.AllocHGlobal(448);for(int i=0;i<448;i++)Marshal.WriteByte(logfile,i,0);
        Marshal.WriteIntPtr(logfile,8,loggerName);Marshal.WriteInt32(logfile,28,0x10000100);Marshal.WriteIntPtr(logfile,424,Marshal.GetFunctionPointerForDelegate(callback));
        consumerHandle=OpenTraceW(logfile);if(consumerHandle==ulong.MaxValue){LastError=(uint)Marshal.GetLastWin32Error();ControlTraceW(traceHandle,session,properties,1);traceHandle=0;return LastError;}
        Running=true;worker=Task.Factory.StartNew(()=>{uint code=ProcessTrace(new[]{consumerHandle},1,IntPtr.Zero,IntPtr.Zero);Running=false;if(code!=0&&code!=1223)LastError=code;},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);return 0;
    }
    private unsafe void OnRecord(IntPtr pointer){
        try{
            var record=(EventRecord*)pointer.ToPointer();Guid provider=record->Header.Provider;byte opcode=record->Header.Descriptor.Opcode;
            if(provider==ProcessGuid){if(record->UserDataLength>=IntPtr.Size+4&&(opcode==1||opcode==2)){int pid=Marshal.ReadInt32(record->UserData,IntPtr.Size);lock(processGate)processes.Remove(pid);}return;}
            bool tcp=provider==TcpGuid;if(!tcp&&provider!=UdpGuid)return;
            if(opcode!=10&&opcode!=11&&opcode!=26&&opcode!=27)return;
            // Version 2 is the documented Windows 10/11 MOF layout. Never decode an unknown layout as if it were known.
            if(record->Header.Descriptor.Version!=2){Interlocked.Increment(ref Malformed);return;}
            FlowKey key;long bytes;bool upload;
            if(!Decode(record->UserData,record->UserDataLength,opcode,tcp,record->Header.TimeStamp,out key,out bytes,out upload))return;
            lock(gate){FlowBytes value;if(!flows.TryGetValue(key,out value)){if(flows.Count>=30000){DroppedFlows++;return;}flows[key]=value=new FlowBytes();}if(upload)value.Upload+=bytes;else value.Download+=bytes;}
        }catch{Interlocked.Increment(ref Malformed);}
    }
    internal static unsafe bool Decode(IntPtr data,int length,byte opcode,bool tcp,long timestamp,out FlowKey key,out long size,out bool upload){
        key=new FlowKey();size=0;upload=opcode==10||opcode==26;bool ipv6=opcode==26||opcode==27;int required=ipv6?44:20;if(length<required)return false;
        byte* p=(byte*)data.ToPointer();key.Pid=*(int*)p;size=*(uint*)(p+4);if(size==0||key.Pid<=0)return false;
        key.Family=(byte)(ipv6?6:4);key.Protocol=(byte)(tcp?6:17);key.Bucket=Clock.Minute((timestamp-116444736000000000L)/10000000L);
        int offset=upload?8:ipv6?24:12;
        if(ipv6){key.Address1=*(ulong*)(p+offset);key.Address2=*(ulong*)(p+offset+8);if(key.Address1==0&&(key.Address2==0||key.Address2==0x0100000000000000UL))return false;
            // IPv4-mapped loopback must not be counted as external traffic either.
            if(key.Address1==0&&(key.Address2&0x00000000FFFFFFFFUL)==0x00000000FFFF0000UL&&((key.Address2>>32)&255)==127)return false;
        }else{key.Address1=*(uint*)(p+offset);if(((uint)key.Address1&255)==127||key.Address1==0)return false;}
        return true;
    }
    private ProcessIdentity Identify(int pid){
        lock(processGate){ProcessIdentity v;if(processes.TryGetValue(pid,out v)&&v.Expires>Clock.Now)return v;
            v=new ProcessIdentity{Name="进程 #"+pid+"（未识别）",Path="",Expires=Clock.Now+15};
            try{using(var p=Process.GetProcessById(pid)){v.Name=p.ProcessName+".exe";try{v.Path=p.MainModule.FileName;}catch{}}}catch{}
            processes[pid]=v;if(processes.Count>2048){foreach(var id in processes.Where(x=>x.Value.Expires<Clock.Now).Select(x=>x.Key).ToArray())processes.Remove(id);}return v;}
    }
    public List<Usage> Drain(){
        Dictionary<FlowKey,FlowBytes> batch;lock(gate){batch=flows;flows=new Dictionary<FlowKey,FlowBytes>();}
        var result=new List<Usage>();
        foreach(var pair in batch){var identity=Identify(pair.Key.Pid);string name=identity.Name.ToLowerInvariant();
            if(name=="verge-mihomo.exe"||name=="mihomo.exe"||name=="clash.exe"||name=="localtrafficledger.exe"||name=="datatrafficledger.exe")continue;
            string ip=pair.Key.Ip();result.Add(new Usage{Bucket=pair.Key.Bucket,Upload=pair.Value.Upload,Download=pair.Value.Download,Dimension=new Dimension{Source="windows",App=identity.Name,Path=identity.Path,Host=ip,Ip=ip,Node="未经过 Clash",Route="BYPASS",Network=pair.Key.Protocol==6?"tcp":"udp",Rule="Windows 网络事件；IP 级统计",Chain="[]"}});
        }return result;
    }
    public object Health(){uint lost=0,buffers=0;if(traceHandle!=0&&ControlTraceW(traceHandle,session,properties,0)==0){lost=(uint)Marshal.ReadInt32(properties,88);buffers=(uint)Marshal.ReadInt32(properties,100);}return new{running=Running,error=LastError,lostEvents=lost,lostBuffers=buffers,unsupportedEvents=Interlocked.Read(ref Malformed),droppedFlows=Interlocked.Read(ref DroppedFlows),at=Clock.Now};}
    public void Dispose(){if(disposed)return;disposed=true;if(traceHandle!=0){ControlTraceW(traceHandle,session,properties,1);traceHandle=0;}if(consumerHandle!=ulong.MaxValue){CloseTrace(consumerHandle);consumerHandle=ulong.MaxValue;}if(worker!=null)worker.Wait(5000);if(properties!=IntPtr.Zero)Marshal.FreeHGlobal(properties);if(logfile!=IntPtr.Zero)Marshal.FreeHGlobal(logfile);if(loggerName!=IntPtr.Zero)Marshal.FreeHGlobal(loggerName);GC.KeepAlive(callback);}
    [StructLayout(LayoutKind.Explicit,Size=120)]private struct TraceProperties {
        [FieldOffset(0)]public uint TotalSize;[FieldOffset(24)]public Guid Guid;[FieldOffset(40)]public uint ClientContext;[FieldOffset(44)]public uint WnodeFlags;
        [FieldOffset(48)]public uint BufferSize;[FieldOffset(52)]public uint MinimumBuffers;[FieldOffset(56)]public uint MaximumBuffers;[FieldOffset(64)]public uint LogFileMode;[FieldOffset(68)]public uint FlushTimer;[FieldOffset(72)]public uint EnableFlags;[FieldOffset(116)]public uint LoggerNameOffset;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Descriptor{public ushort Id;public byte Version,Channel,Level,Opcode;public ushort Task;public ulong Keyword;}
    [StructLayout(LayoutKind.Sequential)]private struct Header{public ushort Size,HeaderType,Flags,Property;public uint ThreadId,ProcessId;public long TimeStamp;public Guid Provider;public Descriptor Descriptor;public ulong ProcessorTime;public Guid Activity;}
    [StructLayout(LayoutKind.Sequential)]private struct EventRecord{public Header Header;public uint BufferContext;public ushort ExtendedDataCount,UserDataLength;public IntPtr ExtendedData,UserData,Context;}
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate void RecordCallback(IntPtr record);
    private static void EnableProfilePrivilege(){IntPtr token;if(!OpenProcessToken(Process.GetCurrentProcess().Handle,0x28,out token))return;try{Luid luid;if(LookupPrivilegeValueW(null,"SeSystemProfilePrivilege",out luid)){var value=new Privilege{Count=1,Low=luid.Low,High=luid.High,Attributes=2};AdjustTokenPrivileges(token,false,ref value,0,IntPtr.Zero,IntPtr.Zero);}}finally{CloseHandle(token);}}
    [StructLayout(LayoutKind.Sequential)]private struct Luid{public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)]private struct Privilege{public uint Count,Low;public int High;public uint Attributes;}
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool LookupPrivilegeValueW(string system,string name,out Luid luid);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool AdjustTokenPrivileges(IntPtr token,bool disableAll,ref Privilege privilege,uint length,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]private static extern uint StartTraceW(out ulong handle,string name,IntPtr properties);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]private static extern uint ControlTraceW(ulong handle,string name,IntPtr properties,uint code);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")]private static extern uint ProcessTrace(ulong[] handles,uint count,IntPtr start,IntPtr end);
    [DllImport("advapi32.dll")]private static extern uint CloseTrace(ulong handle);
}
internal static class EtwHelper {
    public static int Run(string data){
        bool created;using(var mutex=new Mutex(true,Program.MutexName(data)+".Etw",out created)){if(!created)return 0;
            try{using(var stop=new EventWaitHandle(false,EventResetMode.ManualReset,Program.MutexName(data)+".EtwStop"))using(var store=new Store(data))using(var capture=new EtwCapture("LocalTrafficLedger.Network."+Program.UserKey)){
                string stopFile=Path.Combine(data,"etw-stop.request");try{if(File.Exists(stopFile))File.Delete(stopFile);}catch{}
                stop.Reset();uint code=capture.Start();store.SetMeta("etw_health",Json.Write(new{at=Clock.Now,running=code==0,error=code}));
                if(code!=0){store.Event("windows-gap","Windows 补充采集未启动，错误码 "+code+"。需要管理员权限或可用的 ETW 会话。");return (int)code;}
                store.Event("windows-start","Windows 补充采集已启动；排除回环连接和 Mihomo 进程，不解析网页内容。");
                var pending=new List<Usage>();long lastLoss=0;bool suspended=false;long absentSince=0;
                while(!stop.WaitOne(5000)){
                    if(File.Exists(stopFile))break;
                    bool tun=false,live=false;try{var state=Json.Read(File.ReadAllText(Path.Combine(data,"capture-state.json")));live=Clock.Now-Json.Num(state,"at")<20;tun=Json.Bool(state,"tun");}catch{}
                    var batch=capture.Drain();if(live){absentSince=0;if(!tun)pending.AddRange(batch);}else{if(absentSince==0)absentSince=Clock.Now;if(Clock.Now-absentSince>60)break;}
                    bool suspend=tun||!live;if(suspend!=suspended){store.Event("windows-gap",suspend?"Windows 补充计量暂停：TUN 开启或主采集器离线，避免重复记账。":"Windows 补充计量恢复。");suspended=suspend;}
                    try{store.Write(pending,null,"windows_heartbeat");pending.Clear();}catch(Exception ex){store.SetMeta("etw_health",Json.Write(new{at=Clock.Now,running=true,error="历史写入失败："+ex.Message}));if(pending.Count>50000){pending.Clear();store.Event("windows-gap","Windows 记录缓存已满，部分流量未保存。");}continue;}
                    var health=Json.Read(Json.Write(capture.Health()));health["suspended"]=suspend;store.SetMeta("etw_health",Json.Write(health));long losses=Json.Num(health,"lostEvents")+Json.Num(health,"lostBuffers")+Json.Num(health,"unsupportedEvents")+Json.Num(health,"droppedFlows");
                    if(losses>lastLoss){store.Event("windows-gap","Windows 网络事件存在丢失或无法解析，累计 "+losses+"；此时段记录不完整。");lastLoss=losses;}
                    if(!capture.Running)break;
                }
                // Stop tracing before draining the final events; UI and network traffic are never blocked by the recorder.
                capture.Dispose();if(!suspended)pending.AddRange(capture.Drain());try{store.Write(pending,null,"windows_heartbeat");}catch{}
                store.SetMeta("etw_health",Json.Write(new{at=Clock.Now,running=false,error=0}));store.Event("windows-stop","Windows 补充采集已停止。");
            }}finally{mutex.ReleaseMutex();}return 0;
        }
    }
    public static void Stop(string data){try{File.WriteAllText(Path.Combine(data,"etw-stop.request"),Clock.Now.ToString(CultureInfo.InvariantCulture));}catch{}try{using(var signal=EventWaitHandle.OpenExisting(Program.MutexName(data)+".EtwStop"))signal.Set();}catch(WaitHandleCannotBeOpenedException){}catch(UnauthorizedAccessException){} }
}
}
