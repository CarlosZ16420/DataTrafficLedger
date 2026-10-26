using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataTrafficLedger {
internal static partial class Tests {
    private static Dictionary<string,object> Page(Store store,Dictionary<string,object> data,string view="app",bool merge=true,string sort="total",int offset=0,int limit=30,string root=null,string child=null){return Json.Read(Json.Write(store.Groups(Json.Str(data,"snapshot"),view,merge,sort,offset,limit,root,child)));}
    private static Dictionary<string,object>[] PageRows(Dictionary<string,object> page){return Json.Array(page,"rows").OfType<Dictionary<string,object>>().ToArray();}
    private static void TestPaging(string root) {
        using(var store=new Store(root)) {
            long day=Clock.DateBoundary("2026-10-15",false);var facts=new List<Usage>();
            for(int i=0;i<6205;i++)facts.Add(new Usage{Bucket=day,Dimension=new Dimension{App="app-"+(i%43),Path="C:\\"+(i%43)+".exe",Host=i<6000?"tail.youtube.com":"host-"+i,Ip="ip-"+i,Node=i%2==0?"日本 A":"DIRECT",Route=i%2==0?"PROXY":"DIRECT"},Upload=i%7+1,Download=1});
            store.Write(facts,null,"paging");var q=new Query{From=day,To=day+86400};var data=Json.Read(Json.Write(store.Stats(q)));
            Assert(Json.Num(Json.Obj(data,"summary"),"groups")==6205,"Complete dimension count is available beyond the former 5,000-group cap");
            long expected=facts.Sum(r=>r.Upload+r.Download),sum=0;var keys=new HashSet<string>();
            for(int offset=0;offset<43;offset+=17){var page=Page(store,data,offset:offset,limit:17);foreach(var row in PageRows(page)){sum+=Json.Num(row,"upload")+Json.Num(row,"download");Assert(keys.Add(Json.Str(row,"key")),"Stable aggregate page has no duplicate application");}}
            Assert(keys.Count==43&&sum==expected,"Every application total covers all dimensions, across non-overlapping pages");
            var sites=Json.Array(data,"topSites").OfType<Dictionary<string,object>>().ToArray();
            Assert(Json.Str(sites[0],"key")=="YouTube"&&Json.Num(sites[0],"download")==6000,"Popular-site ranking includes the long tail and merges real service subdomains");
            foreach(string view in new[]{"app","host","node"})foreach(bool merge in new[]{false,true}) {
                var rows=PageRows(Page(store,data,view,merge,limit:100));
                foreach(var parent in rows.Take(3)){
                    var children=PageRows(Page(store,data,view,merge,limit:100,root:Json.Str(parent,"key")));
                    long total=children.Sum(r=>Json.Num(r,"upload")+Json.Num(r,"download"));
                    if(children.Length<100)Assert(total==Json.Num(parent,"upload")+Json.Num(parent,"download"),view+": nested aggregate sums match parent totals");
                    Assert(Json.Array(parent,"routes").Any()&&(view=="node"||!Json.Array(parent,"routes").OfType<Dictionary<string,object>>().Any(r=>Json.Str(r,"route")=="PROXY")||Json.Array(parent,"nodes").Any()),view+": route badges and actual nodes accompany aggregates before expansion");
                }
            }
            foreach(string sort in new[]{"total","total-asc","upload","upload-asc","download","download-asc","share","share-asc","name"}) {
                var rows=PageRows(Page(store,data,sort:sort,limit:100));
                if(sort!="name") {var values=rows.Select(r=>sort.StartsWith("upload")?Json.Num(r,"upload"):sort.StartsWith("download")?Json.Num(r,"download"):Json.Num(r,"upload")+Json.Num(r,"download")).ToArray();Assert(values.Zip(values.Skip(1),(a,b)=>sort.EndsWith("asc")?a<=b:a>=b).All(x=>x),"Server sorts complete aggregates: "+sort);}
            }
            var original=PageRows(Page(store,data))[0];store.Write(new[]{new Usage{Bucket=day,Dimension=facts[0].Dimension,Download=90000}},null,"paging");
            Assert(PageRows(Page(store,data)).Sum(r=>Json.Num(r,"download"))<90000,"Open pages remain on their original snapshot while capture continues");
            var updated=Json.Read(Json.Write(store.Stats(q)));Assert(Json.Num(Json.Obj(updated,"summary"),"download")==96205,"Refreshing creates a fresh snapshot with newly captured records");
            Assert(Json.Num(Page(store,data,offset:99999),"total")==43&&PageRows(Page(store,data,offset:99999)).Length==0,"An offset beyond the end is an empty bounded page");
            foreach(int limit in new[]{0,101}) {bool rejected=false;try{Page(store,data,limit:limit);}catch(ArgumentException){rejected=true;}Assert(rejected,"Server enforces page-size bounds: "+limit);}
            bool invalid=false;try{Page(store,data,sort:"upload; DROP TABLE traffic");}catch(ArgumentException){invalid=true;}Assert(invalid,"Sort expressions are allowlisted instead of interpolating client SQL");
            store.Stats(q);store.Stats(q);store.Stats(q);bool expired=false;try{Page(store,data);}catch(SnapshotExpiredException){expired=true;}Assert(expired,"Old snapshots are evicted at the bounded cache size");
            var write=Task.Run(()=>{for(int i=0;i<10;i++)store.Write(new[]{new Usage{Bucket=day,Dimension=facts[0].Dimension,Download=1}},null,"paging");});store.Stats(q);Assert(write.Wait(5000),"Independent statistics reads permit collector writes to proceed");
            int probes=0;bool cancelled=false;try{store.Stats(q,()=>++probes>2);}catch(OperationCanceledException){cancelled=true;}Assert(cancelled&&probes>2,"An obsolete large query is interrupted during SQLite work");
            var recovered=Json.Read(Json.Write(store.Stats(q)));Assert(Json.Num(Json.Obj(recovered,"summary"),"download")==96215,"A cancelled read leaves stored records and the next query intact");
        }
    }
    private static int BenchmarkStatistics() {
        string root=Path.Combine(Environment.CurrentDirectory,"tests","benchmark-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            foreach(int count in new[]{10000,50000,100000}) {
                using(var store=new Store(Path.Combine(root,count.ToString()))) {
                    long day=Clock.DateBoundary("2026-10-15",false);
                    for(int start=0;start<count;start+=1000) {
                        var batch=new List<Usage>();
                        for(int i=start;i<Math.Min(count,start+1000);i++) {
                            var dim=new Dimension{App="app-"+(i%100)+".exe",Path="C:\\Apps\\app-"+(i%100)+".exe",Host="site-"+i+".example",Node="节点 "+(i%10),Ip="10.0."+(i/256%256)+"."+(i%256)};
                            for(int minute=0;minute<5;minute++)batch.Add(new Usage{Bucket=day+minute*60,Dimension=dim,Upload=i%13+1,Download=i%97+1});
                        }
                        store.Write(batch,null,"benchmark");
                    }
                    var q=new Query{From=day,To=day+86400};
                    var watch=Stopwatch.StartNew();var result=Json.Read(Json.Write(store.Stats(q)));watch.Stop();
                    var measures=new Dictionary<string,object>{{"groups",count},{"records",count*5},{"statisticsMs",watch.ElapsedMilliseconds},{"responseBytes",Encoding.UTF8.GetByteCount(Json.Write(result))}};
                    string token=Json.Str(result,"snapshot");
                    if(token!="") {
                        watch.Restart();var page=store.Groups(token,"host",true,"total",0,30,null,null);watch.Stop();
                        measures["firstPageMs"]=watch.ElapsedMilliseconds;measures["pageBytes"]=Encoding.UTF8.GetByteCount(Json.Write(page));
                        watch.Restart();store.Groups(token,"host",true,"total",30,30,null,null);watch.Stop();measures["nextPageMs"]=watch.ElapsedMilliseconds;
                        watch.Restart();store.Groups(token,"host",true,"total",30,30,null,null);watch.Stop();measures["nextPageMs"]=watch.ElapsedMilliseconds;
                    }
                    Console.WriteLine(Json.Write(measures));
                }
            }
            return 0;
        } finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
}
