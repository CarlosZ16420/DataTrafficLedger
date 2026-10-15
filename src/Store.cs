using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LocalTrafficLedger {
internal sealed class Store : IDisposable {
    private readonly object gate=new object();
    private readonly Sqlite db;
    public readonly string FilePath;
    public Store(string directory) {
        Directory.CreateDirectory(directory);FilePath=Path.Combine(directory,"traffic.db");db=new Sqlite(FilePath);
        db.Execute(@"
CREATE TABLE IF NOT EXISTS dimensions(id TEXT PRIMARY KEY,source TEXT NOT NULL,app TEXT NOT NULL,path TEXT NOT NULL,host TEXT NOT NULL,ip TEXT NOT NULL,node TEXT NOT NULL,route TEXT NOT NULL,network TEXT NOT NULL,rule TEXT NOT NULL,chain TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS traffic(bucket INTEGER NOT NULL,dimension TEXT NOT NULL,upload INTEGER NOT NULL,download INTEGER NOT NULL,PRIMARY KEY(bucket,dimension)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS core_totals(bucket INTEGER PRIMARY KEY,upload INTEGER NOT NULL,download INTEGER NOT NULL,attributed_upload INTEGER NOT NULL,attributed_download INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY,at INTEGER NOT NULL,kind TEXT NOT NULL,detail TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS events_at ON events(at);
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
PRAGMA user_version=1;");
    }
    public string GetMeta(string key) { lock(gate){var rows=db.Query("SELECT value FROM meta WHERE key=?",key);return rows.Count==0?"":Convert.ToString(rows[0]["value"],CultureInfo.InvariantCulture);} }
    private void PutMeta(string key,string value) { db.Run("INSERT INTO meta VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",key,value); }
    public void SetMeta(string key,string value) { lock(gate)PutMeta(key,value); }
    public string ApplicationPath(string id) { lock(gate){var rows=db.Query("SELECT path FROM dimensions WHERE id=? LIMIT 1",id);return rows.Count==0?"":Convert.ToString(rows[0]["path"],CultureInfo.InvariantCulture);} }
    public void Event(string kind,string detail,long at=0) { lock(gate)db.Run("INSERT INTO events(at,kind,detail) VALUES(?,?,?)",at==0?Clock.Now:at,kind,detail); }
    public void Write(IEnumerable<Usage> usage,IEnumerable<DeltaResult> totals,string heartbeatKey) {
        lock(gate) {
            db.Execute("BEGIN IMMEDIATE");
            try {
                foreach(var u in usage) {
                    if(u.Upload<0||u.Download<0)throw new InvalidDataException("负流量被拒绝");
                    if(u.Upload==0&&u.Download==0)continue;
                    var d=u.Dimension;string id=d.Id;
                    db.Run("INSERT OR IGNORE INTO dimensions VALUES(?,?,?,?,?,?,?,?,?,?,?)",id,d.Source,d.App,d.Path,d.Host,d.Ip,d.Node,d.Route,d.Network,d.Rule,d.Chain);
                    db.Run("INSERT INTO traffic VALUES(?,?,?,?) ON CONFLICT(bucket,dimension) DO UPDATE SET upload=upload+excluded.upload,download=download+excluded.download",u.Bucket,id,u.Upload,u.Download);
                }
                // A flush can span several minutes; each delta carries its own bucket below.
                if(totals!=null) foreach(var t in totals.OfType<TimedDelta>()) db.Run("INSERT INTO core_totals VALUES(?,?,?,?,?) ON CONFLICT(bucket) DO UPDATE SET upload=upload+excluded.upload,download=download+excluded.download,attributed_upload=attributed_upload+excluded.attributed_upload,attributed_download=attributed_download+excluded.attributed_download",t.Bucket,t.Upload,t.Download,t.AttributedUpload,t.AttributedDownload);
                PutMeta(heartbeatKey,Clock.Now.ToString(CultureInfo.InvariantCulture));db.Execute("COMMIT");
            } catch { db.Execute("ROLLBACK");throw; }
        }
    }
    public void Prune(int days) { lock(gate){long cutoff=Clock.Minute(Clock.Now-days*86400L);db.Execute("BEGIN IMMEDIATE");try{db.Run("DELETE FROM traffic WHERE bucket<?",cutoff);db.Run("DELETE FROM core_totals WHERE bucket<?",cutoff);db.Run("DELETE FROM events WHERE at<?",cutoff);db.Execute("DELETE FROM dimensions WHERE id NOT IN (SELECT DISTINCT dimension FROM traffic); COMMIT; PRAGMA wal_checkpoint(PASSIVE);");}catch{db.Execute("ROLLBACK");throw;}} }
    private static string Where(Query q,List<object> args) {
        var where="t.bucket>=? AND t.bucket<?";args.Add(q.From);args.Add(q.To);
        if(q.Scope=="proxy")where+=" AND d.source='mihomo' AND d.route='PROXY'";
        else if(q.Scope=="direct")where+=" AND d.source='mihomo' AND d.route='DIRECT'";
        else if(q.Scope=="windows")where+=" AND d.source='windows'";
        if(q.Search!=""){where+=" AND (instr(lower(d.app),lower(?))>0 OR instr(lower(d.host),lower(?))>0 OR instr(lower(d.node),lower(?))>0 OR instr(lower(d.ip),lower(?))>0)";for(int i=0;i<4;i++)args.Add(q.Search);}
        if(q.Node!=""){where+=" AND d.node=?";args.Add(q.Node);}
        if(q.App!=""){where+=" AND d.app=?";args.Add(q.App);}
        if(q.Japan)where+=" AND d.source='mihomo' AND d.route='PROXY' AND (instr(d.node,'日本')>0 OR instr(d.node,'🇯🇵')>0 OR instr(lower(d.node),'japan')>0 OR instr(lower(d.node),'[jp]')>0)";
        return where;
    }
    public object Stats(Query q) {
        lock(gate) {
            var args=new List<object>();string where=Where(q,args);string join=" FROM traffic t JOIN dimensions d ON d.id=t.dimension WHERE "+where;
            var summary=db.Query("SELECT COALESCE(SUM(t.upload),0) upload,COALESCE(SUM(t.download),0) download,COUNT(DISTINCT d.app) applications,COUNT(DISTINCT d.host) hosts,MIN(t.bucket) first,MAX(t.bucket) last"+join,args.ToArray())[0];
            var rows=db.Query("SELECT d.id,d.source,d.app,d.path,d.host,d.ip,d.node,d.route,d.network,d.rule,d.chain,SUM(t.upload) upload,SUM(t.download) download,MIN(t.bucket) first,MAX(t.bucket) last"+join+" GROUP BY d.id ORDER BY SUM(t.upload+t.download) DESC LIMIT 5001",args.ToArray());
            bool truncated=rows.Count>5000;if(truncated)rows.RemoveAt(5000);
            long trendFrom=q.TrendTo>q.TrendFrom?q.TrendFrom:q.From,trendTo=q.TrendTo>q.TrendFrom?q.TrendTo:q.To;
            long step=trendTo-trendFrom>3*86400?3600: trendTo-trendFrom>86400?900:300;
            var contextArgs=new List<object>(args);contextArgs[0]=trendFrom;contextArgs[1]=trendTo;
            var trendArgs=new List<object>{step,step};trendArgs.AddRange(contextArgs);
            var trend=db.Query("SELECT (t.bucket / ?) * ? bucket,SUM(t.upload) upload,SUM(t.download) download"+join+" GROUP BY 1 ORDER BY 1",trendArgs.ToArray());
            var kinds=db.Query("SELECT d.source,d.route,SUM(t.upload) upload,SUM(t.download) download"+join+" GROUP BY d.source,d.route",args.ToArray());
            var applicationRoutes=db.Query("SELECT DISTINCT d.app,d.path,d.source,d.route"+join,args.ToArray());
            var global=db.Query("SELECT COALESCE(SUM(upload),0) upload,COALESCE(SUM(download),0) download,COALESCE(SUM(attributed_upload),0) attributedUpload,COALESCE(SUM(attributed_download),0) attributedDownload FROM core_totals WHERE bucket>=? AND bucket<?",q.From,q.To)[0];
            var events=db.Query("SELECT at,kind,detail FROM events WHERE at>=? AND at<? ORDER BY at DESC,id DESC LIMIT 100",q.From,q.To);
            var nodes=db.Query("SELECT DISTINCT d.node FROM traffic t JOIN dimensions d ON t.dimension=d.id WHERE t.bucket>=? AND t.bucket<? AND d.source='mihomo' ORDER BY d.node",trendFrom,trendTo).Select(r=>r["node"]).ToArray();
            return new { summary=summary,rows=rows,applicationRoutes=applicationRoutes,trend=trend,trendStep=step,kinds=kinds,core=global,events=events,nodes=nodes,truncated=truncated,from=q.From,to=q.To,trendFrom=trendFrom,trendTo=trendTo };
        }
    }
    public string Csv(Query q) {
        lock(gate){var args=new List<object>();string where=Where(q,args);var text=new System.Text.StringBuilder("\uFEFF时间（北京时间）,来源,应用,程序路径,域名,目标IP,节点,路线,协议,规则,上传字节,下载字节\r\n");
            using(var s=db.Prepare("SELECT t.bucket,d.source,d.app,d.path,d.host,d.ip,d.node,d.route,d.network,d.rule,t.upload,t.download FROM traffic t JOIN dimensions d ON d.id=t.dimension WHERE "+where+" ORDER BY t.bucket,d.app LIMIT 200001",args.ToArray())) { int count=0;while(s.Step()){if(++count>200000)throw new InvalidOperationException("导出超过 20 万条，请缩小日期范围。");var r=s.Row();var fields=new[]{DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(r["bucket"])).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss"),Convert.ToString(r["source"]),Convert.ToString(r["app"]),Convert.ToString(r["path"]),Convert.ToString(r["host"]),Convert.ToString(r["ip"]),Convert.ToString(r["node"]),Convert.ToString(r["route"]),Convert.ToString(r["network"]),Convert.ToString(r["rule"]),Convert.ToString(r["upload"]),Convert.ToString(r["download"])};text.AppendLine(string.Join(",",fields.Select(CsvCell)));}}
            return text.ToString();}
    }
    internal static string CsvCell(string s) { if(s.Length>0 && "=+-@\t\r".IndexOf(s[0])>=0)s="'"+s;return "\""+s.Replace("\"","\"\"")+"\""; }
    public void Dispose() { lock(gate){db.Execute("PRAGMA wal_checkpoint(PASSIVE)");db.Dispose();} }
}
internal sealed class TimedDelta : DeltaResult {
    public long Bucket;
    public static TimedDelta From(DeltaResult r,long at){return new TimedDelta{Bucket=Clock.Minute(at),Upload=r.Upload,Download=r.Download,AttributedUpload=r.AttributedUpload,AttributedDownload=r.AttributedDownload};}
}
}
