using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexAccountSwitcher.Core;

// Use the SQLite runtime supplied by Windows. No CLI or machine-local SDK is required.
internal sealed class ProfileSqlite : IDisposable
{
    private IntPtr db;
    private readonly Func<bool> stop;
    private readonly ProgressCallback progress;
    internal ProfileSqlite(string path, bool write, CancellationToken ct, Stopwatch clock, TimeSpan budget)
    {
        SecureFiles.RejectLinks(path);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) SecureFiles.RejectLinks(path + suffix);
        stop = () => ct.IsCancellationRequested || clock.Elapsed > budget;
        progress = _ => stop() ? 1 : 0;
        var result = sqlite3_open_v2(path, out db, write ? 2 : 1, IntPtr.Zero);
        if (result != 0) { Dispose(); throw Error(result); }
        sqlite3_busy_timeout(db, 150);
        sqlite3_progress_handler(db, 1000, progress, IntPtr.Zero);
    }
    internal List<Dictionary<string, string?>> Query(string sql, params string?[] values)
    {
        if (stop()) throw new IOException("项目与会话检查超时或已取消，请稍后重试。");
        var result = sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero);
        try {
            if (result != 0) throw Error(result);
            for (var i = 0; i < values.Length; i++) {
                result = values[i] is { } text ? sqlite3_bind_text(statement, i + 1, text, -1, new IntPtr(-1)) : sqlite3_bind_null(statement, i + 1);
                if (result != 0) throw Error(result);
            }
            var rows = new List<Dictionary<string, string?>>();
            while ((result = sqlite3_step(statement)) == 100) {
                if (stop() || rows.Count >= 20000) throw new IOException("会话记录较多，本次快速检查已停止；原有数据已保留。");
                var row = new Dictionary<string, string?>(StringComparer.Ordinal);
                for (var i = 0; i < sqlite3_column_count(statement); i++)
                    row[Marshal.PtrToStringUTF8(sqlite3_column_name(statement, i))!] = sqlite3_column_type(statement, i) == 5 ? null : Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i));
                rows.Add(row);
            }
            if (result != 101) throw Error(result);
            return rows;
        } finally { if (statement != IntPtr.Zero) sqlite3_finalize(statement); }
    }
    internal HashSet<string> Columns(string table) => Query("PRAGMA table_info(\"" + table.Replace("\"", "\"\"") + "\")")
        .Select(row => row["name"]!).ToHashSet(StringComparer.Ordinal);
    internal void Rollback() { if (db != IntPtr.Zero) sqlite3_exec(db, "ROLLBACK", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
    private static IOException Error(int code) => new(code is 5 or 6 ? "项目或会话数据库正被其他进程占用，请关闭相关 Codex 窗口后重试。"
        : code == 9 ? "项目与会话检查超时或已取消，请稍后重试。" : "无法读取或更新项目与会话数据库（错误 " + code + "），已停止修复。");
    public void Dispose() { if (db != IntPtr.Zero) { sqlite3_progress_handler(db, 0, null, IntPtr.Zero); sqlite3_close_v2(db); db = IntPtr.Zero; } GC.KeepAlive(progress); }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProgressCallback(IntPtr value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_progress_handler(IntPtr db, int steps, ProgressCallback? callback, IntPtr value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, IntPtr destructor);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr context, IntPtr error);
}
