using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace DataTrafficLedger {
internal sealed class SnapshotExpiredException : Exception { }

// Keep large dimension sets in SQLite's disk-backed temporary storage. Only a page
// of aggregates crosses into managed objects and the browser; totals always include all rows.
internal sealed class StatisticsSnapshot : IDisposable {
    private readonly Sqlite db;
    public readonly string Id=Guid.NewGuid().ToString("N");
    public long LastUsed=Clock.Now;
    public long Access;
    public object Result;
    private static readonly string[] Sorts={"total","total-asc","upload","upload-asc","download","download-asc","share","share-asc","name"};
    private static string ServiceSql() {
        var services=new[]{new[]{"YouTube","youtube.com","youtu.be","googlevideo.com","ytimg.com","youtube-nocookie.com"},new[]{"ChatGPT / OpenAI","chatgpt.com","openai.com","oaistatic.com","oaiusercontent.com"},new[]{"GitHub","github.com","githubassets.com","githubusercontent.com"},new[]{"Claude / Anthropic","claude.ai","anthropic.com"}};
        return "CASE "+string.Join(" ",services.Select(s=>"WHEN "+string.Join(" OR ",s.Skip(1).Select(h=>"lower(host)='"+h+"' OR lower(host) LIKE '%."+h+"'"))+" THEN '"+s[0]+"'"))+" ELSE lower(host) END";
    }
    public StatisticsSnapshot(string path,Query q,Func<bool> cancelled=null) {
        var watch=Stopwatch.StartNew();db=new Sqlite(path,true);
        db.SetProgress(cancelled??(()=>false));
        try {
            var args=new List<object>();string where=Store.Where(q,args),join=" FROM traffic t JOIN dimensions d ON d.id=t.dimension WHERE "+where;
            string filters=where.Substring("t.bucket>=? AND t.bucket<?".Length);
            long trendFrom=q.TrendTo>q.TrendFrom?q.TrendFrom:q.From,trendTo=q.TrendTo>q.TrendFrom?q.TrendTo:q.To;
            long step=trendTo-trendFrom>3*86400?3600:trendTo-trendFrom>86400?900:300;
            db.Execute("BEGIN");
            // Aggregate narrow minute facts first, then look up each dimension once rather than once per minute.
            db.Run("CREATE TEMP TABLE detail AS SELECT d.id,d.source,d.app,d.path,d.host,d.node,d.route,d.network,t.upload,t.download,t.first,t.last FROM (SELECT dimension,SUM(upload) upload,SUM(download) download,MIN(bucket) first,MAX(bucket) last FROM traffic WHERE bucket>=? AND bucket<? GROUP BY dimension) t JOIN dimensions d ON d.id=t.dimension WHERE 1=1"+filters,args.ToArray());
            var summary=db.Query("SELECT COALESCE(SUM(upload),0) upload,COALESCE(SUM(download),0) download,COUNT(DISTINCT app) applications,COUNT(DISTINCT host) hosts,COUNT(*) groups,MIN(first) first,MAX(last) last FROM detail")[0];
            var contextArgs=new List<object>(args);contextArgs[0]=trendFrom;contextArgs[1]=trendTo;
            var trendArgs=new List<object>{step,step};trendArgs.AddRange(contextArgs);
            var trend=db.Query("SELECT (t.bucket / ?) * ? bucket,SUM(t.upload) upload,SUM(t.download) download"+(filters==""?" FROM traffic t WHERE "+where:join)+" GROUP BY 1 ORDER BY 1",trendArgs.ToArray());
            var kinds=db.Query("SELECT source,route,SUM(upload) upload,SUM(download) download FROM detail GROUP BY source,route");
            var core=db.Query("SELECT COALESCE(SUM(upload),0) upload,COALESCE(SUM(download),0) download,COALESCE(SUM(attributed_upload),0) attributedUpload,COALESCE(SUM(attributed_download),0) attributedDownload FROM core_totals WHERE bucket>=? AND bucket<?",q.From,q.To)[0];
            var events=db.Query("SELECT at,kind,detail FROM events WHERE at>=? AND at<? ORDER BY at DESC,id DESC LIMIT 100",q.From,q.To);
            var nodes=filters==""&&trendFrom==q.From&&trendTo==q.To?db.Query("SELECT DISTINCT node FROM detail WHERE source='mihomo' ORDER BY node").Select(r=>r["node"]).ToArray():db.Query("SELECT DISTINCT node FROM dimensions WHERE source='mihomo' AND id IN (SELECT DISTINCT dimension FROM traffic WHERE bucket>=? AND bucket<?) ORDER BY node",trendFrom,trendTo).Select(r=>r["node"]).ToArray();
            db.Execute("COMMIT");
            // No main-database transaction remains open while the user browses this snapshot.
            db.Execute("ALTER TABLE detail ADD COLUMN service TEXT; UPDATE detail SET service="+ServiceSql()+"; CREATE INDEX temp.detail_app ON detail(app,path); CREATE INDEX temp.detail_node ON detail(node); CREATE INDEX temp.detail_host ON detail(host); CREATE INDEX temp.detail_service ON detail(service);");
            var topSites=db.Query("SELECT service key,SUM(upload) upload,SUM(download) download FROM detail GROUP BY service ORDER BY SUM(upload+download) DESC,service LIMIT 3");
            Result=new{snapshot=Id,summary=summary,topSites=topSites,trend=trend,trendStep=step,kinds=kinds,core=core,events=events,nodes=nodes,from=q.From,to=q.To,trendFrom=trendFrom,trendTo=trendTo,elapsedMs=watch.ElapsedMilliseconds};db.SetProgress(null);
        } catch { db.Dispose();throw; }
    }
    private static string AppKey(Dictionary<string,object> row){return Json.Write(new[]{Json.Str(row,"app"),Json.Str(row,"path")});}
    private static void Restrict(List<string> where,List<object> args,string kind,string key) {
        if(kind=="app") {
            string[] parts;try{parts=new JavaScriptSerializer().Deserialize<string[]>(key);}catch{throw new ArgumentException("无效的应用分组");}
            if(parts==null||parts.Length!=2||parts.Any(p=>p==null))throw new ArgumentException("无效的应用分组");
            where.Add("app=? AND path=?");args.AddRange(parts);
        }else{where.Add(kind+"=?");args.Add(key);}
    }
    public object Groups(string view,bool merge,string sort,int offset,int limit,string root,string child,Func<bool> cancelled=null) {
        db.SetProgress(cancelled??(()=>false));try{return ReadGroups(view,merge,sort,offset,limit,root,child);}finally{db.SetProgress(null);}
    }
    private object ReadGroups(string view,bool merge,string sort,int offset,int limit,string root,string child) {
        if(!new[]{"app","host","node"}.Contains(view)||!Sorts.Contains(sort)||offset<0||limit<1||limit>100||root==null&&child!=null)throw new ArgumentException("无效的明细分页参数");
        LastUsed=Clock.Now;
        int level=root==null?0:child==null?1:2;
        string website=merge?"service":"host",rootKind=view=="host"?website:view,childKind=view=="app"?website:"app";
        var args=new List<object>();var terms=new List<string>();
        if(root!=null)Restrict(terms,args,rootKind,root);
        if(child!=null)Restrict(terms,args,childKind,child);
        string where=terms.Count==0?"":" WHERE "+string.Join(" AND ",terms),kind=level==0?rootKind:level==1?childKind:"leaf";
        string columns=kind=="app"?"app,path":kind=="leaf"?"host,node,source,route":kind;
        string title=kind=="app"?"app":kind=="leaf"?"host":kind;
        string nameOrder=title+" COLLATE NOCASE,"+columns;
        string value=sort.StartsWith("upload")?"upload":sort.StartsWith("download")?"download":"(upload+download)";
        string order=sort=="name"?nameOrder:value+(sort.EndsWith("-asc")?" ASC,":" DESC,")+nameOrder;
        // Materialize each requested branch only once, so subsequent pages do not rescan history.
        string cacheKey=Json.Write(new{view,merge,root,child});
        PageCache cache;
        if(!pages.TryGetValue(cacheKey,out cache)) {
            string table="page_"+(++pageSequence).ToString(CultureInfo.InvariantCulture);
            try{
                db.Run("CREATE TEMP TABLE "+table+" AS SELECT "+columns+",MIN(id) id,MIN(network) network,SUM(upload) upload,SUM(download) download,COUNT(DISTINCT host) hosts,COUNT(DISTINCT node) exits,COUNT(DISTINCT app) apps FROM detail"+where+" GROUP BY "+columns,args.ToArray());
                cache=new PageCache{Table=table,Total=Convert.ToInt32(db.Query("SELECT COUNT(*) n FROM "+table)[0]["n"])};
            }catch{db.SetProgress(null);db.Execute("DROP TABLE IF EXISTS "+table);throw;}
            // Share aggregates between sort orders and bound cached rows as well as branch count.
            // One exceptionally large branch can exceed the row budget by itself; paging still stays bounded.
            while(pages.Count>0&&(pages.Count>=48||pages.Values.Sum(p=>(long)p.Total)+cache.Total>250000)){var old=pages.OrderBy(p=>p.Value.Used).First();db.Execute("DROP TABLE "+old.Value.Table);pages.Remove(old.Key);}
            pages.Add(cacheKey,cache);
        }
        cache.Used=++useSequence;
        string sortIndex=(sort.StartsWith("share")?sort.Replace("share","total"):sort).Replace('-','_');
        if(!cache.Indexes.Contains(sortIndex)){db.Execute("CREATE INDEX temp."+cache.Table+"_"+sortIndex+" ON "+cache.Table+"("+order+")");cache.Indexes.Add(sortIndex);}
        var rows=db.Query("SELECT * FROM "+cache.Table+" ORDER BY "+order+" LIMIT ? OFFSET ?",limit,offset);
        foreach(var row in rows) {
            string key=kind=="app"?AppKey(row):kind=="leaf"?Json.Write(new[]{Json.Str(row,"host"),Json.Str(row,"node"),Json.Str(row,"source"),Json.Str(row,"route")}):Json.Str(row,kind);
            row["key"]=key;row["title"]=kind=="app"?Json.Str(row,"app"):kind=="leaf"?Json.Str(row,"host"):key;
            row["subtitle"]=kind=="app"?Json.Str(row,"path"):kind=="node"?row["apps"]+" 个应用 · "+row["hosts"]+" 个域名":kind=="leaf"?Json.Str(row,"node")+(Json.Str(row,"source")=="windows"?" · 无法从加密连接确定域名":" · "+Json.Str(row,"network").ToUpperInvariant()):row["apps"]+" 个应用 · "+row["exits"]+" 个出口";
            row["applicationId"]=kind=="app"&&Json.Str(row,"path")!=""?row["id"]:"";
            row["icon"]=kind=="app"?(Json.Str(row,"app").ToLowerInvariant().Contains("chrome")?"chrome":""):kind=="node"?"node":"domain";
            var branchTerms=new List<string>(terms);var branchArgs=new List<object>(args);
            if(level<2)Restrict(branchTerms,branchArgs,kind,key);
            else {branchTerms.Add("host=? AND node=? AND source=? AND route=?");branchArgs.AddRange(new[]{row["host"],row["node"],row["source"],row["route"]});}
            string branchWhere=" WHERE "+string.Join(" AND ",branchTerms);
            row["routes"]=db.Query("SELECT DISTINCT source,route FROM detail"+branchWhere,branchArgs.ToArray());
            row["nodes"]=level<2&&!(level==0&&view=="node")?db.Query("SELECT DISTINCT node FROM detail"+branchWhere+" AND source='mihomo' AND route='PROXY' AND node<>'' ORDER BY node",branchArgs.ToArray()).Select(n=>n["node"]).ToArray():new object[0];
        }
        return new{rows=rows,total=cache.Total,offset=offset,limit=limit,hasMore=(long)offset+rows.Count<cache.Total,level=level};
    }
    private sealed class PageCache { public string Table;public int Total;public long Used;public HashSet<string> Indexes=new HashSet<string>(); }
    private readonly Dictionary<string,PageCache> pages=new Dictionary<string,PageCache>();
    private int pageSequence;private long useSequence;
    public void Dispose(){db.Dispose();}
}
}
