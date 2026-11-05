using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SQLExtended.Snippets;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;

namespace SQLExtended.IntelliSense;

/// <summary>
/// MEF-exported provider that creates <see cref="SqlCompletionCommitManager"/> instances.
/// The commit manager intercepts snippet completion commits and starts a custom
/// tab-stop session for interactive placeholder editing.
/// </summary>
[Export(typeof(IAsyncCompletionCommitManagerProvider))]
[Name("SQLExtended SQL Completion Commit")]
[ContentType("SQL")]
internal sealed class SqlCompletionCommitManagerProvider : IAsyncCompletionCommitManagerProvider
{
    public IAsyncCompletionCommitManager GetOrCreate(ITextView textView)
    {
        SqlCompletionSource.DebugLog("[CommitManager] GetOrCreate called");
        return textView.Properties.GetOrCreateSingletonProperty(
            () => new SqlCompletionCommitManager(textView));
    }
}

/// <summary>
/// Custom commit manager that intercepts snippet completion items with custom placeholders
/// and starts a <see cref="SnippetSession"/> for tab-stop navigation.
/// Non-snippet items fall through to default commit behavior.
/// </summary>
internal sealed class SqlCompletionCommitManager : IAsyncCompletionCommitManager
{
    /// <summary>Property key set on CompletionItems that should trigger expansion.</summary>
    internal const string SnippetExpansionKey = "IsSnippetExpansion";

    /// <summary>Property key set on ALL snippet CompletionItems (expansion or not).</summary>
    internal const string IsSnippetKey = "IsSnippet";

    private static readonly char[] CommitChars = { '\t', '\n', '\r', ' ' };

    private readonly ITextView _textView;

    public SqlCompletionCommitManager(ITextView textView)
    {
        _textView = textView;
    }

    public IEnumerable<char> PotentialCommitCharacters => CommitChars;

    public bool ShouldCommitCompletion(
        IAsyncCompletionSession session,
        SnapshotPoint location,
        char typedChar,
        CancellationToken token)
    {
        // Honor the user's choice of which keys accept a completion. A disabled key
        // returns false so it types through normally and leaves the session open.
        var settings = Settings.SQLExtendedSettings.Current;
        switch (typedChar)
        {
            case '\t' when !settings.CommitOnTab:
            case '\n' when !settings.CommitOnEnter:
            case '\r' when !settings.CommitOnEnter:
            case ' ' when !settings.CommitOnSpace:
                return false;
        }

        if (typedChar == ' ')
        {
            try
            {
                var selectedItem = session.GetComputedItems(token)?.SelectedItem;
                if (selectedItem != null)
                {
                    // Snippets must only be committed by Tab/Enter, never by Space.
                    // This prevents partial SQL keywords (e.g. "FROM" in "SELECT * FROM")
                    // from accidentally committing a snippet whose title contains that word.
                    if (selectedItem.Properties.TryGetProperty(IsSnippetKey, out bool isSnippet) && isSnippet)
                        return false;

                    // Multi-word items, a word that only occurs inside the item's name, and a complete
                    // reserved word are never replaced by Space (issue #11). Tab/Enter still commit.
                    // Checked here as well as by soft selection in the item manager, so it holds even
                    // if the editor commits a soft-selected item.
                    string typed = session.ApplicableToSpan.GetText(location.Snapshot) ?? string.Empty;
                    if (!CompletionCommitRules.SpaceMayCommit(typed, selectedItem.DisplayText, selectedItem.FilterText))
                        return false;
                }
            }
            catch { }
        }

        return true;
    }

    public CommitResult TryCommit(
        IAsyncCompletionSession session,
        ITextBuffer buffer,
        CompletionItem item,
        char typedChar,
        CancellationToken token)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SqlCompletionSource.DebugLog($"[CommitManager] TryCommit for '{item.DisplayText}'");

        if (TryCommitQuotedLiteral(session, buffer, item))
            return CommitResult.Handled;

        // Resolve the snippet to expand. Two sources:
        //   1. Stored-procedure items — the "@param = value" call is built on demand from the
        //      cached parameters carried on the item.
        //   2. Snippet items with custom placeholders — looked up by their trigger code.
        // Anything else falls through to the editor's default (plain insert-text) commit.
        SqlSnippet snippet;
        if (item.Properties.TryGetProperty(ProcParameterExpansion.InfoKey, out ProcParameterExpansion.Info procInfo))
        {
            snippet = ProcParameterExpansion.Build(procInfo);
            if (snippet == null)
                return CommitResult.Unhandled; // no parameters — let the default insert just the name
            SqlCompletionSource.DebugLog($"[CommitManager] Detected procedure expansion: {item.DisplayText}");
        }
        else if (item.Properties.TryGetProperty(SnippetExpansionKey, out bool isExpansion) && isExpansion)
        {
            SqlCompletionSource.DebugLog($"[CommitManager] Detected snippet expansion: {item.DisplayText}");

            snippet = SnippetManager.Instance.FindByCode(item.DisplayText);
            if (snippet == null)
            {
                SqlCompletionSource.DebugLog($"[CommitManager] Snippet not found by code");
                return CommitResult.Unhandled;
            }
        }
        else
        {
            return CommitResult.Unhandled;
        }

        // Get the span to replace (the typed prefix)
        var applicableSpan = session.ApplicableToSpan.GetSpan(buffer.CurrentSnapshot);
        SqlCompletionSource.DebugLog($"[CommitManager] Replacing span [{applicableSpan.Start}..{applicableSpan.End}] = '{applicableSpan.GetText()}'");

        // Start a custom tab-stop session
        var snippetSession = new SnippetSession(_textView);
        bool started = false;

        try
        {
            started = snippetSession.Start(snippet, applicableSpan);
        }
        catch (Exception ex)
        {
            SqlCompletionSource.DebugLog($"[CommitManager] Session start failed: {ex.Message}");
        }

        if (started)
        {
            // Store the session on the text view so the key processor can find it
            _textView.Properties.RemoveProperty(typeof(SnippetSession));
            _textView.Properties.AddProperty(typeof(SnippetSession), snippetSession);

            SqlCompletionSource.DebugLog("[CommitManager] Snippet session started, returning Handled");
            return CommitResult.Handled;
        }

        SqlCompletionSource.DebugLog("[CommitManager] Session start returned false, falling back");
        return CommitResult.Unhandled;
    }

    /// <summary>
    /// An item inserted as its own quoted literal (a USE HINT name) committed inside a pair
    /// <see cref="QuotePairCommandFilter"/> opened: the span already includes the typed opening quote, and the
    /// closing one sits just past it. The default commit would leave that one behind - <c>'DISABLE_…''</c> - so
    /// it is replaced along with the span.
    /// </summary>
    private bool TryCommitQuotedLiteral(IAsyncCompletionSession session, ITextBuffer buffer, CompletionItem item)
    {
        string insert = item.InsertText;
        if (insert == null || insert.Length < 2 || insert[0] != '\'' || insert[insert.Length - 1] != '\'')
            return false;

        var snapshot = buffer.CurrentSnapshot;
        var span = session.ApplicableToSpan.GetSpan(snapshot);
        if (span.Length == 0 || snapshot[span.Start] != '\'' || span.End.Position >= snapshot.Length || snapshot[span.End] != '\'')
            return false;

        using (var edit = buffer.CreateEdit())
        {
            edit.Replace(new Span(span.Start, span.Length + 1), insert);
            snapshot = edit.Apply();
        }
        _textView.Caret.MoveTo(new SnapshotPoint(snapshot, span.Start.Position + insert.Length));
        return true;
    }
}
