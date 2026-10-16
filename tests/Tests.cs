using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Drawing;
using System.Drawing.Imaging;

namespace LocalTrafficLedger {
internal static class Tests {
    private static int passed;
    private static void Assert(bool value,string name){if(!value)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS "+name);}
    private static Snapshot Sample(long upload,long download,params Connection[] connections){return new Snapshot{Upload=upload,Download=download,Connections=connections.ToList()};}
    private static Connection Conn(string id,long start,long upload,long download){return new Connection{Id=id,Started=start,Upload=upload,Download=download,Dimension=new Dimension{App="chrome.exe",Host="example.com",Node="日本测试节点"}};}
    public static int Main(string[] args){
        if(args.Length==2&&args[0]=="--serve-fixture")return ServeFixture(args[1]);
        string root=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"tests","run-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        try{
            TestDeltas();TestHttp();TestNativeEvents();TestStorage(root);TestTimeFilters(Path.Combine(root,"time-filters"));TestIcons(Path.Combine(root,"icons"));TestConfig(Path.Combine(root,"config"));TestServer(Path.Combine(root,"server"));TestPortConflict(Path.Combine(root,"port-conflict"));
            Console.WriteLine("\n"+passed+" meaningful checks passed.");return 0;
        }catch(Exception ex){Console.WriteLine(ex);return 1;}
        finally{string allowed=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"tests"))+Path.DirectorySeparatorChar;if(root.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static int ServeFixture(string directory){
        string root=Path.GetFullPath(directory),allowed=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"tests"))+Path.DirectorySeparatorChar;
        if(!root.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Fixture data must stay inside tests.");
        Directory.CreateDirectory(root);var config=new LedgerConfig();config.Save(root);
        using(var store=new Store(root))using(var collector=new Collector(store,config,root))using(var done=new ManualResetEventSlim(false)){
            if(store.GetMeta("fixture")==""){
                string chrome=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"Google\Chrome\Application\chrome.exe");
                var dimensions=new[]{
                    new Dimension{App="chrome.exe",Path=chrome,Host="video.googlevideo.com",Node="🇯🇵 日本 · Tokyo 01"},
                    new Dimension{App="chrome.exe",Path=chrome,Host="www.youtube.com",Node="🇯🇵 日本 · Tokyo 01"},
                    new Dimension{App="Code.exe",Host="github.com",Node="🇸🇬 新加坡 · Singapore 02"},
                    new Dimension{App="msedge.exe",Host="learn.microsoft.com",Node="DIRECT",Route="DIRECT"},
                    new Dimension{App="Telegram.exe",Host="149.154.167.220",Node="Windows 网络事件",Source="windows",Route="WINDOWS"},
                    new Dimension{App="explorer.exe",Path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),Host="download.windowsupdate.com",Node="DIRECT",Route="DIRECT"},
                    new Dimension{App="chrome.exe",Path=chrome,Host="chatgpt.com",Node="🇯🇵 日本 · Tokyo 01"},
                    new Dimension{App="chrome.exe",Path=chrome,Host="www.bilibili.com",Node="DIRECT",Route="DIRECT"},
                    new Dimension{App="chrome.exe",Path=chrome,Host="www.youtube.com",Node="DIRECT",Route="DIRECT"},
                    new Dimension{App="chrome.exe",Path=chrome,Host="video.googlevideo.com",Node="🇯🇵 日本 · Tokyo 02"}};
                long day=Clock.DateBoundary(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd"),false);
                var facts=new List<Usage>();var totals=new List<TimedDelta>();
                for(int i=0;i<132;i++){if(i>=60&&i<72)continue;long bucket=day+7*3600+i*300;long coreUp=0,coreDown=0;
                    for(int j=0;j<dimensions.Length;j++){double wave=(Math.Sin(i*.19+j)+1.3)*(.4+Math.Pow(Math.Sin(i*.043+j*.7),2));long down=(long)((j==0?9500000:1100000)*(j==1?.15:1)*wave),up=(long)(down*(j==2?.65:.09));facts.Add(new Usage{Bucket=bucket,Dimension=dimensions[j],Upload=up,Download=down});if(j!=4){coreUp+=up;coreDown+=down;}}
                    totals.Add(new TimedDelta{Bucket=bucket,Upload=coreUp+12000,Download=coreDown+64000,AttributedUpload=coreUp,AttributedDownload=coreDown});
                }
                store.Write(facts,totals,"fixture");store.Event("connected","演示数据 · 采集已连接，开始记录本机应用流量。",day+7*3600);store.Event("gap","演示数据 · 12:00 至 13:00 未监控；不补记缺失时段。",day+13*3600);
            }
            collector.Connected=true;collector.Version="演示内核";collector.ConnectionCount=42;
            using(var server=new LocalServer(store,collector,config,root,()=>done.Set())){server.Start();Console.WriteLine("Fixture server ready (synthetic traffic only).");done.Wait();}
        }return 0;
    }
    private static void TestDeltas(){
        var tracker=new DeltaTracker();var initial=tracker.Observe(Sample(1000,2000,Conn("old",10,1000,2000)),100);
        Assert(initial.Baseline&&initial.Usage.Count==0,"Startup never charges old cumulative bytes to today");
        var next=tracker.Observe(Sample(1050,2300,Conn("old",10,1020,2100),Conn("new",101,30,200)),102);
        Assert(next.Upload==50&&next.Download==300&&next.AttributedUpload==50&&next.AttributedDownload==300,"Counter differences and first observed new connection are counted once");
        var shortOnly=tracker.Observe(Sample(1080,2800),103);
        Assert(shortOnly.Upload==30&&shortOnly.Download==500&&shortOnly.Usage.Count==0,"Closed short connections stay in core total and are not invented as node traffic");
        var reset=tracker.Observe(Sample(5,10,Conn("reset",104,5,10)),104);
        Assert(reset.Reset&&reset.Baseline&&reset.Usage.Count==0,"Core reset creates a new baseline without negative or duplicate bytes");
        tracker.Reset();Assert(tracker.Observe(Sample(500,600),120).Baseline,"Reconnection does not silently backfill unobserved periods");
        var parse=Connection.Parse(Json.Read("{\"id\":\"1\",\"start\":\"2026-10-15T00:00:00Z\",\"upload\":1,\"download\":2,\"metadata\":{\"process\":\"chrome.exe\",\"host\":\"x.googlevideo.com\"},\"chains\":[\"日本 A\",\"AI 分组\"]}"));
        Assert(parse.Dimension.Node=="日本 A"&&parse.Dimension.Host=="x.googlevideo.com","Actual outbound is preserved separately from policy group");
        var malformed=false;try{Snapshot.Parse(Json.Read("{}"));}catch(InvalidDataException){malformed=true;}Assert(malformed,"Non-Mihomo JSON cannot be mistaken for a zero-byte snapshot");
    }
    private static void TestHttp(){
        byte[] unicode=Encoding.UTF8.GetBytes("{\"网站\":\"日本\"}");byte[] prefix=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n"+unicode.Length.ToString("x")+"\r\n");byte[] suffix=Encoding.ASCII.GetBytes("\r\n0\r\n\r\n");
        Assert(MihomoClient.DecodeHttp(prefix.Concat(unicode).Concat(suffix).ToArray())==Encoding.UTF8.GetString(unicode),"HTTP chunk lengths are decoded as UTF-8 bytes, not characters");
        bool failed=false;try{MihomoClient.DecodeHttp(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nff\r\nx"));}catch(InvalidDataException){failed=true;}Assert(failed,"Truncated chunk responses fail instead of corrupting records");
        Assert(LocalServer.ParseQuery("?search=a%27%20OR%201%3D1&node=%E6%97%A5%E6%9C%AC")["node"]=="日本","Query strings preserve Unicode and literal SQL-like search values");
        Assert(Clock.DateBoundary("2026-10-15",false)==new DateTimeOffset(2026,10,15,0,0,0,TimeSpan.FromHours(8)).ToUnixTimeSeconds(),"Daily boundaries use Beijing time including midnight");
        bool range=false;try{LocalServer.BuildQuery(new Dictionary<string,string>{{"from","2026-10-16"},{"to","2026-10-15"}});}catch(ArgumentException){range=true;}Assert(range,"Invalid date range is rejected");
        Assert(Store.CsvCell("=HYPERLINK(\"x\")").StartsWith("\"'="),"CSV cells cannot become spreadsheet formulas");
        var minute=LocalServer.BuildQuery(new Dictionary<string,string>{{"from","2026-10-15"},{"to","2026-10-15"},{"fromTime","15:45"},{"toTime","15:45"}});
        Assert(minute.To-minute.From==60&&minute.TrendTo-minute.TrendFrom==86400,"Equal start/end minutes include one full minute while preserving the daily trend context");
        Assert(minute.From==Clock.DateBoundary("2026-10-15",false)+15*3600+45*60,"Custom clock times use Beijing time independently of system timezone");
        Assert(Clock.TimeBoundary("2026-10-15","23:59",true)==Clock.DateBoundary("2026-10-16",false),"The ending minute includes the last minute before midnight without including the next day");
        foreach(string bad in new[]{"24:00","12:60","12:30:45","abc"}){bool rejected=false;try{Clock.TimeBoundary("2026-10-15",bad,false);}catch(ArgumentException){rejected=true;}Assert(rejected,"Reject malformed minute filter "+bad);}
        bool backwards=false;try{LocalServer.BuildQuery(new Dictionary<string,string>{{"from","2026-10-15"},{"to","2026-10-15"},{"fromTime","16:01"},{"toTime","16:00"}});}catch(ArgumentException){backwards=true;}Assert(backwards,"An ending minute before the starting minute is rejected");
        bool outside=false;try{LocalServer.BuildQuery(new Dictionary<string,string>{{"from","2026-10-15"},{"to","2026-10-15"},{"chartFrom","2026-10-16"}});}catch(ArgumentException){outside=true;}Assert(outside,"A chart context cannot exclude the filtered range");
    }
    private static void TestNativeEvents(){
        IntPtr data=Marshal.AllocHGlobal(64);try{
            for(int i=0;i<64;i++)Marshal.WriteByte(data,i,0);Marshal.WriteInt32(data,0,1234);Marshal.WriteInt32(data,4,4096);
            byte[] remote=IPAddress.Parse("8.8.4.4").GetAddressBytes();Marshal.Copy(remote,0,IntPtr.Add(data,8),4);
            FlowKey key;long size;bool up;long filetime=DateTime.UtcNow.ToFileTimeUtc();
            Assert(EtwCapture.Decode(data,32,10,true,filetime,out key,out size,out up)&&key.Pid==1234&&size==4096&&up&&key.Ip()=="8.8.4.4","IPv4 send events use the event payload PID and remote destination");
            Marshal.Copy(IPAddress.Parse("1.2.3.4").GetAddressBytes(),0,IntPtr.Add(data,12),4);
            Assert(EtwCapture.Decode(data,32,11,true,filetime,out key,out size,out up)&&!up&&key.Ip()=="1.2.3.4","Receive events use the remote source without reversing attribution");
            Marshal.Copy(IPAddress.Loopback.GetAddressBytes(),0,IntPtr.Add(data,8),4);
            Assert(!EtwCapture.Decode(data,32,10,true,filetime,out key,out size,out up),"Loopback app-to-proxy legs are excluded to prevent double counting");
            byte[] ipv6=IPAddress.Parse("2606:4700:4700::1111").GetAddressBytes();Marshal.Copy(ipv6,0,IntPtr.Add(data,8),16);
            Assert(EtwCapture.Decode(data,48,26,false,filetime,out key,out size,out up)&&key.Family==6&&key.Protocol==17&&key.Ip()=="2606:4700:4700::1111","IPv6 UDP events preserve address and protocol");
            Assert(!EtwCapture.Decode(data,16,26,false,filetime,out key,out size,out up),"Undersized event payloads are rejected");
        }finally{Marshal.FreeHGlobal(data);}
    }
    private static void TestStorage(string root){
        using(var store=new Store(Path.Combine(root,"storage"))){long today=Clock.Minute(Clock.Now);var dim=new Dimension{App="chrome.exe",Host="x.googlevideo.com",Node="🇯🇵 日本 A",Path="C:\\测试\\chrome.exe"};
            var facts=new[]{new Usage{Bucket=today,Dimension=dim,Upload=30,Download=1000},new Usage{Bucket=today,Dimension=dim,Upload=20,Download=500}};
            store.Write(facts,new[]{new TimedDelta{Bucket=today,Upload=50,Download=1700,AttributedUpload=50,AttributedDownload=1500}},"test");
            var q=new Query{From=today-60,To=today+60};var result=Json.Read(Json.Write(store.Stats(q)));var sum=Json.Obj(result,"summary");
            Assert(Json.Num(sum,"upload")==50&&Json.Num(sum,"download")==1500,"SQLite upserts sum integer deltas without duplicate dimensions");
            Assert(Json.Num(Json.Obj(result,"core"),"download")==1700,"Unattributed core totals are retained independently from node facts");
            q.Japan=true;Assert(Json.Num(Json.Obj(Json.Read(Json.Write(store.Stats(q))),"summary"),"download")==1500,"Japan filter follows actual node identity");
            q.Search="' OR 1=1 --";Assert(Json.Num(Json.Obj(Json.Read(Json.Write(store.Stats(q))),"summary"),"download")==0,"SQL injection-like search is treated as a bound literal");q.Search="";
            Assert(store.Csv(q).Contains("x.googlevideo.com")&&store.Csv(q).Contains("日本 A"),"CSV preserves original domain, application and actual node");
            bool rolledBack=false;try{store.Write(new[]{new Usage{Bucket=today,Dimension=dim,Upload=100,Download=100},new Usage{Bucket=today,Dimension=dim,Upload=-1}},null,"test");}catch(InvalidDataException){rolledBack=true;}
            Assert(rolledBack&&Json.Num(Json.Obj(Json.Read(Json.Write(store.Stats(q))),"summary"),"upload")==50,"A failed batch rolls back atomically");
            store.Write(new[]{new Usage{Bucket=today-100*86400,Dimension=dim,Download=999}},null,"test");store.Prune(90);q.From=today-200*86400;q.Japan=false;
            Assert(Json.Num(Json.Obj(Json.Read(Json.Write(store.Stats(q))),"summary"),"download")==1500,"Retention removes expired facts without changing recent totals");
        }
        using (var reopened = new Store(Path.Combine(root, "storage"))) { Assert(reopened.GetMeta("test")!="","History and checkpoints survive a process restart"); }
    }
    private static string Request(int port,string target,string headers="",string method="GET"){
        using(var client=new TcpClient("127.0.0.1",port))using(var stream=client.GetStream()){client.ReceiveTimeout=5000;byte[] req=Encoding.ASCII.GetBytes(method+" "+target+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\n"+headers+"Connection: close\r\n\r\n");stream.Write(req,0,req.Length);using(var reader=new StreamReader(stream))return reader.ReadToEnd();}
    }
    private static void TestTimeFilters(string root){
        using(var store=new Store(root)){
            long day=Clock.DateBoundary("2026-10-15",false),start=day+15*3600+45*60;
            var proxy=new Dimension{App="chrome.exe",Path=@"C:\Apps\chrome.exe",Host="example.com",Node="日本 A"};var direct=new Dimension{App="chrome.exe",Path=@"C:\Apps\chrome.exe",Host="direct.example",Node="DIRECT",Route="DIRECT"};
            var facts=new[]{new Usage{Bucket=start-60,Dimension=proxy,Download=10},new Usage{Bucket=start,Dimension=proxy,Download=20},new Usage{Bucket=start+60,Dimension=direct,Download=30},new Usage{Bucket=start+120,Dimension=proxy,Download=40}};
            var totals=facts.Select(f=>new TimedDelta{Bucket=f.Bucket,Download=f.Download,AttributedDownload=f.Download});store.Write(facts,totals,"test");
            var q=LocalServer.BuildQuery(new Dictionary<string,string>{{"from","2026-10-15"},{"to","2026-10-15"},{"fromTime","15:45"},{"toTime","15:46"}});
            var data=Json.Read(Json.Write(store.Stats(q)));
            Assert(Json.Num(Json.Obj(data,"summary"),"download")==50,"Minute range excludes the preceding/following minutes and includes its final minute");
            Assert(Json.Num(Json.Obj(data,"core"),"download")==50,"Core totals use the same selected minute boundaries as application totals");
            Assert(Json.Array(data,"trend").OfType<Dictionary<string,object>>().Sum(r=>Json.Num(r,"download"))==100,"Trend retains unfiltered time context so a third click can choose a new start outside the old selection");
            Assert(Json.Array(data,"applicationRoutes").OfType<Dictionary<string,object>>().Select(r=>Json.Str(r,"route")).Distinct().Count()==2,"An application's route summary contains both direct and proxy routes in the selected time range");
            var routeFacts=Json.Array(data,"applicationRoutes").OfType<Dictionary<string,object>>().ToArray();
            Assert(routeFacts.Any(r=>Json.Str(r,"host")=="example.com"&&Json.Str(r,"node")=="日本 A")&&routeFacts.Any(r=>Json.Str(r,"host")=="direct.example"&&Json.Str(r,"node")=="DIRECT"),"Full route metadata retains original hosts and actual nodes for website and nested branch summaries");
            string csv=store.Csv(q);Assert(csv.Contains("15:45:00")&&csv.Contains("15:46:00")&&!csv.Contains("15:44:00")&&!csv.Contains("15:47:00"),"CSV export honors the exact same minute range");
            q.Scope="proxy";data=Json.Read(Json.Write(store.Stats(q)));Assert(Json.Array(data,"applicationRoutes").OfType<Dictionary<string,object>>().All(r=>Json.Str(r,"route")=="PROXY"),"Application badges follow active source filters instead of reporting historical routes outside the query");
        }
    }
    private static void TestConfig(string root){
        Directory.CreateDirectory(root);Assert(LedgerConfig.Load(root).Port==0,"New installations let Windows atomically allocate a free local port");
        foreach(int legacy in new[]{8793,18793}){File.WriteAllText(Path.Combine(root,"settings.json"),Json.Write(new{port=legacy,retentionDays=90}));Assert(LedgerConfig.Load(root).Port==0,"Legacy default "+legacy+" migrates to automatic allocation");}
        var config=new LedgerConfig{Port=18793,Theme="dark",AutoRefresh=true};config.Save(root);config=LedgerConfig.Load(root);Assert(config.Port==18793&&config.Theme=="dark"&&config.AutoRefresh,"Explicit fixed port and appearance preferences survive reload");
        config.Port=0;config.Save(root);Assert(LedgerConfig.Load(root).Port==0,"Saving automatic mode never turns zero into privileged port 1024");
    }
    private static void TestPortConflict(string root){
        Directory.CreateDirectory(root);
        var occupied=new TcpListener(IPAddress.Loopback,0);occupied.ExclusiveAddressUse=true;occupied.Start();try{int port=((IPEndPoint)occupied.LocalEndpoint).Port;var config=new LedgerConfig{Port=port};config.Save(root);
            using(var store=new Store(root))using(var collector=new Collector(store,config,root))using(var server=new LocalServer(store,collector,config,root,()=>{})){server.Start();Assert(server.Port!=port&&server.Port>=1024,"An occupied fixed port falls back to a Windows-allocated free port");Assert(Request(server.Port,"/api/status","Cookie: ledger_session="+server.Token+"\r\n").Contains("\"fallback\":true"),"Settings report the fixed-port fallback");}
        }finally{occupied.Stop();}
    }
    private static byte[] TestPng(){using(var bitmap=new Bitmap(16,16))using(var stream=new MemoryStream()){bitmap.SetPixel(8,8,Color.Teal);bitmap.Save(stream,ImageFormat.Png);return stream.ToArray();}}
    private static void TestIcons(string root){
        Directory.CreateDirectory(root);string exe=Path.Combine(root,"first.exe"),second=Path.Combine(root,"second.exe"),third=Path.Combine(root,"third.exe");
        File.Copy(Program.Executable,exe);File.Copy(Program.Executable,second);File.Copy(Program.Executable,third);
        long now=1000;int calls=0;byte[] png=TestPng();string disk=Path.Combine(root,"cache");
        var icons=new AppIcons(disk,p=>{calls++;return png;},2,2048,()=>now);
        Assert(!Directory.Exists(disk)&&icons.Stats().requests==0,"Icon cache is inactive until a page requests an icon");
        Assert(new[]{@"\\server\share\app.exe",@"\\?\C:\app.exe","https://example.com/app.exe","app.exe",@"C:\app.dll",@"C:\app.exe:stream"}.All(p=>AppIcons.LocalExecutable(p)==null),"Icons reject network, device, relative, non-executable and alternate-stream paths");
        Assert(AppIcons.ValidPng(icons.Get(exe))&&calls==1,"A local executable gets a validated PNG on first demand");
        Assert(AppIcons.ValidPng(icons.Get(exe))&&calls==1&&icons.Stats().memoryHits==1,"Repeated app rows reuse the extracted icon without calling Windows again");
        var persisted=new AppIcons(disk,p=>{throw new Exception("Disk cache should avoid extraction");});
        Assert(AppIcons.ValidPng(persisted.Get(exe))&&persisted.Stats().diskHits==1&&persisted.Stats().extractions==0,"PNG cache survives an application restart");
        File.WriteAllText(Directory.GetFiles(disk,"*.png")[0],"corrupt cache");
        var repaired=new AppIcons(disk,p=>png);Assert(AppIcons.ValidPng(repaired.Get(exe))&&repaired.Stats().extractions==1,"A damaged cached image is re-extracted instead of displayed");
        File.SetLastWriteTimeUtc(exe,File.GetLastWriteTimeUtc(exe).AddSeconds(2));now+=31;icons.Get(exe);
        Assert(calls==2,"Executable modification invalidates its cached icon");
        icons.Get(second);icons.Get(third);Assert(icons.Stats().entries==2&&icons.Stats().memoryBytes<=2048,"Icon memory cache evicts old entries at its configured bounds");
        var tiny=new AppIcons(Path.Combine(root,"tiny"),p=>png,2,1);tiny.Get(exe);Assert(tiny.Stats().memoryBytes==0&&tiny.Stats().entries==0,"Compressed image byte budget is enforced independently of entry count");
        string missing=Path.Combine(root,"missing.exe");int attempts=0;var failures=new AppIcons(Path.Combine(root,"negative"),p=>{attempts++;return null;},128,2048,()=>now);
        Assert(failures.Get(missing)==null&&attempts==0,"Missing historical applications fall back without invoking the shell");
        failures.Get(exe);failures.Get(exe);Assert(attempts==1,"Failed extraction is negatively cached to avoid repeated disk and shell work");now+=61;failures.Get(exe);Assert(attempts==2,"Failed extraction retries after the negative-cache interval");
        Assert(!AppIcons.ValidPng(new byte[32])&&!AppIcons.ValidPng(new byte[70000]),"Invalid and oversized PNG data is rejected");
        string command=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");var native=new AppIcons(Path.Combine(root,"native"));
        Assert(AppIcons.ValidPng(native.Get(command)),"Native Windows executable-icon extraction returns an actual PNG");
    }
    private static void TestServer(string root){
        var config=new LedgerConfig{Port=0};using(var store=new Store(root))using(var collector=new Collector(store,config,root))using(var server=new LocalServer(store,collector,config,root,()=>{})){
            server.Start();string cookie="Cookie: ledger_session="+server.Token+"\r\n";
            Assert(Request(server.Port,"/api/status").StartsWith("HTTP/1.1 401"),"Local traffic API requires a tray-issued session");
            Assert(Request(server.Port,"/open/"+server.Token).Contains("HttpOnly; SameSite=Strict"),"Tray entrance issues a strict HttpOnly session cookie");
            Assert(Request(server.Port,"/api/status",cookie).Contains("\"retentionDays\":90"),"Authenticated status reports capture health and actual settings");
            Assert(Request(server.Port,"/api/quit",cookie,"POST").StartsWith("HTTP/1.1 403"),"State-changing calls reject missing anti-forgery tokens");
            string write=cookie+"X-Ledger-Token: "+server.Token+"\r\nContent-Length: 0\r\n";
            Assert(Request(server.Port,"/api/port?port=65536",write,"POST").StartsWith("HTTP/1.1 400"),"Out-of-range fixed ports cannot be persisted");
            Assert(Request(server.Port,"/api/port?port=18794",write,"POST").Contains("\"restartRequired\":true")&&LedgerConfig.Load(root).Port==18794,"Port edits are persisted without interrupting the active session");
            Assert(Request(server.Port,"/api/port?port=0",write,"POST").Contains("\"restartRequired\":false")&&LedgerConfig.Load(root).Port==0,"Automatic port selection is available through the authenticated settings API");
            Assert(Request(server.Port,"/api/preferences?theme=dark&autoRefresh=true",write,"POST").Contains("\"theme\":\"dark\"")&&LedgerConfig.Load(root).AutoRefresh,"Theme and refresh preferences are stored independently of the browser origin");
            Assert(Request(server.Port,"/api/preferences?theme=unknown",write,"POST").StartsWith("HTTP/1.1 400"),"Unknown themes are rejected");
            Assert(Request(server.Port,"/api/port?port=0",cookie,"POST").StartsWith("HTTP/1.1 403"),"Port settings require anti-forgery authentication");
            Assert(Request(server.Port,"/open/"+server.Token+"?settings=1").Contains("Location: /#settings"),"The tray settings entry opens the visible settings panel");
            Assert(Request(server.Port,"/api/status",cookie).Contains("\"initialized\":false"),"Background status checks never initialize or extract application icons");
            Assert(Request(server.Port,"/api/icon?id="+Uri.EscapeDataString(new Dimension().Id)).StartsWith("HTTP/1.1 401"),"Icon endpoint requires the same local session as traffic records");
            Assert(Request(server.Port,"/api/icon?path=C%3A%5CWindows%5CSystem32%5Ccmd.exe",cookie).StartsWith("HTTP/1.1 400"),"Icon endpoint cannot be used to request arbitrary filesystem paths");
            var dim=new Dimension{App="cmd.exe",Path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe")};store.Write(new[]{new Usage{Bucket=Clock.Minute(Clock.Now),Dimension=dim,Download=1}},null,"test");
            string image=Request(server.Port,"/api/icon?id="+Uri.EscapeDataString(dim.Id),cookie);
            Assert(image.StartsWith("HTTP/1.1 200")&&image.Contains("Content-Type: image/png")&&image.Contains("private, max-age=300"),"Recorded application icons are served as privately cacheable PNG images");
            Assert(Request(server.Port,"/api/icon?id="+Uri.EscapeDataString(Convert.ToBase64String(new byte[32])),cookie).StartsWith("HTTP/1.1 204"),"Unknown application icons return an empty response for the letter fallback");
            Assert(Request(server.Port,"/",cookie).Contains("流量账本"),"HTML and assets are embedded in the native executable");
            using(var client=new TcpClient("127.0.0.1",server.Port))using(var s=client.GetStream()){byte[] request=Encoding.ASCII.GetBytes("GET /api/status HTTP/1.1\r\nHost: attacker.example:"+server.Port+"\r\n"+cookie+"\r\n");s.Write(request,0,request.Length);using(var r=new StreamReader(s))Assert(r.ReadToEnd().StartsWith("HTTP/1.1 403"),"Unexpected Host headers are rejected against DNS rebinding");}
            server.Dispose();server.Dispose();collector.Dispose();collector.Dispose();Assert(true,"Repeated Windows application-context disposal is safe");
        }
    }
}
}
