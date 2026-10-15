using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalTrafficLedger {
internal sealed class LocalServer : IDisposable {
    private readonly Store store;private readonly Collector collector;private readonly LedgerConfig config;private readonly string data;private readonly Action quit;
    private TcpListener listener;private readonly CancellationTokenSource cancel=new CancellationTokenSource();private readonly SemaphoreSlim slots=new SemaphoreSlim(8);private Task acceptTask;
    private readonly Lazy<AppIcons> icons;private readonly object settingsGate=new object();private int startingPort;
    public int Port;public string Token;private const string CookieName="ledger_session";private bool disposed;
    public LocalServer(Store store,Collector collector,LedgerConfig config,string data,Action quit){this.store=store;this.collector=collector;this.config=config;this.data=data;this.quit=quit;icons=new Lazy<AppIcons>(()=>new AppIcons(Path.Combine(data,"icons")));using(var random=RandomNumberGenerator.Create()){byte[] bytes=new byte[32];random.GetBytes(bytes);Token=BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}}
    public void Start(){
        if(config.Port!=0&&(config.Port<1024||config.Port>65535))throw new ArgumentException("端口应为 0（自动），或 1024–65535。");
        startingPort=config.Port;
        try{Bind(startingPort);}catch(SocketException){if(startingPort==0)throw;Bind(0);}
        AtomicFile.Write(Path.Combine(data,"runtime.json"),Json.Write(new{port=Port,token=Token,pid=Process.GetCurrentProcess().Id}));
        acceptTask=Task.Run(async()=>{while(!cancel.IsCancellationRequested){try{var client=await listener.AcceptTcpClientAsync().ConfigureAwait(false);if(!slots.Wait(0)){client.Dispose();continue;}_ = Task.Run(()=>{try{Handle(client);}finally{client.Dispose();slots.Release();}});}catch(ObjectDisposedException){break;}catch(SocketException){if(cancel.IsCancellationRequested)break;}}});
    }
    private void Bind(int port){var candidate=new TcpListener(IPAddress.Loopback,port);try{candidate.ExclusiveAddressUse=true;candidate.Start(16);listener=candidate;Port=((IPEndPoint)candidate.LocalEndpoint).Port;}catch{candidate.Stop();throw;}}
    public void Open(bool settings=false){Process.Start(new ProcessStartInfo("http://127.0.0.1:"+Port+"/open/"+Token+(settings?"?settings=1":"")){UseShellExecute=true});}
    public static void OpenExisting(string data){try{var state=Json.Read(File.ReadAllText(Path.Combine(data,"runtime.json")));Process.Start(new ProcessStartInfo("http://127.0.0.1:"+Json.Num(state,"port")+"/open/"+Json.Str(state,"token")){UseShellExecute=true});}catch{}}
    public static void SendQuit(string data){try{var state=Json.Read(File.ReadAllText(Path.Combine(data,"runtime.json")));var request=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+Json.Num(state,"port")+"/api/quit");request.Proxy=null;request.Method="POST";request.Headers["Cookie"]=CookieName+"="+Json.Str(state,"token");request.Headers["X-Ledger-Token"]=Json.Str(state,"token");request.ContentLength=0;request.Timeout=3000;using(var response=request.GetResponse()){} }catch{} }
    private void Handle(TcpClient client){
        client.ReceiveTimeout=4000;client.SendTimeout=10000;using(var stream=client.GetStream()){
            try{
                var header=new MemoryStream();int a=0,b=0,c=0;while(header.Length<16384){int v=stream.ReadByte();if(v<0)return;header.WriteByte((byte)v);if(a==13&&b==10&&c==13&&v==10)break;a=b;b=c;c=v;}
                if(header.Length>=16384){Respond(stream,431,"请求过大");return;}
                string[] lines=Encoding.ASCII.GetString(header.ToArray()).Split(new[]{"\r\n"},StringSplitOptions.None);string[] first=lines[0].Split(' ');if(first.Length!=3){Respond(stream,400,"无效请求");return;}
                var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(string line in lines.Skip(1)){int colon=line.IndexOf(':');if(colon>0)headers[line.Substring(0,colon)]=line.Substring(colon+1).Trim();}
                string host;headers.TryGetValue("Host",out host);if(host!="127.0.0.1:"+Port){Respond(stream,403,"仅允许本机访问");return;}
                Uri uri;if(!Uri.TryCreate("http://127.0.0.1:"+Port+first[1],UriKind.Absolute,out uri)||!first[1].StartsWith("/",StringComparison.Ordinal)||first[1].StartsWith("//",StringComparison.Ordinal)){Respond(stream,400,"无效地址");return;}
                string method=first[0],path=uri.AbsolutePath;
                if(method!="GET"&&method!="POST"){Respond(stream,405,"不支持的请求");return;}
                if(path=="/open/"+Token&&method=="GET"){Respond(stream,302,"","text/plain; charset=utf-8","Set-Cookie: "+CookieName+"="+Token+"; Path=/; HttpOnly; SameSite=Strict\r\nLocation: /"+(uri.Query=="?settings=1"?"#settings":"")+"\r\n");return;}
                string cookie;headers.TryGetValue("Cookie",out cookie);bool authenticated=(cookie??"").Split(';').Any(x=>x.Trim()==CookieName+"="+Token);
                if(!authenticated){Respond(stream,401,"请点击系统托盘中的“本机流量账本”图标打开页面。");return;}
                if(method=="POST"){string csrf,origin;headers.TryGetValue("X-Ledger-Token",out csrf);headers.TryGetValue("Origin",out origin);if(csrf!=Token||(origin!=null&&origin!="http://127.0.0.1:"+Port)){Respond(stream,403,"请求校验失败，请从托盘重新打开。");return;}string contentLength;if(headers.ContainsKey("Transfer-Encoding")||(headers.TryGetValue("Content-Length",out contentLength)&&contentLength!="0")){Respond(stream,400,"不接受请求正文");return;}}
                var query=ParseQuery(uri.Query);
                if(method=="GET"&&(path=="/"||path=="/app.js"||path=="/app.css")){string name=path=="/"?"index.html":path.Substring(1);using(var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("web."+name)){if(resource==null)throw new FileNotFoundException(name);using(var reader=new StreamReader(resource,Encoding.UTF8))Respond(stream,200,reader.ReadToEnd(),name.EndsWith(".js")?"application/javascript; charset=utf-8":name.EndsWith(".css")?"text/css; charset=utf-8":"text/html; charset=utf-8");}return;}
                if(path=="/api/status"&&method=="GET"){
                    Dictionary<string,object> etw;try{etw=Json.Read(store.GetMeta("etw_health"))??new Dictionary<string,object>();}catch{etw=new Dictionary<string,object>();}if(Clock.Now-Json.Num(etw,"at")>20)etw["running"]=false;
                    long bytes=0;try{bytes=new FileInfo(store.FilePath).Length;var wal=store.FilePath+"-wal";if(File.Exists(wal))bytes+=new FileInfo(wal).Length;}catch{}
                    var saved=LedgerConfig.Load(data);
                    using(var process=Process.GetCurrentProcess())RespondJson(stream,new{collector=collector.Status(),etw=etw,startup=Startup.Enabled,startupNeedsRepair=Startup.NeedsRepair(data),fullRequested=saved.EtwRequested,retentionDays=config.RetentionDays,dataDirectory=data,databaseBytes=bytes,memoryBytes=process.WorkingSet64,icons=icons.IsValueCreated?icons.Value.Stats():new IconCacheStats(),preferences=new{theme=saved.Theme,autoRefresh=saved.AutoRefresh},endpoint=new{port=Port,configuredPort=saved.Port,automatic=saved.Port==0,restartRequired=saved.Port!=startingPort,fallback=startingPort!=0&&startingPort!=Port},portable=File.Exists(Path.Combine(Path.GetDirectoryName(Program.Executable),"portable.json")),csrf=Token,version="1.2.0",now=Clock.Now});return;
                }
                if(path=="/api/icon"&&method=="GET"){
                    string id=Get(query,"id");byte[] identifier;try{identifier=Convert.FromBase64String(id);}catch(FormatException){Respond(stream,400,"无效的应用标识");return;}
                    if(id.Length!=44||identifier.Length!=32){Respond(stream,400,"无效的应用标识");return;}
                    string application=store.ApplicationPath(id);byte[] png=string.IsNullOrEmpty(application)?null:icons.Value.Get(application);
                    if(png==null)RespondBytes(stream,204,new byte[0],"image/png", "", "private, max-age=60");
                    else RespondBytes(stream,200,png,"image/png", "", "private, max-age=300");return;
                }
                if((path=="/api/stats"||path=="/api/export")&&method=="GET"){var q=BuildQuery(query);if(path=="/api/export")Respond(stream,200,store.Csv(q),"text/csv; charset=utf-8","Content-Disposition: attachment; filename=traffic-"+Get(query,"from")+".csv\r\n");else RespondJson(stream,store.Stats(q));return;}
                if(path=="/api/startup"&&method=="POST"){Startup.Set(Get(query,"enabled")=="true",data);RespondJson(stream,new{enabled=Startup.Enabled});return;}
                if(path=="/api/preferences"&&method=="POST"){
                    string theme=Get(query,"theme");if(!new[]{"system","light","dark"}.Contains(theme))throw new ArgumentException("无效的主题。");
                    lock(settingsGate){var saved=LedgerConfig.Load(data);saved.Theme=theme;saved.AutoRefresh=Get(query,"autoRefresh")=="true";saved.Save(data);RespondJson(stream,new{theme=saved.Theme,autoRefresh=saved.AutoRefresh});}return;
                }
                if(path=="/api/port"&&method=="POST"){
                    int port;if(!int.TryParse(Get(query,"port"),out port)||(port!=0&&(port<1024||port>65535)))throw new ArgumentException("端口应为 0（自动），或 1024–65535。");
                    lock(settingsGate){var saved=LedgerConfig.Load(data);saved.Port=port;saved.Save(data);RespondJson(stream,new{port=port,restartRequired=port!=startingPort});}return;
                }
                if(path=="/api/enable-windows"&&method=="POST"){try{Startup.RequestFull(data);RespondJson(stream,new{accepted=true});}catch(System.ComponentModel.Win32Exception ex){RespondJson(stream,new{accepted=false,message=ex.NativeErrorCode==1223?"已取消管理员授权，Clash 流量记录仍在继续。":ex.Message});}return;}
                if(path=="/api/quit"&&method=="POST"){RespondJson(stream,new{ok=true});quit();return;}
                Respond(stream,404,"页面不存在");
            }catch(ArgumentException ex){Respond(stream,400,ex.Message);}catch(Exception){try{Respond(stream,500,"暂时无法读取记录，请稍后刷新。详情可查看数据文件夹中的状态记录。");}catch{}}
        }
    }
    internal static Query BuildQuery(Dictionary<string,string> d){string from=Get(d,"from"),to=Get(d,"to");var q=new Query{From=Clock.DateBoundary(from,false),To=Clock.DateBoundary(to,true),Scope=Get(d,"scope","all"),Search=Get(d,"search"),Node=Get(d,"node"),App=Get(d,"app"),Japan=Get(d,"japan")=="true"};if(q.To<=q.From||q.To-q.From>3660L*86400)throw new ArgumentException("日期范围无效");if(q.Search.Length>200||q.Node.Length>300||q.App.Length>300)throw new ArgumentException("筛选条件过长");return q;}
    internal static Dictionary<string,string> ParseQuery(string query){var d=new Dictionary<string,string>();foreach(var pair in query.TrimStart('?').Split('&')){if(pair=="")continue;var split=pair.Split(new[]{'='},2);d[Uri.UnescapeDataString(split[0])]=split.Length>1?Uri.UnescapeDataString(split[1].Replace("+"," ")):"";}return d;}
    private static string Get(Dictionary<string,string> d,string key,string fallback=""){string v;return d.TryGetValue(key,out v)?v:fallback;}
    private static void RespondJson(Stream stream,object value){Respond(stream,200,Json.Write(value),"application/json; charset=utf-8");}
    private static void Respond(Stream stream,int code,string text,string type="text/plain; charset=utf-8",string extra=""){
        RespondBytes(stream,code,Encoding.UTF8.GetBytes(text),type,extra,"no-store");
    }
    private static void RespondBytes(Stream stream,int code,byte[] body,string type,string extra,string cache){
        string reason=code==200?"OK":code==204?"No Content":code==302?"Found":code==400?"Bad Request":code==401?"Unauthorized":code==403?"Forbidden":code==404?"Not Found":code==405?"Method Not Allowed":"Error";
        string response="HTTP/1.1 "+code+" "+reason+"\r\nContent-Type: "+type+"\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\nCache-Control: "+cache+"\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'\r\n"+extra+"\r\n";
        byte[] head=Encoding.ASCII.GetBytes(response);stream.Write(head,0,head.Length);stream.Write(body,0,body.Length);
    }
    public void Dispose(){if(disposed)return;disposed=true;cancel.Cancel();if(listener!=null)listener.Stop();if(acceptTask!=null)acceptTask.Wait(5000);try{string path=Path.Combine(data,"runtime.json");if(File.Exists(path)&&Json.Str(Json.Read(File.ReadAllText(path)),"token")==Token)File.Delete(path);}catch{}cancel.Dispose();}
}
}
