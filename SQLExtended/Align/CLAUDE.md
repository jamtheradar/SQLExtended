# Align On

Issue #8, the vertical-alignment half. `Align/AlignOnCommand.cs` (Ctrl+=, the editor's right-click menu, the
SQLExtended menu) asks for a token in `AlignOnWindow` and pads the lines with `ColumnAligner`, which is pure and
linked into the tests (`SQLExtended.Tests/Align/ColumnAlignerTests.cs`).

- **A text pass, not a formatter rule, on purpose.** The formatter regenerates the whole script from ScriptDom's AST,
  so nothing in it can be applied "on its own" to a few lines; and it needs SQL that parses. Align On touches only the
  whitespace in front of the token and works on any fragment. If it is ever offered as a formatter option, run it as
  the last pass over `PostProcessor`'s output, never inside it.
- **Every rule in `ColumnAligner` keeps it off a token that only looks like the one asked for**, and getting one wrong
  still leaves SQL that runs, so only the text shows it: strings, `[brackets]`, quoted identifiers and comments are
  skipped with their state carried across lines; words match whole words; `=` does not match inside `>=`/`+=`;
  parentheses opened on the same line are skipped (the alias AS, not the CAST's) — except in every-occurrence mode,
  which is what lines up the commas in `VALUES (…)` rows; a token that starts its line is passed over. `--` is the
  one token that matches *in* a comment: it means the start of a trailing comment.
- **The prefix is trimmed and re-padded, never added to**, so aligning twice is a no-op and a block aligned too far out
  comes back in. Each line keeps its own gap (one space if it had whitespace before the token, none if not), which is
  what keeps a tight comma tight instead of producing `(200 ,`.
- **Tabs follow the editor**, read from the active WPF view's `ConvertTabsToSpaces`/`TabSize` options, not
  `FormatterOptions.IndentStyle` — the issue asks for the editor's setting. With tabs the column is rounded up to a
  tab stop. Falls back to spaces and 4 (logged) if the view cannot be reached.
- **No selection means the block around the caret** (the run of non-blank lines), as CodeAlignment does. A whole-line
  selection ends at column 1 of the next line, which is dropped. After aligning, the block is left selected so the
  next token is one Ctrl+= away.
- **Ctrl+= is CodeAlignment's chord** (`VK_OEM_PLUS`), bound in the SQL Query Editor scope only. Not yet checked under
  Tools > Options > Keyboard on a reset scheme.

Format Selection is in `Formatting/SelectionFormatter.cs` (see `Formatting/CLAUDE.md`). Not done from the issue yet:
Format With… and presets with shortcuts.
