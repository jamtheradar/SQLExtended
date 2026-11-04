using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SQLExtended.SchemaExporter;

/// <summary>
/// The git half, through the git CLI. Azure DevOps and GitHub are both plain git remotes, so nothing here is
/// provider-specific; the only difference between them is the credential, and a PAT works for both.
/// </summary>
internal sealed class GitRepository(string path, GitOptions options, RunLog log)
{
    /// <summary>
    /// Files are written CRLF with a BOM, byte-for-byte what the extension writes. Without this, a repository or
    /// service account with core.autocrlf set would rewrite every line ending on checkout, and a folder compare
    /// against an export taken in SSMS would differ in every file.
    /// </summary>
    private const string GitAttributesLine = "*.sql -text";

    public void Prepare()
    {
        if (!Directory.Exists(path) || Run("rev-parse --is-inside-work-tree", throwOnError: false).ExitCode != 0)
            throw new InvalidOperationException($"{path} is not a git working copy. Clone the DevOps / GitHub repository there first (see README).");

        // Left behind when a run (or a git) is killed mid-step, and then every add and commit fails. Not deleted here:
        // it can't be told from a git that is still running, and deleting that one's lock can corrupt the index.
        string indexLock = Path.Combine(path, ".git", "index.lock");
        if (File.Exists(indexLock))
            throw new InvalidOperationException($"{indexLock} exists: another git is running in this repository, or a killed run left it behind. " +
                "If no git.exe is running, delete it and run again (--push-only commits what the killed run exported).");

        // A clone of a brand-new, empty DevOps / GitHub repository has no commits, so there is no branch to check out
        // and `checkout main` fails. Point the unborn HEAD at the branch instead; the first commit creates it.
        bool unborn = Run("rev-parse --verify --quiet HEAD", throwOnError: false).ExitCode != 0;
        if (!string.IsNullOrEmpty(options.Branch))
            Run(unborn ? $"symbolic-ref HEAD {Quote("refs/heads/" + options.Branch)}" : $"checkout {Quote(options.Branch)}");

        // Nothing to pull from a remote that doesn't have the branch yet (an empty repository): the first push creates it.
        // ls-remote exits 2 for "no such ref", and anything else non-zero (auth, network) is left for the pull to report.
        bool remoteHasBranch = true;
        if (options.Push && HasRemote() && !string.IsNullOrEmpty(options.Branch))
        {
            var ls = Run($"ls-remote --exit-code --heads {Quote(options.Remote)} {Quote(options.Branch)}", throwOnError: false);
            if (ls.ExitCode == 2)
            {
                remoteHasBranch = false;
                log.Info($"git: {options.Remote} has no {options.Branch} branch yet — the first push will create it.");
            }
        }

        if (options.Push && HasRemote() && remoteHasBranch)
        {
            // Fast-forward only: this tool is the repository's only writer, so anything else means someone pushed by
            // hand. Better to stop and say so than to merge or rebase unattended.
            var pull = Run($"pull --ff-only {Quote(options.Remote)}" + (string.IsNullOrEmpty(options.Branch) ? "" : " " + Quote(options.Branch)), throwOnError: false, activity: "Pulling");
            if (pull.ExitCode != 0) log.Warn($"git pull failed — exporting anyway, but the push will probably be rejected: {pull.Output.Trim()}");
        }

        string attributes = Path.Combine(path, ".gitattributes");
        string existing = File.Exists(attributes) ? File.ReadAllText(attributes) : "";
        if (!existing.Split('\n').Any(l => l.Trim() == GitAttributesLine))
            File.WriteAllText(attributes, existing + (existing.Length == 0 || existing.EndsWith("\n") ? "" : "\n") + GitAttributesLine + "\n");
    }

    /// <summary>Commits the server's folder if anything in it changed. Returns whether a commit was made.</summary>
    public bool CommitServer(ServerConfig server, DateTime takenAt) => CommitFolder(server.Name, server.Folder, "Schema", takenAt);

    /// <summary>Commits the report server's folder if anything in it changed. Returns whether a commit was made.</summary>
    public bool CommitReportServer(ReportServerConfig server, DateTime takenAt) => CommitFolder(server.Name, server.Folder, "Report", takenAt);

    private bool CommitFolder(string name, string folder, string snapshotOf, DateTime takenAt)
    {
        // A pathspec that matches nothing is a fatal error to add and commit. The folder can be absent on disk (a
        // server with no readable databases yet) and still be tracked (every database dropped), so ask both.
        bool folderKnown = Directory.Exists(Path.Combine(path, folder)) || Run($"ls-files -- {Quote(folder)}").Output.Trim().Length > 0;
        string pathspec = (folderKnown ? Quote(folder) + " " : "") + ".gitattributes";

        // The slow step on a first run: add hashes every file, and on a server with tens of thousands of scripts (and
        // an on-access virus scan of each) that is many minutes with no output — killed as a hang the first time.
        // --verbose prints one line per file, which is counted here into a progress line every few seconds.
        log.Progress($"{name}: staging changes…");
        var clock = Stopwatch.StartNew();
        int staged = 0;
        var lastReport = Stopwatch.StartNew();
        Run($"add -A --verbose -- {pathspec}", activity: $"{name}: staging", onLine: line =>
        {
            if (!line.StartsWith("add '") && !line.StartsWith("remove '")) return false;
            staged++;
            if (lastReport.Elapsed.TotalSeconds >= 5) { log.Progress($"{name}: staging — {staged:N0} files so far ({Elapsed(clock)})"); lastReport.Restart(); }
            return true;
        });
        if (staged > 0) log.Progress($"{name}: staged {staged:N0} file{(staged == 1 ? "" : "s")} in {Elapsed(clock)}.");

        var status = Run($"diff --cached --name-status -- {pathspec}").Output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (status.Count == 0) return false;

        int Count(char code) => status.Count(l => l.Length > 0 && l[0] == code);

        var message = new StringBuilder();
        message.AppendLine($"{name}: {Count('A')} added, {Count('M')} changed, {Count('D')} removed");
        message.AppendLine();
        message.AppendLine($"{snapshotOf} snapshot of {name} taken {takenAt:yyyy-MM-dd HH:mm}.");
        message.AppendLine();
        foreach (string line in status.Take(500)) message.AppendLine(line.Replace('\t', ' '));
        if (status.Count > 500) message.AppendLine($"… and {status.Count - 500} more");

        string messageFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(messageFile, message.ToString(), new UTF8Encoding(false));
            string author = string.IsNullOrEmpty(options.AuthorName) ? "" : $"-c user.name={Quote(options.AuthorName)} ";
            if (!string.IsNullOrEmpty(options.AuthorEmail)) author += $"-c user.email={Quote(options.AuthorEmail)} ";
            Run($"{author}commit --quiet -F {Quote(messageFile)} -- {pathspec}", activity: $"{name}: committing");
        }
        finally { File.Delete(messageFile); }

        log.Info($"{name}: committed ({status.Count:N0} file{(status.Count == 1 ? "" : "s")}, {Elapsed(clock)}).");
        return true;
    }

    /// <summary>Pushes whether or not tonight committed anything, so a push that failed on an earlier night catches up.</summary>
    public void Push()
    {
        if (!options.Push) return;
        if (!HasRemote()) { log.Warn($"No remote named '{options.Remote}' — nothing pushed."); return; }

        string refspec = string.IsNullOrEmpty(options.Branch) ? "HEAD" : $"HEAD:refs/heads/{options.Branch}";
        // --progress: git only reports upload progress to a terminal otherwise. Its lines ("Writing objects:  42%
        // (1234/2938), 3.1 MiB | 1.2 MiB/s") are \r-separated updates of one line, so they are thinned to one every
        // few seconds, plus the first of each phase. The first push of a big history can take many minutes.
        log.Progress($"Pushing to {options.Remote}…");
        var clock = Stopwatch.StartNew();
        var lastReport = Stopwatch.StartNew();
        string lastPhase = null;
        Run($"push --progress {Quote(options.Remote)} {Quote(refspec)}", activity: "Pushing", onLine: line =>
        {
            int colon = line.IndexOf(':');
            bool isProgress = colon > 0 && line.IndexOf('%') > colon;
            if (!isProgress) return false;

            string phase = line.Substring(0, colon);
            if (phase != lastPhase || lastReport.Elapsed.TotalSeconds >= 5 || line.Contains(", done"))
            {
                log.Progress("  " + line.Trim());
                lastPhase = phase;
                lastReport.Restart();
            }
            return true;
        });
        log.Info($"Pushed to {options.Remote} ({Elapsed(clock)}).");
    }

    private static string Elapsed(Stopwatch clock) => clock.Elapsed.TotalMinutes >= 1 ? $"{(int)clock.Elapsed.TotalMinutes}m {clock.Elapsed.Seconds}s" : $"{clock.Elapsed.TotalSeconds:0.0}s";

    private bool HasRemote() => !string.IsNullOrEmpty(options.Remote) && Run($"remote get-url {Quote(options.Remote)}", throwOnError: false).ExitCode == 0;

    /// <param name="activity">Names the step in a "still running" line every 30 s; null for the quick queries.</param>
    /// <param name="onLine">Sees each output line; returns true for a progress line it has dealt with.</param>
    private (int ExitCode, string Output) Run(string arguments, bool throwOnError = true, string activity = null, Func<string, bool> onLine = null)
    {
        string git = string.IsNullOrEmpty(options.Executable) ? "git" : options.Executable;
        var psi = new ProcessStartInfo(git, $"-C {Quote(path)} -c core.quotepath=off {arguments}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Unattended: a credential prompt would hang the scheduled task forever instead of failing it.
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";

        // The PAT travels as an extra HTTP header set through environment config (git 2.31+): never on a command line
        // another process can read, never written to .git/config. "x-access-token" is the user name GitHub expects
        // with a token; Azure DevOps ignores the user name.
        string token = string.IsNullOrEmpty(options.TokenEnvironmentVariable) ? null : Environment.GetEnvironmentVariable(options.TokenEnvironmentVariable);
        if (!string.IsNullOrEmpty(token))
        {
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
            psi.EnvironmentVariables["GIT_CONFIG_COUNT"] = "1";
            psi.EnvironmentVariables["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            psi.EnvironmentVariables["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + basic;
        }

        // Not found surfaces as a Win32Exception, which nothing upstream expects: report it as a git failure instead.
        Process started;
        try { started = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitException(string.IsNullOrEmpty(options.Executable)
                ? $"Could not start git ({ex.Message}). Install it, put it on PATH for the account running the task, or set git.executable (Portable Git: <folder>\\cmd\\git.exe)."
                : $"Could not start git at {options.Executable} ({ex.Message}).");
        }

        using var process = started ?? throw new GitException("Could not start git.");

        // Both streams line by line. The async reader treats a bare \r as a line end too, which is how git redraws its
        // progress line. A line onLine handles (returns true for) is progress, and stays out of the output that an error
        // message or a caller parses.
        var output = new StringBuilder();
        var gate = new object();
        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (gate)
            {
                if (onLine != null && onLine(e.Data)) return;
                output.AppendLine(e.Data);
            }
        }
        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // A step that prints nothing for minutes can't be told from a hung one, so say it's still going.
        var clock = Stopwatch.StartNew();
        while (!process.WaitForExit(30_000))
            if (activity != null) log.Progress($"{activity} — still running ({Elapsed(clock)})");
        process.WaitForExit(); // the no-timeout overload also waits for the stream handlers to drain

        string text;
        lock (gate) text = output.ToString();

        if (throwOnError && process.ExitCode != 0)
            throw new GitException($"git {arguments} failed ({process.ExitCode}): {text.Trim()}");

        return (process.ExitCode, text);
    }

    /// <summary>Windows command-line quoting (the rules CommandLineToArgvW reverses).</summary>
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;

        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2);
        return sb.Append('"').ToString();
    }
}

internal sealed class GitException(string message) : Exception(message);
