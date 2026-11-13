using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataTrafficLedger {
internal static partial class Tests {
    private static void TestCaptureRecovery(){
        var tracker=new DeltaTracker();
        tracker.Observe(Sample(1000,2000,Conn("long",10,1000,2000)),100,"pipe|123|created-1");
        var recovered=tracker.Observe(Sample(1500,3000,Conn("long",10,1400,2800),Conn("new",102,100,200)),110,"pipe|123|created-1",true);
        Assert(recovered.Recovered&&!recovered.Baseline&&recovered.Upload==500&&recovered.Download==1000&&recovered.AttributedDownload==1000,"A failed-read interval keeps exact cumulative deltas when the same core instance is verified");
        var closed=tracker.Observe(Sample(1600,3500),120,"pipe|123|created-1",true);
        Assert(closed.Download==500&&closed.AttributedDownload==0,"Connections that close during an interruption remain in recovered core totals without invented attribution");
        Assert(tracker.Observe(Sample(1600,3500),121,"pipe|123|created-1").Download==0,"A recovered interval is never charged twice");
        var replacement=tracker.Observe(Sample(9000,9000),130,"pipe|123|created-2",true);
        Assert(replacement.Baseline&&replacement.Reset&&!replacement.Recovered,"PID reuse with a different process creation time creates a new baseline even when counters increase");
        Assert(tracker.Observe(Sample(10000,10000),140,"other-pipe|123|created-2").Baseline,"Changing pipes cannot merge separate cores' cumulative counters");
        Assert(tracker.Observe(Sample(10,10),150,"other-pipe|123|created-2",true).Baseline,"Counter rollback after an interruption cannot produce negative or fictitious traffic");
        tracker.Reset();tracker.Observe(Sample(100,100),160,"");
        Assert(tracker.Observe(Sample(200,200),170,"",true).Baseline,"Recovery without a verifiable core identity stays conservative");
        tracker.Observe(Sample(300,300),180,"pipe|456",false,false);
        Assert(tracker.Observe(Sample(400,400),190,"pipe|456",true,false).Baseline,"A matching PID without process creation time is insufficient to recover a failed-read interval");
        Assert(tracker.Observe(Sample(500,500),200,"pipe|789",false,false).Baseline,"Even without process-query permission, a changed PID cannot merge separate core totals");
        foreach(string counter in new[]{"-1","null","\"invalid\"","1.5"}){
            bool rejected=false;try{Snapshot.Parse(Json.Read("{\"uploadTotal\":"+counter+",\"downloadTotal\":1,\"connections\":null}"));}catch(InvalidDataException){rejected=true;}
            Assert(rejected,"Malformed global counters cannot silently become a zero-byte baseline: "+counter);
        }
        tracker.Reset();tracker.Observe(Sample(1000,2000),105,"same-core",false,true,100);
        var duringRead=tracker.Observe(Sample(1040,2060,Conn("during-response",103,40,60)),110,"same-core",false,true,108);
        Assert(duringRead.AttributedUpload==40&&duringRead.AttributedDownload==60,"A connection created during a slow previous response is not mistaken for pre-monitoring traffic");
    }
    private static void TestCaptureBuffer(string root){
        using(var store=new Store(root)){
            long bucket=Clock.Minute(Clock.Now);
            var dimension=new Dimension{App="load-test.exe",Host="synthetic.example",Node="test node"};
            var many=new DeltaResult{Upload=50001,Download=100002,AttributedUpload=50001,AttributedDownload=100002};
            many.Usage.AddRange(Enumerable.Range(0,50001).Select(i=>new Usage{Bucket=bucket,Dimension=dimension,Upload=1,Download=2}));
            var buffer=new CaptureBuffer();buffer.Add(many,bucket);buffer.Flush(store);
            var data=Json.Read(Json.Write(store.Stats(new Query{From=bucket,To=bucket+60})));
            Assert(Json.Num(Json.Obj(data,"summary"),"download")==100002&&buffer.DroppedRecords==0&&buffer.Count==0,"Healthy storage saves more than 50,000 buffered details without trimming any bytes");
            using(var writer=new Sqlite(store.FilePath)){
                writer.Execute("CREATE TRIGGER reject_capture BEFORE INSERT ON traffic BEGIN SELECT RAISE(ABORT,'synthetic write failure'); END;");
                var retry=new CaptureBuffer(2);
                var delta=new DeltaResult{Upload=3,Download=6,AttributedUpload=3,AttributedDownload=6};
                delta.Usage.AddRange(Enumerable.Range(0,3).Select(i=>new Usage{Bucket=bucket+60,Dimension=dimension,Upload=1,Download=2}));retry.Add(delta,bucket+60);
                var next=new DeltaResult{Download=5,AttributedDownload=5};next.Usage.Add(new Usage{Bucket=bucket+120,Dimension=dimension,Download=5});retry.Add(next,bucket+120);
                bool failed=false;try{retry.Flush(store);}catch(InvalidOperationException){failed=true;}
                Assert(failed&&retry.Count==2&&retry.DroppedRecords==2,"Only a failed transaction enforces the detail cache limit and records exact lost-row counts");
                writer.Execute("DROP TRIGGER reject_capture");retry.Flush(store);retry.Flush(store);
                data=Json.Read(Json.Write(store.Stats(new Query{From=bucket+60,To=bucket+180})));
                Assert(Json.Num(Json.Obj(data,"summary"),"download")==7&&Json.Num(Json.Obj(data,"core"),"download")==11&&Json.Num(Json.Obj(data,"core"),"attributedDownload")==7,"A failed batch retries atomically; dropped details become unattributed bytes in their own minute without double counting");
            }
        }
    }
    private static void TestCollectorReconnect(string root){
        using(var pipe=new CollectorPipe())using(var store=new Store(root))using(var collector=new Collector(store,new LedgerConfig{Pipe=pipe.Name},root)){
            using(var writer=new Sqlite(store.FilePath))writer.Execute("CREATE TRIGGER reject_event BEFORE INSERT ON events BEGIN SELECT RAISE(ABORT,'synthetic event failure'); END;");
            collector.Start();
            bool connected=SpinWait.SpinUntil(()=>collector.Connected,TimeSpan.FromSeconds(10));
            if(!connected)Console.WriteLine("Collector connection error: "+collector.Error);
            Assert(connected,"The Windows collector connects through an isolated named pipe even if event logging fails");
            pipe.Failing=true;
            Assert(SpinWait.SpinUntil(()=>!collector.Connected,TimeSpan.FromSeconds(10)),"The collector detects failed HTTP reads without losing its worker");
            using(var writer=new Sqlite(store.FilePath))writer.Execute("DROP TRIGGER reject_event");
            pipe.Recovered=true;pipe.Failing=false;
            Assert(SpinWait.SpinUntil(()=>collector.Connected,TimeSpan.FromSeconds(15)),"The same named-pipe core recovers after temporary read failures");
            collector.Dispose();
            long now=Clock.Now;var data=Json.Read(Json.Write(store.Stats(new Query{From=now-120,To=now+60})));
            Assert(Json.Num(Json.Obj(data,"core"),"upload")==500&&Json.Num(Json.Obj(data,"core"),"download")==1000&&Json.Num(Json.Obj(data,"summary"),"download")==1000,"End-to-end reconnect commits the interrupted interval exactly once to SQLite");
            Assert(Json.Array(data,"events").OfType<System.Collections.Generic.Dictionary<string,object>>().Any(e=>Json.Str(e,"detail").Contains("恢复分钟")),"Recovered traffic explicitly records that original minute timing is unavailable");
        }
    }
    private static void TestCheckpoints(string root){
        long bucket=Clock.Minute(Clock.Now);const string identity="pipe|123|created-1";
        var first=Sample(1000,2000,Conn("long",10,1000,2000));
        var initial=new CaptureCheckpoint{At=bucket,CoreIdentity=identity,IdentityVerified=true,Snapshot=first};
        using(var store=new Store(root))using(var writer=new Sqlite(store.FilePath)){
            store.Write(new Usage[0],null,"test",initial.Serialize());
            var saved=CaptureCheckpoint.Parse(store.GetMeta(CaptureCheckpoint.Key));
            Assert(saved!=null&&saved.Snapshot.Connections.Single().Id=="long"&&saved.Snapshot.Connections.Single().Dimension==null,"Checkpoint round-trip preserves connection counters without persisting application or domain metadata");
            var tracker=new DeltaTracker();tracker.Restore(saved);
            var next=Sample(1300,2600,Conn("long",10,1300,2600));var delta=tracker.Observe(next,bucket+60,identity,true);
            var buffer=new CaptureBuffer();buffer.Add(delta,bucket+60,new CaptureCheckpoint{At=bucket+60,CoreIdentity=identity,IdentityVerified=true,Snapshot=next});
            writer.Execute("CREATE TRIGGER reject_checkpoint BEFORE UPDATE ON meta WHEN NEW.key='mihomo_checkpoint_v1' BEGIN SELECT RAISE(ABORT,'synthetic cursor failure'); END;");
            bool failed=false;try{buffer.Flush(store);}catch(InvalidOperationException){failed=true;}
            var data=Json.Read(Json.Write(store.Stats(new Query{From=bucket,To=bucket+120})));
            Assert(failed&&Json.Num(Json.Obj(data,"summary"),"download")==0&&Json.Num(Json.Obj(data,"core"),"download")==0&&CaptureCheckpoint.Parse(store.GetMeta(CaptureCheckpoint.Key)).Snapshot.Download==2000,"A checkpoint write failure rolls back facts, core totals and the cursor as one transaction");
            writer.Execute("DROP TRIGGER reject_checkpoint");buffer.Flush(store);
        }
        using(var reopened=new Store(root)){
            var saved=CaptureCheckpoint.Parse(reopened.GetMeta(CaptureCheckpoint.Key));var tracker=new DeltaTracker();tracker.Restore(saved);
            var unchanged=tracker.Observe(Sample(1300,2600,Conn("long",10,1300,2600)),bucket+120,identity,true);
            var data=Json.Read(Json.Write(reopened.Stats(new Query{From=bucket,To=bucket+180})));
            Assert(unchanged.Download==0&&Json.Num(Json.Obj(data,"core"),"download")==600&&Json.Num(Json.Obj(data,"summary"),"download")==600,"Reopening a committed checkpoint never counts a retried batch twice");
            initial.IdentityVerified=false;reopened.Write(new Usage[0],null,"test",initial.Serialize());
            Assert(CaptureCheckpoint.Parse(reopened.GetMeta(CaptureCheckpoint.Key))==null,"An unverified snapshot replaces the old cursor and cannot recover stale counters");
        }
        Assert(CaptureCheckpoint.Parse("{")==null&&CaptureCheckpoint.Parse("{\"version\":2}")==null,"Damaged or unsupported checkpoints fall back to a fresh baseline");
        Assert(CaptureCheckpoint.Parse("{\"version\":1,\"at\":1,\"core\":\"test\",\"verified\":true,\"connections\":[]}")==null,"A checkpoint missing cumulative counters cannot trigger false recovery from zero");
    }
    private static void TestCollectorRestart(string root){
        using(var pipe=new CollectorPipe())using(var store=new Store(root)){
            var config=new LedgerConfig{Pipe=pipe.Name};
            using(var first=new Collector(store,config,root)){first.Start();Assert(SpinWait.SpinUntil(()=>first.Connected,TimeSpan.FromSeconds(10)),"Initial collector establishes a checkpoint for restart recovery");}
            Assert(CaptureCheckpoint.Parse(store.GetMeta(CaptureCheckpoint.Key))!=null,"Normal shutdown persists the last successful snapshot");
            pipe.Recovered=true;
            using(var second=new Collector(store,config,root)){second.Start();Assert(SpinWait.SpinUntil(()=>second.Connected,TimeSpan.FromSeconds(10)),"A new collector restores its cursor from SQLite and verifies the same running core");}
            using(var third=new Collector(store,config,root)){third.Start();Assert(SpinWait.SpinUntil(()=>third.Connected,TimeSpan.FromSeconds(10)),"Repeated collector restart remains available");}
            long now=Clock.Now;var data=Json.Read(Json.Write(store.Stats(new Query{From=now-120,To=now+60})));
            Assert(Json.Num(Json.Obj(data,"core"),"upload")==500&&Json.Num(Json.Obj(data,"core"),"download")==1000&&Json.Num(Json.Obj(data,"summary"),"download")==1000,"Traffic while the ledger was stopped is recovered once, including existing live connections");
        }
    }
    private static void TestCollectorCancellation(string root){
        using(var pipe=new CollectorPipe())using(var store=new Store(root))using(var collector=new Collector(store,new LedgerConfig{Pipe=pipe.Name},root)){
            pipe.Stall=true;collector.Start();
            Assert(pipe.Requested.Wait(10000),"A synthetic core can stall a real named-pipe response during shutdown");
            var watch=System.Diagnostics.Stopwatch.StartNew();collector.Dispose();
            Assert(watch.ElapsedMilliseconds<3000,"Shutdown cancels stalled pipe reads and waits for the final database flush");
        }
    }
    private static void TestCoreReconciliation(string root){
        using(var store=new Store(root)){
            long bucket=Clock.Minute(Clock.Now);
            var facts=new[]{new Usage{Bucket=bucket,Download=400,Dimension=new Dimension{App="test.exe",Host="proxy.example",Node="test node"}},new Usage{Bucket=bucket,Download=200,Dimension=new Dimension{App="test.exe",Host="direct.example",Node="DIRECT",Route="DIRECT"}},new Usage{Bucket=bucket,Download=900,Dimension=new Dimension{Source="windows",App="outside.exe",Host="192.0.2.1",Route="BYPASS"}}};
            store.Write(facts,new[]{new TimedDelta{Bucket=bucket,Download=1000,AttributedDownload=1000}},"test");
            var query=new Query{From=bucket,To=bucket+60};var data=Json.Read(Json.Write(store.Stats(query)));
            Assert(Json.Num(Json.Obj(data,"core"),"recordedDownload")==600,"Reconciliation uses actual saved Mihomo facts rather than old attributed counters or Windows bytes");
            query.Scope="proxy";query.Node="test node";data=Json.Read(Json.Write(store.Stats(query)));
            Assert(Json.Num(Json.Obj(data,"summary"),"download")==400&&Json.Num(Json.Obj(data,"core"),"recordedDownload")==600&&Json.Num(Json.Obj(data,"core"),"download")==1000,"Node and source filters cannot change the full-core time-range reconciliation");
            query.Search="no-match";data=Json.Read(Json.Write(store.Stats(query)));
            Assert(Json.Num(Json.Obj(data,"summary"),"download")==0&&Json.Num(Json.Obj(data,"core"),"recordedDownload")==600,"An empty filtered result still retains the complete core comparison");
        }
        using(var store=new Store(Path.Combine(root,"windows-errors")))using(var writer=new Sqlite(store.FilePath)){
            writer.Execute("CREATE TRIGGER reject_event BEFORE INSERT ON events BEGIN SELECT RAISE(ABORT,'synthetic event failure'); END; CREATE TRIGGER reject_health BEFORE INSERT ON meta BEGIN SELECT RAISE(ABORT,'synthetic health failure'); END;");
            Assert(!EtwHelper.Log(store,"windows-gap","synthetic failure")&&!EtwHelper.SaveHealth(store,new{running=true}),"Windows health and event write failures cannot escape and terminate the supplemental collector");
            writer.Execute("DROP TRIGGER reject_event; DROP TRIGGER reject_health;");
            Assert(EtwHelper.Log(store,"windows-start","synthetic recovery")&&EtwHelper.SaveHealth(store,new{running=true})&&store.GetMeta("etw_health")!="","Windows status writes recover when storage becomes available again");
        }
        var state=Json.Read("{\"at\":100,\"connected\":true,\"tunKnown\":true,\"tun\":false}");
        Assert(EtwHelper.CanCount(state,105),"Supplemental traffic requires a fresh connected core with verified TUN-off mode");
        state["connected"]=false;Assert(EtwHelper.MainAlive(state,105)&&!EtwHelper.CanCount(state,105),"A disconnected core pauses Windows attribution without treating the live main recorder as absent");
        state["connected"]=true;state["tunKnown"]=false;Assert(!EtwHelper.CanCount(state,105),"Unknown TUN mode cannot silently enable duplicate Windows traffic");
        state["tunKnown"]=true;state["tun"]=true;Assert(!EtwHelper.CanCount(state,105),"TUN mode continues to suspend supplemental attribution");
        state["tun"]=false;Assert(!EtwHelper.CanCount(state,121)&&!EtwHelper.CanCount(state,99)&&!EtwHelper.CanCount(null,105),"Stale, future-dated or missing capture state cannot enable supplemental attribution");
    }
    private sealed class CollectorPipe : IDisposable {
        public readonly string Name="ledger-test-"+Guid.NewGuid().ToString("N");
        public volatile bool Failing,Recovered,Stall;
        public readonly ManualResetEventSlim Requested=new ManualResetEventSlim(false);
        private readonly CancellationTokenSource cancel=new CancellationTokenSource();
        private readonly Task worker;
        public CollectorPipe(){worker=Task.Run(async()=>{
            while(!cancel.IsCancellationRequested){
                using(var pipe=new NamedPipeServerStream(Name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))using(cancel.Token.Register(()=>pipe.Dispose())){
                    try{
                        await pipe.WaitForConnectionAsync(cancel.Token);
                        using(var reader=new StreamReader(pipe,Encoding.ASCII,false,1024,true)){
                            string request=await reader.ReadLineAsync(),line;while(!string.IsNullOrEmpty(line=await reader.ReadLineAsync())){}
                            bool connections=request!=null&&request.StartsWith("GET /connections ");
                            if(connections&&Stall){Requested.Set();await Task.Delay(Timeout.Infinite,cancel.Token);}
                            string body=connections?Json.Write(new{uploadTotal=Recovered?1500:1000,downloadTotal=Recovered?3000:2000,connections=new[]{new{id="long",start="2020-01-01T00:00:00Z",upload=Recovered?1500:1000,download=Recovered?3000:2000,metadata=new{process="synthetic.exe",host="synthetic.example"},chains=new[]{"test node"}}}}):"{}";
                            byte[] response=Encoding.UTF8.GetBytes("HTTP/1.1 "+(Failing&&connections?"503 Unavailable":"200 OK")+"\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\n\r\n"+body);
                            await pipe.WriteAsync(response,0,response.Length,cancel.Token);
                        }
                    }catch(OperationCanceledException){break;}catch(ObjectDisposedException){if(cancel.IsCancellationRequested)break;throw;}catch(IOException){if(cancel.IsCancellationRequested)break;}
                }
            }
        });}
        public void Dispose(){cancel.Cancel();if(!worker.Wait(5000))throw new TimeoutException("Synthetic pipe server failed to stop");worker.GetAwaiter().GetResult();cancel.Dispose();Requested.Dispose();}
    }
}
}
