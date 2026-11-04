using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace SQLExtended.SchemaExporter;

/// <summary>
/// SchemaExporter [config.json] [--server NAME] [--full] [--no-git] [--no-push] [--verbose]
///
/// Exit codes, for Task Scheduler's "last run result": 0 every server exported (warnings allowed), 1 at least one
/// server or the git step failed, 2 bad arguments or config, 3 cancelled.
/// </summary>
internal static class Program
{
    private const int Ok = 0, Failed = 1, BadConfig = 2, Cancelled = 3;

    private static int Main(string[] args)
    {
        string configPath = null, onlyServer = null;
        bool noGit = false, noPush = false, verbose = false, full = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--server" when i + 1 < args.Length: onlyServer = args[++i]; break;
                case "--no-git": noGit = true; break;
                case "--no-push": noPush = true; break;
                case "--full": full = true; break;
                case "--verbose": case "-v": verbose = true; break;
                case "--help": case "-h": case "/?": PrintUsage(); return Ok;
                default:
                    if (args[i].StartsWith("-") || configPath != null) { Console.Error.WriteLine($"Unrecognised argument: {args[i]}"); PrintUsage(); return BadConfig; }
                    configPath = args[i];
                    break;
            }
        }

        configPath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SchemaExporter.json");

        ExporterConfig config;
        try { config = ExporterConfig.Load(configPath); }
        catch (ConfigException ex) { Console.Error.WriteLine(ex.Message); return BadConfig; }

        var servers = config.Servers.Where(s => onlyServer == null || string.Equals(s.Name, onlyServer, StringComparison.OrdinalIgnoreCase)).ToList();
        if (servers.Count == 0) { Console.Error.WriteLine($"No server named '{onlyServer}' in {configPath}."); return BadConfig; }

        if (noPush) config.Git.Push = false;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var log = new RunLog(config.LogDirectory, config.LogRetentionDays, verbose);
        log.Info($"SchemaExporter — {servers.Count} server{(servers.Count == 1 ? "" : "s")} into {config.OutputPath}");

        int exit = Ok;
        GitRepository git = null;

        try
        {
            Directory.CreateDirectory(config.OutputPath);

            if (config.Git.Enabled && !noGit)
            {
                git = new GitRepository(config.OutputPath, config.Git, log);
                git.Prepare();
            }

            foreach (var server in servers)
            {
                cts.Token.ThrowIfCancellationRequested();
                log.Info($"{server.Name}: exporting…");
                DateTime started = DateTime.Now;

                try
                {
                    ServerExporter.Export(server, config, log, full, cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Its folder is untouched, so its history simply has no entry tonight. The other servers carry on.
                    log.Error($"{server.Name}: export failed, previous scripts kept — {Innermost(ex)}");
                    exit = Failed;
                    continue;
                }

                // Committed per server, so one server's history reads independently of the others'.
                if (git != null && !git.CommitServer(server, started))
                    log.Info($"{server.Name}: no schema changes.");
            }

            git?.Push();
        }
        catch (OperationCanceledException)
        {
            log.Error("Cancelled. Servers already committed stay committed; nothing was pushed.");
            return Cancelled;
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            log.Error(ex.Message);
            exit = Failed;
        }

        log.Info($"Done — {(exit == Ok ? "succeeded" : "finished with errors")}, {log.WarningCount} warning{(log.WarningCount == 1 ? "" : "s")}." + (log.FilePath != null ? $" Log: {log.FilePath}" : ""));
        return exit;
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex.Message;
    }

    private static void PrintUsage() => Console.WriteLine(@"SchemaExporter [config.json] [options]

Exports every configured server's schema, one file per object, in the same layout as the
SQLExtended Schema Cache window's folder export, then commits the changes to git.

  config.json      Defaults to SchemaExporter.json beside the exe. See SchemaExporter.sample.json.
  --server NAME    Export only this server.
  --full           Export every database, even those whose schema fingerprint is unchanged.
  --no-git         Write the tree only; no commit or push.
  --no-push        Commit locally but don't push.
  --verbose, -v    Show per-database progress.

Exit codes: 0 ok, 1 a server or git failed, 2 bad arguments or config, 3 cancelled.");
}
