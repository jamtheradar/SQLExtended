# T-SQL Standard Formatting Profile

This is the set of **Format SQL** (Ctrl+K, Ctrl+Y) options that reproduce the leading-comma /
stacked-SELECT / stacked-CTE / river-WHERE T-SQL style. Set them once in **SQLExtended → Format
Options** (they're saved per profile — use **Save As...** to keep them under their own name).

## Settings

| Option (Format Options dialog) | Value | Rule it satisfies |
|---|---|---|
| General → Indent style | **Tabs** | Tab indentation |
| General → *Indent AND/OR under WHERE* | **unchecked** | WHERE `AND`/`OR` left-aligned with `WHERE` |
| Layout → Column layout | **Stacked (SELECT on own line)** | `SELECT` alone, one column per line |
| Layout → Comma position | **Leading (, col1 , col2)** | Leading commas |
| Layout → *Leading comma at column indent* | **checked** | Comma sits at the column's indent (`, [Name]`) |
| Layout → JOIN layout | **New Line** | Each JOIN on its own line |
| Layout → *ON on same line as JOIN* | **checked** | Table + `ON` stay on the JOIN line |
| Layout → *Normalize JOIN types* | **checked** | `LEFT/RIGHT OUTER JOIN` → `LEFT/RIGHT JOIN` |
| Layout → *Stacked CTE layout* | **checked** | `WITH x AS (` … `)` at margin, blank line between CTEs |
| Layout → Condition layout | **New Line Per Condition** | Each `AND`/`OR` on its own line |
| Layout → *New line before close parenthesis* | **checked** | `CREATE TABLE` closing `)` on its own line |
| Style → Alias style | **Column = value** | `alias = expression` |
| Style → Bracket quoting | **Remove Brackets** | Brackets kept only on keyword-named columns |
| Style → *Align column definition fields* | **unchecked** | Single-space column definitions |

`INNER JOIN` is produced automatically — the generator already expands a bare `JOIN` to `INNER JOIN`.

## Example

Input:

```sql
select p.PropertyGuid, p.Name as PropName, p.Type from Properties p
join Owners o on p.PropertyGuid = o.PropertyGuid
left join Agents a on a.Id = p.AgentId
where p.Status = 'Active' and p.CreatedDate > '2024-01-01'
```

Output:

```sql
SELECT
	p.PropertyGuid
	, PropName = p.Name
	, p.Type
FROM Properties AS p
INNER JOIN Owners AS o ON p.PropertyGuid = o.PropertyGuid
LEFT JOIN Agents AS a ON a.Id = p.AgentId
WHERE p.Status = 'Active'
AND p.CreatedDate > '2024-01-01'
```

CTEs:

```sql
WITH RankedPhones AS (
	SELECT
		PropertyGuid
		, Phone
		, rn = row_number() OVER (PARTITION BY PropertyGuid ORDER BY Created DESC)
	FROM Phones
)

, AggregatedEmails AS (
	SELECT
		PropertyGuid
		, Email
	FROM Emails
)

SELECT ...
```

## Not yet automated (deferred — these rewrite identifiers, which a formatter can't do safely
## without resolving every reference)

- **`cte` prefix on CTE names** — `RankedPhones` is not renamed to `cteRankedPhones`.
- **`RankSort` alias on `ROW_NUMBER()`** — an existing alias is left as-is.
- **Adding brackets to keyword-named columns** — `Name`/`Type` written without brackets stay
  without brackets. (Existing brackets on keyword names *are* preserved by Remove Brackets.)

Do these by hand for now; they're tracked as a follow-up.
