# Manifests and the command table

Four files at the project root drive registration and packaging. Two are committed inputs, one is a
build output, one is a local dev-deploy artefact. They are easy to confuse — three of them are called
some variation of `extension.vsixmanifest`.

| File | Committed? | Needed? | Role |
|------|-----------|---------|------|
| `SsmsSchemaViewerPackage.vsct` | yes | **required** | The command table — menus, groups, buttons, keybindings |
| `source.extension.vsixmanifest` | yes | **required** | The VSIX manifest source; the only one you edit by hand |
| `merged.source.extension.vsixmanifest` | no (gitignored) | build output | Generated each build; safe to delete |
| `extension.vsixmanifest` | no (gitignored) | dev deploy only | Loose manifest for the xcopy install into SSMS |

## `SsmsSchemaViewerPackage.vsct`

The command table. It declares every menu, group and button the extension adds, plus the keybindings
(`Ctrl+Shift+D`, `Ctrl+K, Ctrl+Y`, …), and places them both in the top-level **SQLExtended** menu and
the editor context menu.

`SQLExtended.csproj` compiles it as a `VSCTCompile` item with `ResourceName=Menus.ctmenu`, which embeds
the compiled `.cto` as a managed resource in `SQLExtended.dll`. `SsmsSchemaViewerPackage.cs:34` then
declares `[ProvideMenuResource("Menus.ctmenu", 1)]`, which is what puts the pointer to that resource in
the generated `.pkgdef` so the shell can find it. **All three have to agree** — a command added to the
`.vsct` but not backed by a `MenuCommand` in code shows as greyed out; a `MenuCommand` with no `.vsct`
entry never appears at all.

## `source.extension.vsixmanifest`

The committed VSIX manifest, and the one to edit for display name, description, tags, install target,
or prerequisites. Two things in it must not be touched:

- **`Identity/@Id`** — how VSIXInstaller and Manage Extensions recognise an *upgrade* of this extension
  rather than a different one. Changing it orphans every installed copy.
- **`Identity/@Version`** — deliberately the placeholder `0.0.0`. The `StampVsixVersion` target in
  `SQLExtended.csproj` overwrites it from `version.txt` in the *generated* manifest, so this file stays
  out of every release diff. Don't hand-bump it. See the `releasing` skill for the version rules.

Its `Assets` use project tokens (`|%CurrentProject%;PkgdefProjectOutputGroup|`) which the SDK resolves
during the build — that is the difference between this file and the two below.

## `merged.source.extension.vsixmanifest`

Pure build output, written by the VSSDK's `MergeVsixManifestFile` target and consumed by
`DetokenizeVsixManifestFile`. It is the file `StampVsixVersion` actually edits. It lands in the project
root rather than `obj\` because the VSSDK targets are imported before the SDK's, so
`IntermediateOutputPath` is still empty when the default is computed. Gitignored; delete it any time
and the next build regenerates it. Nothing reads it after the `.vsix` is zipped.

## `extension.vsixmanifest`

Not part of the `.vsix` build at all. It is the **detokenized** manifest — real asset paths
(`SQLExtended.pkgdef`, `SQLExtended.dll`) instead of project tokens — that an xcopy-deployed extension
folder needs in order for SSMS to load it. `CopyToSsms` (in `SQLExtended.csproj`) copies it, if present,
into `…\Common7\IDE\Extensions\SQLExtended\` after every build, alongside the DLL and the pkgdef.

Because `DeployExtension=false`, that xcopy copy *is* the dev inner loop: build, then F5 into
`Ssms.exe`. Without this file the extension folder is inert.

**It is a build output, not a hand-written file** — `DetokenizeVsixManifestFile` writes it here on every
build, landing in the project root for the same reason `merged.source.extension.vsixmanifest` does.
Gitignored; delete it and the next build puts it back, carrying the version that build stamped. (An
earlier note here claimed it was hand-made and that its `Version` drifted from `version.txt`; deleting it
and rebuilding disproves both. `SoluitionDocs/ManualInstall.md` still describes copying and renaming
`source.extension.vsixmanifest` by hand — that is for a machine with no build, where there is no
detokenized manifest to copy.)

Anything the manifest names by relative path has to exist next to it in that folder, or Manage Extensions
has nothing to load — which is why `CopyToSsms` also copies `Resources\` for `Icon`/`PreviewImage`.

> Naming trap: the `.vsix` container also holds an entry named `extension.vsixmanifest` (that is the
> standard entry name inside the zip, and what `publish-release.ps1:173` reads the version from). That
> entry is the merged/stamped manifest — it has nothing to do with the loose file described here.
