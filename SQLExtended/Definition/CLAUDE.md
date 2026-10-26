# Open Definition

`OpenDefinitionCommand` (F12 in a query window, issue #6) scripts the object under the cursor into a new query window,
skipping the View Schema preview; the preview's **Open in Script Window** button ends up in the same `OpenScript`.

- **The script is View Schema's** (`SchemaQueryService.GetSchemaScript`), so tables come with indexes and foreign keys
  and encrypted modules follow the decryption setting. The object is resolved first (`SchemaQueryService.ResolveObject`)
  so an unqualified procedure name works — View Schema's own `ResolveSchema` used to know only tables and views, and
  now resolves every scriptable type, default schema before `dbo`.
- **The window is opened connected, through `QueryWindowOpener`**: `ScriptFactory.CreateNewBlankScript(ScriptType.Sql,
  UIConnectionInfo, null)` by reflection, with the source window's `UIConnectionInfo` taken *before* the background
  query (the active window can change while it runs). Every other "open in a new window" in the extension writes a temp
  `.sql` file, which leaves the connection to SSMS; that is still the fallback, and the status bar says the window may
  not be connected. The text is inserted only if the factory left a *new* document active — writing it into whatever
  happens to be active could land it in the user's own script.
- **A `USE [db]` + `GO` heads the script** either way; that, not the connection, is what pins the database.
- **The preview may belong to another server** (SQL Search and Validate open previews for any server), so the active
  window's connection is used only when its server matches the preview's.
- **`DefinitionScriptMode`** (Settings → Search → Open Definition, default `CREATE OR ALTER`) rewrites the module's
  first `CREATE` outside comments and strings — a stored header comment saying "create procedure" is common and must not
  be the one rewritten. `DefinitionScript` is linked into the tests (`SQLExtended.Tests/Definition/DefinitionScriptTests.cs`).
- F12 is bound in the SQL Query Editor scope only; Global F12 stays the shell's Edit.GoToDefinition.
