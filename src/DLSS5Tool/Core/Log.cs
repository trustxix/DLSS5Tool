using System.Text;

namespace DLSS5Tool.Core;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>Process-wide log: a rolling file under %LOCALAPPDATA%\DLSS5Tool\logs plus optional live sinks (UI, console).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly List<Action<LogLevel, string>> Sinks = new();
    private static StreamWriter? _file;

    public static string? FilePath { get; private set; }

    public static void Init(string? fileName = null)
    {
        lock (Gate)
        {
            if (_file != null) return;
            try
            {
                AppPaths.EnsureDirectories();
                FilePath = Path.Combine(AppPaths.Logs, fileName ?? "DLSS5Tool.log");
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    var old = FilePath + ".1";
                    File.Copy(FilePath, old, true);
                    File.Delete(FilePath);
                }
                _file = new StreamWriter(FilePath, true, new UTF8Encoding(false)) { AutoFlush = true };
                _file.WriteLine();
                _file.WriteLine($"==== DLSS5Tool {AppPaths.Version} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
            }
            catch
            {
                _file = null;
            }
        }
    }

    public static void AddSink(Action<LogLevel, string> sink)
    {
        lock (Gate) Sinks.Add(sink);
    }

    public static void RemoveSink(Action<LogLevel, string> sink)
    {
        lock (Gate) Sinks.Remove(sink);
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string message, Exception ex) => Write(LogLevel.Error, message + ": " + ex.GetType().Name + ": " + ex.Message);

    public static void Write(LogLevel level, string message)
    {
        Action<LogLevel, string>[] sinks;
        lock (Gate)
        {
            _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] {message}");
            sinks = Sinks.ToArray();
        }
        foreach (var s in sinks)
        {
            try { s(level, message); } catch { /* a dead sink must not take the app down */ }
        }
    }
}
