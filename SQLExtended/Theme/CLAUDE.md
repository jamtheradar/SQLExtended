# Theme — the dark/light palette for the extension's own WPF chrome

Covers every window, tool window and dialog the extension draws itself. It does **not** cover editor
colouring (comment tags, rainbow parens — those write Fonts and Colors, see `Comments/` and `Rainbow/`) or
the results grid's Find tint (GDI, picks its pair from the cell's own background).

## Files

- `ThemePalette.cs` — pure: ~45 roles, each `(Key, Dark, Light)`, plus `Resolve`, which merges in the
  shell's colours. Linked into the test project.
- `ThemeManager.cs` — reads the SSMS theme's own colours for the chrome roles (`ShellRoles`: tool-window
  background/text/border, header, button, text-box border, tree selection), resolves, builds one
  `ResourceDictionary` of frozen brushes into `Application.Current.Resources.MergedDictionaries`. Rebuilds on
  every `VSColorTheme.ThemeChanged` (skipped if no resolved colour changed) and raises `Changed`.
- `TsqlHighlighting.cs` — the AvalonEdit T-SQL definition for all five SQL previews. The light variant is the
  embedded `Search/TsqlDarkHighlighting.xshd` with its foregrounds string-swapped before parsing.
- `DarkCombo.xaml` — the full ComboBox template (see its header for why setters alone are broken). Name kept
  for its references; it is themed like everything else.
- `ThemeSnapshot.cs` — pure: the resolved palette as `key=value` lines. `ThemeManager` writes it to
  `%APPDATA%\SQLExtended\SSMS\theme.ini` on every palette install (temp file, then replace), and SchemaCompare (outside
  SSMS: the standalone shell's `SchemaCompare/UI/AppTheme.cs`) reads and watches it to match the SSMS theme. `ThemePalette.cs` and this file
  are linked into that shell. Schema Compare's own XAML now lives in `SQLExtended/SchemaCompare/UI/` and is scanned by
  `ThemePaletteTests` like the rest. Linked into the tests.

## Where each colour comes from

1. **The selected SSMS theme**, for the roles in `ThemeManager.ShellRoles`. This is what makes Blue or a
   custom theme match exactly. A key the theme leaves transparent, or that throws, is skipped per role.
2. **Derived from the theme's surface and text** for shades no theme key covers (alt surface, alt rows, row
   hover, hairlines) — `ThemePalette.Derived`, tuned so the stock dark theme lands on the old dark column.
3. **The built-in dark or light column** (chosen by the theme's brightness) for everything else: status
   (good/warn/bad), syntax accents, headings, muted text. No VS theme defines these.
4. **Guards** (`ThemePalette.Guards`): pairs we compose ourselves are held to AA and fall back to the built-in
   pair if a theme breaks them. The theme's own designed pair (selection) is trusted to 2.5:1, because
   VS Light/Blue ship white on #3399FF at 2.9:1 and overruling it would stop us matching.

## Rules

- **Never write a colour literal in XAML.** Use `{DynamicResource SqlxRole}`. `ThemePaletteTests` fails on any
  `="#…"` outside a comment, and on any `Sqlx…` key the palette does not define. A missing key is silent in
  WPF — the property just stays unset — which is why that test exists.
- **Pick the role by what it paints, not by the dark hex.** `#333337` was both a fill (`SqlxControl`) and a hairline
  (`SqlxBorderSubtle`); `#FFFFFF` text is `SqlxSelectionText` on a selection, `SqlxOnAccent` on an accent fill, and
  `SqlxTextStrong` otherwise. Getting this wrong is invisible in dark and white-on-white in light.
- **StaticResource does not work for these keys.** The palette is swapped at runtime; only `DynamicResource`
  and `SetResourceReference` follow it.
- **Code-built UI:** use `element.SetResourceReference(prop, "SqlxRole")`. `ThemeManager.Get(key)` returns the
  *current* brush as a value — fine for converters and short-lived popups (Quick Info), but it will not follow
  a switch. Converter-coloured cells in the monitors pick up the new variant the next time the binding re-evaluates.
- **Why application resources, not a per-control merge:** context menus, combo popups and DataGrid columns sit
  outside the visual tree and would miss a dictionary merged on the UserControl. Keys are `Sqlx`-prefixed so
  nothing of the shell's is shadowed. The swap replaces the one merged dictionary — one resource walk — rather
  than rewriting keys one by one, each of which re-resolves every window in the shell.
- **New light values must clear 4.5:1** on the surface they sit on; add the pair to
  `LightText_IsReadable_OnItsSurface` when introducing a text role.

## Not yet observed

Written and unit-tested but not yet watched in a running SSMS: which values the `ShellRoles` keys actually
return in SSMS 22's themes (the session log records the surface/text it installed), the live switch (Tools >
Theme while windows are open), and how each window reads in each theme. In particular `ToolWindowTabSelectedTab`
as the accent and `ButtonPressed` as the pressed fill are best guesses at the closest key.
