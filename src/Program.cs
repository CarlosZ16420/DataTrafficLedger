using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LocalTrafficLedger {
internal static class Program {
    public static readonly string UserSid,UserKey;
    static Program(){using(var identity=WindowsIdentity.GetCurrent())UserSid=identity.User.Value;using(var hash=SHA256.Create())UserKey=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(UserSid))).Replace("-","").Substring(0,12);}
    public static string MutexName(string data){using(var hash=SHA256.Create())return @"Local\LocalTrafficLedger."+UserKey+"."+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(data).ToUpperInvariant()))).Replace("-","").Substring(0,16);}
    public static string Executable {get{return Assembly.GetExecutingAssembly().Location;}}
    public static bool IsAdmin {get{using(var identity=WindowsIdentity.GetCurrent())return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}}
    [STAThread]public static int Main(string[] args){
        string data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalTrafficLedger");int port=-1;
        // An explicit portable location survives packaged launchers' AppData redirection.
        string portable=Path.Combine(Path.GetDirectoryName(Executable),"portable.json");if(File.Exists(portable)){var location=Json.Str(Json.Read(File.ReadAllText(portable)),"data");if(location!="")data=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Executable),location));}
        for(int i=0;i<args.Length;i++){if(args[i]=="--data"&&i+1<args.Length)data=Path.GetFullPath(args[++i]);else if(args[i]=="--port"&&i+1<args.Length)port=int.Parse(args[++i]);}
        Directory.CreateDirectory(data);
        try{
            if(args.Contains("--uninstall")){RequestUninstall();return 0;}
            if(args.Contains("--etw-helper"))return EtwHelper.Run(data);
            if(args.Contains("--setup-etw")){Startup.RegisterEtw(data);Startup.RunEtw();return 0;}
            if(args.Contains("--disable-etw")){Startup.RemoveEtw();EtwHelper.Stop(data);var saved=LedgerConfig.Load(data);saved.EtwRequested=false;saved.Save(data);return 0;}
            if(args.Contains("--install-startup")){Startup.Set(true,data);return 0;}
            if(args.Contains("--remove-startup")){Startup.Set(false,data);return 0;}
            if(args.Contains("--stop")){LocalServer.SendQuit(data);EtwHelper.Stop(data);return 0;}
            bool created;using(var singleton=new Mutex(true,MutexName(data),out created)){
                if(!created){if(!args.Contains("--background")&&!args.Contains("--headless"))LocalServer.OpenExisting(data);return 0;}
                try{
                    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                    var config=LedgerConfig.Load(data);if(port>=0)config.Port=port;
                    using(var store=new Store(data))using(var collector=new Collector(store,config,data))using(var context=new TrayContext(store,collector,config,data,args.Contains("--headless"))){
                        collector.Start();if(config.EtwRequested)try{Startup.RunEtw();}catch{}
                        if(args.Contains("--open")||(!args.Contains("--background")&&!args.Contains("--headless")))context.Open();Application.Run(context);
                    }
                }finally{singleton.ReleaseMutex();}
            }return 0;
        }catch(Exception ex){try{File.AppendAllText(Path.Combine(data,"error.log"),DateTime.Now.ToString("s")+" "+ex+Environment.NewLine);}catch{}
            if(!args.Contains("--headless")&&!args.Contains("--etw-helper"))MessageBox.Show("流量账本无法启动：\n"+ex.Message,"流量账本",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
    }
    public static void RequestUninstall(){
        string root=Path.GetDirectoryName(Executable),script=Path.Combine(root,"Uninstall.ps1");
        if(!File.Exists(script)&&string.Equals(Path.GetFileName(root),"app",StringComparison.OrdinalIgnoreCase))script=Path.Combine(Path.GetDirectoryName(root),"Uninstall.ps1");
        if(!File.Exists(script))throw new FileNotFoundException("找不到卸载程序，请完整解压新版便携包后运行 Uninstall.cmd。",script);
        string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
        Process.Start(new ProcessStartInfo(shell,"-NoProfile -ExecutionPolicy Bypass -File \""+script+"\""){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden});
    }
}
internal static class Startup {
    private const string RegistryPath=@"Software\Microsoft\Windows\CurrentVersion\Run",ValueName="LocalTrafficLedger";
    private static bool cachedEnabled;private static long cacheUntil;
    private static string TrayTaskName {get{return "LocalTrafficLedger-Tray-"+Program.UserKey;}}
    public static bool Enabled {get{if(Clock.Now<cacheUntil)return cachedEnabled;object service=null,folder=null,task=null;try{service=Scheduler();folder=Com(service,"GetFolder",BindingFlags.InvokeMethod,"\\");task=Com(folder,"GetTask",BindingFlags.InvokeMethod,TrayTaskName);cachedEnabled=Convert.ToBoolean(Com(task,"Enabled",BindingFlags.GetProperty));}catch{cachedEnabled=false;}finally{Release(task);Release(folder);Release(service);}cacheUntil=Clock.Now+5;return cachedEnabled;}}
    public static bool NeedsRepair(string data){
        if(!Enabled)return false;
        object service=null,folder=null,task=null,definition=null,actions=null,action=null;
        try{service=Scheduler();folder=Com(service,"GetFolder",BindingFlags.InvokeMethod,"\\");task=Com(folder,"GetTask",BindingFlags.InvokeMethod,TrayTaskName);definition=Com(task,"Definition",BindingFlags.GetProperty);actions=Com(definition,"Actions",BindingFlags.GetProperty);action=Com(actions,"Item",BindingFlags.GetProperty,1);
            return !string.Equals(Convert.ToString(Com(action,"Path",BindingFlags.GetProperty)),Program.Executable,StringComparison.OrdinalIgnoreCase)||!string.Equals(Convert.ToString(Com(action,"Arguments",BindingFlags.GetProperty)),"--background --data \""+data+"\"",StringComparison.OrdinalIgnoreCase);
        }catch{return true;}finally{Release(action);Release(actions);Release(definition);Release(task);Release(folder);Release(service);}
    }
    public static void Set(bool enable,string data){
        dynamic service=Scheduler();dynamic folder=service.GetFolder("\\");
        if(enable){
            string sid=Program.UserSid;dynamic task=service.NewTask(0);task.RegistrationInfo.Description="流量账本：登录后在托盘只读记录本机流量。";
            task.Principal.UserId=sid;task.Principal.LogonType=3;task.Principal.RunLevel=0;
            task.Settings.Enabled=true;task.Settings.DisallowStartIfOnBatteries=false;task.Settings.StopIfGoingOnBatteries=false;task.Settings.ExecutionTimeLimit="PT0S";task.Settings.MultipleInstances=2;
            dynamic trigger=task.Triggers.Create(9);trigger.UserId=sid;trigger.Delay="PT5S";
            dynamic action=task.Actions.Create(0);action.Path=Program.Executable;action.Arguments="--background --data \""+data+"\"";action.WorkingDirectory=Path.GetDirectoryName(Program.Executable);
            folder.RegisterTaskDefinition(TrayTaskName,task,6,sid,null,3,null);
        }else{try{folder.DeleteTask(TrayTaskName,0);}catch(System.Runtime.InteropServices.COMException ex){if((uint)ex.HResult!=0x80070002)throw;}}
        // Remove only this application's obsolete registry entry, including virtualized copies.
        using(var key=Registry.CurrentUser.OpenSubKey(RegistryPath,true)){if(key!=null)key.DeleteValue(ValueName,false);}
        cachedEnabled=enable;cacheUntil=Clock.Now+5;
    }
    private static string TaskName {get{return "LocalTrafficLedger-ETW-"+Program.UserKey;}}
    private static object Com(object target,string name,BindingFlags flags,params object[] args){try{return target.GetType().InvokeMember(name,flags,null,target,args);}catch(TargetInvocationException ex){throw ex.InnerException??ex;}}
    private static void Release(object target){if(target!=null&&System.Runtime.InteropServices.Marshal.IsComObject(target))System.Runtime.InteropServices.Marshal.FinalReleaseComObject(target);}
    private static object Scheduler(){object service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));Com(service,"Connect",BindingFlags.InvokeMethod);return service;}
    public static void RegisterEtw(string data){
        if(!Program.IsAdmin)throw new UnauthorizedAccessException("启用 Windows 网络事件采集需要管理员权限。");
        dynamic service=Scheduler();dynamic task=service.NewTask(0);string sid=Program.UserSid;
        task.RegistrationInfo.Description="流量账本：只读记录 Windows 网络事件，不修改路由、防火墙或 Clash 配置。";
        task.Principal.UserId=sid;task.Principal.LogonType=3;task.Principal.RunLevel=1;
        task.Settings.Enabled=true;task.Settings.Hidden=true;task.Settings.DisallowStartIfOnBatteries=false;task.Settings.StopIfGoingOnBatteries=false;task.Settings.ExecutionTimeLimit="PT0S";task.Settings.MultipleInstances=2;
        // Demand-start only: the unelevated tray application starts this approved helper.
        // Disabling the tray's startup entry also disables all automatic collection.
        task.Settings.AllowDemandStart=true;
        dynamic action=task.Actions.Create(0);action.Path=Program.Executable;action.Arguments="--etw-helper --data \""+data+"\"";action.WorkingDirectory=Path.GetDirectoryName(Program.Executable);
        service.GetFolder("\\").RegisterTaskDefinition(TaskName,task,6,sid,null,3,null);
        var config=LedgerConfig.Load(data);config.EtwRequested=true;config.Save(data);
    }
    public static void RunEtw(){object service=null,folder=null,task=null;try{service=Scheduler();folder=Com(service,"GetFolder",BindingFlags.InvokeMethod,"\\");task=Com(folder,"GetTask",BindingFlags.InvokeMethod,TaskName);Com(task,"Run",BindingFlags.InvokeMethod,new object[]{null});}finally{Release(task);Release(folder);Release(service);}}
    public static void RemoveEtw(){dynamic service=Scheduler();try{service.GetFolder("\\").DeleteTask(TaskName,0);}catch(System.Runtime.InteropServices.COMException ex){if((uint)ex.HResult!=0x80070002)throw;}}
    public static bool RequestFull(string data){
        if(Program.IsAdmin){RegisterEtw(data);RunEtw();return true;}
        var start=new ProcessStartInfo(Program.Executable,"--setup-etw --data \""+data+"\""){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        using(var process=Process.Start(start)){}return true;
    }
}
internal sealed class TrayContext : ApplicationContext {
    private readonly Store store;private readonly Collector collector;private readonly LedgerConfig config;private readonly string data;private readonly LocalServer server;
    private NotifyIcon tray;private Icon icon;private System.Windows.Forms.Timer timer;private ToolStripMenuItem autoItem,statusItem;private Control dispatcher;private bool disposed;
    public TrayContext(Store store,Collector collector,LedgerConfig config,string data,bool headless){
        this.store=store;this.collector=collector;this.config=config;this.data=data;
        dispatcher=new Control();var handle=dispatcher.Handle;
        server=new LocalServer(store,collector,config,data,()=>dispatcher.BeginInvoke((Action)ExitThread));server.Start();
        if(!headless){
            icon=MakeIcon();var menu=new ContextMenuStrip();menu.Items.Add("打开流量账本",null,(s,e)=>Open());statusItem=new ToolStripMenuItem("正在连接 Clash Verge…"){Enabled=false};menu.Items.Add(statusItem);menu.Items.Add(new ToolStripSeparator());
            menu.Opening+=(s,e)=>{autoItem.Checked=Startup.Enabled;};
            var settingsMenu=new ToolStripMenuItem("设置");settingsMenu.DropDownItems.Add("打开设置面板…",null,(s,e)=>server.Open(true));
            autoItem=new ToolStripMenuItem("开机自启动（登录 Windows 后）"){Checked=Startup.Enabled};autoItem.Click+=(s,e)=>{try{Startup.Set(!Startup.Enabled,data);autoItem.Checked=Startup.Enabled;}catch(Exception ex){MessageBox.Show(ex.Message,"无法设置自启动");}};settingsMenu.DropDownItems.Add(autoItem);menu.Items.Add(settingsMenu);
            menu.Items.Add("启用 Windows 补充采集…",null,(s,e)=>{try{Startup.RequestFull(data);}catch(System.ComponentModel.Win32Exception ex){if(ex.NativeErrorCode!=1223)MessageBox.Show(ex.Message);}catch(Exception ex){MessageBox.Show(ex.Message);}});
            settingsMenu.DropDownItems.Add("卸载与清理…",null,(s,e)=>{try{Program.RequestUninstall();}catch(Exception ex){MessageBox.Show(ex.Message,"无法启动卸载程序");}});
            menu.Items.Add("打开数据文件夹",null,(s,e)=>Process.Start(new ProcessStartInfo(data){UseShellExecute=true}));menu.Items.Add(new ToolStripSeparator());menu.Items.Add("退出",null,(s,e)=>ExitThread());
            tray=new NotifyIcon{Icon=icon,Text="流量账本",Visible=true,ContextMenuStrip=menu};tray.MouseClick+=(s,e)=>{if(e.Button==MouseButtons.Left)Open();};
            timer=new System.Windows.Forms.Timer{Interval=5000};timer.Tick+=(s,e)=>{statusItem.Text=collector.Connected?"正在记录 · "+collector.ConnectionCount+" 个活动连接":"Clash 暂未连接 · 历史可查看";autoItem.Checked=Startup.Enabled;};timer.Start();
        }
        SystemEvents.SessionEnding+=SessionEnding;
    }
    private void SessionEnding(object sender,SessionEndingEventArgs e){try{dispatcher.BeginInvoke((Action)ExitThread);}catch{}}
    public void Open(){server.Open();}
    protected override void ExitThreadCore(){EtwHelper.Stop(data);base.ExitThreadCore();}
    protected override void Dispose(bool disposing){if(disposing&&!disposed){disposed=true;SystemEvents.SessionEnding-=SessionEnding;if(timer!=null)timer.Dispose();if(tray!=null){tray.Visible=false;tray.Dispose();}if(icon!=null)icon.Dispose();server.Dispose();dispatcher.Dispose();}base.Dispose(disposing);}
    private static Icon MakeIcon(){using(var bitmap=new Bitmap(32,32))using(var g=Graphics.FromImage(bitmap)){g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;g.Clear(Color.FromArgb(19,31,49));using(var pen=new Pen(Color.FromArgb(62,207,174),3)){g.DrawLines(pen,new[]{new Point(5,22),new Point(11,15),new Point(17,19),new Point(26,8)});}IntPtr handle=bitmap.GetHicon();try{return (Icon)Icon.FromHandle(handle).Clone();}finally{DestroyIcon(handle);}}}
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr handle);
}
}
