using Microsoft.VisualStudio.Shell;
using SQLExtended.Formatting;
using SQLExtended.Settings;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended;

/// <summary>
/// Format With… (issue #8), Format Preset 1–5, and Uppercase Keywords / Remove Brackets / Insert Semicolons — single
/// Format With rules on their own commands. Format With… opens <see cref="FormatWithWindow"/> to tick rules for one
/// run or manage the presets; each Format Preset n applies the n-th saved preset with no dialog, which is what lets a
/// preset have its own shortcut. The presets have no default shortcuts and no menu entries — they are bound under
/// Tools › Options › Keyboard (<c>SQLExtended.FormatPreset1</c>…) — so the dialog says so.
/// </summary>
internal sealed class FormatWithCommand
{
    public const int FormatWithCommandId = 0x0260;
    public const int FormatPreset1CommandId = 0x0261;
    public const int PresetSlots = 5;

    // Single rules on their own commands, at SQL Prompt's chords (Ctrl+B, Ctrl+U / B / C) so its users find them.
    public const int UppercaseKeywordsCommandId = 0x0270;
    public const int RemoveBracketsCommandId = 0x0271;
    public const int InsertSemicolonsCommandId = 0x0272;

    private static readonly (int Id, string Title, FormatPreset Rules)[] SingleRules =
    {
        (UppercaseKeywordsCommandId, "Uppercase Keywords", new FormatPreset { KeywordCase = CasingOption.Upper, FunctionCase = CasingOption.Upper }),
        (RemoveBracketsCommandId, "Remove Brackets", new FormatPreset { RemoveBrackets = true }),
        (InsertSemicolonsCommandId, "Insert Semicolons", new FormatPreset { AddSemicolons = true }),
    };

    private const string Title = "Format With";

    private readonly AsyncPackage _package;

    private FormatWithCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        commandService.AddCommand(new MenuCommand(ExecuteFormatWith, new CommandID(FormatCommand.CommandSet, FormatWithCommandId)));
        for (int slot = 0; slot < PresetSlots; slot++)
        {
            int index = slot;
            commandService.AddCommand(new MenuCommand((s, e) => ExecutePreset(index), new CommandID(FormatCommand.CommandSet, FormatPreset1CommandId + slot)));
        }
        foreach (var (id, title, rules) in SingleRules)
            commandService.AddCommand(new MenuCommand((s, e) => Apply(rules, title), new CommandID(FormatCommand.CommandSet, id)));
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            _ = new FormatWithCommand(package, commandService);
    }

    private void ExecuteFormatWith(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // The dialog is shown first, before the text is read: nothing about the window changes while it is open.
        var window = new FormatWithWindow();
        if (window.ShowDialog() != true || window.Chosen == null)
            return;

        Apply(window.Chosen, string.IsNullOrEmpty(window.Chosen.Name) ? Title : $"{Title} \"{window.Chosen.Name}\"");
    }

    private void ExecutePreset(int index)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var presets = SQLExtendedSettings.Load().GetFormatPresets();
        if (index >= presets.Count)
        {
            FormatSelectionCommand.SetStatusBarText($"Format Preset {index + 1}: no preset saved in that slot — open SQLExtended › Format With… to save one.");
            return;
        }

        Apply(presets[index], $"Format Preset {index + 1} \"{presets[index].Name}\"");
    }

    private void Apply(FormatPreset preset, string title) =>
        FormatSelectionCommand.Run(_package, title, (text, start, end) => PresetFormatter.Format(text, start, end, preset, FormatterOptions.Load()));
}
