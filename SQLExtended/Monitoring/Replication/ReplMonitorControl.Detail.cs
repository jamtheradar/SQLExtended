using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SQLExtended.Monitoring.Replication;

/// <summary>
/// The detail pane under the tabs: click a subscription, agent or error and it shows what troubleshooting needs
/// and the grids cannot — the whole MSrepl_errors chain, the agent's recent history, and the command it stopped
/// on with the article it belongs to. Everything here loads on the click and never on the timer; a refresh merges
/// rows in place, so the selection (and this pane) survive it without reloading.
/// </summary>
public partial class ReplMonitorControl
{
    private ReplDetailTarget _detailTarget;
    private ReplAgentDetail _detail;
    private ReplAgentType _detailAgentType;
    private int? _detailErrorId;
    private ReplFailingCommand _detailCommand;
    private CancellationTokenSource _detailCts;
    private CancellationTokenSource _commandCts;
    private double _detailHeight = 320;

    private void DetailSource_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        // Selection is by cell in these grids; any cell of a row stands for the row. A deselection (Ctrl+click,
        // or a refresh dropping the row) leaves the pane as it is rather than blanking it under the reader.
        if (e.AddedCells.Count == 0) return;

        var target = TargetFor(e.AddedCells[0].Item);
        if (target == null) return;
        if (DetailPane.Visibility == Visibility.Visible && _detailTarget != null && _detailTarget.Key == target.Key) return;

        ShowDetail(target);
    }

    private static ReplDetailTarget TargetFor(object item)
    {
        switch (item)
        {
            case ReplSubscriptionRow s:
                return new ReplDetailTarget
                {
                    AgentType = ReplAgentType.Distribution,
                    AgentId = s.AgentId,
                    Title = $"{s.Publication}  →  {s.Subscriber}{(string.IsNullOrEmpty(s.SubscriberDb) ? "" : " / " + s.SubscriberDb)}",
                    JobName = s.JobName,
                    KnownError = s.LastError ?? s.LastComment
                };

            case ReplAgentRow a:
                string what = a.Publication ?? a.PublisherDb ?? a.Name;
                string to = string.IsNullOrEmpty(a.Subscriber) ? "" : $"  →  {a.Subscriber}{(string.IsNullOrEmpty(a.SubscriberDb) ? "" : " / " + a.SubscriberDb)}";
                return new ReplDetailTarget
                {
                    AgentType = a.AgentType,
                    AgentId = a.AgentId,
                    Title = $"{a.AgentTypeText} agent · {what}{to}",
                    JobName = a.JobName,
                    KnownError = a.LastError ?? a.Comments
                };

            case ReplErrorRow r when r.Id != null:
                return new ReplDetailTarget
                {
                    ErrorId = r.Id,
                    Title = $"Error {r.Id}{(string.IsNullOrEmpty(r.ErrorCode) ? "" : " · " + r.ErrorCode)}{(r.Time == null ? "" : $" · {r.Time:yyyy-MM-dd HH:mm:ss}")}",
                    KnownError = r.ErrorText
                };

            default:
                return null;
        }
    }

    private void ShowDetail(ReplDetailTarget target)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _detailTarget = target;
        _detail = null;
        _detailAgentType = target.AgentType;
        _detailErrorId = null;
        _detailCommand = null;
        _commandCts?.Cancel();
        _detailCts?.Cancel();

        DetailPane.Visibility = Visibility.Visible;
        DetailSplitter.Visibility = Visibility.Visible;
        if (DetailRow.Height.Value < 1) DetailRow.Height = new GridLength(_detailHeight);

        DetailTitle.Text = target.Title;
        DetailErrorWhen.Text = "";
        DetailErrorText.Text = target.KnownError ?? "";
        DetailCommandSummary.Text = "";
        DetailCommandText.Text = "";
        DetailHistoryGrid.ItemsSource = null;

        var package = _package;
        string connection = _distributionConnection;
        var caps = _caps;
        if (package == null || string.IsNullOrEmpty(connection) || caps == null)
        {
            DetailSubtitle.Text = "Agent history, the error chain and the failing command live in the distribution database — open this window from a query connected to the distributor.";
            return;
        }

        if (target.AgentId == null && target.ErrorId == null)
        {
            DetailSubtitle.Text = "This subscription has no distribution agent recorded, so there is no history to read.";
            return;
        }

        DetailSubtitle.Text = target.AgentId == null ? "Finding the agent that logged this error…" : "Reading the agent's history and errors…";

        var cts = CancellationTokenSource.CreateLinkedTokenSource(package.DisposalToken);
        _detailCts = cts;

        _ = package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await TaskScheduler.Default;

                var type = target.AgentType;
                int? agentId = target.AgentId;
                string lookupProblem = null;

                if (agentId == null && target.ErrorId != null)
                {
                    try
                    {
                        var found = await ReplQueryService.FindAgentForErrorAsync(connection, caps, target.ErrorId.Value, cts.Token).ConfigureAwait(false);
                        if (found != null) { type = found.Value.Type; agentId = found.Value.AgentId; }
                        else lookupProblem = "No agent history row points at this error any more (history retention has cleaned it up), so only the error chain is shown.";
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lookupProblem = "Could not find the agent for this error: " + ex.Message;
                    }
                }

                var detail = await ReplQueryService.ReadAgentDetailAsync(connection, caps, type, agentId, target.ErrorId, cts.Token).ConfigureAwait(false);

                await package.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
                if (!ReferenceEquals(_detailTarget, target)) return;

                _detail = detail;
                _detailAgentType = type;
                DetailHistoryGrid.ItemsSource = detail.History;
                DetailSubtitle.Text = BuildDetailSubtitle(target, type, detail, lookupProblem);

                // The error the user clicked (Errors tab), else the agent's most recent one.
                int? errorId = target.ErrorId ?? detail.Errors.Select(r => r.Id).FirstOrDefault(id => id != null);
                ShowErrorChain(errorId);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                try
                {
                    await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                    if (ReferenceEquals(_detailTarget, target)) DetailSubtitle.Text = "Could not read the detail: " + ex.Message;
                }
                catch { }
            }
        });
    }

    private static string BuildDetailSubtitle(ReplDetailTarget target, ReplAgentType type, ReplAgentDetail detail, string lookupProblem)
    {
        var parts = new List<string>();

        if (detail.AgentId != null)
        {
            string typeText = new ReplAgentRow { AgentType = type }.AgentTypeText;
            parts.Add($"{typeText} agent {detail.AgentName ?? "#" + detail.AgentId}");
        }

        if (!string.IsNullOrEmpty(target.JobName)) parts.Add("job " + target.JobName);
        if (!string.IsNullOrEmpty(detail.Publisher)) parts.Add($"publisher {detail.Publisher} / {detail.PublisherDb}");
        if (target.AgentId == null && !string.IsNullOrEmpty(detail.Publication))
            parts.Add($"{detail.Publication}{(string.IsNullOrEmpty(detail.Subscriber) ? "" : $" → {detail.Subscriber} / {detail.SubscriberDb}")}");

        int errorIds = detail.Errors.Select(r => r.Id).Distinct().Count();
        if (detail.AgentId != null) parts.Add($"{detail.History.Count} history row(s), {errorIds} error(s)");

        string text = string.Join(" · ", parts);
        if (lookupProblem != null) text += (text.Length > 0 ? "\n" : "") + lookupProblem;
        if (detail.Warnings.Count > 0) text += (text.Length > 0 ? "\n" : "") + "Not read: " + string.Join("; ", detail.Warnings);
        return text;
    }

    /// <summary>Shows one error id's chain and starts reading the command it stopped on.</summary>
    private void ShowErrorChain(int? errorId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _detailErrorId = errorId;
        _detailCommand = null;
        _commandCts?.Cancel();

        var chain = _detail == null || errorId == null ? new List<ReplErrorRow>() : _detail.Errors.Where(r => r.Id == errorId).ToList();
        if (chain.Count == 0)
        {
            DetailErrorWhen.Text = "";
            DetailErrorText.Text = _detailTarget?.KnownError ?? "No error recorded for this agent in MSrepl_errors.";
            DetailCommandSummary.Text = "No error to trace back to a command.";
            DetailCommandText.Text = "";
            return;
        }

        DetailErrorWhen.Text = $"id {errorId} · {chain[0].Time:yyyy-MM-dd HH:mm:ss} · {chain.Count} message(s), outermost first";
        DetailErrorText.Text = FormatChain(chain);

        // Every row of a chain carries the same sequence number when it has one; the first meaningful one will do.
        var withSeqno = chain.FirstOrDefault(r => ReplQueryService.IsMeaningfulSeqno(r.XactSeqno));
        if (_detailAgentType != ReplAgentType.Distribution)
        {
            DetailCommandSummary.Text = "Only a distribution agent stops on a command that can be decoded; this error came from another agent.";
            DetailCommandText.Text = "";
            return;
        }

        if (withSeqno == null || _detail == null)
        {
            DetailCommandSummary.Text = "No transaction recorded with this error — the agent failed before it reached a command (a login, path or snapshot problem rather than a data one).";
            DetailCommandText.Text = "";
            return;
        }

        LoadFailingCommand(withSeqno.XactSeqno, withSeqno.CommandId);
    }

    private static string FormatChain(List<ReplErrorRow> chain)
    {
        var builder = new StringBuilder();
        foreach (var row in chain)
        {
            if (builder.Length > 0) builder.AppendLine().AppendLine();

            var head = new List<string>();
            if (!string.IsNullOrEmpty(row.ErrorCode)) head.Add("code " + row.ErrorCode);
            if (!string.IsNullOrEmpty(row.SourceName)) head.Add(row.SourceName);
            if (head.Count > 0) builder.AppendLine("[" + string.Join(" · ", head) + "]");
            builder.Append((row.ErrorText ?? "").Trim());
        }
        return builder.ToString();
    }

    private void LoadFailingCommand(string seqno, int? commandId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var package = _package;
        string connection = _distributionConnection;
        var detail = _detail;
        var target = _detailTarget;
        int? errorId = _detailErrorId;
        if (package == null || string.IsNullOrEmpty(connection) || detail == null) return;

        DetailCommandSummary.Text = $"Decoding transaction {seqno}{(commandId == null ? "" : $", command {commandId}")} with sp_browsereplcmds…";
        DetailCommandText.Text = "";

        var cts = CancellationTokenSource.CreateLinkedTokenSource(package.DisposalToken);
        _commandCts = cts;

        _ = package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await TaskScheduler.Default;
                var command = await ReplQueryService.ReadFailingCommandAsync(connection, detail, seqno, commandId, cts.Token).ConfigureAwait(false);

                await package.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
                if (!ReferenceEquals(_detailTarget, target) || _detailErrorId != errorId) return;

                _detailCommand = command;
                DetailCommandSummary.Text = DescribeCommand(command);
                DetailCommandText.Text = command.CommandText ?? command.Problem ?? "";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                try
                {
                    await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                    if (ReferenceEquals(_detailTarget, target) && _detailErrorId == errorId)
                    {
                        DetailCommandSummary.Text = $"Could not decode transaction {seqno}.";
                        DetailCommandText.Text = ex.Message;
                    }
                }
                catch { }
            }
        });
    }

    private static string DescribeCommand(ReplFailingCommand command)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(command.Article)) parts.Add("article " + command.Article);
        if (!string.IsNullOrEmpty(command.SourceObject) || !string.IsNullOrEmpty(command.DestinationObject))
            parts.Add($"{command.SourceObject ?? "?"} → {command.DestinationObject ?? "?"} on the subscriber");
        parts.Add("transaction " + command.XactSeqno);
        if (command.CommandId != null)
            parts.Add(command.CommandsInTransaction > 0 ? $"command {command.CommandId} of {command.CommandsInTransaction}" : $"command {command.CommandId}");
        return string.Join(" · ", parts);
    }

    private void DetailHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetailHistoryGrid.SelectedItem is ReplHistoryRow row && row.HasError && row.ErrorId != _detailErrorId)
        {
            if (_detail != null && _detail.Errors.Any(r => r.Id == row.ErrorId)) ShowErrorChain(row.ErrorId);
            else
            {
                DetailErrorWhen.Text = $"id {row.ErrorId}";
                DetailErrorText.Text = "This error is older than the ones loaded — the pane reads the agent's ten most recent error ids. Load it from the Errors tab.";
            }
        }
    }

    private void DetailClose_Click(object sender, RoutedEventArgs e) => CloseDetail();

    private void CloseDetail()
    {
        _detailCts?.Cancel();
        _commandCts?.Cancel();
        _detailTarget = null;
        _detail = null;
        _detailCommand = null;

        if (DetailRow.ActualHeight > 60) _detailHeight = DetailRow.ActualHeight;
        DetailRow.Height = new GridLength(0);
        DetailPane.Visibility = Visibility.Collapsed;
        DetailSplitter.Visibility = Visibility.Collapsed;
    }

    private void DetailCopy_Click(object sender, RoutedEventArgs e)
    {
        var builder = new StringBuilder();
        builder.AppendLine(DetailTitle.Text);
        if (!string.IsNullOrWhiteSpace(DetailSubtitle.Text)) builder.AppendLine(DetailSubtitle.Text);
        builder.AppendLine();
        builder.AppendLine("--- error " + DetailErrorWhen.Text + " ---");
        builder.AppendLine(DetailErrorText.Text);
        builder.AppendLine();
        builder.AppendLine("--- failing command ---");
        builder.AppendLine(DetailCommandSummary.Text);
        if (!string.IsNullOrWhiteSpace(DetailCommandText.Text)) builder.AppendLine(DetailCommandText.Text);

        if (_detail != null && _detail.History.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("--- agent history (newest first) ---");
            foreach (var row in _detail.History)
                builder.AppendLine($"{row.Time:yyyy-MM-dd HH:mm:ss}\t{row.RunStatusText}\t{(row.HasError ? "error " + row.ErrorId : "")}\t{row.Comments}");
        }

        try
        {
            Clipboard.SetText(builder.ToString());
            StatusText.Text = "Copied the detail pane to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not copy: " + ex.Message;
        }
    }

    private void DetailOpenAsQuery_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var caps = _caps;
        var detail = _detail;
        if (caps == null || detail == null) { StatusText.Text = "Nothing loaded in the detail pane yet."; return; }

        var sql = new StringBuilder();
        if (!string.IsNullOrEmpty(caps.DistributionDatabase)) sql.AppendLine($"USE [{caps.DistributionDatabase.Replace("]", "]]")}];").AppendLine();

        if (_detailErrorId != null)
        {
            sql.AppendLine("-- The whole error chain: one failure writes several rows under the same id.");
            sql.AppendLine($"SELECT e.id, e.time, e.source_name, e.error_code, e.error_text, e.xact_seqno, e.command_id FROM dbo.MSrepl_errors AS e WHERE e.id = {_detailErrorId} ORDER BY e.time;");
            sql.AppendLine();
        }

        string historyTable = _detailAgentType switch
        {
            ReplAgentType.Distribution => "MSdistribution_history",
            ReplAgentType.LogReader => "MSlogreader_history",
            ReplAgentType.Snapshot => "MSsnapshot_history",
            ReplAgentType.Merge => "MSmerge_history",
            _ => null
        };
        if (detail.AgentId != null && historyTable != null)
        {
            sql.AppendLine("-- The agent's recent history.");
            sql.AppendLine($"SELECT TOP (50) * FROM dbo.{historyTable} WHERE agent_id = {detail.AgentId} ORDER BY time DESC;");
            sql.AppendLine();
        }

        var command = _detailCommand;
        if (command != null && ReplQueryService.IsMeaningfulSeqno(command.XactSeqno))
            sql.AppendLine(ReplQueryService.FailingCommandSql(command.XactSeqno, detail.PublisherDatabaseId, command.CommandId));

        OpenTextInNewQueryWindow(sql.ToString());
    }
}
