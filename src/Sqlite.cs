using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DataTrafficLedger {
// Windows 10/11 ships winsqlite3. No bundled database server or native package.
internal sealed class Sqlite : IDisposable {
    private IntPtr db;
    public Sqlite(string path) {
        Check(Native.sqlite3_open_v2(Utf8(path),out db,0x00000002|0x00000004|0x00010000,IntPtr.Zero));
        Native.sqlite3_busy_timeout(db,5000);
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA cache_size=-2048; PRAGMA temp_store=MEMORY;");
    }
    private static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s+"\0"); }
    public void Check(int result) { if(result!=0 && result!=100 && result!=101) throw new InvalidOperationException("SQLite "+result+": "+ReadString(Native.sqlite3_errmsg(db))); }
    public void Execute(string sql) {
        IntPtr error;int rc=Native.sqlite3_exec(db,Utf8(sql),IntPtr.Zero,IntPtr.Zero,out error);
        if(error!=IntPtr.Zero) Native.sqlite3_free(error);Check(rc);
    }
    public Statement Prepare(string sql,params object[] args) { var s=new Statement(this,db,sql);try{s.Bind(args);return s;}catch{s.Dispose();throw;} }
    public List<Dictionary<string,object>> Query(string sql,params object[] args) {
        var result=new List<Dictionary<string,object>>();
        using(var s=Prepare(sql,args)) while(s.Step()) result.Add(s.Row());
        return result;
    }
    public void Run(string sql,params object[] args) { using(var s=Prepare(sql,args)) s.Step(); }
    public void Dispose() { if(db!=IntPtr.Zero){Native.sqlite3_close_v2(db);db=IntPtr.Zero;} }
    private static string ReadString(IntPtr pointer) { if(pointer==IntPtr.Zero)return "";int length=0;while(Marshal.ReadByte(pointer,length)!=0)length++;byte[] bytes=new byte[length];Marshal.Copy(pointer,bytes,0,length);return Encoding.UTF8.GetString(bytes); }
    internal sealed class Statement : IDisposable {
        private IntPtr statement; private Sqlite owner;
        public Statement(Sqlite owner,IntPtr database,string sql) { this.owner=owner;owner.Check(Native.sqlite3_prepare_v2(database,Utf8(sql),-1,out statement,IntPtr.Zero)); }
        public void Bind(params object[] args) { for(int i=0;i<args.Length;i++){object v=args[i];int rc;if(v==null)rc=Native.sqlite3_bind_null(statement,i+1);else if(v is long||v is int||v is bool)rc=Native.sqlite3_bind_int64(statement,i+1,Convert.ToInt64(v,CultureInfo.InvariantCulture));else{byte[] bytes=Utf8(Convert.ToString(v,CultureInfo.InvariantCulture));rc=Native.sqlite3_bind_text(statement,i+1,bytes,bytes.Length-1,new IntPtr(-1));}owner.Check(rc);} }
        public bool Step() { int rc=Native.sqlite3_step(statement);owner.Check(rc);return rc==100; }
        public Dictionary<string,object> Row() { var row=new Dictionary<string,object>();int count=Native.sqlite3_column_count(statement);for(int i=0;i<count;i++){string name=ReadString(Native.sqlite3_column_name(statement,i));int type=Native.sqlite3_column_type(statement,i);row[name]=type==1?(object)Native.sqlite3_column_int64(statement,i):type==2?(object)Native.sqlite3_column_double(statement,i):type==5?null:(object)ReadString(Native.sqlite3_column_text(statement,i));}return row; }
        public void Dispose() { if(statement!=IntPtr.Zero){Native.sqlite3_finalize(statement);statement=IntPtr.Zero;} }
    }
    private static class Native {
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_open_v2(byte[] file,out IntPtr db,int flags,IntPtr vfs);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_exec(IntPtr db,byte[] sql,IntPtr callback,IntPtr arg,out IntPtr error);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern void sqlite3_free(IntPtr p);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_busy_timeout(IntPtr db,int ms);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_prepare_v2(IntPtr db,byte[] sql,int bytes,out IntPtr statement,IntPtr tail);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_bind_text(IntPtr s,int index,byte[] text,int bytes,IntPtr destructor);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_bind_int64(IntPtr s,int index,long v);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_bind_null(IntPtr s,int index);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_step(IntPtr s);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_finalize(IntPtr s);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_column_count(IntPtr s);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr sqlite3_column_name(IntPtr s,int c);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_column_type(IntPtr s,int c);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern long sqlite3_column_int64(IntPtr s,int c);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern double sqlite3_column_double(IntPtr s,int c);
        [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr sqlite3_column_text(IntPtr s,int c);
    }
}
}
