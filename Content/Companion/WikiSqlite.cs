using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TerrarAI.Content.Companion;

// OS SQLite only; never loads a native binary supplied by a Wiki package.
internal sealed class WikiSqlite : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Close(IntPtr db);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Prepare(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr stmt, IntPtr tail);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Bind(IntPtr stmt, int pos, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Step(IntPtr stmt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Column(IntPtr stmt, int pos);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Finalize(IntPtr stmt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProgressCallback(IntPtr unused);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Progress(IntPtr db, int instructions, ProgressCallback callback, IntPtr unused);
    private static class Api
    {
        // The handle remains loaded for the lifetime of the delegates.
        // Windows: a bare "winsqlite3.dll" fails inside tModLoader (LoadLibraryEx with restricted search
        // flags rejects relative names, E_INVALIDARG), so name the copy in System32 explicitly.
        private static readonly IntPtr Library = NativeLibrary.Load(OperatingSystem.IsMacOS() ? "/usr/lib/libsqlite3.dylib"
            : OperatingSystem.IsWindows() ? System.IO.Path.Combine(Environment.SystemDirectory, "winsqlite3.dll") : "libsqlite3.so.0");
        private static T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library, "sqlite3_" + name));
        internal static readonly Open Open = Get<Open>("open_v2");
        internal static readonly Close Close = Get<Close>("close_v2");
        internal static readonly Prepare Prepare = Get<Prepare>("prepare_v2");
        internal static readonly Bind Bind = Get<Bind>("bind_text");
        internal static readonly Step Step = Get<Step>("step");
        internal static readonly Column Column = Get<Column>("column_text");
        internal static readonly Finalize Finalize = Get<Finalize>("finalize");
        internal static readonly Progress Progress = Get<Progress>("progress_handler");
    }
    private IntPtr _db;
    private readonly CancellationToken _token;
    private readonly long _deadline = Environment.TickCount64 + 2000;
    private readonly ProgressCallback _progress;
    public WikiSqlite(string path, CancellationToken token)
    {
        _token = token;
        _progress = _ => token.IsCancellationRequested || Environment.TickCount64 > _deadline ? 1 : 0;
        if (Api.Open(path, out _db, 1 | 0x8000, IntPtr.Zero) != 0) { Dispose(); throw new InvalidOperationException("离线 Wiki 数据库无法只读打开。"); }
        Api.Progress(_db, 1000, _progress, IntPtr.Zero);
    }
    public List<string[]> Query(string sql, int columns, params string[] values)
    {
        _token.ThrowIfCancellationRequested();
        IntPtr stmt = IntPtr.Zero;
        try
        {
            if (Api.Prepare(_db, sql, -1, out stmt, IntPtr.Zero) != 0) throw new InvalidOperationException("离线 Wiki 索引格式不兼容，请重新导入。");
            for (int i = 0; i < values.Length; i++)
                if (Api.Bind(stmt, i + 1, values[i], -1, new IntPtr(-1)) != 0) throw new InvalidOperationException("Wiki 查询参数无效。");
            int status;
            var rows = new List<string[]>();
            while ((status = Api.Step(stmt)) == 100)
            {
                _token.ThrowIfCancellationRequested();
                if (Environment.TickCount64 > _deadline) throw new TimeoutException("Wiki 本地检索超时。");
                var row = new string[columns];
                for (int i = 0; i < columns; i++) row[i] = Marshal.PtrToStringUTF8(Api.Column(stmt, i)) ?? "";
                rows.Add(row);
                if (rows.Count > 20000) throw new InvalidOperationException("Wiki 查询结果超出限制。");
            }
            _token.ThrowIfCancellationRequested();
            if (status != 101) throw new InvalidOperationException("Wiki 检索失败或超时，请重新导入索引。");
            return rows;
        }
        finally { if (stmt != IntPtr.Zero) Api.Finalize(stmt); }
    }
    public void Dispose()
    { if (_db != IntPtr.Zero) { Api.Close(_db); _db = IntPtr.Zero; } GC.KeepAlive(_progress); }
}
