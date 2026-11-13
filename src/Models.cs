using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DataTrafficLedger {
internal static class Json {
    public static string Write(object value) { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Serialize(value); }
    public static Dictionary<string, object> Read(string text) { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(text); }
    public static string Str(IDictionary<string, object> obj, string key, string fallback = "") { object v; return obj != null && obj.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback; }
    public static long Num(IDictionary<string, object> obj, string key) { long n; return long.TryParse(Str(obj,key), NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : 0; }
    public static long Counter(IDictionary<string,object> obj,string key){long value;if(!long.TryParse(Str(obj,key),NumberStyles.Integer,CultureInfo.InvariantCulture,out value)||value<0)throw new InvalidDataException("无效的累计计数："+key);return value;}
    public static bool Bool(IDictionary<string, object> obj, string key) { return Str(obj,key).Equals("true",StringComparison.OrdinalIgnoreCase); }
    public static Dictionary<string, object> Obj(IDictionary<string, object> obj, string key) { object v; return obj != null && obj.TryGetValue(key,out v) ? v as Dictionary<string,object> : null; }
    public static IEnumerable<object> Array(IDictionary<string, object> obj, string key) { object v; if(obj!=null&&obj.TryGetValue(key,out v)){var values=v as System.Collections.IEnumerable;if(values!=null&&!(v is string))return values.Cast<object>();}return new object[0]; }
}
internal static class Clock {
    public static long Now { get { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); } }
    public static long Minute(long seconds) { return seconds - seconds % 60; }
    public static long DateBoundary(string text, bool nextDay) {
        DateTime day;
        if (!DateTime.TryParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out day)) throw new ArgumentException("日期格式应为 yyyy-MM-dd");
        if (nextDay) day = day.AddDays(1);
        return new DateTimeOffset(day,TimeSpan.FromHours(8)).ToUnixTimeSeconds();
    }
    public static long TimeBoundary(string day,string time,bool end) {
        if(string.IsNullOrEmpty(time))return DateBoundary(day,end);
        DateTime value;
        if(!DateTime.TryParseExact(day+"T"+time,"yyyy-MM-dd'T'HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out value))throw new ArgumentException("日期或时间无效，时间格式应为 HH:mm（北京时间）。");
        // The ledger stores minute buckets; the selected ending minute is included in full.
        return new DateTimeOffset(value,TimeSpan.FromHours(8)).ToUnixTimeSeconds()+(end?60:0);
    }
}
internal sealed class LedgerConfig {
    public int Port = 0;
    public int RetentionDays = 90;
    public bool EtwRequested;
    public string Theme = "system";
    public bool AutoRefresh;
    public bool AutoMultiplier = true;
    public string Pipe = "";
    public static LedgerConfig Load(string directory) {
        try { var d = Json.Read(File.ReadAllText(Path.Combine(directory,"settings.json")));long port=Json.Num(d,"port");
            // Migrate the two old defaults. Explicit fixed ports written by this version are preserved.
            if(Json.Str(d,"portMode")!="fixed"&&(port==8793||port==18793))port=0;
            string theme=Json.Str(d,"theme","system");
            return new LedgerConfig { Port = port>=1024&&port<=65535?(int)port:0, RetentionDays = d.ContainsKey("retentionDays")?(int)Math.Max(7,Math.Min(3650,Json.Num(d,"retentionDays"))):90, EtwRequested = Json.Bool(d,"etwRequested"), Pipe=Json.Str(d,"pipe"), Theme=new[]{"system","light","dark"}.Contains(theme)?theme:"system", AutoRefresh=Json.Bool(d,"autoRefresh"), AutoMultiplier=!d.ContainsKey("autoMultiplier")||Json.Bool(d,"autoMultiplier") }; }
        catch { return new LedgerConfig(); }
    }
    public void Save(string directory) { AtomicFile.Write(Path.Combine(directory,"settings.json"),Json.Write(new { port=Port,portMode=Port==0?"auto":"fixed",retentionDays=RetentionDays,etwRequested=EtwRequested,pipe=Pipe,theme=Theme,autoRefresh=AutoRefresh,autoMultiplier=AutoMultiplier })); }
}
internal static class AtomicFile {
    public static void Write(string path,string value) {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temporary,value,new UTF8Encoding(false));
        try { if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
internal sealed class Dimension {
    public string Source="mihomo", App="未识别应用", Path="", Host="", Ip="", Node="", Route="PROXY", Network="tcp", Rule="", Chain="";
    public string Id {
        get {
            using (var hash=SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(new[]{Source,App,Path,Host,Ip,Node,Route,Network,Rule,Chain}))));
        }
    }
}
internal sealed class Usage {
    public Dimension Dimension;
    public long Bucket, Upload, Download;
    public NodeRate Rate;
}
internal sealed class Connection {
    public string Id;
    public long Started, Upload, Download;
    public Dimension Dimension;
    public static Connection Parse(Dictionary<string,object> obj) {
        var m=Json.Obj(obj,"metadata");
        var chains=Json.Array(obj,"chains").Select(x=>Convert.ToString(x,CultureInfo.InvariantCulture)).Where(x=>!string.IsNullOrWhiteSpace(x)).ToArray();
        DateTimeOffset started;
        if(Json.Str(obj,"id")==""||!DateTimeOffset.TryParse(Json.Str(obj,"start"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out started))throw new InvalidDataException("连接标识或建立时间无效");
        string node=chains.Length>0?chains[0]:"未知出口";
        string route=node.Equals("DIRECT",StringComparison.OrdinalIgnoreCase)?"DIRECT":node.StartsWith("REJECT",StringComparison.OrdinalIgnoreCase)?"REJECT":"PROXY";
        string host=Json.Str(m,"host"); string ip=Json.Str(m,"destinationIP");
        return new Connection { Id=Json.Str(obj,"id")+"|"+Json.Str(obj,"start"),Started=started.ToUnixTimeSeconds(),Upload=Json.Counter(obj,"upload"),Download=Json.Counter(obj,"download"),Dimension=new Dimension {
            App=Json.Str(m,"process","未识别应用"),Path=Json.Str(m,"processPath"),Host=string.IsNullOrEmpty(host)?ip:host,Ip=ip,Node=node,Route=route,Network=Json.Str(m,"network","tcp"),Rule=Json.Str(obj,"rule")+":"+Json.Str(obj,"rulePayload"),Chain=Json.Write(chains)
        }};
    }
}
internal sealed class Snapshot {
    public long Upload,Download;
    public List<Connection> Connections=new List<Connection>();
    public static Snapshot Parse(Dictionary<string,object> obj) {
        if (obj==null || !obj.ContainsKey("connections") || !obj.ContainsKey("uploadTotal") || !obj.ContainsKey("downloadTotal")) throw new InvalidDataException("不是兼容的 Mihomo 连接响应");
        return new Snapshot { Upload=Json.Counter(obj,"uploadTotal"),Download=Json.Counter(obj,"downloadTotal"),Connections=Json.Array(obj,"connections").OfType<Dictionary<string,object>>().Select(Connection.Parse).ToList() };
    }
}
internal sealed class CaptureCheckpoint {
    public const string Key="mihomo_checkpoint_v1";
    public long At;
    public string CoreIdentity;
    public bool IdentityVerified;
    public Snapshot Snapshot;
    public string Serialize(){return Json.Write(new{version=1,at=At,core=CoreIdentity,verified=IdentityVerified,upload=Snapshot.Upload,download=Snapshot.Download,connections=Snapshot.Connections.Select(c=>new{id=c.Id,started=c.Started,upload=c.Upload,download=c.Download}).ToArray()});}
    public static CaptureCheckpoint Parse(string text){
        try{
            var value=Json.Read(text);
            if(Json.Num(value,"version")!=1||Json.Num(value,"at")<=0||!Json.Bool(value,"verified")||Json.Str(value,"core")==""||!value.ContainsKey("connections"))return null;
            var snapshot=new Snapshot{Upload=Json.Counter(value,"upload"),Download=Json.Counter(value,"download")};
            foreach(var item in Json.Array(value,"connections")){
                var row=item as Dictionary<string,object>;if(row==null||Json.Str(row,"id")=="")return null;
                var connection=new Connection{Id=Json.Str(row,"id"),Started=Json.Num(row,"started"),Upload=Json.Counter(row,"upload"),Download=Json.Counter(row,"download")};
                snapshot.Connections.Add(connection);
            }
            return new CaptureCheckpoint{At=Json.Num(value,"at"),CoreIdentity=Json.Str(value,"core"),IdentityVerified=true,Snapshot=snapshot};
        }catch{return null;}
    }
}
internal class DeltaResult {
    public List<Usage> Usage=new List<Usage>();
    public long Upload,Download,AttributedUpload,AttributedDownload;
    public bool Baseline,Reset,Recovered;
}
internal sealed class DeltaTracker {
    private Snapshot previous;
    private long previousTime;
    private string previousCore;
    public void Reset() { previous=null;previousCore=null; }
    public void Restore(CaptureCheckpoint checkpoint){previous=checkpoint.Snapshot;previousTime=checkpoint.At;previousCore=checkpoint.CoreIdentity;}
    public DeltaResult Observe(Snapshot snapshot,long now,string coreIdentity=null,bool interrupted=false,bool identityVerified=true,long sampleStarted=0) {
        var result=new DeltaResult();
        // A connection may start after the server enumerates its snapshot but before
        // the response finishes. Use the request start as the next new-connection cutoff.
        long cutoff=sampleStarted>0?sampleStarted:now;
        // A failed read does not erase cumulative counters. Resume only when the pipe,
        // PID and process creation time prove this is still the same core instance.
        bool changed=previousCore!=coreIdentity || interrupted&&(!identityVerified||string.IsNullOrEmpty(coreIdentity));
        if (previous==null || changed || snapshot.Upload<previous.Upload || snapshot.Download<previous.Download) {
            result.Baseline=true; result.Reset=previous!=null; previous=snapshot;previousTime=cutoff;previousCore=coreIdentity;return result;
        }
        result.Recovered=interrupted;
        result.Upload=snapshot.Upload-previous.Upload;result.Download=snapshot.Download-previous.Download;
        var old=previous.Connections.GroupBy(c=>c.Id).ToDictionary(g=>g.Key,g=>g.Last());
        foreach(var c in snapshot.Connections) {
            Connection p;long up=0,down=0;
            if(old.TryGetValue(c.Id,out p)) { up=Math.Max(0,c.Upload-p.Upload);down=Math.Max(0,c.Download-p.Download); }
            else if(c.Started>=previousTime) { up=c.Upload;down=c.Download; }
            if(up>0 || down>0) { result.Usage.Add(new Usage{Dimension=c.Dimension,Bucket=Clock.Minute(now),Upload=up,Download=down});result.AttributedUpload+=up;result.AttributedDownload+=down; }
        }
        previous=snapshot;previousTime=cutoff;return result;
    }
}
internal sealed class Query {
    public long From,To,TrendFrom,TrendTo;
    public string Scope="all",Search="",Node="",App="";
    public bool Japan;
    public bool ApplyMultipliers;
}
}
