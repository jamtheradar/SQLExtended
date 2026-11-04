using System;
using System.IO;
using System.Linq;
using System.Text;

namespace SQLExtended.SchemaExporter;

/// <summary>
/// Console plus a log file per run. Kept outside the repository: everything in a log is volatile (times, counts),
/// and the history must change only when a schema does.
/// </summary>
internal sealed class RunLog : IDisposable
{
    private readonly StreamWriter _file;
    private readonly bool _verbose;
    private readonly object _gate = new();

    public int WarningCount { get; private set; }
    public string FilePath { get; }

    public RunLog(string directory, int retentionDays, bool verbose)
    {
        _verbose = verbose;
        try
        {
            Directory.CreateDirectory(directory);
            Prune(directory, retentionDays);
            FilePath = Path.Combine(directory, $"SchemaExporter-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _file = new StreamWriter(FilePath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open a log file in {directory}: {ex.Message}. Logging to the console only.");
        }
    }

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) { WarningCount++; Write("WARN ", message); }

    public void Error(string message) => Write("ERROR", message);

    /// <summary>
    /// Progress lines from the exporter. Always on the console — a run that prints nothing for fifteen minutes can't be
    /// told from a hung one — but in the file only with --verbose, so a nightly log stays readable.
    /// </summary>
    public void Progress(string message) => Write("     ", message, toFile: _verbose);

    private void Write(string level, string message, bool toFile = true)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {level} {message}";
        lock (_gate)
        {
            (level == "ERROR" ? Console.Error : Console.Out).WriteLine(line);
            if (toFile) _file?.WriteLine(line);
        }
    }

    private static void Prune(string directory, int retentionDays)
    {
        if (retentionDays <= 0) return;
        foreach (var file in new DirectoryInfo(directory).GetFiles("SchemaExporter-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-retentionDays)))
        {
            try { file.Delete(); } catch { /* next run */ }
        }
    }

    public void Dispose() => _file?.Dispose();
}
