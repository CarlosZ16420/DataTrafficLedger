using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace DataTrafficLedger {
internal static partial class Tests {
    private static Dictionary<string,object> NodePage(NodeCatalog catalog){return Json.Read(Json.Write(catalog.Page("","all",0,100)));}
    private static string RateFile(params object[] nodes){return Json.Write(new{version=1,source="synthetic",nodes});}
    private static void ImportRates(NodeCatalog catalog,string file,bool create=false){var preview=Json.Read(Json.Write(catalog.Import(file,false,create,0)));catalog.Import(file,true,create,Json.Num(preview,"revision"));}
    private static void TestNodes(string root){
        Directory.CreateDirectory(root);
        Assert(NodeRate.Detect("日本 | 7.5x | V3")==7.5m&&NodeRate.Detect("[倍率:0.5] 日本")==.5m&&NodeRate.Detect("香港 x0")==0,"Rates recognize decimal and free nodes without mistaking protocol versions for multipliers");
        Assert(!NodeRate.Detect("IPv6 | V1 | 200M").HasValue&&!NodeRate.Detect("proxy.example.com:123").HasValue&&!NodeRate.Detect("1x | 2x").HasValue,"Missing and conflicting name rates remain unknown");
        foreach(string bad in new[]{"-1","NaN","Infinity","10001","0.0000001"}){bool rejected=false;try{NodeRate.Parse(bad);}catch(ArgumentException){rejected=true;}Assert(rejected,"Invalid multiplier rejected: "+bad);}
        var raw=Json.Read("{\"proxies\":{\"日本 | 7.5x | V3\":{\"type\":\"Vless\"},\"未使用节点\":{\"type\":\"Trojan\"},\"DIRECT\":{\"type\":\"Direct\"},\"PASS-RULE\":{\"type\":\"PassRule\"},\"策略组\":{\"type\":\"Selector\",\"all\":[\"未使用节点\"]}}}");
        var provider=Json.Read("{\"providers\":{\"策略组\":{\"vehicleType\":\"Compatible\",\"proxies\":[{\"name\":\"未使用节点\",\"type\":\"Trojan\"}]}}}");
        var inventory=NodeCatalog.ReadInventory(raw,provider);
        Assert(inventory.Count==2&&inventory.All(n=>n.Provider==""),"Complete node reading includes unused proxies and excludes policy copies, direct and pass-rule entries");
        var catalog=new NodeCatalog(root);catalog.ReplaceInventory(inventory);
        Assert(Json.Num(NodePage(catalog),"all")==2&&Json.Num(NodePage(catalog),"known")==1,"Node inventory is populated without observing any traffic");
        string id=NodeCatalog.Id("","未使用节点");
        ImportRates(catalog,RateFile(new{name="未使用节点",multiplier=3}));
        Assert(catalog.Resolve(new Dimension{Node="未使用节点"}).Multiplier==3,"Exact imported node names fill missing subscription multipliers");
        catalog.SetManual(id,"2");catalog.ReplaceInventory(inventory);ImportRates(catalog,RateFile(new{name="未使用节点",multiplier=15}));
        Assert(catalog.Resolve(new Dimension{Node="未使用节点"}).Multiplier==2,"Manual locks survive both inventory refresh and file import");
        catalog.SetManual(id,"");Assert(catalog.Resolve(new Dimension{Node="未使用节点"}).Multiplier==3,"Restoring automatic rates restores the last imported value rather than silently assuming one");
        var stale=Json.Read(Json.Write(catalog.Import(RateFile(new{name="未使用节点",multiplier=5}),false,false,0)));catalog.SetManual(id,"4");bool conflict=false;try{catalog.Import(RateFile(new{name="未使用节点",multiplier=5}),true,false,Json.Num(stale,"revision"));}catch(ArgumentException){conflict=true;}
        Assert(conflict,"Import cannot apply a preview made before another edit or inventory refresh");
        catalog.SetManual(id,"");string before=catalog.Export();bool invalid=false;try{ImportRates(catalog,RateFile(new{name="未使用节点",multiplier=9},new{name="invalid",multiplier=-1}));}catch(ArgumentException){invalid=true;}
        Assert(invalid&&catalog.Export()==before,"A malformed rate file cannot partially update valid rows");
        bool duplicate=false;try{ImportRates(catalog,RateFile(new{name="新节点",multiplier=2},new{id=NodeCatalog.Id("","新节点"),name="新节点",multiplier=3}),true);}catch(ArgumentException){duplicate=true;}Assert(duplicate&&catalog.Export()==before,"Alias entries targeting the same newly created node cannot overwrite each other");
        ImportRates(catalog,RateFile(new{name="离线节点",multiplier=.5m}),true);var reopened=new NodeCatalog(root);
        Assert(reopened.Resolve(new Dimension{Node="离线节点"}).Multiplier==.5m,"Explicitly created offline nodes and rates survive restart in a separate file");
        ImportRates(catalog,RateFile(new{name="同名",provider="A",multiplier=2},new{name="同名",provider="B",multiplier=3}),true);
        var ambiguous=Json.Read(Json.Write(catalog.Import(RateFile(new{name="同名",multiplier=5}),false,false,0)));
        Assert(Json.Str(Json.Array(ambiguous,"rows").OfType<Dictionary<string,object>>().Single(),"status")=="ambiguous"&&!catalog.Resolve(new Dimension{Node="同名"}).Known,"Ambiguous provider names never get an invented billing multiplier");
        Assert(catalog.Resolve(new Dimension{Node="同名",Route="DIRECT"}).Multiplier==1&&catalog.Resolve(new Dimension{Node="同名",Source="windows"}).Multiplier==1,"Direct and Windows flows cannot inherit proxy multipliers");
        var paged=new NodeCatalog(Path.Combine(root,"pages"));Directory.CreateDirectory(Path.Combine(root,"pages"));ImportRates(paged,RateFile(Enumerable.Range(0,65).Select(i=>(object)new{name="测试节点 "+i.ToString("D2"),multiplier=2}).ToArray()),true);
        var first=Json.Read(Json.Write(paged.Page("","all",0,30)));var second=Json.Read(Json.Write(paged.Page("","all",30,30)));var last=Json.Read(Json.Write(paged.Page("","all",60,30)));
        Assert(Json.Num(first,"total")==65&&Json.Array(first,"rows").Count()==30&&Json.Array(second,"rows").Count()==30&&Json.Array(last,"rows").Count()==5,"Node catalog pagination covers all 65 unused nodes");
    }
    private static void TestBilling(string root){
        using(var store=new Store(root)){
            long minute=Clock.Minute(Clock.Now);var dim=new Dimension{App="billing.exe",Host="synthetic.example",Node="计费节点"};
            store.Catalog.SetManual("","7.5","计费节点");
            var buffered=new Usage{Bucket=minute,Dimension=dim,Upload=2,Download=10,Rate=store.Catalog.Resolve(dim)};
            store.Catalog.SetManual(NodeCatalog.Id("","计费节点"),"2");store.Write(new[]{buffered,new Usage{Bucket=minute,Dimension=dim,Upload=3,Download=20}},null,"billing");
            var query=new Query{From=minute,To=minute+60,ApplyMultipliers=true};var data=Json.Read(Json.Write(store.Stats(query)));var summary=Json.Obj(data,"summary");
            Assert(Json.Num(summary,"upload")==21&&Json.Num(summary,"download")==115&&Json.Num(summary,"rawUpload")==5&&Json.Num(summary,"rawDownload")==30,"Rates are frozen before buffering; multiple decimal rates in the same minute preserve raw bytes");
            Assert(Json.Num(summary,"unknownRaw")==0&&Json.Num(Json.Obj(data,"core"),"recordedDownload")==30,"Billing never multiplies raw core reconciliation and confirmed 1x is distinguishable from unknown");
            store.Catalog.SetManual(NodeCatalog.Id("","计费节点"),"15");data=Json.Read(Json.Write(store.Stats(query)));Assert(Json.Num(Json.Obj(data,"summary"),"download")==115,"Changing a node multiplier cannot reprice historical facts");
            var group=PageRows(Page(store,data,"node"))[0];Assert(Json.Num(group,"download")==115&&Json.Num(group,"rawDownload")==30&&Json.Array(data,"trend").OfType<Dictionary<string,object>>().Sum(r=>Json.Num(r,"download"))==115,"Trend and paged detail groups use the same weighted snapshot as the summary");
            var direct=new Dimension{Node="计费节点",Route="DIRECT"};var outside=new Dimension{Node="计费节点",Source="windows"};var unknown=new Dimension{Node="未知倍率"};
            store.Write(new[]{new Usage{Bucket=minute,Dimension=direct,Download=10},new Usage{Bucket=minute,Dimension=outside,Download=20},new Usage{Bucket=minute,Dimension=unknown,Download=40}},null,"billing");
            // Emulate a copied 1.6.1 fact that has never had a rate snapshot.
            using(var writer=new Sqlite(store.FilePath))writer.Run("UPDATE traffic SET download=download+5 WHERE bucket=? AND dimension=?",minute,dim.Id);
            data=Json.Read(Json.Write(store.Stats(query)));summary=Json.Obj(data,"summary");
            Assert(Json.Num(summary,"download")==190&&Json.Num(summary,"rawDownload")==105&&Json.Num(summary,"unknownRaw")==45,"Legacy and unknown proxy bytes stay at 1x; direct and Windows traffic is never weighted");
            string csv=store.Csv(query);Assert(csv.Contains("倍率后估算")&&csv.Contains("原始下载字节")&&csv.Contains("展示下载字节"),"CSV explicitly includes raw and selected measurement columns");
            query.ApplyMultipliers=false;Assert(Json.Num(Json.Obj(Json.Read(Json.Write(store.Stats(query))),"summary"),"download")==105,"Turning off automatic multiplication restores the original bytes without rewriting history");
            store.Catalog.SetManual(NodeCatalog.Id("","计费节点"),"0");store.Write(new[]{new Usage{Bucket=minute+60,Dimension=dim,Download=9}},null,"billing");query.From=minute+60;query.To=minute+120;query.ApplyMultipliers=true;
            Assert(Json.Num(Json.Obj(Json.Read(Json.Write(store.Stats(query))),"summary"),"download")==0,"A confirmed zero multiplier preserves raw bytes while estimating zero package debit");
            using(var writer=new Sqlite(store.FilePath)){writer.Execute("CREATE TRIGGER fail_rates BEFORE INSERT ON rate_usage BEGIN SELECT RAISE(ABORT,'synthetic failure'); END");bool failed=false;try{store.Write(new[]{new Usage{Bucket=minute+60,Dimension=dim,Download=9}},null,"billing");}catch(InvalidOperationException){failed=true;}Assert(failed&&Convert.ToInt64(writer.Query("SELECT SUM(download) n FROM traffic WHERE bucket=?",minute+60)[0]["n"])==9,"A rate snapshot failure rolls back raw facts and billing adjustments together");writer.Execute("DROP TRIGGER fail_rates");}
        }
    }
    private static string NodePost(int port,string target,string token,string body){using(var client=new TcpClient("127.0.0.1",port))using(var stream=client.GetStream()){byte[] bytes=Encoding.UTF8.GetBytes(body),head=Encoding.ASCII.GetBytes("POST "+target+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nCookie: ledger_session="+token+"\r\nX-Ledger-Token: "+token+"\r\nContent-Length: "+bytes.Length+"\r\nConnection: close\r\n\r\n");stream.Write(head,0,head.Length);stream.Write(bytes,0,bytes.Length);using(var reader=new StreamReader(stream))return reader.ReadToEnd();}}
    private static void TestNodeServer(string root){var config=new LedgerConfig();using(var store=new Store(root))using(var collector=new Collector(store,config,root))using(var server=new LocalServer(store,collector,config,root,()=>{})){server.Start();string cookie="Cookie: ledger_session="+server.Token+"\r\n",write=cookie+"X-Ledger-Token: "+server.Token+"\r\nContent-Length: 0\r\n";
        Assert(Request(server.Port,"/api/nodes").StartsWith("HTTP/1.1 401"),"Node catalogs require an authenticated local session");
        Assert(Request(server.Port,"/api/nodes/manual?name=test&multiplier=3",cookie,"POST").StartsWith("HTTP/1.1 403"),"Manual rate edits require anti-forgery authentication");
        string file=RateFile(new{name="UTF-8 日本",multiplier=7.5m});string preview=NodePost(server.Port,"/api/nodes/import?createMissing=true",server.Token,file);
        Assert(preview.StartsWith("HTTP/1.1 200")&&preview.Contains("UTF-8 日本")&&Json.Num(NodePage(store.Catalog),"all")==0,"UTF-8 body import can preview without writing catalog data");
        Assert(NodePost(server.Port,"/api/nodes/import?createMissing=true&apply=true&revision=0",server.Token,file).StartsWith("HTTP/1.1 200")&&Json.Num(NodePage(store.Catalog),"all")==1,"Applying an unchanged preview writes the authenticated node import");
        Assert(NodePost(server.Port,"/api/nodes/import?createMissing=true&apply=true&revision=0",server.Token,file).StartsWith("HTTP/1.1 400"),"The HTTP import endpoint rejects stale catalog revisions");
        Assert(Request(server.Port,"/api/multipliers?enabled=false",write,"POST").StartsWith("HTTP/1.1 200")&&!LedgerConfig.Load(root).AutoMultiplier,"The default-enabled billing preference is persisted independently of theme settings");
        Assert(NodePost(server.Port,"/api/port?port=0",server.Token,file).StartsWith("HTTP/1.1 400"),"Only the node import endpoint accepts request bodies");
        Assert(Request(server.Port,"/api/nodes/export",cookie).Contains("filename=node-rates.json"),"Users and AI tools can export a portable, versioned multiplier file");
    }}
}
}
