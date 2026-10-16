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
    public string Pipe = "";
    public static LedgerConfig Load(string directory) {
        try { var d = Json.Read(File.ReadAllText(Path.Combine(directory,"settings.json")));long port=Json.Num(d,"port");
            // Migrate the two old defaults. Explicit fixed ports written by this version are preserved.
            if(Json.Str(d,"portMode")!="fixed"&&(port==8793||port==18793))port=0;
            string theme=Json.Str(d,"theme","system");
            return new LedgerConfig { Port = port>=1024&&port<=65535?(int)port:0, RetentionDays = d.ContainsKey("retentionDays")?(int)Math.Max(7,Math.Min(3650,Json.Num(d,"retentionDays"))):90, EtwRequested = Json.Bool(d,"etwRequested"), Pipe=Json.Str(d,"pipe"), Theme=new[]{"system","light","dark"}.Contains(theme)?theme:"system", AutoRefresh=Json.Bool(d,"autoRefresh") }; }
        catch { return new LedgerConfig(); }
    }
    public void Save(string directory) { AtomicFile.Write(Path.Combine(directory,"settings.json"),Json.Write(new { port=Port,portMode=Port==0?"auto":"fixed",retentionDays=RetentionDays,etwRequested=EtwRequested,pipe=Pipe,theme=Theme,autoRefresh=AutoRefresh })); }
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
}
internal sealed class Connection {
    public string Id;
    public long Started, Upload, Download;
    public Dimension Dimension;
    public static Connection Parse(Dictionary<string,object> obj) {
        var m=Json.Obj(obj,"metadata");
        var chains=Json.Array(obj,"chains").Select(x=>Convert.ToString(x,CultureInfo.InvariantCulture)).Where(x=>!string.IsNullOrWhiteSpace(x)).ToArray();
        DateTimeOffset started;
        DateTimeOffset.TryParse(Json.Str(obj,"start"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out started);
        string node=chains.Length>0?chains[0]:"未知出口";
        string route=node.Equals("DIRECT",StringComparison.OrdinalIgnoreCase)?"DIRECT":node.StartsWith("REJECT",StringComparison.OrdinalIgnoreCase)?"REJECT":"PROXY";
        string host=Json.Str(m,"host"); string ip=Json.Str(m,"destinationIP");
        return new Connection { Id=Json.Str(obj,"id")+"|"+Json.Str(obj,"start"),Started=started.ToUnixTimeSeconds(),Upload=Json.Num(obj,"upload"),Download=Json.Num(obj,"download"),Dimension=new Dimension {
            App=Json.Str(m,"process","未识别应用"),Path=Json.Str(m,"processPath"),Host=string.IsNullOrEmpty(host)?ip:host,Ip=ip,Node=node,Route=route,Network=Json.Str(m,"network","tcp"),Rule=Json.Str(obj,"rule")+":"+Json.Str(obj,"rulePayload"),Chain=Json.Write(chains)
        }};
    }
}
internal sealed class Snapshot {
    public long Upload,Download;
    public List<Connection> Connections=new List<Connection>();
    public static Snapshot Parse(Dictionary<string,object> obj) {
        if (obj==null || !obj.ContainsKey("connections") || !obj.ContainsKey("uploadTotal") || !obj.ContainsKey("downloadTotal")) throw new InvalidDataException("不是兼容的 Mihomo 连接响应");
        return new Snapshot { Upload=Json.Num(obj,"uploadTotal"),Download=Json.Num(obj,"downloadTotal"),Connections=Json.Array(obj,"connections").OfType<Dictionary<string,object>>().Select(Connection.Parse).ToList() };
    }
}
internal class DeltaResult {
    public List<Usage> Usage=new List<Usage>();
    public long Upload,Download,AttributedUpload,AttributedDownload;
    public bool Baseline,Reset;
}
internal sealed class DeltaTracker {
    private Snapshot previous;
    private long previousTime;
    public void Reset() { previous=null; }
    public DeltaResult Observe(Snapshot snapshot,long now) {
        var result=new DeltaResult();
        if (previous==null || snapshot.Upload<previous.Upload || snapshot.Download<previous.Download) {
            result.Baseline=true; result.Reset=previous!=null; previous=snapshot;previousTime=now;return result;
        }
        result.Upload=snapshot.Upload-previous.Upload;result.Download=snapshot.Download-previous.Download;
        var old=previous.Connections.GroupBy(c=>c.Id).ToDictionary(g=>g.Key,g=>g.Last());
        foreach(var c in snapshot.Connections) {
            Connection p;long up=0,down=0;
            if(old.TryGetValue(c.Id,out p)) { up=Math.Max(0,c.Upload-p.Upload);down=Math.Max(0,c.Download-p.Download); }
            else if(c.Started>=previousTime) { up=c.Upload;down=c.Download; }
            if(up>0 || down>0) { result.Usage.Add(new Usage{Dimension=c.Dimension,Bucket=Clock.Minute(now),Upload=up,Download=down});result.AttributedUpload+=up;result.AttributedDownload+=down; }
        }
        previous=snapshot;previousTime=now;return result;
    }
}
internal sealed class Query {
    public long From,To,TrendFrom,TrendTo;
    public string Scope="all",Search="",Node="",App="";
    public bool Japan;
}
}
