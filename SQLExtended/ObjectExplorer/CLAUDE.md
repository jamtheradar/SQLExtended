# Object Explorer context menu

`ObjectExplorer/ObjectExplorerMenuService.cs` appends a "SQLExtended" submenu to OE node menus through a runtime
reflection hook (`TreeView.ContextMenuStripChanged`), not the VSCT — SSMS builds those menus dynamically and there
is no stable `IDM_OBJEXPL_*` GUID to place against. Node kind and names come from `INodeInformation.UrnPath` and
`NavigationContext`, parsed in `ObjectExplorerHelper.GetNodeContext`.

For the monitoring dashboards this is the **only launch point that needs no query window**: the node carries its own
connection (`NodeContext.ConnectionString`), so any server connected in Object Explorer can be pinned. All four
commands take `Show(package, connectionString, serverLabel)`. The server node offers all four; the Always On and
replication subtrees offer their own dashboard plus Performance.

**`KindFromUrnPath` matches the Always On and replication subtrees by substring, not by exact path, deliberately.**
Hierarchy nodes have predictable paths (`Server/JobServer/Job`, from the node's `Xpath`), and a folder *with* a
`UniqueName` contributes that name plus "Folder" (`UniqueName=Jobs` → `Server/JobServer/JobsFolder`). But the
folders these two subtrees hang off declare **no `UniqueName` at all** — `<Object name='AvailabilityGroups'
base='Folder'>` in ObjectExplorer.dll's embedded `sqlexplorerhier.xml`, `<Object name='Replication' base='Folder'>`
in its `objectexplorerreplication.xml` — so the segment is whatever the node builder emits at runtime and only a
live SSMS will confirm it. Substring matching also means every node in the subtree (folder, group, publication,
subscription) offers the dashboard, and a renamed folder in a future SSMS does not silently drop the menu. The
substring rules run *after* the exact switch so they can never shadow a more specific kind. Guessing an exact path
here fails silently as "the menu never appears", which is why the guaranteed entry point is the server node.

To read those embedded resources: `Assembly.LoadFrom` the DLL and `GetManifestResourceStream` the `*.xml` names —
no decompiler needed, and it is how the folder question above was settled.

## Locate in Object Explorer

`ObjectExplorer/LocateInObjectExplorerCommand.cs` (Ctrl+F12 — SQL Prompt's Select in Object Explorer chord; issue #7)
expands the tree to the object under the cursor, selects it and gives the tree the focus. The walk is
`ObjectExplorerHelper.RevealAsync`, which the validation window's `RevealObjectAsync` now wraps.

- **The name is resolved on the server first** (`SchemaQueryService.ResolveObject`): the tree cannot say which schema an
  unqualified name means (default schema, then `dbo`), what kind of object it is (which decides the folder), or which
  table a trigger hangs off. "Not found" is then the server's answer, not a tree walk timing out.
- **Strict server match for Locate.** `RevealObjectAsync` has always fallen back to the first server in the tree when the
  name does not match; for Locate that would select a same-named table on another server — a wrong answer that looks
  right — so `strictServer` turns it off.
- **Not connected → `IObjectExplorerService.ConnectToServer` with the query window's own `UIConnectionInfo`**
  (`ServiceCacheProxy.GetActiveUIConnectionInfo`), by reflection because the parameter type is in an assembly we do not
  reference. It returns before the node exists, so the server node is polled for (20 s).
- Paths it knows: Tables, Views, Synonyms, Programmability › Stored Procedures / Functions › Scalar- or Table-valued,
  and triggers as `Tables › dbo.T › Triggers › trg` (listed without a schema). System databases sit under
  `Databases › System Databases`. Table labels can carry a suffix (`dbo.T (System-Versioned)`), which the leaf match
  strips. Folder names are the English UI's, as before.
- `IObjectExplorerService.FindNode`/`SynchronizeTree` exist and would be the shorter route, but were not used: the tree
  walk is what the validation window has been exercising, and it needs no guess about the URN format they take.
