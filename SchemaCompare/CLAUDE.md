# SchemaCompare (standalone shell)

The compare itself — engine, view, diff, change scripts, the rules — lives in the extension and is documented in
**`SQLExtended/SchemaCompare/CLAUDE.md`**. Read that first. This project is the thin net48 WinExe that runs the same
code without SSMS (for example on the box that runs SchemaExporter), and it holds only what differs outside SSMS.

## What is here

- `App` / `MainWindow` — the app and a frame round the extension's `SchemaCompareView`. `SchemaCompare.exe [left] [right]`
  starts a compare straight away with both given.
- `UI\ShellCompareHost` — the shell's `ICompareHost`.
- `UI\ServerLogins` — `ShellSettings` (the shared `CompareSettings` plus its theme and saved sign-ins), `ServerLogin`
  (DPAPI for a remembered SQL password) and `ConnectWindow`.
- `UI\AppTheme` — the shell's stand-in for the extension's `ThemeManager`.

## Linking, not copying

`SchemaCompare.csproj` links `SQLExtended\SchemaCompare\Engine\*.cs` and `UI\*` (code and XAML), with links that keep the
folder shape (`SchemaCompare\UI\…`) so the XAML's relative `Source="Theme.xaml"` resolves. `SQLExtended\SchemaCompare\Ssms\`
is the extension's half of the seam and is **not** linked. The export, decryption, palette and snapshot files are linked
from the extension as before, with `SCHEMA_EXPORTER` defined. A file added to the extension's `UI\` is picked up here by
the wildcard; it must not reference SSMS types, or this project stops building.

Package versions (ScriptDom, SMO, SqlClient, AvalonEdit, Newtonsoft) track `SQLExtended.csproj`.

## Theme

- **"Match SSMS" reads `%APPDATA%\SQLExtended\SSMS\theme.ini`**, which the extension's `ThemeManager` rewrites whenever it
  installs a palette (`ThemeSnapshot`). A standalone app has no way to ask SSMS for its theme, and the resolved palette
  carries Blue and custom themes exactly. A `FileSystemWatcher` follows it live. With no file (extension not installed,
  or SSMS not opened since), it follows the Windows app theme (`AppsUseLightTheme`). Light and Dark force the built-in
  columns.
- `UI\Theme.xaml` is merged application-wide here (the shell's own app), which `ConnectWindow` relies on; the view and
  the script window also merge it themselves, as they must inside SSMS.

## Errors

Unhandled UI exceptions are logged to `%APPDATA%\SQLExtended\SchemaCompare\error.log` and shown, not fatal.
