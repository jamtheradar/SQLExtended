DataByte for SSMS 2026.10.27.858

New
- Copy as IN Filter. Select cells in one column of a results grid and press Ctrl+Shift+I (or right-click the grid and choose Copy as IN Filter) to copy them as an `IN (...)` list, one value per line. Text, dates and GUIDs are quoted and numbers are not, going by the column's type, so a varchar `007` stays `'007'`. Quotes inside values are escaped and NULLs are left out. The item is greyed out when the selection covers more than one column. Thanks to @walterstypula for the suggestion (#5).
- Open Definition. Press F12 on a table, view, procedure, function or trigger name to open its script in a new query window connected to the same server and database. Procedures, functions, views and triggers come out as CREATE OR ALTER; you can pick CREATE or ALTER instead under Settings → Search → Open Definition. The View Schema window also has an Open in Script Window button. Thanks to @walterstypula for the suggestion (#6).
- Locate in Object Explorer. Press Ctrl+F12 on an object name to expand Object Explorer to it, select it and move to it. If the server isn't connected in Object Explorer yet, it connects it with the query window's own login. Triggers are found under their table, and system databases work too. Thanks to @walterstypula for the suggestion (#7).
- Align On. Select some lines (or put the cursor in a block) and press Ctrl+= to line up AS, ON, =, --, commas, THEN or any word you type. Press Enter to align the first one on each line, or Shift+Enter to align every one. Words inside strings, comments and brackets are ignored. Press Ctrl+= again to align on the next word, since the lines stay selected. Thanks to @walterstypula for the suggestion (#8).
- Format Selection. Press Ctrl+K, Ctrl+Shift+F to format only the statements the selection touches, or the one the cursor is in. The rest of the script is left exactly as it was, and the formatted statement keeps its indent, so a SELECT inside a procedure stays where it is. Format SQL is unchanged (#8).
- Format With. Choose SQLExtended → Format With (or right-click) to pick which rules to apply this time: keyword case, function name case, removing brackets that aren't needed, LEFT OUTER JOIN → LEFT JOIN, adding semicolons, tabs or spaces, trimming trailing spaces, or the full format. Save a choice as a named preset; "Casing only" and "Full format" are there to start with. The first five presets can be given their own shortcuts under Tools → Options → Keyboard (search for FormatPreset) (#8).
- Uppercase Keywords (Ctrl+B, Ctrl+U), Remove Brackets (Ctrl+B, Ctrl+B) and Insert Semicolons (Ctrl+B, Ctrl+C) each do one job without changing the layout.

Changed
- Shortcuts now match Redgate SQL Prompt wherever SQLExtended has the same command, so they will be familiar if you have used it. Format SQL is now Ctrl+K, Ctrl+Y (it was Ctrl+K, Ctrl+F and Ctrl+K, Ctrl+Q), and Qualify Columns is now Ctrl+B, Ctrl+Q (it was Ctrl+K, Ctrl+A). The old shortcuts no longer run these commands; if you want them back, set them under Tools → Options → Keyboard.
- In a query window, Ctrl+Shift+I now runs Copy as IN Filter instead of the editor's reverse incremental search.

Fixed
- View Schema now finds a procedure, function or trigger named without its schema (such as `usp_GetOrders` in `EXEC usp_GetOrders`). Before, only tables and views were found that way.
