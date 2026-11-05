using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLExtended.SchemaCompare.Engine;

/// <summary>How a change script is written.</summary>
/// <remarks>Plain settable properties (no records/init): the engine is linked into the net48 test project.</remarks>
public sealed class ScriptOptions
{
    /// <summary>True: the right becomes like the left (the script runs against the right). False: the other way.</summary>
    public bool LeftToRight { get; set; } = true;

    /// <summary>Drop objects that are only on the target. Off by default: a missing object is often just out of scope.</summary>
    public bool IncludeDrops { get; set; }

    /// <summary>One transaction, with every batch after a failed one skipped (SET NOEXEC ON).</summary>
    public bool UseTransaction { get; set; } = true;

    /// <summary>For the header: what each side is.</summary>
    public string LeftName { get; set; } = "left";

    public string RightName { get; set; } = "right";
}

public sealed class ChangeScript
{
    public string Text { get; set; } = "";
    public int Created { get; set; }
    public int Altered { get; set; }
    public int Dropped { get; set; }

    /// <summary>Data-loss risks and differences the script leaves for a person; each is also a comment in the script.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Objects only on the target that weren't dropped because <see cref="ScriptOptions.IncludeDrops"/> is off.</summary>
    public List<string> NotDropped { get; } = new();
}

/// <summary>
/// Writes the T-SQL that turns the target side of a compare into the source side.
///
/// <para>Each object is scripted from what the compare found, with the compare's options deciding what counts as a
/// difference, so a change script never "fixes" noise the list doesn't show. Modules become <c>CREATE OR ALTER</c>
/// with the author's text. Tables are diffed element by element (columns, defaults, constraints, indexes, extended
/// properties) and altered in place; every index, constraint and default that leans on a column being altered or
/// dropped is dropped first and put back after. Foreign keys are dropped before anything else and added after every
/// table exists, including the ones on unchanged tables that reference a key being rebuilt.</para>
///
/// <para>What can't be altered in place (a changed type, identity, column order, table options) is left as a REVIEW
/// comment, never guessed at.</para>
/// </summary>
public static class ChangeScripter
{
    // Phases, in the order they run.
    // A dropped table goes before the parts of altered ones: its FKs go with it, so a key it references can be rebuilt.
    private const int DropForeignKeys = 0, DropModules = 1, DropTables = 2, DropTableParts = 3, DropOthers = 4, CreateContainers = 5, Tables = 6,
        Modules = 7, AddForeignKeys = 8, Others = 9, PhaseCount = 10;

    public static ChangeScript Generate(IEnumerable<CompareItem> items, SchemaSet left, SchemaSet right, CompareOptions compare, ScriptOptions options)
    {
        var run = new Run(left, right, compare, options);
        foreach (var item in items.Where(i => i.Status != CompareStatus.Identical)) run.Add(item);
        return run.Finish();
    }

    /// <summary>The module's own text with its CREATE or ALTER made CREATE OR ALTER, comments and layout untouched.</summary>
    internal static string CreateOrAlter(string text)
    {
        var tokens = Sql.NewParser().GetTokenStream(new StringReader(text), out _);
        if (tokens == null) return text;

        int first = -1, second = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment) continue;
            if (first < 0) first = i;
            else { second = i; break; }
        }

        if (first < 0) return text;
        var head = tokens[first];
        if (head.TokenType == TSqlTokenType.Alter) return text.Substring(0, head.Offset) + "CREATE OR ALTER" + text.Substring(head.Offset + head.Text.Length);
        if (head.TokenType == TSqlTokenType.Create && (second < 0 || tokens[second].TokenType != TSqlTokenType.Or))
            return text.Substring(0, head.Offset + head.Text.Length) + " OR ALTER" + text.Substring(head.Offset + head.Text.Length);
        return text;
    }

    /// <summary>One table's parts, read twice: as written (to emit) and as the compare normalises them (to decide).</summary>
    private sealed class TableShape
    {
        public Normalizer.TableModel Faithful, Compared;
        public string QualifiedName;
        public readonly List<ColumnPart> Columns = new();
        public readonly List<ConstraintPart> Constraints = new();
        public readonly List<IndexPart> Indexes = new();
        public bool HasIndexes, HasProperties;
    }

    private sealed class ColumnPart
    {
        public string Name;
        public ColumnDefinition Faithful, Compared;
        public string CompareText, DefaultCompareText, IdentityText;
        public bool IsComputed => Compared.ComputedColumnExpression != null;
    }

    private sealed class ConstraintPart
    {
        public string Key, Name, CompareText;
        public ConstraintDefinition Faithful;
        public bool NotTrusted;
        public HashSet<string> Columns;
        public bool IsForeignKey => Faithful is ForeignKeyConstraintDefinition;
        public bool IsKey => Faithful is UniqueConstraintDefinition;
        public bool IsPrimaryKey => Faithful is UniqueConstraintDefinition { IsPrimaryKey: true };
        public string ReferencedTable;
    }

    private sealed class IndexPart
    {
        public string Key, Name, Text, CompareText;
        public bool IsUnique, IsAlter, IsFullText;
        public HashSet<string> Columns;
    }

    /// <summary>A module waiting for its place in dependency order.</summary>
    private sealed class PendingModule
    {
        public string Name, Text;
        public SchemaObjectType Type;
        public List<string> Batches;
    }

    private sealed class Run
    {
        private readonly SchemaSet _source, _target;
        private readonly CompareOptions _compare, _faithful;
        private readonly ScriptOptions _options;
        private readonly bool _sourceIsLeft;
        private readonly ChangeScript _result = new();
        private readonly List<string>[] _phases = Enumerable.Range(0, PhaseCount).Select(_ => new List<string>()).ToArray();
        private readonly List<PendingModule> _modules = new();
        private readonly Dictionary<SchemaObject, TableShape> _shapes = new();

        // Foreign keys are planned across tables: "TABLE|key" of each one already dropped or added.
        private readonly HashSet<string> _droppedForeignKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _addedForeignKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _rebuiltKeyTables = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _droppedTables = new(StringComparer.OrdinalIgnoreCase);
        private bool _hasFullText;

        public Run(SchemaSet left, SchemaSet right, CompareOptions compare, ScriptOptions options)
        {
            _options = options;
            _sourceIsLeft = options.LeftToRight;
            _source = _sourceIsLeft ? left : right;
            _target = _sourceIsLeft ? right : left;
            _compare = compare;

            // Every ignore off: the source as written, which is what the target should become.
            _faithful = new CompareOptions
            {
                IgnoreComments = false, IgnoreWhitespace = false, IgnoreCase = false, IgnoreBrackets = false, IgnoreSemicolons = false, IgnoreSessionSettings = false,
                IgnoreCreateOrAlter = false, IgnoreDefaultIndexOptions = false, IgnoreAllIndexOptions = false, IgnoreDefaultFilegroup = false, IgnoreAllFilegroups = false,
                IgnoreCollation = false, IgnoreSystemNames = false, IgnoreConstraintTrust = false, IgnoreRedundantParentheses = false, IgnoreColumnOrder = false,
                IgnoreIndexes = false, IgnoreSchemaOwnerDbo = false, IgnoreExtendedProperties = false, IgnoreSecurity = false,
            };
        }

        // ---- names and text ---------------------------------------------------------------------------------

        /// <summary>Source text in the target's names (<see cref="CompareOptions.EquivalentNames"/> applied the right way round).</summary>
        private string ToTarget(string text) => _sourceIsLeft ? _compare.MapLeftNames(text) : _compare.MapRightNames(text);

        /// <summary>Equal as the compare sees it: its case and whitespace choices, the right side's names read as the left's.</summary>
        private bool Same(string sourceText, string targetText)
        {
            string l = _sourceIsLeft ? sourceText : targetText, r = _sourceIsLeft ? targetText : sourceText;
            return LineDiff.AreEqual(l ?? "", _compare.MapRightNames(r ?? ""), _compare.IgnoreCase, _compare.IgnoreWhitespace);
        }

        private static string Q(string name) => Normalizer.Bracket(name);

        private static string Qualified(SchemaObject obj) => obj.Schema.Length == 0 ? Q(obj.Name) : Q(obj.Schema) + "." + Q(obj.Name);

        private static string TableKey(string schema, string name) => ((string.IsNullOrEmpty(schema) ? Sql.DefaultSchema : schema) + "." + name).ToUpperInvariant();

        private static string TableKey(SchemaObject obj) => TableKey(obj.Schema, obj.Name);

        private static string Literal(string s) => "N'" + (s ?? "").Replace("'", "''") + "'";

        private void Warn(int phase, string message)
        {
            _result.Warnings.Add(message);
            _phases[phase].Add("-- WARNING: " + message);
        }

        private void Review(int phase, string message, string definition = null)
        {
            _result.Warnings.Add(message);
            var sb = new StringBuilder("-- REVIEW: ").Append(message);
            if (!string.IsNullOrWhiteSpace(definition)) sb.Append("\r\n/*\r\n").Append(definition.Trim().Replace("*/", "* /")).Append("\r\n*/");
            _phases[phase].Add(sb.ToString());
        }

        private void Print(int phase, string verb, SchemaObject obj) => _phases[phase].Add($"PRINT N'{verb} {obj.Type} {Qualified(obj).Replace("'", "''")}'");

        // ---- dispatch ----------------------------------------------------------------------------------------

        public void Add(CompareItem item)
        {
            var source = _sourceIsLeft ? item.Left : item.Right;
            var target = _sourceIsLeft ? item.Right : item.Left;

            if (source == null && target != null)
            {
                Drop(target);
                return;
            }

            if (source == null) return;
            bool create = target == null;
            if (create) _result.Created++;
            else _result.Altered++;

            switch (source.Type)
            {
                case SchemaObjectType.Table:
                    if (create) CreateTable(source);
                    else AlterTable(source, target);
                    break;

                case SchemaObjectType.View or SchemaObjectType.Procedure or SchemaObjectType.Function or SchemaObjectType.Trigger or SchemaObjectType.DatabaseTrigger:
                    Module(source, target);
                    break;

                case SchemaObjectType.Synonym when !create:
                    // A synonym has no ALTER; dropping and re-creating it loses nothing.
                    Print(CreateContainers, "Altering", source);
                    _phases[CreateContainers].Add($"DROP SYNONYM {Qualified(target)}");
                    Statements(source, null, CreateContainers);
                    break;

                case SchemaObjectType.Schema or SchemaObjectType.UserDefinedType or SchemaObjectType.TableType or SchemaObjectType.Sequence or SchemaObjectType.Synonym:
                    if (create)
                    {
                        Print(CreateContainers, "Creating", source);
                        Statements(source, null, CreateContainers);
                    }
                    else
                    {
                        // A changed type, sequence or schema has no in-place change that keeps what depends on it.
                        Statements(source, target, CreateContainers);
                    }

                    break;

                default:
                    Print(Others, create ? "Creating" : "Altering", source);
                    Statements(source, target, Others);
                    break;
            }
        }

        // ---- drops -------------------------------------------------------------------------------------------

        private void Drop(SchemaObject target)
        {
            if (!_options.IncludeDrops)
            {
                _result.NotDropped.Add(target.Type + " " + target.DisplayName);
                return;
            }

            string name = Qualified(target);
            string drop = target.Type switch
            {
                SchemaObjectType.Table => $"DROP TABLE IF EXISTS {name}",
                SchemaObjectType.View => $"DROP VIEW IF EXISTS {name}",
                SchemaObjectType.Procedure => $"DROP PROCEDURE IF EXISTS {name}",
                SchemaObjectType.Function => $"DROP FUNCTION IF EXISTS {name}",
                SchemaObjectType.Trigger => $"DROP TRIGGER IF EXISTS {name}",
                SchemaObjectType.DatabaseTrigger => $"DROP TRIGGER IF EXISTS {Q(target.Name)} ON DATABASE",
                SchemaObjectType.TableType or SchemaObjectType.UserDefinedType => $"DROP TYPE IF EXISTS {name}",
                SchemaObjectType.Sequence => $"DROP SEQUENCE IF EXISTS {name}",
                SchemaObjectType.Synonym => $"DROP SYNONYM IF EXISTS {name}",
                SchemaObjectType.Schema => $"DROP SCHEMA IF EXISTS {Q(target.Name)}",
                SchemaObjectType.User => $"DROP USER IF EXISTS {Q(target.Name)}",
                SchemaObjectType.Role => $"DROP ROLE IF EXISTS {Q(target.Name)}",
                _ => null,
            };

            int phase = target.Type switch
            {
                SchemaObjectType.Table => DropTables,
                SchemaObjectType.View or SchemaObjectType.Procedure or SchemaObjectType.Function or SchemaObjectType.Trigger or SchemaObjectType.DatabaseTrigger
                    or SchemaObjectType.Synonym => DropModules,
                _ => DropOthers,
            };

            if (drop == null)
            {
                // Permissions, logins, standalone properties: undo what can be undone, one statement at a time.
                Statements(null, target, Others);
                return;
            }

            _result.Dropped++;
            if (target.Type == SchemaObjectType.Table)
            {
                Warn(phase, $"{name} is dropped, with every row in it.");
                _droppedTables.Add(TableKey(target));
            }

            Print(phase, "Dropping", target);
            _phases[phase].Add(drop);
        }

        // ---- tables ------------------------------------------------------------------------------------------

        private TableShape Shape(SchemaObject obj)
        {
            if (_shapes.TryGetValue(obj, out var shape)) return shape;

            var faithful = Normalizer.BuildTable(obj, _faithful);
            var compared = Normalizer.BuildTable(obj, _compare);
            shape = new TableShape { Faithful = faithful, Compared = compared, QualifiedName = Qualified(obj) };

            for (int i = 0; i < faithful.Columns.Count && i < compared.Columns.Count; i++)
            {
                var f = faithful.Columns[i];
                var c = compared.Columns[i];
                var d = c.DefaultConstraint;
                c.DefaultConstraint = null;
                var identity = c.IdentityOptions;
                string identityText = identity == null ? "" : Normalizer.Generate(identity);
                string text = Normalizer.GenerateCanonical(c, _compare);
                c.DefaultConstraint = d;
                shape.Columns.Add(new ColumnPart
                {
                    Name = f.ColumnIdentifier.Value, Faithful = f, Compared = c, CompareText = text, IdentityText = identityText,
                    DefaultCompareText = d == null ? "" : Normalizer.GenerateCanonical(d, _compare),
                });
            }

            for (int i = 0; i < faithful.Constraints.Count && i < compared.Constraints.Count; i++)
            {
                var f = faithful.Constraints[i];
                var c = compared.Constraints[i].Constraint;
                string compareText = Normalizer.GenerateCanonical(c, _compare);
                var part = new ConstraintPart
                {
                    Faithful = f.Constraint, NotTrusted = f.NotTrusted, Name = f.Constraint.ConstraintIdentifier?.Value, CompareText = compareText,
                    Key = c.ConstraintIdentifier?.Value ?? compareText, Columns = Normalizer.ColumnsOf(f.Constraint),
                };
                if (f.Constraint is ForeignKeyConstraintDefinition fk && fk.ReferenceTableName != null)
                    part.ReferencedTable = TableKey(fk.ReferenceTableName.SchemaIdentifier?.Value, fk.ReferenceTableName.BaseIdentifier.Value);
                shape.Constraints.Add(part);
            }

            // The compared model leaves indexes and properties out when the options ignore them; then so does the script.
            shape.HasIndexes = !_compare.IgnoreIndexes;
            if (shape.HasIndexes)
            {
                for (int i = 0; i < faithful.Indexes.Count && i < compared.Indexes.Count; i++)
                {
                    var f = faithful.Indexes[i];
                    var c = compared.Indexes[i];
                    shape.Indexes.Add(new IndexPart
                    {
                        Key = c.Key, Name = f.Name, Text = f.Text, CompareText = c.Text, IsUnique = f.IsUnique, IsAlter = f.IsAlter, IsFullText = f.IsFullText, Columns = f.Columns,
                    });
                }
            }

            shape.HasProperties = !_compare.IgnoreExtendedProperties;
            _shapes[obj] = shape;
            return shape;
        }

        /// <summary>A constraint's definition for ADD: a system-generated name is left for the server to make up again.</summary>
        private static string ConstraintText(ConstraintDefinition c)
        {
            var name = c.ConstraintIdentifier;
            if (Normalizer.IsSystemName(name?.Value)) c.ConstraintIdentifier = null;
            string text = Normalizer.GenerateCanonical(c, null);
            c.ConstraintIdentifier = name;
            return text;
        }

        private string AddConstraint(string table, ConstraintPart c)
            => ToTarget($"ALTER TABLE {table}{(c.NotTrusted ? " WITH NOCHECK" : "")} ADD {ConstraintText(c.Faithful)}");

        private void CreateTable(SchemaObject source)
        {
            var shape = Shape(source);
            var model = shape.Faithful;
            if (model.Create == null || model.Definition == null)
            {
                // Only ALTERs on this side, or a CREATE that didn't parse: as written.
                Print(Tables, "Creating", source);
                Review(Tables, $"{shape.QualifiedName} couldn't be read as a CREATE TABLE; its statements are scripted as written.");
                foreach (var s in source.Statements.Where(s => !s.IsSessionSetting)) _phases[Tables].Add(ToTarget(s.Text.Trim()));
                return;
            }

            Print(Tables, "Creating", source);
            foreach (var column in shape.Columns)
            {
                var d = column.Faithful.DefaultConstraint;
                if (d != null && Normalizer.IsSystemName(d.ConstraintIdentifier?.Value)) d.ConstraintIdentifier = null;
                model.Definition.ColumnDefinitions.Add(column.Faithful);
            }

            foreach (var c in shape.Constraints.Where(c => !c.IsForeignKey && !c.NotTrusted))
            {
                if (Normalizer.IsSystemName(c.Faithful.ConstraintIdentifier?.Value)) c.Faithful.ConstraintIdentifier = null;
                model.Definition.TableConstraints.Add(c.Faithful);
            }

            _phases[Tables].Add(ToTarget(Normalizer.GenerateCanonical(model.Create, null)));
            model.Definition.ColumnDefinitions.Clear();
            model.Definition.TableConstraints.Clear();

            // An untrusted constraint can only be added afterwards, WITH NOCHECK.
            foreach (var c in shape.Constraints.Where(c => !c.IsForeignKey && c.NotTrusted)) _phases[Tables].Add(AddConstraint(shape.QualifiedName, c));
            foreach (var index in model.Indexes) _phases[Tables].Add(ToTarget(IndexCreateText(shape.QualifiedName, index.Text)));
            foreach (var c in shape.Constraints.Where(c => c.IsForeignKey)) AddForeignKey(source, shape, c);
            foreach (var line in model.Disabled) _phases[AddForeignKeys].Add(ToTarget(line));
            foreach (var line in model.Tail) _phases[Tables].Add(ToTarget(line));
            foreach (var p in model.Properties) _phases[Tables].Add(ToTarget(p.ToScript()));
            _hasFullText |= model.Indexes.Any(i => i.IsFullText);
        }

        /// <summary>An inline index of a kind CREATE INDEX can't say (hash, memory-optimised) is added through ALTER TABLE.</summary>
        private static string IndexCreateText(string table, string text)
            => text.StartsWith("INDEX ", StringComparison.OrdinalIgnoreCase) ? $"ALTER TABLE {table} ADD {text}" : text;

        private void AddForeignKey(SchemaObject table, TableShape shape, ConstraintPart c)
        {
            if (!_addedForeignKeys.Add(TableKey(table) + "|" + c.Key)) return;
            _phases[AddForeignKeys].Add(AddConstraint(shape.QualifiedName, c));
        }

        private void DropForeignKey(SchemaObject table, TableShape shape, ConstraintPart c)
        {
            if (!_droppedForeignKeys.Add(TableKey(table) + "|" + c.Key)) return;
            DropConstraint(DropForeignKeys, shape.QualifiedName, c);
        }

        private void DropConstraint(int phase, string table, ConstraintPart c)
        {
            if (c.Name != null)
            {
                _phases[phase].Add($"ALTER TABLE {table} DROP CONSTRAINT {Q(c.Name)}");
            }
            else if (c.IsPrimaryKey)
            {
                _phases[phase].Add(DropByLookup(table, $"SELECT name FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID({Literal(table)}) AND type = 'PK'"));
            }
            else if (c.Faithful is DefaultConstraintDefinition { Column: not null } d)
            {
                _phases[phase].Add(DropDefaultByLookup(table, d.Column.Value));
            }
            else if (c.Faithful is ForeignKeyConstraintDefinition fk && fk.ReferenceTableName != null && fk.Columns.Count > 0)
            {
                // The one FK from this table to that one over exactly these columns.
                string columns = string.Join(", ", fk.Columns.Select(x => Literal(x.Value)));
                _phases[phase].Add(DropByLookup(table, $"SELECT TOP (1) fk.name FROM sys.foreign_keys AS fk WHERE fk.parent_object_id = OBJECT_ID({Literal(table)}) "
                    + $"AND fk.referenced_object_id = OBJECT_ID({Literal(Normalizer.Name(fk.ReferenceTableName))}) "
                    + $"AND (SELECT COUNT(*) FROM sys.foreign_key_columns AS fkc WHERE fkc.constraint_object_id = fk.object_id) = {fk.Columns.Count} "
                    + "AND NOT EXISTS (SELECT 1 FROM sys.foreign_key_columns AS fkc JOIN sys.columns AS c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id "
                    + $"WHERE fkc.constraint_object_id = fk.object_id AND c.name NOT IN ({columns}))"));
            }
            else
            {
                Review(phase, $"An unnamed constraint on {table} needs dropping by hand, under whatever name the server gave it: {c.CompareText}");
            }
        }

        private static string DropByLookup(string table, string query)
            => $"DECLARE @constraint sysname = ({query});\r\n"
                + $"DECLARE @drop nvarchar(max) = N'ALTER TABLE {table.Replace("'", "''")} DROP CONSTRAINT ' + QUOTENAME(@constraint);\r\n"
                + "IF @constraint IS NOT NULL EXECUTE sp_executesql @drop;";

        private static string DropDefaultByLookup(string table, string column)
            => DropByLookup(table, "SELECT dc.name FROM sys.default_constraints AS dc JOIN sys.columns AS c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id "
                + $"WHERE dc.parent_object_id = OBJECT_ID({Literal(table)}) AND c.name = {Literal(column)}");

        private void DropDefault(int phase, string table, ColumnPart column)
        {
            string name = column.Faithful.DefaultConstraint?.ConstraintIdentifier?.Value;
            _phases[phase].Add(name != null ? $"ALTER TABLE {table} DROP CONSTRAINT {Q(name)}" : DropDefaultByLookup(table, column.Name));
        }

        private string AddDefault(string table, ColumnPart column)
        {
            var d = column.Faithful.DefaultConstraint;
            string name = d.ConstraintIdentifier?.Value;
            string constraint = name == null || Normalizer.IsSystemName(name) ? "" : $"CONSTRAINT {Q(name)} ";
            return ToTarget($"ALTER TABLE {table} ADD {constraint}DEFAULT {Normalizer.Generate(d.Expression)} FOR {Q(column.Name)}");
        }

        /// <summary>The column as ALTER COLUMN takes it: type, collation, nullability; no default, identity or constraints.</summary>
        private static string AlterColumnText(ColumnDefinition column)
        {
            var (d, identity) = (column.DefaultConstraint, column.IdentityOptions);
            var constraints = column.Constraints.ToList();
            column.DefaultConstraint = null;
            column.IdentityOptions = null;
            column.Constraints.Clear();
            foreach (var c in constraints.OfType<NullableConstraintDefinition>()) column.Constraints.Add(c);
            string text = Normalizer.GenerateCanonical(column, null);
            column.DefaultConstraint = d;
            column.IdentityOptions = identity;
            column.Constraints.Clear();
            foreach (var c in constraints) column.Constraints.Add(c);
            return text;
        }

        private static bool IsNotNull(ColumnDefinition column) => column.Constraints.OfType<NullableConstraintDefinition>().Any(n => !n.Nullable);

        private void AlterTable(SchemaObject source, SchemaObject target)
        {
            var s = Shape(source);
            var t = Shape(target);
            string table = t.QualifiedName;
            var work = new List<string>();
            int before = _phases.Sum(p => p.Count);

            var sourceColumns = Index(s.Columns, c => c.Name);
            var targetColumns = Index(t.Columns, c => c.Name);

            // Columns that change shape: everything that leans on one of them is dropped first and put back after.
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dropColumns = new List<string>();
            var alterColumns = new List<string>();
            var addColumns = new List<string>();
            var defaultsToAdd = new List<ColumnPart>();

            foreach (var tc in t.Columns.Where(c => !sourceColumns.ContainsKey(c.Name)))
            {
                touched.Add(tc.Name);
                if (tc.Faithful.DefaultConstraint != null) DropDefault(DropTableParts, table, tc);
                Warn(Tables, $"Column {table}.{Q(tc.Name)} is dropped, with its data.");
                dropColumns.Add($"ALTER TABLE {table} DROP COLUMN {Q(tc.Name)}");
            }

            foreach (var sc in s.Columns)
            {
                if (!targetColumns.TryGetValue(sc.Name, out var tc))
                {
                    if (IsNotNull(sc.Faithful) && sc.Faithful.DefaultConstraint == null && sc.Faithful.IdentityOptions == null && !sc.IsComputed)
                        Warn(Tables, $"Column {table}.{Q(sc.Name)} is added NOT NULL with no default: this fails if the table has rows.");
                    var d = sc.Faithful.DefaultConstraint;
                    if (d != null && Normalizer.IsSystemName(d.ConstraintIdentifier?.Value)) d.ConstraintIdentifier = null;
                    addColumns.Add(ToTarget($"ALTER TABLE {table} ADD {Normalizer.GenerateCanonical(sc.Faithful, null)}"));
                    continue;
                }

                bool columnSame = Same(sc.CompareText, tc.CompareText);
                if (!columnSame)
                {
                    if (sc.IsComputed || tc.IsComputed)
                    {
                        // A computed column can't be altered, only dropped and added again.
                        touched.Add(tc.Name);
                        if (tc.Faithful.DefaultConstraint != null) DropDefault(DropTableParts, table, tc);
                        dropColumns.Add($"ALTER TABLE {table} DROP COLUMN {Q(tc.Name)}");
                        addColumns.Add(ToTarget($"ALTER TABLE {table} ADD {Normalizer.GenerateCanonical(sc.Faithful, null)}"));
                        if (!tc.IsComputed) Warn(Tables, $"Column {table}.{Q(sc.Name)} becomes computed: it is dropped and added again, and its data is lost.");
                        continue;
                    }

                    if (!Same(sc.IdentityText, tc.IdentityText))
                    {
                        Review(Tables, $"Column {table}.{Q(sc.Name)} differs in IDENTITY, which ALTER COLUMN can't change; the table needs rebuilding.",
                            Normalizer.GenerateCanonical(sc.Faithful, null));
                    }

                    // Same column apart from identity: nothing else to alter.
                    var withoutIdentity = AlterColumnText(sc.Compared);
                    if (!Same(withoutIdentity, AlterColumnText(tc.Compared)))
                    {
                        touched.Add(tc.Name);
                        if (IsNotNull(sc.Faithful) && !IsNotNull(tc.Faithful))
                            Warn(Tables, $"Column {table}.{Q(sc.Name)} becomes NOT NULL: this fails if any row holds a NULL in it.");
                        alterColumns.Add(ToTarget($"ALTER TABLE {table} ALTER COLUMN {AlterColumnText(sc.Faithful)}"));
                    }
                }

                // Defaults: changed, or in the way of an ALTER COLUMN.
                bool defaultSame = Same(sc.DefaultCompareText, tc.DefaultCompareText);
                if (!defaultSame || touched.Contains(tc.Name))
                {
                    if (tc.Faithful.DefaultConstraint != null) DropDefault(DropTableParts, table, tc);
                    if (sc.Faithful.DefaultConstraint != null) defaultsToAdd.Add(sc);
                }
            }

            if (!_compare.IgnoreColumnOrder)
            {
                var common = s.Columns.Select(c => c.Name).Where(targetColumns.ContainsKey).ToList();
                var targetOrder = t.Columns.Select(c => c.Name).Where(sourceColumns.ContainsKey).ToList();
                bool appendedInMiddle = s.Columns.SkipWhile(c => targetColumns.ContainsKey(c.Name)).Any(c => targetColumns.ContainsKey(c.Name));
                if (!common.SequenceEqual(targetOrder, StringComparer.OrdinalIgnoreCase) || appendedInMiddle)
                    Review(Tables, $"{table}'s column order differs (new columns go on the end). Matching it means rebuilding the table, which isn't scripted.");
            }

            // Constraints, then indexes: anything changed, gone, or standing on a touched column.
            var sourceConstraints = Index(s.Constraints, c => c.Key);
            var targetConstraints = Index(t.Constraints, c => c.Key);
            var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tc in t.Constraints)
            {
                bool keep = sourceConstraints.TryGetValue(tc.Key, out var sc) && Same(sc.CompareText, tc.CompareText) && !tc.Columns.Overlaps(touched);
                if (keep) continue;
                dropped.Add(tc.Key);
                if (tc.IsKey) _rebuiltKeyTables.Add(TableKey(target));
                if (tc.IsForeignKey) DropForeignKey(target, t, tc);
                else DropConstraint(DropTableParts, table, tc);
            }

            var addConstraints = new List<string>();
            foreach (var sc in s.Constraints)
            {
                if (targetConstraints.ContainsKey(sc.Key) && !dropped.Contains(sc.Key)) continue;
                if (sc.IsForeignKey) AddForeignKey(source, s, sc);
                else addConstraints.Add(AddConstraint(table, sc));
            }

            var addIndexes = new List<string>();
            if (s.HasIndexes)
            {
                var sourceIndexes = Index(s.Indexes, i => i.Key);
                var targetIndexes = Index(t.Indexes, i => i.Key);
                var droppedIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ti in t.Indexes)
                {
                    bool keep = sourceIndexes.TryGetValue(ti.Key, out var si) && Same(si.CompareText, ti.CompareText) && !ti.Columns.Overlaps(touched);
                    if (keep) continue;
                    droppedIndexes.Add(ti.Key);
                    if (ti.IsUnique) _rebuiltKeyTables.Add(TableKey(target));
                    if (ti.IsAlter) continue; // An ALTER INDEX (DISABLE…) has nothing to undo by name; the CREATE's drop covers it.
                    if (ti.IsFullText) _phases[DropTableParts].Add($"DROP FULLTEXT INDEX ON {table}");
                    else if (ti.Name != null) _phases[DropTableParts].Add($"DROP INDEX {Q(ti.Name)} ON {table}");
                    else Review(DropTableParts, $"An unnamed index on {table} needs dropping by hand: {ti.CompareText}");
                    _hasFullText |= ti.IsFullText;
                }

                foreach (var si in s.Indexes)
                {
                    if (targetIndexes.ContainsKey(si.Key) && !droppedIndexes.Contains(si.Key)) continue;
                    addIndexes.Add(ToTarget(IndexCreateText(table, si.Text)));
                    _hasFullText |= si.IsFullText;
                }
            }

            var properties = new List<string>();
            if (s.HasProperties) properties.AddRange(PropertyChanges(s.Faithful.Properties, t.Faithful.Properties, DropTableParts));

            // NOCHECK CONSTRAINT lines, and other ALTER TABLE … SET: what the source has that the target doesn't.
            foreach (var line in s.Compared.Disabled.Where(l => !t.Compared.Disabled.Any(o => Same(l, o)))) _phases[AddForeignKeys].Add(ToTarget(line));
            foreach (var line in t.Compared.Disabled.Where(l => !s.Compared.Disabled.Any(o => Same(o, l))))
                _phases[AddForeignKeys].Add(line.Replace(" NOCHECK CONSTRAINT ", " WITH CHECK CHECK CONSTRAINT "));
            var tail = s.Compared.Tail.Where(l => !t.Compared.Tail.Any(o => Same(l, o))).Select(ToTarget).ToList();

            work.AddRange(dropColumns);
            work.AddRange(alterColumns);
            work.AddRange(addColumns);
            work.AddRange(addConstraints);
            work.AddRange(defaultsToAdd.Select(c => AddDefault(table, c)));
            work.AddRange(addIndexes);
            work.AddRange(properties);
            work.AddRange(tail);

            if (!Same(s.Compared.ShellText(), t.Compared.ShellText()))
            {
                Review(Tables, $"{table}'s own options differ (filegroup, compression, memory-optimised, system-versioned…). They aren't scripted.",
                    ToTarget(s.Faithful.ShellText()));
            }

            if (work.Count > 0)
            {
                Print(Tables, "Altering", source);
                _phases[Tables].AddRange(work);
            }
            else if (_phases.Sum(p => p.Count) == before)
            {
                Review(Tables, $"{table} differs in a way this script doesn't reach; compare it by hand.");
            }
        }

        private static Dictionary<string, T> Index<T>(IEnumerable<T> parts, Func<T, string> key)
        {
            var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in parts)
            {
                string k = key(p);
                if (k != null && !result.ContainsKey(k)) result[k] = p;
            }

            return result;
        }

        /// <summary>sp_add/update/dropextendedproperty for the difference; drops go to <paramref name="dropPhase"/>.</summary>
        private List<string> PropertyChanges(IEnumerable<ExtendedProperty> source, IEnumerable<ExtendedProperty> target, int dropPhase)
        {
            var result = new List<string>();
            var targetByKey = Index(target, p => p.SortKey);
            var sourceByKey = Index(source, p => p.SortKey);
            foreach (var t in targetByKey.Values.Where(t => !sourceByKey.ContainsKey(t.SortKey))) _phases[dropPhase].Add(t.ToScript("sp_dropextendedproperty"));
            foreach (var s in sourceByKey.Values)
            {
                if (!targetByKey.TryGetValue(s.SortKey, out var t)) result.Add(ToTarget(s.ToScript()));
                else if (!Same(s.Value, t.Value)) result.Add(ToTarget(s.ToScript("sp_updateextendedproperty")));
            }

            return result;
        }

        /// <summary>
        /// Foreign keys on any table that point at a key being rebuilt or a table being dropped: dropped before it, and
        /// (where the source has them) added again after.
        /// </summary>
        private void ReferencingForeignKeys()
        {
            if (_rebuiltKeyTables.Count == 0 && _droppedTables.Count == 0) return;

            foreach (var target in _target.Objects.Values.Where(o => o.Type == SchemaObjectType.Table && !_compare.Excludes(o)).ToList())
            {
                // A table being dropped still loses its FKs first, so dropped tables can go in any order.
                bool tableDropped = _droppedTables.Contains(TableKey(target));
                var t = Shape(target);
                foreach (var fk in t.Constraints.Where(c => c.IsForeignKey && c.ReferencedTable != null))
                {
                    bool rebuilt = _rebuiltKeyTables.Contains(fk.ReferencedTable);
                    if (!rebuilt && !_droppedTables.Contains(fk.ReferencedTable)) continue;
                    DropForeignKey(target, t, fk);
                    if (tableDropped || !rebuilt) continue;

                    // Put it back from the source's own definition, if it still has one.
                    if (!_source.Objects.TryGetValue(target.Key, out var source)) continue;
                    var s = Shape(source);
                    var match = s.Constraints.FirstOrDefault(c => c.IsForeignKey && string.Equals(c.Key, fk.Key, StringComparison.OrdinalIgnoreCase));
                    if (match != null) AddForeignKey(source, s, match);
                }
            }
        }

        // ---- modules -----------------------------------------------------------------------------------------

        private static bool IsModuleHeader(string text)
        {
            var words = LeadingWords(text, 4);
            if (words.Count < 2 || (words[0] != "CREATE" && words[0] != "ALTER")) return false;
            int i = words.Count >= 3 && words[1] == "OR" && words[2] == "ALTER" ? 3 : 1;
            return i < words.Count && words[i] is "PROC" or "PROCEDURE" or "VIEW" or "FUNCTION" or "TRIGGER";
        }

        private static List<string> LeadingWords(string text, int count)
        {
            var result = new List<string>();
            var tokens = Sql.NewParser().GetTokenStream(new StringReader(text), out _);
            if (tokens == null) return result;
            foreach (var t in tokens)
            {
                if (t.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment) continue;
                if (t.TokenType == TSqlTokenType.EndOfFile || result.Count == count) break;
                result.Add(t.Text.ToUpperInvariant());
            }

            return result;
        }

        private void Module(SchemaObject source, SchemaObject target)
        {
            var batches = new List<string> { $"PRINT N'{(target == null ? "Creating" : "Altering")} {source.Type} {Qualified(source).Replace("'", "''")}'" };
            var main = source.Statements.FirstOrDefault(s => !s.IsSessionSetting && IsModuleHeader(s.Text));
            var targetMain = target?.Statements.FirstOrDefault(s => !s.IsSessionSetting && IsModuleHeader(s.Text));
            bool mainChanged = main != null && (targetMain == null || !Same(Normalizer.ModuleText(main.Text, _compare), Normalizer.ModuleText(targetMain.Text, _compare)));

            if (main == null)
            {
                Review(Modules, $"{Qualified(source)} has no CREATE statement this script could read.");
            }
            else if (mainChanged)
            {
                // QUOTED_IDENTIFIER and ANSI_NULLS are kept with the module: an OFF in the source has to be repeated.
                int at = source.Statements.IndexOf(main);
                var settings = source.Statements.Take(at).Reverse().TakeWhile(s => s.IsSessionSetting).Reverse().Select(s => s.Text.Trim()).ToList();
                bool off = settings.Any(s => Regex.IsMatch(s, @"\bOFF\b", RegexOptions.IgnoreCase));
                if (off) batches.Add(string.Join("\r\n", settings));
                batches.Add(ToTarget(CreateOrAlter(main.Text.Trim())));
                if (off) batches.Add("SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON");
            }

            // ALTER VIEW drops the view's indexes: every one of them goes back, changed or not.
            var rest = source.Statements.Where(s => s != main && !s.IsSessionSetting).ToList();
            if (source.Type == SchemaObjectType.View && mainChanged && target != null)
            {
                foreach (var index in rest.Where(s => LeadingWords(s.Text, 1).FirstOrDefault() == "CREATE" && Normalizer.Parse(s.Text) is CreateIndexStatement).ToList())
                {
                    batches.Add(ToTarget(index.Text.Trim()));
                    rest.Remove(index);
                }
            }

            var targetRest = target?.Statements.Where(s => s != targetMain && !s.IsSessionSetting).ToList() ?? new List<SourceStatement>();
            batches.AddRange(StatementChanges(source, rest, targetRest, Modules));
            if (batches.Count > 1)
            {
                string text = main == null ? "" : Normalizer.ModuleText(main.Text, _compare);
                _modules.Add(new PendingModule { Name = source.Name, Type = source.Type, Text = text, Batches = batches });
            }
        }

        /// <summary>
        /// Modules in an order that creates what each one uses first: functions, views, procedures, triggers, moved
        /// later wherever one names another being scripted. A cycle keeps the plain order.
        /// </summary>
        private IEnumerable<string> OrderedModules()
        {
            int Rank(SchemaObjectType t) => t switch
            {
                SchemaObjectType.Function => 0,
                SchemaObjectType.View => 1,
                SchemaObjectType.Procedure => 2,
                _ => 3,
            };

            var pending = _modules.OrderBy(m => Rank(m.Type)).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var uses = pending.ToDictionary(m => m, m => pending.Where(o => o != m && NameIn(o.Name, m.Text)).ToList());
            var done = new HashSet<PendingModule>();
            while (pending.Count > 0)
            {
                var next = pending.FirstOrDefault(m => uses[m].All(done.Contains)) ?? pending[0];
                pending.Remove(next);
                done.Add(next);
                foreach (var b in next.Batches) yield return b;
            }
        }

        private static bool NameIn(string name, string text)
            => !string.IsNullOrEmpty(text) && Regex.IsMatch(text, @"(?<![\w@#$])\[?" + Regex.Escape(name) + @"\]?(?![\w@#$])", RegexOptions.IgnoreCase);

        // ---- statement by statement --------------------------------------------------------------------------

        /// <summary>
        /// For everything that isn't a table or a module: the source's statements the target lacks are run, and the
        /// target's the source lacks are undone where there's an undo (REVOKE, DROP MEMBER, ENABLE TRIGGER,
        /// sp_dropextendedproperty). A CREATE of something that already exists can't run, so it is left for review.
        /// </summary>
        private void Statements(SchemaObject source, SchemaObject target, int phase)
        {
            var s = source?.Statements.Where(x => !x.IsSessionSetting).ToList() ?? new List<SourceStatement>();
            var t = target?.Statements.Where(x => !x.IsSessionSetting).ToList() ?? new List<SourceStatement>();
            var changes = StatementChanges(source ?? target, s, t, phase);
            if (changes.Count > 0 && source == null) Print(phase, "Undoing", target);
            _phases[phase].AddRange(changes);
        }

        private List<string> StatementChanges(SchemaObject owner, List<SourceStatement> source, List<SourceStatement> target, int phase)
        {
            var result = new List<string>();
            string Normal(SourceStatement st)
            {
                var single = new SchemaObject(owner.Type, owner.Schema, owner.Name);
                single.Statements.Add(st);
                return Normalizer.Normalize(single, _compare);
            }

            var sourceProps = new List<ExtendedProperty>();
            var targetProps = new List<ExtendedProperty>();
            var sourceOther = new List<(SourceStatement Statement, string Normal)>();
            var targetOther = new List<(SourceStatement Statement, string Normal, TSqlStatement Parsed)>();
            foreach (var st in source)
            {
                var parsed = st.Parsed ? Normalizer.Parse(st.Text) : null;
                if (ExtendedProperty.TryParse(parsed, out var p)) sourceProps.Add(p);
                else sourceOther.Add((st, Normal(st)));
            }

            foreach (var st in target)
            {
                var parsed = st.Parsed ? Normalizer.Parse(st.Text) : null;
                if (ExtendedProperty.TryParse(parsed, out var p)) targetProps.Add(p);
                else targetOther.Add((st, Normal(st), parsed));
            }

            bool reviewed = false;
            foreach (var (st, normal) in sourceOther)
            {
                if (targetOther.Any(o => Same(normal, o.Normal))) continue;
                string first = LeadingWords(st.Text, 1).FirstOrDefault();
                if (first == "CREATE" && target.Count > 0)
                {
                    Review(phase, $"{owner.Type} {Qualified(owner)} differs, and it can't be altered in place: drop and re-create it (and what depends on it) by hand. The source's definition:",
                        ToTarget(st.Text));
                    reviewed = true;
                    continue;
                }

                result.Add(ToTarget(st.Text.Trim()));
            }

            foreach (var (st, normal, parsed) in targetOther)
            {
                if (sourceOther.Any(o => Same(o.Normal, normal))) continue;
                if (reviewed && LeadingWords(st.Text, 1).FirstOrDefault() == "CREATE") continue; // The target's side of that same review.
                string undo = Undo(parsed);
                if (undo != null) result.Add(undo);
                else if (source.Count > 0) Review(phase, $"{owner.Type} {Qualified(owner)}: a statement only on the target isn't undone: {FirstLine(st.Text)}");
            }

            if (!_compare.IgnoreExtendedProperties || target.Count == 0) result.AddRange(PropertyChanges(sourceProps, targetProps, phase));
            return result;
        }

        private static string FirstLine(string text)
        {
            string line = text.Trim().Split('\n')[0].Trim();
            return line.Length > 120 ? line.Substring(0, 120) + "…" : line;
        }

        /// <summary>The statement that takes back <paramref name="statement"/>, where there is one.</summary>
        private static string Undo(TSqlStatement statement)
        {
            switch (statement)
            {
                case GrantStatement g:
                {
                    var revoke = new RevokeStatement { SecurityTargetObject = g.SecurityTargetObject, CascadeOption = g.WithGrantOption, AsClause = g.AsClause };
                    foreach (var p in g.Permissions) revoke.Permissions.Add(p);
                    foreach (var p in g.Principals) revoke.Principals.Add(p);
                    return Normalizer.Generate(revoke);
                }

                case DenyStatement d:
                {
                    var revoke = new RevokeStatement { SecurityTargetObject = d.SecurityTargetObject, CascadeOption = d.CascadeOption, AsClause = d.AsClause };
                    foreach (var p in d.Permissions) revoke.Permissions.Add(p);
                    foreach (var p in d.Principals) revoke.Principals.Add(p);
                    return Normalizer.Generate(revoke);
                }

                case AlterRoleStatement { Action: AddMemberAlterRoleAction add } role:
                    return Normalizer.Generate(new AlterRoleStatement { Name = role.Name, Action = new DropMemberAlterRoleAction { Member = add.Member } });

                case EnableDisableTriggerStatement ed when ed.TriggerEnforcement == TriggerEnforcement.Disable:
                    ed.TriggerEnforcement = TriggerEnforcement.Enable;
                    return Normalizer.Generate(ed);

                case AlterTableTriggerModificationStatement atm when atm.TriggerEnforcement == TriggerEnforcement.Disable:
                    atm.TriggerEnforcement = TriggerEnforcement.Enable;
                    return Normalizer.Generate(atm);
            }

            return null;
        }

        // ---- the script --------------------------------------------------------------------------------------

        public ChangeScript Finish()
        {
            ReferencingForeignKeys();
            _phases[Modules].AddRange(OrderedModules());

            var batches = _phases.SelectMany(p => p).ToList();
            var sb = new StringBuilder();
            string source = _sourceIsLeft ? _options.LeftName : _options.RightName, target = _sourceIsLeft ? _options.RightName : _options.LeftName;

            sb.Append("/*\r\n");
            sb.Append("    Change script from SQLExtended Schema Compare, ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append("\r\n\r\n");
            sb.Append("    Makes the target like the source. Run it against the target database.\r\n");
            sb.Append("    Source: ").Append(source).Append("\r\n");
            sb.Append("    Target: ").Append(target).Append("\r\n\r\n");
            sb.Append($"    {_result.Created} to create, {_result.Altered} to change, {_result.Dropped} to drop.\r\n");
            if (_result.Warnings.Count > 0)
            {
                sb.Append($"\r\n    {_result.Warnings.Count} warning{(_result.Warnings.Count == 1 ? "" : "s")} (each is also a -- WARNING or -- REVIEW comment where it applies):\r\n");
                foreach (var w in _result.Warnings) sb.Append("      - ").Append(w.Replace("*/", "* /")).Append("\r\n");
            }

            if (_result.NotDropped.Count > 0)
            {
                sb.Append($"\r\n    Only on the target, and not dropped (tick \"Drop objects only on the target\" to drop them):\r\n");
                foreach (var n in _result.NotDropped.Take(50)) sb.Append("      - ").Append(n.Replace("*/", "* /")).Append("\r\n");
                if (_result.NotDropped.Count > 50) sb.Append($"      … and {_result.NotDropped.Count - 50} more\r\n");
            }

            if (_options.UseTransaction && _hasFullText)
                sb.Append("\r\n    Full-text index changes can't run inside a transaction: move them out, or untick \"Wrap in a transaction\".\r\n");
            if (Regex.IsMatch(string.Join("\n", batches), @"\$\([A-Za-z_][\w]*\)")) sb.Append("\r\n    It uses SQLCMD variables, $(Name): run it in SQLCMD mode, or map them under Equivalent names.\r\n");
            sb.Append("*/\r\n");

            if (batches.Count == 0)
            {
                sb.Append("\r\n-- Nothing to change.\r\n");
                _result.Text = sb.ToString();
                return _result;
            }

            void Batch(string text) => sb.Append("\r\n").Append(text.TrimEnd()).Append("\r\nGO\r\n");

            Batch("SET NUMERIC_ROUNDABORT OFF");
            Batch("SET ANSI_PADDING, ANSI_WARNINGS, CONCAT_NULL_YIELDS_NULL, ARITHABORT, QUOTED_IDENTIFIER, ANSI_NULLS ON");
            if (_options.UseTransaction)
            {
                // Any error rolls the whole transaction back (XACT_ABORT), and NOEXEC stops the batches after it from running.
                Batch("SET XACT_ABORT ON");
                Batch("BEGIN TRANSACTION");
            }

            foreach (var b in batches)
            {
                bool commentOnly = b.Split('\n').All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal)) || b.TrimStart().StartsWith("-- REVIEW", StringComparison.Ordinal);
                if (commentOnly)
                {
                    sb.Append("\r\n").Append(b.TrimEnd()).Append("\r\n");
                    continue;
                }

                Batch(b);
                if (_options.UseTransaction && !b.StartsWith("PRINT ", StringComparison.Ordinal)) Batch("IF @@ERROR <> 0 SET NOEXEC ON");
            }

            if (_options.UseTransaction)
            {
                Batch("COMMIT TRANSACTION");
                Batch("IF @@ERROR <> 0 SET NOEXEC ON");
                // Under NOEXEC the SET below never runs, so @Succeeded is NULL exactly when something failed.
                Batch("DECLARE @Succeeded bit\r\nSET @Succeeded = 1\r\nSET NOEXEC OFF\r\nIF @Succeeded = 1 PRINT N'The changes were applied.'\r\nELSE\r\nBEGIN\r\n    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION\r\n    PRINT N'The changes failed and were rolled back.'\r\nEND");
            }

            _result.Text = sb.ToString();
            return _result;
        }
    }
}
