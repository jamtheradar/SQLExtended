DataByte for SSMS 2026.10.27.1019

New
- Match whole word in SQL Search. Tick the box beside the search box (Ctrl+Alt+F) to find the term only as a whole name or word: `person` finds `person`, `dbo.person`, `[person]` and `WHERE person = 1`, but not `personal`, `PersonAddress`, `person_id`, `@person` or `#person`. It applies to names, columns, definitions and Agent jobs, and the preview highlights only those matches. The box remembers its setting between sessions. Thanks to @RalphNehme for the suggestion (#9).

Fixed
- In SQL Search, a search containing `_` (as most column names do) treated it as "any character" when searching definitions, so it could return objects that didn't contain the term. A `%` did the same.
- Find References missed objects whose own name contained the term. For example, asking what uses `Person` left out a view called `vPerson` even though its definition used `Person`.
