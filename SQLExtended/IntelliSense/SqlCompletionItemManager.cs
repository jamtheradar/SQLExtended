using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SQLExtended.IntelliSense;

/// <summary>
/// MEF-exported provider for the SQL completion item manager. Runs before the
/// default so our ranking takes precedence for SQL buffers.
/// </summary>
[Export(typeof(IAsyncCompletionItemManagerProvider))]
[Name("SQLExtended SQL Completion Item Manager")]
[ContentType("SQL")]
[Order(Before = "default")]
internal sealed class SqlCompletionItemManagerProvider : IAsyncCompletionItemManagerProvider
{
    public IAsyncCompletionItemManager GetOrCreate(ITextView textView)
        => textView.Properties.GetOrCreateSingletonProperty(() => new SqlCompletionItemManager());
}

/// <summary>
/// Filters and ranks completion items with a preference for exact object-name
/// matches. Without this, when typing "TimesheetItem" both "TimesheetItem" and
/// "TimesheetItem_BU" score as substring matches and the default item manager
/// can pick the wrong one as the preselected best match.
/// </summary>
internal sealed class SqlCompletionItemManager : IAsyncCompletionItemManager
{
    public Task<ImmutableArray<CompletionItem>> SortCompletionListAsync(
        IAsyncCompletionSession session,
        AsyncCompletionSessionInitialDataSnapshot data,
        CancellationToken token)
    {
        var sorted = data.InitialItemList
            .OrderBy(i => i.SortText ?? i.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return Task.FromResult(sorted);
    }

    public Task<FilteredCompletionModel> UpdateCompletionListAsync(
        IAsyncCompletionSession session,
        AsyncCompletionSessionDataSnapshot data,
        CancellationToken token)
    {
        string typedText = session.ApplicableToSpan.GetText(data.Snapshot) ?? string.Empty;

        // Read once per update, not per item. This runs on a worker thread, where
        // SQLExtendedSettings.Current must not be faulted in (Diagnostics/CLAUDE.md) - it cannot be here,
        // because SqlCompletionSource.InitializeCompletion read it on the UI thread to open this session.
        bool camelCase = Settings.SQLExtendedSettings.Current.CamelCaseMatching;

        // Score every item against the typed text
        var scored = data.InitialSortedItemList
            .Select(item => new { Item = item, Score = Score(item, typedText, camelCase) })
            .Where(x => x.Score.Matched)
            .ToList();

        if (scored.Count == 0)
        {
            // Nothing matches the typed text — dismiss so a commit char doesn't
            // accept some unrelated first item. The user can re-trigger completion
            // (Ctrl+Space) if they want to see the full list again.
            session.Dismiss();
            return Task.FromResult(new FilteredCompletionModel(
                ImmutableArray<CompletionItemWithHighlight>.Empty, 0, data.SelectedFilters,
                UpdateSelectionHint.SoftSelected, centerSelection: false, uniqueItem: null));
        }

        // Pick the best-ranked item (lowest rank; tiebreak by sortText)
        int bestIdx = 0;
        for (int i = 1; i < scored.Count; i++)
        {
            int cmp = scored[i].Score.Rank.CompareTo(scored[bestIdx].Score.Rank);
            if (cmp < 0 || (cmp == 0 && string.Compare(
                scored[i].Item.SortText ?? scored[i].Item.DisplayText,
                scored[bestIdx].Item.SortText ?? scored[bestIdx].Item.DisplayText,
                StringComparison.OrdinalIgnoreCase) < 0))
            {
                bestIdx = i;
            }
        }

        var filtered = scored
            .Select(x => new CompletionItemWithHighlight(x.Item))
            .ToImmutableArray();

        // Hard-select only when the user has typed the start of the best item's name. With nothing typed, or when
        // the best item merely contains the typed text (FROM inside DATEFROMPARTS, issue #11), soft-select so
        // finishing the word with Space does not accept it.
        var best = scored[bestIdx].Item;
        var hint = CompletionCommitRules.IsWordStartMatch(typedText, best.DisplayText, best.FilterText)
            ? UpdateSelectionHint.Selected
            : UpdateSelectionHint.SoftSelected;

        return Task.FromResult(new FilteredCompletionModel(
            filtered, bestIdx, data.SelectedFilters,
            hint, centerSelection: false, uniqueItem: null));
    }

    private struct MatchScore
    {
        public bool Matched;
        public int Rank; // lower is better
    }

    /// <summary>
    /// Ranks an item against typed text. Lower rank wins.
    /// Exact name match beats prefix match beats substring match beats a camelCase hump match.
    /// </summary>
    private static MatchScore Score(CompletionItem item, string typed, bool camelCase)
    {
        // Empty query — everything matches, keep original sort order
        if (string.IsNullOrEmpty(typed))
            return new MatchScore { Matched = true, Rank = 100 };

        string display = item.DisplayText ?? string.Empty;
        string filter = item.FilterText ?? display;

        // The "primary name" is the part after the last dot (e.g., "dbo.Table" → "Table").
        string primaryName = display;
        int dotIdx = display.LastIndexOf('.');
        if (dotIdx >= 0 && dotIdx + 1 < display.Length)
            primaryName = display.Substring(dotIdx + 1);

        // 0: exact match on display text (e.g., typed "dbo.TimesheetItem")
        if (string.Equals(display, typed, StringComparison.OrdinalIgnoreCase))
            return new MatchScore { Matched = true, Rank = 0 };

        // 1: exact match on primary name (e.g., typed "TimesheetItem" → "TimesheetItem" wins over "TimesheetItem_BU")
        if (string.Equals(primaryName, typed, StringComparison.OrdinalIgnoreCase))
            return new MatchScore { Matched = true, Rank = 1 };

        // 2: prefix match on display text
        if (display.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            return new MatchScore { Matched = true, Rank = 2 };

        // 3: prefix match on primary name
        if (primaryName.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            return new MatchScore { Matched = true, Rank = 3 };

        // 4: substring in primary name
        if (primaryName.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0)
            return new MatchScore { Matched = true, Rank = 4 };

        // 5: substring anywhere in filter text
        if (filter.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0)
            return new MatchScore { Matched = true, Rank = 5 };

        // 6: camelCase hump match ("OD" -> "OrderDetails", "od" -> "order_details"). Last, and only when the
        // setting is on: it is the loosest rule here, and every item it admits is one the five rules above
        // already rejected, so it can only ever add to the bottom of the list. Off, a typed string matching
        // nothing still dismisses the session - the pre-existing behaviour, which must not change for anyone
        // who did not ask for hump matching.
        if (camelCase && SqlCompletionContext.IsCamelCaseMatch(typed, primaryName))
            return new MatchScore { Matched = true, Rank = 6 };

        return new MatchScore { Matched = false };
    }
}
