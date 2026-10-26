using EnvDTE;
using Microsoft.VisualStudio.CommandBars;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended;

/// <summary>
/// Adds the top-level "SQLExtended" menu to the SSMS main menu bar at runtime.
///
/// SSMS 22's shell does not reliably merge the VSCT-declared top-level menu (the command table
/// merges without error yet the menu never renders, and only appeared once under a debugger-forced
/// rebuild). Rather than keep fighting the static command-table merge, we build the menu against the
/// live menu model via DTE <see cref="CommandBars"/> — the same "inject at runtime" approach
/// <see cref="ObjectExplorer.ObjectExplorerMenuService"/> uses for the Object Explorer tree.
///
/// Each item routes to the command that is already registered on the package's command service
/// (by <c>*Command.InitializeAsync</c>), so the existing handlers are reused as-is. Everything is
/// wrapped in try/catch and polls for the menu bar, so a failure here never crashes SSMS.
/// </summary>
internal static class MainMenuService
{
    private static readonly Guid CommandSet = new Guid("a1b2c3d4-e5f6-7890-abcd-123456789abc");
    private const string MenuCaption = "SQLExtended";
    private const string MenuBarName = "MenuBar";

    // Keep the event sinks alive: CommandBarEvents are held only weakly by the automation model, so
    // without a strong reference the GC collects them and the Click handlers silently stop firing.
    private static readonly List<CommandBarEvents> _eventSinks = new List<CommandBarEvents>();

    private static AsyncPackage _package;
    private static OleMenuCommandService _commandService;
    private static bool _built;

    // Items in display order. A null caption inserts a separator before the next item. Groups run from the
    // query editor outwards: editor actions, tool windows, results grid, monitoring, cache, servers, settings.
    // Keep SsmsSchemaViewerPackage.vsct's SQLExtended menu groups in the same order.
    private static readonly (string Caption, int CommandId)[] Items =
    {
        // Query editor
        ("View Schema", 0x0100),
        ("Open Definition", 0x0110),
        ("Locate in Object Explorer", 0x0120),
        ("Format SQL", 0x0200),
        ("Format Selection", 0x0250),
        ("Format With...", 0x0260),
        ("Uppercase Keywords", 0x0270),
        ("Remove Brackets", 0x0271),
        ("Insert Semicolons", 0x0272),
        ("Qualify Columns", 0x0230),
        ("Align On...", 0x0240),
        ("Parse Statistics", 0x0d00),
        (null, 0),
        // Tool windows
        ("SQL Search", 0x0400),
        ("SQL History", 0x0700),
        ("Script Library", 0x0900),
        ("Validate Schema References", 0x0a00),
        (null, 0),
        // Results grid
        ("Find in Results", 0x0c20),
        ("Go to Column...", 0x0c30),
        ("Grid Aggregates", 0x0c10),
        ("Copy as IN Filter", 0x0c40),
        ("Script Results as INSERT", 0x0c00),
        (null, 0),
        // Monitoring dashboards
        ("Performance Monitor", 0x0f00),
        ("Agent Jobs", 0x0f10),
        ("Always On Monitor", 0x0e00),
        ("Replication Monitor", 0x0f20),
        (null, 0),
        // Schema cache
        ("Schema Cache...", 0x0330),
        ("Refresh Schema Cache", 0x0300),
        ("Refresh All Databases", 0x0310),
        ("Clear Schema Cache", 0x0320),
        (null, 0),
        // Servers and connections
        ("Environment Tabs...", 0x0505),
        ("Regroup Servers Now", 0x0b00),
        (null, 0),
        // Settings
        ("SQLExtended Settings...", 0x0500),
        ("Formatter...", 0x0210),
        ("Snippets...", 0x0220),
        ("Check for Updates...", 0x0800),
    };

    /// <summary>
    /// Resolves the main menu bar and injects the SQLExtended menu. The menu bar may not be ready when
    /// the package loads, so we poll for a short while before giving up.
    /// </summary>
    public static async Task InitializeAsync(AsyncPackage package)
    {
        _package = package;
        _commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

        for (int attempt = 0; attempt < 20 && !_built; attempt++)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (TryBuild())
                return;
            await Task.Delay(1000);
        }
    }

    private static bool TryBuild()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (!(Package.GetGlobalService(typeof(SDTE)) is DTE dte))
                return false;

            var commandBars = dte.CommandBars as CommandBars;
            var menuBar = commandBars?[MenuBarName];
            if (menuBar == null)
                return false;

            // Idempotent: remove any SQLExtended popup we (or a prior session) left behind.
            for (int i = menuBar.Controls.Count; i >= 1; i--)
            {
                var existing = menuBar.Controls[i];
                if (string.Equals(existing.Caption, MenuCaption, StringComparison.Ordinal))
                    existing.Delete(false);
            }

            var popup = (CommandBarPopup)menuBar.Controls.Add(
                MsoControlType.msoControlPopup, Type.Missing, Type.Missing, Type.Missing, true);
            popup.Caption = MenuCaption;

            foreach (var item in Items)
            {
                if (item.Caption == null)
                {
                    // Next added button starts a new group (separator line above it).
                    _pendingSeparator = true;
                    continue;
                }

                var button = (CommandBarButton)popup.CommandBar.Controls.Add(
                    MsoControlType.msoControlButton, Type.Missing, Type.Missing, Type.Missing, true);
                button.Caption = item.Caption;
                button.Style = MsoButtonStyle.msoButtonCaption;
                // A runtime-built button shows no shortcut of its own, and the shortcut is how most of these are
                // reached - without it the menu is where people never learn there is one.
                string shortcut = ShortcutText(dte, item.CommandId);
                if (shortcut != null)
                    button.ShortcutText = shortcut;
                if (_pendingSeparator)
                {
                    button.BeginGroup = true;
                    _pendingSeparator = false;
                }

                int commandId = item.CommandId;
                var sink = (CommandBarEvents)dte.Events.CommandBarEvents[button];
                sink.Click += (object ctrl, ref bool handled, ref bool cancelDefault) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    try { _commandService?.GlobalInvoke(new CommandID(CommandSet, commandId)); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SQLExtended] menu invoke {commandId:X} failed: {ex}"); }
                    handled = true;
                };
                _eventSinks.Add(sink);
            }

            _built = true;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SQLExtended] MainMenuService.TryBuild failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Every command on the menu with its current shortcuts, in menu order — what the Settings dialog's Keyboard Shortcuts
    /// tab lists, so the tab and the menu cannot disagree. A command with no binding has an empty list.
    /// Format Preset 1–5 follow Format With…: they are on no menu (they exist to be bound to a key), so this tab is the one
    /// place that shows which preset each slot holds and what, if anything, it is bound to.
    /// </summary>
    internal static List<(string Caption, IReadOnlyList<string> Shortcuts)> CommandShortcuts()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
        var rows = new List<(string, IReadOnlyList<string>)>();
        IReadOnlyList<string> Bound(int id) => dte == null ? Array.Empty<string>() : Shortcuts(dte, id);

        foreach (var item in Items)
        {
            if (item.Caption == null)
                continue;
            rows.Add((item.Caption, Bound(item.CommandId)));

            if (item.CommandId == FormatWithCommand.FormatWithCommandId)
            {
                var presets = Settings.SQLExtendedSettings.Load().GetFormatPresets();
                for (int slot = 0; slot < FormatWithCommand.PresetSlots; slot++)
                {
                    string name = slot < presets.Count ? presets[slot].Name : "(no preset saved)";
                    rows.Add(($"Format Preset {slot + 1} — {name}", Bound(FormatWithCommand.FormatPreset1CommandId + slot)));
                }
            }
        }
        return rows;
    }

    /// <summary>The first of <see cref="Shortcuts"/>, for the menu item; null when the command has none.</summary>
    private static string ShortcutText(DTE dte, int commandId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var shortcuts = Shortcuts(dte, commandId);
        return shortcuts.Count > 0 ? shortcuts[0] : null;
    }

    /// <summary>
    /// The command's shortcuts as SSMS currently binds them ("Ctrl+K, Ctrl+Y"), read rather than hard-coded so a user's own
    /// reassignment shows. Query-editor scope first, as it wins at the keyboard; a chord bound in several scopes appears once.
    /// Within that scope a chord bound nowhere else leads, rather than whatever order SSMS returns the bindings in.
    /// </summary>
    private static IReadOnlyList<string> Shortcuts(DTE dte, int commandId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var editor = new List<string>();
        var other = new List<string>();
        try
        {
            if (dte.Commands.Item(CommandSet.ToString("B"), commandId)?.Bindings is not object[] bindings)
                return editor;

            foreach (var binding in bindings)
            {
                // "Scope::Keys", e.g. "SQL Query Editor::Ctrl+K, Ctrl+A" or "Global::Ctrl+Shift+D"
                string text = binding as string;
                int split = text?.IndexOf("::", StringComparison.Ordinal) ?? -1;
                if (split < 0)
                    continue;
                string keys = text.Substring(split + 2);
                (text.StartsWith("SQL Query Editor", StringComparison.OrdinalIgnoreCase) ? editor : other).Add(keys);
            }
        }
        catch
        {
            // Fall through with whatever was read
        }
        var ordered = editor.Distinct().OrderBy(keys => other.Contains(keys)).ToList();
        foreach (var keys in other)
        {
            if (!ordered.Contains(keys))
                ordered.Add(keys);
        }
        return ordered;
    }

    [ThreadStatic]
    private static bool _pendingSeparator;
}
