using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace DataTrafficLedger {
internal sealed class NodeRate {
    public decimal Multiplier=1;
    public bool Known;
    public string Source="unknown";
    public static string Number(decimal value){return value.ToString("0.######",CultureInfo.InvariantCulture);}
    public static decimal Parse(string text){decimal value;if(!decimal.TryParse(text,NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out value)||value<0||value>10000||decimal.Round(value,6)!=value)throw new ArgumentException("倍率应为 0–10000 的数字，最多六位小数。");return value;}
    public static decimal? Detect(string name){
        // Delimiters prevent interpreting protocol versions, IPv6, ports or hostnames as rates.
        var found=Regex.Matches(name??"",@"(?:^|[\s|｜\[【(（])(?:(?:倍率\s*[:：]?\s*|[xX×]\s*)(\d+(?:\.\d+)?)|(\d+(?:\.\d+)?)\s*(?:[xX×]|倍))(?:$|[\s|｜\]】)）])");
        var values=new HashSet<decimal>();foreach(Match m in found){try{values.Add(Parse(m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value));}catch{}}
        return values.Count==1?(decimal?)values.Single():null;
    }
}
internal sealed class CatalogNode {
    public string Id,Name,Provider="",Type="",Origin="manual",DetectedSource="",ImportedSource="";
    public bool Present;
    public long LastSeen,Updated;
    public decimal? Detected,Imported,Manual;
    public NodeRate Rate(){return Manual.HasValue?new NodeRate{Multiplier=Manual.Value,Known=true,Source="manual"}:Imported.HasValue?new NodeRate{Multiplier=Imported.Value,Known=true,Source="file:"+ImportedSource}:Detected.HasValue?new NodeRate{Multiplier=Detected.Value,Known=true,Source=DetectedSource}:new NodeRate();}
}
internal sealed class CatalogState {
    public int Version=1;
    public long Revision,LastRead;
    public List<CatalogNode> Nodes=new List<CatalogNode>();
}
internal sealed class NodeCatalog {
    private readonly object gate=new object();
    private readonly string path;
    private CatalogState state;
    private string error="";
    public NodeCatalog(string directory){
        path=Path.Combine(directory,"nodes.json");
        if(File.Exists(path)){
            try{state=new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Deserialize<CatalogState>(File.ReadAllText(path));if(state==null||state.Version!=1||state.Nodes==null||state.Nodes.Count>10000)throw new InvalidDataException();foreach(var node in state.Nodes){ValidateNode(node.Name,node.Provider);if(node.Id!=Id(node.Provider,node.Name))throw new InvalidDataException();foreach(var rate in new[]{node.Manual,node.Imported,node.Detected})if(rate.HasValue)NodeRate.Parse(rate.Value.ToString(CultureInfo.InvariantCulture));}if(state.Nodes.Select(n=>n.Id).Distinct().Count()!=state.Nodes.Count)throw new InvalidDataException();}
            catch{throw new InvalidDataException("节点文件 nodes.json 无效；请保留原文件并修复，程序不会覆盖它。");}
        }else state=new CatalogState();
    }
    internal static string Id(string provider,string name){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(new[]{provider,name})))).Replace("-","").ToLowerInvariant();}
    private static void ValidateNode(string name,string provider){if(string.IsNullOrWhiteSpace(name)||name.Length>300||provider==null||provider.Length>300||name.Any(char.IsControl)||provider.Any(char.IsControl))throw new ArgumentException("节点名称和来源不能为空或超过 300 个字符，且不能含控制字符。");}
    private CatalogState Copy(){return new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Deserialize<CatalogState>(Json.Write(state));}
    private void Commit(CatalogState next){if(next.Nodes.Count>10000)throw new ArgumentException("节点目录最多保存 10000 项。");next.Revision=state.Revision+1;AtomicFile.Write(path,Json.Write(next));state=next;}
    public void SeedHistory(IEnumerable<string> names){lock(gate){var next=Copy();foreach(string name in names.Where(n=>!string.IsNullOrWhiteSpace(n)&&n.Length<=300&&!n.Any(char.IsControl)).Distinct())if(!next.Nodes.Any(n=>n.Name==name))next.Nodes.Add(new CatalogNode{Id=Id("",name),Name=name,Origin="history",Detected=NodeRate.Detect(name),DetectedSource="name"});if(next.Nodes.Count!=state.Nodes.Count)Commit(next);}}
    internal static bool IsProxy(Dictionary<string,object> row){string type=Json.Str(row,"type");return Json.Str(row,"name")!=""&&!row.ContainsKey("all")&&!new[]{"direct","reject","rejectdrop","pass","passrule","compatible","dns","selector","urltest","fallback","loadbalance","relay"}.Contains(type.ToLowerInvariant());}
    public static List<CatalogNode> ReadInventory(Dictionary<string,object> proxies,Dictionary<string,object> providers){
        var nodes=new List<CatalogNode>();var collection=Json.Obj(providers,"providers");
        // Compatible providers are synthetic policy-group copies, not subscriptions.
        if(collection!=null)foreach(var pair in collection){var provider=pair.Value as Dictionary<string,object>;if(provider==null||Json.Str(provider,"vehicleType").Equals("Compatible",StringComparison.OrdinalIgnoreCase))continue;foreach(var row in Json.Array(provider,"proxies").OfType<Dictionary<string,object>>().Where(IsProxy))nodes.Add(FromApi(row,pair.Key));}
        var all=Json.Obj(proxies,"proxies");if(all==null)throw new InvalidDataException("Mihomo 未返回节点目录。");
        foreach(var pair in all){var row=pair.Value as Dictionary<string,object>;if(row==null)continue;var named=new Dictionary<string,object>(row);named["name"]=pair.Key;if(IsProxy(named)&&!nodes.Any(n=>n.Name==pair.Key))nodes.Add(FromApi(named,""));}
        return nodes.GroupBy(n=>n.Id).Select(g=>g.First()).ToList();
    }
    private static CatalogNode FromApi(Dictionary<string,object> row,string provider){
        string name=Json.Str(row,"name");ValidateNode(name,provider);decimal? multiplier=null;string source="name";
        foreach(string field in new[]{"traffic-multiplier","trafficMultiplier","multiplier"})if(row.ContainsKey(field)){try{multiplier=NodeRate.Parse(Json.Str(row,field));source="metadata";break;}catch{}}
        return new CatalogNode{Id=Id(provider,name),Name=name,Provider=provider,Type=Json.Str(row,"type"),Origin="mihomo",Present=true,LastSeen=Clock.Now,Detected=multiplier??NodeRate.Detect(name),DetectedSource=source};
    }
    public object Refresh(string pipe,CancellationToken cancellation=default(CancellationToken)){try{var client=new MihomoClient(pipe,cancellation);var proxies=client.Get("/proxies");Dictionary<string,object> providers=null;try{providers=client.Get("/providers/proxies");}catch(OperationCanceledException){throw;}catch{}cancellation.ThrowIfCancellationRequested();ReplaceInventory(ReadInventory(proxies,providers));return Page("","all",0,30);}catch(Exception ex){lock(gate)error=ex.GetBaseException().Message;throw;}}
    public void ReplaceInventory(IEnumerable<CatalogNode> inventory){var read=inventory.ToList();lock(gate){var next=Copy();foreach(var old in next.Nodes)old.Present=false;
        foreach(var item in read){var node=next.Nodes.FirstOrDefault(n=>n.Id==item.Id);if(node==null){node=next.Nodes.FirstOrDefault(n=>n.Name==item.Name&&n.Provider==""&&n.Origin=="history");if(node!=null){node.Id=item.Id;node.Provider=item.Provider;}else{node=new CatalogNode{Id=item.Id,Name=item.Name,Provider=item.Provider};next.Nodes.Add(node);}}
            node.Type=item.Type;node.Origin="mihomo";node.Present=true;node.LastSeen=Clock.Now;node.Detected=item.Detected;node.DetectedSource=item.DetectedSource;}
        next.LastRead=Clock.Now;Commit(next);error="";}}
    public NodeRate Resolve(Dimension dimension){if(dimension.Source!="mihomo"||dimension.Route!="PROXY")return new NodeRate{Known=true,Source="not-proxy"};lock(gate){var matches=state.Nodes.Where(n=>n.Name==dimension.Node).ToList();var present=matches.Where(n=>n.Present).ToList();if(present.Count>0)matches=present;if(matches.Count==1)return matches[0].Rate();if(matches.Count>1)return new NodeRate{Source="ambiguous"};decimal? detected=NodeRate.Detect(dimension.Node);return detected.HasValue?new NodeRate{Known=true,Multiplier=detected.Value,Source="name"}:new NodeRate();}}
    private static object View(CatalogNode n){var rate=n.Rate();return new{id=n.Id,name=n.Name,provider=n.Provider,type=n.Type,origin=n.Origin,present=n.Present,lastSeen=n.LastSeen,updated=n.Updated,multiplier=rate.Known?(decimal?)rate.Multiplier:null,source=rate.Source,manual=n.Manual.HasValue,detected=n.Detected,imported=n.Imported,importedSource=n.ImportedSource};}
    public object Page(string search,string filter,int offset,int limit){if(search.Length>300||offset<0||limit<1||limit>100||!new[]{"all","unknown","manual","present"}.Contains(filter))throw new ArgumentException("无效的节点筛选或分页。");lock(gate){var rows=state.Nodes.Where(n=>(search==""||(n.Name+" "+n.Provider).IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0)&&(filter=="all"||filter=="unknown"&&!n.Rate().Known||filter=="manual"&&n.Manual.HasValue||filter=="present"&&n.Present)).OrderBy(n=>n.Name,StringComparer.OrdinalIgnoreCase).ThenBy(n=>n.Provider,StringComparer.Ordinal).ToList();return new{rows=rows.Skip(offset).Take(limit).Select(View).ToArray(),total=rows.Count,offset,limit,revision=state.Revision,lastRead=state.LastRead,error,all=state.Nodes.Count,known=state.Nodes.Count(n=>n.Rate().Known),present=state.Nodes.Count(n=>n.Present)};}}
    public object SetManual(string id,string multiplier,string name="",string provider=""){
        lock(gate){var next=Copy();var node=next.Nodes.SingleOrDefault(n=>n.Id==id);if(node==null){ValidateNode(name,provider);if(next.Nodes.Any(n=>n.Name==name&&n.Provider==provider))throw new ArgumentException("同一来源已有该节点。");node=new CatalogNode{Id=Id(provider,name),Name=name,Provider=provider,Detected=NodeRate.Detect(name),DetectedSource="name"};next.Nodes.Add(node);}
            node.Manual=multiplier==""?null:(decimal?)NodeRate.Parse(multiplier);node.Updated=Clock.Now;Commit(next);return View(node);}
    }
    private sealed class ImportRow {public string Id,Name,Provider;public decimal? Multiplier;}
    private static List<ImportRow> ParseImport(string text,out string source){
        if(text==null||Encoding.UTF8.GetByteCount(text)>1024*1024)throw new ArgumentException("倍率文件不能超过 1 MB。");Dictionary<string,object> doc;try{doc=Json.Read(text);}catch{throw new ArgumentException("倍率文件应为 JSON 对象。");}
        if(doc==null||Json.Num(doc,"version")!=1||!doc.ContainsKey("nodes"))throw new ArgumentException("倍率文件需要 version: 1 和 nodes 数组。");source=Json.Str(doc,"source","倍率文件");if(source.Length>120||source.Any(char.IsControl))throw new ArgumentException("文件来源说明过长或含控制字符。");
        var rows=new List<ImportRow>();foreach(var value in Json.Array(doc,"nodes")){var row=value as Dictionary<string,object>;if(row==null)throw new ArgumentException("nodes 中每一项应为节点对象。");string name=Json.Str(row,"name"),provider=Json.Str(row,"provider");ValidateNode(name,provider);object raw;decimal? rate=row.TryGetValue("multiplier",out raw)&&raw!=null?(decimal?)NodeRate.Parse(Json.Str(row,"multiplier")):null;rows.Add(new ImportRow{Id=Json.Str(row,"id"),Name=name,Provider=provider,Multiplier=rate});if(rows.Count>10000)throw new ArgumentException("倍率文件最多 10000 项。");}
        if(rows.Count==0||rows.GroupBy(r=>Json.Write(new[]{r.Id,r.Provider,r.Name})).Any(g=>g.Count()>1))throw new ArgumentException("倍率文件为空或存在重复节点。");return rows;
    }
    public object Import(string text,bool apply,bool createMissing,long revision){string source;var rows=ParseImport(text,out source);lock(gate){if(apply&&revision!=state.Revision)throw new ArgumentException("节点目录已变化，请重新预览后导入。");var next=Copy();var preview=new List<object>();var used=new HashSet<string>();int changed=0;
        foreach(var row in rows){var matches=next.Nodes.Where(n=>row.Id!=""?n.Id==row.Id:row.Provider!=""?n.Provider==row.Provider&&n.Name==row.Name:n.Name==row.Name).ToList();string status;
            if(matches.Count>1)status="ambiguous";
            else if(matches.Count==0){if(row.Id!=""&&row.Id!=Id(row.Provider,row.Name))throw new ArgumentException("节点标识与名称、来源不一致。");status=createMissing?"create":"missing";if(createMissing){var node=new CatalogNode{Id=Id(row.Provider,row.Name),Name=row.Name,Provider=row.Provider,Origin="file",Imported=row.Multiplier,ImportedSource=source,Updated=Clock.Now};next.Nodes.Add(node);used.Add(node.Id);changed++;}}
            else{var node=matches[0];if(node.Name!=row.Name||row.Provider!=""&&node.Provider!=row.Provider)throw new ArgumentException("节点标识与名称、来源不一致。");if(!used.Add(node.Id))throw new ArgumentException("文件中多个条目匹配到同一节点。");status=node.Manual.HasValue?"locked":row.Multiplier.HasValue?"match":"no-rate";if(status=="match"){node.Imported=row.Multiplier;node.ImportedSource=source;node.Updated=Clock.Now;changed++;}}
            preview.Add(new{name=row.Name,provider=row.Provider,multiplier=row.Multiplier,status});}
        if(apply&&changed>0)Commit(next);return new{source,rows=preview,changed,applied=apply,revision=state.Revision};}}
    public string Export(){lock(gate)return Json.Write(new{version=1,source="流量账本导出",nodes=state.Nodes.OrderBy(n=>n.Name).Select(n=>new{id=n.Id,name=n.Name,provider=n.Provider,multiplier=n.Rate().Known?(decimal?)n.Rate().Multiplier:null}).ToArray()});}
}
}
