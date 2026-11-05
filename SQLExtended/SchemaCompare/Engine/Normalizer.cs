using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLExtended.SchemaCompare.Engine;

/// <summary>
/// Turns an object's statements into the text that is compared, with every difference the options ignore taken out.
///
/// <para>Two strategies, because the two sides write them differently in two different ways:</para>
/// <list type="bullet">
/// <item><b>Tables and other declarative objects are rebuilt from the parse tree</b> into one canonical shape: columns
/// with their defaults inline, every other constraint inside the CREATE TABLE sorted by kind and name, then indexes
/// sorted by name, then disabled constraints, then extended properties. SMO's separate <c>ALTER TABLE … ADD DEFAULT … FOR</c>
/// and <c>WITH CHECK ADD CONSTRAINT</c> statements and a project's inline <c>DEFAULT ((0))</c> land on the same line.
/// Formatting comes from ScriptDom's generator, so layout, keyword case and bracketing never differ. Comments in a
/// CREATE TABLE are lost this way; the Original view still has them.</item>
/// <item><b>Modules (procedures, views, functions, triggers) keep their author's text</b>, filtered token by token
/// (comments, whitespace, brackets, semicolons, CREATE OR ALTER). Re-generating them would hide real edits behind
/// the generator's layout.</item>
/// </list>
/// Each statement is parsed again here rather than reusing the extractor's tree: the normaliser mutates the tree,
/// and an options change has to start from the original.
/// </summary>
public static class Normalizer
{
    public const string BatchSeparator = "GO";

    /// <summary>The object's statements as written, separated by GO, for the Original view.</summary>
    public static string Raw(SchemaObject obj)
    {
        if (obj == null) return "";
        var sb = new StringBuilder();
        foreach (var s in obj.Statements)
        {
            if (sb.Length > 0) sb.Append("\r\n").Append(BatchSeparator).Append("\r\n\r\n");
            sb.Append(s.Text);
        }

        return NormalizeNewLines(sb.ToString());
    }

    public static string Normalize(SchemaObject obj, CompareOptions o)
    {
        if (obj == null) return "";

        var settings = new List<string>();
        var body = new List<string>();
        var table = new TableModel(o);

        foreach (var s in obj.Statements)
        {
            if (s.IsSessionSetting)
            {
                if (!o.IgnoreSessionSettings) settings.Add(Generate(Parse(s.Text)) ?? ModuleText(s.Text, o));
                continue;
            }

            if (!s.Parsed)
            {
                body.Add(PlainText(s.Text, o));
                continue;
            }

            var statement = Parse(s.Text);
            if (statement == null)
            {
                body.Add(PlainText(s.Text, o));
                continue;
            }

            if (table.TryAdd(statement)) continue;

            switch (statement)
            {
                case ViewStatementBody or ProcedureStatementBody or FunctionStatementBody or TriggerStatementBody:
                    body.Add(ModuleText(s.Text, o));
                    break;

                case EnableDisableTriggerStatement ed:
                    if (ed.TriggerEnforcement == TriggerEnforcement.Disable) table.Tail.Add(Generate(ed));
                    break;

                case AlterTableTriggerModificationStatement atm:
                    if (atm.TriggerEnforcement == TriggerEnforcement.Disable) table.Tail.Add("DISABLE TRIGGER " + string.Join(", ", atm.TriggerNames.Select(n => Bracket(n.Value))));
                    break;

                case CreateSchemaStatement schema:
                    if (o.IgnoreSchemaOwnerDbo && string.Equals(schema.Owner?.Value, "dbo", StringComparison.OrdinalIgnoreCase)) schema.Owner = null;
                    body.Add(GenerateCanonical(schema, o));
                    break;

                case CreateTypeStatement or CreateSequenceStatement or AlterSequenceStatement or CreateSynonymStatement or CreateUserStatement
                    or CreateRoleStatement or AlterRoleStatement or SecurityStatement or CreateLoginStatement or AlterUserStatement or AlterSchemaStatement:
                    body.Add(GenerateCanonical(statement, o));
                    break;

                case ExecuteStatement exec when !ExtendedProperty.IsProcedure(exec, "sp_addextendedproperty"):
                    body.Add(GenerateCanonical(exec, o));
                    break;

                default:
                    body.Add(ModuleText(s.Text, o));
                    break;
            }
        }

        var parts = new List<string>();
        if (settings.Count > 0) parts.Add(string.Join("\r\n", settings.Distinct(StringComparer.OrdinalIgnoreCase)));

        // Role memberships and permissions arrive as one statement each and in no particular order.
        if (obj.IsSecurity) body.Sort(StringComparer.OrdinalIgnoreCase);
        parts.AddRange(body);
        parts.AddRange(table.Render(obj));

        return NormalizeNewLines(string.Join("\r\n\r\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.TrimEnd())));
    }

    // ---- parsing and generation -----------------------------------------------------------------------------

    internal static TSqlStatement Parse(string text)
    {
        var parser = Sql.NewParser();
        var script = parser.Parse(new StringReader(text), out IList<ParseError> errors) as TSqlScript;
        if (errors.Count > 0 || script == null) return null;
        return script.Batches.SelectMany(b => b.Statements).FirstOrDefault();
    }

    internal static string Generate(TSqlFragment fragment)
    {
        if (fragment == null) return null;
        Sql.NewGenerator().GenerateScript(fragment, out string text);
        return text.Trim();
    }

    /// <summary>Generated text with names bracketed and built-in types upper-cased, the same on both sides.</summary>
    internal static string GenerateCanonical(TSqlFragment fragment, CompareOptions o)
    {
        Canonicalize(fragment, o);
        return Generate(fragment);
    }

    internal static void Canonicalize(TSqlFragment fragment, CompareOptions o)
    {
        var skip = new SkipCollector();
        fragment.Accept(skip);
        fragment.Accept(new BracketVisitor(skip.Skip));
        fragment.Accept(new DataTypeVisitor());
    }

    /// <summary>Identifiers that must keep their spelling: function names, built-in type names, collations.</summary>
    private sealed class SkipCollector : TSqlFragmentVisitor
    {
        public readonly HashSet<Identifier> Skip = new(ReferenceComparer.Instance);

        public override void Visit(FunctionCall node)
        {
            if (node.FunctionName != null) Skip.Add(node.FunctionName);
        }

        public override void Visit(SqlDataTypeReference node)
        {
            if (node.Name != null) foreach (var id in node.Name.Identifiers) Skip.Add(id);
        }

        public override void Visit(UserDataTypeReference node)
        {
            if (DataTypeVisitor.IsBuiltIn(node)) foreach (var id in node.Name.Identifiers) Skip.Add(id);
        }

        public override void Visit(ColumnDefinition node)
        {
            if (node.Collation != null) Skip.Add(node.Collation);
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<Identifier>
    {
        public static readonly ReferenceComparer Instance = new();
        public bool Equals(Identifier x, Identifier y) => ReferenceEquals(x, y);
        public int GetHashCode(Identifier obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class BracketVisitor : TSqlFragmentVisitor
    {
        private readonly HashSet<Identifier> _skip;
        public BracketVisitor(HashSet<Identifier> skip) => _skip = skip;

        public override void Visit(Identifier node)
        {
            if (!_skip.Contains(node) && node.Value != null && !node.Value.StartsWith("@", StringComparison.Ordinal)) node.QuoteType = QuoteType.SquareBracket;
        }
    }

    /// <summary>
    /// <c>[nvarchar](50)</c> (SMO) and <c>NVARCHAR (50)</c> (SSDT) are the same type. A bracketed built-in name may parse as
    /// a user type, so both kinds are upper-cased and unquoted.
    /// </summary>
    private sealed class DataTypeVisitor : TSqlFragmentVisitor
    {
        private static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
        {
            "bigint", "int", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "smallmoney", "float", "real", "date", "time", "datetime",
            "datetime2", "datetimeoffset", "smalldatetime", "char", "varchar", "text", "nchar", "nvarchar", "ntext", "binary", "varbinary", "image",
            "uniqueidentifier", "xml", "sql_variant", "timestamp", "rowversion", "hierarchyid", "geometry", "geography", "sysname", "json", "vector",
        };

        public static bool IsBuiltIn(DataTypeReference node)
            => node is UserDataTypeReference u && u.Name?.SchemaIdentifier == null && u.Name?.BaseIdentifier != null && BuiltIn.Contains(u.Name.BaseIdentifier.Value);

        public override void Visit(SqlDataTypeReference node)
        {
            Upper(node.Name);
            DropDefaultSize(node);
        }

        public override void Visit(UserDataTypeReference node)
        {
            if (!IsBuiltIn(node)) return;
            Upper(node.Name);
            DropDefaultSize(node);
        }

        /// <summary>
        /// The size SQL Server assumes when none is given is the same type: FLOAT(53), DATETIME2(7), TIME(7),
        /// DATETIMEOFFSET(7), DECIMAL(18,0)/(18), and a length of 1 for the character and binary types. A project often
        /// writes one form and the server scripts the other (HangFire's FLOAT vs FLOAT (53)).
        /// </summary>
        private static void DropDefaultSize(ParameterizedDataTypeReference node)
        {
            string type = node.Name?.BaseIdentifier?.Value?.ToUpperInvariant();
            var p = node.Parameters.Select(x => (x as Literal)?.Value).ToList();
            if (type == null || p.Count == 0 || p.Any(v => v == null)) return;

            bool isDefault = type switch
            {
                "FLOAT" => p.Count == 1 && p[0] == "53",
                "DATETIME2" or "TIME" or "DATETIMEOFFSET" => p.Count == 1 && p[0] == "7",
                "DECIMAL" or "NUMERIC" => p[0] == "18" && (p.Count == 1 || p[1] == "0"),
                "CHAR" or "VARCHAR" or "NCHAR" or "NVARCHAR" or "BINARY" or "VARBINARY" => p.Count == 1 && p[0] == "1",
                _ => false,
            };
            if (isDefault) node.Parameters.Clear();
        }

        private static void Upper(SchemaObjectName name)
        {
            if (name == null) return;
            foreach (var id in name.Identifiers)
            {
                id.QuoteType = QuoteType.NotQuoted;
                id.Value = id.Value?.ToUpperInvariant();
            }
        }
    }

    // ---- the table model ------------------------------------------------------------------------------------

    private static readonly HashSet<string> DefaultIndexOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAD_INDEX = OFF", "STATISTICS_NORECOMPUTE = OFF", "IGNORE_DUP_KEY = OFF", "ALLOW_ROW_LOCKS = ON", "ALLOW_PAGE_LOCKS = ON",
        "SORT_IN_TEMPDB = OFF", "DROP_EXISTING = OFF", "ONLINE = OFF", "OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF", "STATISTICS_INCREMENTAL = OFF",
        "DATA_COMPRESSION = NONE", "XML_COMPRESSION = OFF", "FILLFACTOR = 0", "FILLFACTOR = 100", "MAXDOP = 0", "RESUMABLE = OFF",
    };

    private static readonly Regex SystemName = new(@"^(PK|UQ|DF|CK|FK)__.+__[0-9A-Fa-f]{8,16}$", RegexOptions.Compiled);

    /// <summary>
    /// A table's parts, read with <paramref name="o"/>'s normalisation, for <see cref="ChangeScripter"/> to diff element by
    /// element. Every parsed statement that belongs to the table is taken; anything else is skipped.
    /// </summary>
    internal static TableModel BuildTable(SchemaObject obj, CompareOptions o)
    {
        var table = new TableModel(o);
        foreach (var s in obj.Statements)
        {
            if (s.IsSessionSetting || !s.Parsed) continue;
            var statement = Parse(s.Text);
            if (statement != null) table.TryAdd(statement);
        }

        table.Prepare();
        return table;
    }

    /// <summary>One index of a table: its name (or text, for one without), its rendered text and the columns it uses.</summary>
    internal sealed class IndexPart
    {
        public string Name;
        public string Key;
        public string Text;
        public bool IsUnique;
        public bool IsAlter;
        public bool IsFullText;
        public HashSet<string> Columns;
    }

    /// <summary>Collects every statement that defines part of a table (or table type), and renders the canonical form.</summary>
    internal sealed class TableModel
    {
        private readonly CompareOptions _o;
        private TSqlStatement _create;
        private TableDefinition _definition;
        private SchemaObjectName _name;
        private readonly List<ColumnDefinition> _columns = new();
        private readonly List<(ConstraintDefinition Constraint, bool NotTrusted)> _constraints = new();
        private readonly List<DefaultConstraintDefinition> _looseDefaults = new();
        private readonly List<IndexPart> _indexes = new();
        private readonly List<string> _disabled = new();
        private readonly List<ExtendedProperty> _properties = new();
        private bool _prepared;
        public readonly List<string> Tail = new();

        public TableModel(CompareOptions o) => _o = o;

        public TSqlStatement Create => _create;
        public TableDefinition Definition => _definition;
        public SchemaObjectName TableName => _name;
        public List<ColumnDefinition> Columns => _columns;
        public List<(ConstraintDefinition Constraint, bool NotTrusted)> Constraints => _constraints;
        public List<IndexPart> Indexes => _indexes;
        public List<string> Disabled => _disabled;
        public List<ExtendedProperty> Properties => _properties;

        public bool TryAdd(TSqlStatement statement)
        {
            switch (statement)
            {
                case CreateTableStatement create:
                    _create = create;
                    _name = create.SchemaObjectName;
                    TakeDefinition(create.Definition, notTrusted: false);
                    _definition = create.Definition;
                    return true;

                case CreateTypeTableStatement type:
                    _create = type;
                    _name = type.Name;
                    TakeDefinition(type.Definition, notTrusted: false);
                    _definition = type.Definition;
                    return true;

                case AlterTableAddTableElementStatement add:
                    _name ??= add.SchemaObjectName;
                    TakeDefinition(add.Definition, add.ExistingRowsCheckEnforcement == ConstraintEnforcement.NoCheck);
                    return true;

                case AlterTableConstraintModificationStatement mod:
                    // WITH CHECK ADD … followed by CHECK CONSTRAINT is SMO saying "enabled", which is the default.
                    if (mod.ConstraintEnforcement == ConstraintEnforcement.NoCheck)
                    {
                        string names = mod.All ? "ALL" : string.Join(", ", mod.ConstraintNames.Select(n => Bracket(n.Value)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                        _disabled.Add($"ALTER TABLE {Name(mod.SchemaObjectName)} NOCHECK CONSTRAINT {names}");
                    }

                    return true;

                case CreateIndexStatement index:
                    if (!_o.IgnoreIndexes) AddIndex(index.Name?.Value, index.Unique, index, () => RenderIndex(index));
                    return true;

                case IndexStatement or CreateColumnStoreIndexStatement or CreateXmlIndexStatement or CreateSelectiveXmlIndexStatement or CreateSpatialIndexStatement
                    or CreateFullTextIndexStatement:
                    if (!_o.IgnoreIndexes) AddIndex(IndexName(statement), false, statement, () => GenerateCanonical(statement, _o));
                    return true;

                case ExecuteStatement exec when ExtendedProperty.TryParse(exec, out var property):
                    if (!_o.IgnoreExtendedProperties) _properties.Add(property);
                    return true;

                // ENABLE/DISABLE TRIGGER belongs to the trigger (its own object), handled by the caller.
                case AlterTableTriggerModificationStatement:
                    return false;

                case AlterTableStatement alter:
                    Tail.Add(GenerateCanonical(alter, _o));
                    return true;
            }

            return false;
        }

        private void AddIndex(string name, bool unique, TSqlFragment fragment, Func<string> render)
        {
            var columns = ColumnsOf(fragment);
            string text = render();
            bool isAlter = fragment is AlterIndexStatement, isFullText = fragment is CreateFullTextIndexStatement;
            string key = isFullText ? "FULLTEXT" : (name ?? text) + (isAlter ? " (alter)" : "");
            _indexes.Add(new IndexPart { Name = name, Key = key, Text = text, IsUnique = unique, IsAlter = isAlter, IsFullText = isFullText, Columns = columns });
        }

        private static string IndexName(TSqlStatement statement) => statement switch
        {
            IndexStatement i => i.Name?.Value,
            CreateColumnStoreIndexStatement c => c.Name?.Value,
            CreateSpatialIndexStatement s => s.Name?.Value,
            _ => null,
        };

        private void TakeDefinition(TableDefinition definition, bool notTrusted)
        {
            if (definition == null) return;

            foreach (var column in definition.ColumnDefinitions)
            {
                _columns.Add(column);

                // Column-level PK/UNIQUE/FK/CHECK move to table level, where SMO always writes them.
                foreach (var c in column.Constraints.Where(c => c is not NullableConstraintDefinition).ToList())
                {
                    column.Constraints.Remove(c);
                    ToTableLevel(c, column.ColumnIdentifier);
                    _constraints.Add((c, notTrusted));
                }

                if (column.Index != null && !_o.IgnoreIndexes)
                {
                    var index = column.Index;
                    column.Index = null;
                    if (index.Columns.Count == 0) index.Columns.Add(new ColumnWithSortOrder { Column = ColumnRef(column.ColumnIdentifier), SortOrder = SortOrder.Ascending });
                    AddInlineIndex(index);
                }
            }

            foreach (var c in definition.TableConstraints)
            {
                if (c is DefaultConstraintDefinition d && d.Column != null) _looseDefaults.Add(d);
                else _constraints.Add((c, notTrusted));
            }

            foreach (var index in definition.Indexes)
            {
                if (!_o.IgnoreIndexes) AddInlineIndex(index);
            }

            definition.ColumnDefinitions.Clear();
            definition.TableConstraints.Clear();
            definition.Indexes.Clear();
        }

        private static void ToTableLevel(ConstraintDefinition c, Identifier column)
        {
            switch (c)
            {
                case UniqueConstraintDefinition u when u.Columns.Count == 0:
                    u.Columns.Add(new ColumnWithSortOrder { Column = ColumnRef(column), SortOrder = SortOrder.Ascending });
                    break;
                case ForeignKeyConstraintDefinition f when f.Columns.Count == 0:
                    f.Columns.Add(new Identifier { Value = column.Value, QuoteType = column.QuoteType });
                    break;
            }
        }

        private static ColumnReferenceExpression ColumnRef(Identifier column)
        {
            var multi = new MultiPartIdentifier();
            multi.Identifiers.Add(new Identifier { Value = column.Value, QuoteType = column.QuoteType });
            return new ColumnReferenceExpression { ColumnType = ColumnType.Regular, MultiPartIdentifier = multi };
        }

        private void AddInlineIndex(IndexDefinition def) => AddIndex(def.Name?.Value, def.Unique, def, () => RenderInlineIndex(def));

        private string RenderInlineIndex(IndexDefinition def)
        {
            var kind = def.IndexType?.IndexTypeKind;
            if (kind != null && kind != IndexTypeKind.Clustered && kind != IndexTypeKind.NonClustered)
            {
                CleanIndexOptions(def.IndexOptions);
                def.OnFileGroupOrPartitionScheme = CleanFileGroup(def.OnFileGroupOrPartitionScheme);
                return "INDEX " + GenerateCanonical(def, _o);
            }

            var create = new CreateIndexStatement
            {
                Name = def.Name,
                OnName = _name,
                Unique = def.Unique,
                Clustered = kind == IndexTypeKind.Clustered,
                FilterPredicate = def.FilterPredicate,
                OnFileGroupOrPartitionScheme = def.OnFileGroupOrPartitionScheme,
                FileStreamOn = def.FileStreamOn,
            };
            foreach (var c in def.Columns) create.Columns.Add(c);
            foreach (var c in def.IncludeColumns) create.IncludeColumns.Add(c);
            foreach (var option in def.IndexOptions) create.IndexOptions.Add(option);
            return RenderIndex(create);
        }

        private string RenderIndex(CreateIndexStatement index)
        {
            index.Clustered ??= false;
            DefaultSortOrder(index.Columns);
            CleanIndexOptions(index.IndexOptions);
            index.OnFileGroupOrPartitionScheme = CleanFileGroup(index.OnFileGroupOrPartitionScheme);
            if (_o.IgnoreAllFilegroups) index.FileStreamOn = null;
            return GenerateCanonical(index, _o);
        }

        private static void DefaultSortOrder(IList<ColumnWithSortOrder> columns)
        {
            foreach (var c in columns)
            {
                if (c.SortOrder == SortOrder.NotSpecified) c.SortOrder = SortOrder.Ascending;
            }
        }

        private void CleanIndexOptions(IList<IndexOption> options)
        {
            if (options == null) return;
            foreach (var option in options.ToList())
            {
                if (_o.IgnoreAllIndexOptions || (_o.IgnoreDefaultIndexOptions && DefaultIndexOptions.Contains(CollapseSpaces(Generate(option)))))
                    options.Remove(option);
            }
        }

        private FileGroupOrPartitionScheme CleanFileGroup(FileGroupOrPartitionScheme fg)
        {
            if (fg == null || _o.IgnoreAllFilegroups) return null;
            if (_o.IgnoreDefaultFilegroup && fg.PartitionSchemeColumns.Count == 0 && IsPrimary(fg.Name)) return null;
            return fg;
        }

        private static bool IsPrimary(IdentifierOrValueExpression name)
            => string.Equals(name?.Identifier?.Value ?? name?.Value, "PRIMARY", StringComparison.OrdinalIgnoreCase);

        /// <summary>Defaults back on their columns, and every column and constraint normalised. Runs once.</summary>
        public void Prepare()
        {
            if (_prepared) return;
            _prepared = true;

            // Defaults written as ALTER TABLE … ADD DEFAULT … FOR [col] go back on their column.
            foreach (var d in _looseDefaults)
            {
                var column = _columns.FirstOrDefault(c => string.Equals(c.ColumnIdentifier.Value, d.Column.Value, StringComparison.OrdinalIgnoreCase));
                if (column != null && column.DefaultConstraint == null)
                {
                    d.Column = null;
                    column.DefaultConstraint = d;
                }
                else
                {
                    _constraints.Add((d, false));
                }
            }

            var keyColumns = new HashSet<string>(_constraints.Select(c => c.Constraint).OfType<UniqueConstraintDefinition>().Where(u => u.IsPrimaryKey)
                .SelectMany(u => u.Columns).Select(c => c.Column?.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value).Where(v => v != null), StringComparer.OrdinalIgnoreCase);

            foreach (var column in _columns) NormalizeColumn(column, keyColumns.Contains(column.ColumnIdentifier.Value));
            foreach (var (c, _) in _constraints) NormalizeConstraint(c);
        }

        public IEnumerable<string> Render(SchemaObject obj)
        {
            if (_create == null && _name == null && _indexes.Count == 0 && _disabled.Count == 0 && _properties.Count == 0 && Tail.Count == 0) yield break;
            Prepare();

            var columns = _o.IgnoreColumnOrder ? _columns.OrderBy(c => c.ColumnIdentifier.Value, StringComparer.OrdinalIgnoreCase).ToList() : _columns;
            var constraints = _constraints.Select(c => (c.Constraint, c.NotTrusted, Text: GenerateCanonical(c.Constraint, _o)))
                .OrderBy(c => ConstraintRank(c.Constraint)).ThenBy(c => c.Constraint.ConstraintIdentifier?.Value ?? "~" + c.Text, StringComparer.OrdinalIgnoreCase).ToList();

            if (_create != null && _definition != null)
            {
                foreach (var c in columns) _definition.ColumnDefinitions.Add(c);
                foreach (var c in constraints) _definition.TableConstraints.Add(c.Constraint);
                CleanTableOptions();
                yield return GenerateCanonical(_create, _o);
            }
            else
            {
                // Only ALTERs and indexes for this table on this side (its CREATE TABLE is missing or excluded).
                foreach (var c in columns) yield return $"ALTER TABLE {Name(_name)} ADD {GenerateCanonical(c, _o)}";
                foreach (var c in constraints) yield return $"ALTER TABLE {Name(_name)} ADD {c.Text}";
            }

            if (!_o.IgnoreConstraintTrust)
            {
                var notTrusted = constraints.Where(c => c.NotTrusted).Select(c => $"-- {Bracket(c.Constraint.ConstraintIdentifier?.Value ?? "(unnamed)")} was added WITH NOCHECK (not trusted)").ToList();
                if (notTrusted.Count > 0) yield return string.Join("\r\n", notTrusted);
            }

            if (_indexes.Count > 0) yield return string.Join("\r\n\r\n", _indexes.Select(i => i.Text).OrderBy(i => i, StringComparer.OrdinalIgnoreCase));
            if (_disabled.Count > 0) yield return string.Join("\r\n", _disabled.OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
            foreach (var t in Tail) yield return t;
            if (_properties.Count > 0) yield return string.Join("\r\n", _properties.OrderBy(p => p.SortKey, StringComparer.OrdinalIgnoreCase).Select(p => p.ToScript()));
        }

        private void CleanTableOptions()
        {
            if (_create is not CreateTableStatement table) return;
            table.OnFileGroupOrPartitionScheme = CleanFileGroup(table.OnFileGroupOrPartitionScheme);
            if (_o.IgnoreAllFilegroups || (_o.IgnoreDefaultFilegroup && IsPrimary(table.TextImageOn))) table.TextImageOn = null;
            if (_o.IgnoreAllFilegroups || (_o.IgnoreDefaultFilegroup && IsPrimary(table.FileStreamOn))) table.FileStreamOn = null;
            foreach (var option in table.Options.ToList())
            {
                if (_o.IgnoreAllIndexOptions || (_o.IgnoreDefaultIndexOptions && DefaultIndexOptions.Contains(CollapseSpaces(Generate(option))))) table.Options.Remove(option);
            }
        }

        /// <summary>
        /// The CREATE TABLE with no columns, constraints or indexes in it: what is left is the table's own options
        /// (filegroup, compression, memory-optimised, temporal…), which no ALTER in a change script reaches. Call it on a
        /// model that won't be rendered, and before its definition is filled.
        /// </summary>
        public string ShellText()
        {
            if (_create == null) return "";
            CleanTableOptions();
            return GenerateCanonical(_create, _o);
        }

        private static int ConstraintRank(ConstraintDefinition c) => c switch
        {
            UniqueConstraintDefinition u when u.IsPrimaryKey => 0,
            UniqueConstraintDefinition => 1,
            CheckConstraintDefinition => 2,
            ForeignKeyConstraintDefinition => 3,
            DefaultConstraintDefinition => 4,
            _ => 5,
        };

        private void NormalizeColumn(ColumnDefinition column, bool isKey)
        {
            if (_o.IgnoreCollation) column.Collation = null;

            if (column.IdentityOptions != null)
            {
                column.IdentityOptions.IdentitySeed ??= new IntegerLiteral { Value = "1" };
                column.IdentityOptions.IdentityIncrement ??= new IntegerLiteral { Value = "1" };
            }

            // NULL is what SQL Server assumes when nothing is said (ANSI_NULL_DFLT_ON), and a key column is NOT NULL.
            if (column.ComputedColumnExpression == null && !column.Constraints.OfType<NullableConstraintDefinition>().Any())
                column.Constraints.Add(new NullableConstraintDefinition { Nullable = !isKey });

            if (column.DefaultConstraint != null)
            {
                NormalizeName(column.DefaultConstraint);
                if (_o.IgnoreRedundantParentheses) column.DefaultConstraint.Expression = StripTop(column.DefaultConstraint.Expression);
            }

            if (column.ComputedColumnExpression != null && _o.IgnoreRedundantParentheses) column.ComputedColumnExpression = StripTop(column.ComputedColumnExpression);
        }

        private void NormalizeConstraint(ConstraintDefinition c)
        {
            NormalizeName(c);
            switch (c)
            {
                case UniqueConstraintDefinition u:
                    if (u.IndexType == null) u.Clustered ??= u.IsPrimaryKey;
                    DefaultSortOrder(u.Columns);
                    CleanIndexOptions(u.IndexOptions);
                    u.OnFileGroupOrPartitionScheme = CleanFileGroup(u.OnFileGroupOrPartitionScheme);
                    break;

                case ForeignKeyConstraintDefinition f:
                    if (f.DeleteAction == DeleteUpdateAction.NoAction) f.DeleteAction = DeleteUpdateAction.NotSpecified;
                    if (f.UpdateAction == DeleteUpdateAction.NoAction) f.UpdateAction = DeleteUpdateAction.NotSpecified;
                    break;

                case CheckConstraintDefinition check when _o.IgnoreRedundantParentheses:
                    check.CheckCondition = StripBoolean(check.CheckCondition);
                    break;

                case DefaultConstraintDefinition d when _o.IgnoreRedundantParentheses:
                    d.Expression = StripTop(d.Expression);
                    break;
            }
        }

        private void NormalizeName(ConstraintDefinition c)
        {
            if (_o.IgnoreSystemNames && c.ConstraintIdentifier != null && SystemName.IsMatch(c.ConstraintIdentifier.Value)) c.ConstraintIdentifier = null;
        }
    }

    /// <summary>The (unqualified) column names a fragment refers to, for knowing which indexes and constraints a column change touches.</summary>
    internal static HashSet<string> ColumnsOf(TSqlFragment fragment)
    {
        var visitor = new ColumnCollector();
        fragment?.Accept(visitor);
        return visitor.Columns;
    }

    private sealed class ColumnCollector : TSqlFragmentVisitor
    {
        public readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(ColumnReferenceExpression node)
        {
            string name = node.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
            if (name != null) Columns.Add(name);
        }

        public override void Visit(ForeignKeyConstraintDefinition node)
        {
            foreach (var c in node.Columns) Columns.Add(c.Value);
        }

        public override void Visit(DefaultConstraintDefinition node)
        {
            if (node.Column != null) Columns.Add(node.Column.Value);
        }
    }

    internal static bool IsSystemName(string name) => name != null && SystemName.IsMatch(name);

    // ---- redundant parentheses ------------------------------------------------------------------------------
    // SQL Server stores ((0)), (getdate()) and ([a]>(0)); hand-written projects say 0, getdate() and [a] > 0. Outer
    // layers go, and so do parentheses around a single literal, column or function call anywhere inside. Parentheses
    // around an operator expression stay: they can change precedence.

    private static ScalarExpression StripTop(ScalarExpression e)
    {
        while (e is ParenthesisExpression p) e = p.Expression;
        return StripInner(e);
    }

    private static BooleanExpression StripBoolean(BooleanExpression e)
    {
        while (e is BooleanParenthesisExpression p) e = p.Expression;
        StripInner(e);
        return e;
    }

    private static bool IsAtom(ScalarExpression e) => e is Literal or ColumnReferenceExpression or FunctionCall or VariableReference or UnaryExpression { Expression: Literal };

    private static ScalarExpression Atom(ScalarExpression e)
    {
        var inner = e;
        while (inner is ParenthesisExpression p) inner = p.Expression;
        return IsAtom(inner) ? StripInner(inner) : StripInner(e);
    }

    private static ScalarExpression StripInner(ScalarExpression e)
    {
        switch (e)
        {
            case ParenthesisExpression p: p.Expression = Atom(p.Expression); break;
            case BinaryExpression b: b.FirstExpression = Atom(b.FirstExpression); b.SecondExpression = Atom(b.SecondExpression); break;
            case UnaryExpression u: u.Expression = Atom(u.Expression); break;
            case FunctionCall f: for (int i = 0; i < f.Parameters.Count; i++) f.Parameters[i] = Atom(f.Parameters[i]); break;
            case CastCall c: c.Parameter = Atom(c.Parameter); break;
            case ConvertCall cv: cv.Parameter = Atom(cv.Parameter); break;
            case SearchedCaseExpression sc:
                foreach (var w in sc.WhenClauses) { StripInner(w.WhenExpression); w.ThenExpression = Atom(w.ThenExpression); }
                if (sc.ElseExpression != null) sc.ElseExpression = Atom(sc.ElseExpression);
                break;
        }

        return e;
    }

    private static void StripInner(BooleanExpression e)
    {
        switch (e)
        {
            case BooleanParenthesisExpression p: StripInner(p.Expression); break;
            case BooleanBinaryExpression b: StripInner(b.FirstExpression); StripInner(b.SecondExpression); break;
            case BooleanNotExpression n: StripInner(n.Expression); break;
            case BooleanComparisonExpression c: c.FirstExpression = Atom(c.FirstExpression); c.SecondExpression = Atom(c.SecondExpression); break;
            case BooleanIsNullExpression isNull: isNull.Expression = Atom(isNull.Expression); break;
            case LikePredicate like: like.FirstExpression = Atom(like.FirstExpression); like.SecondExpression = Atom(like.SecondExpression); break;
            case BooleanTernaryExpression t:
                t.FirstExpression = Atom(t.FirstExpression); t.SecondExpression = Atom(t.SecondExpression); t.ThirdExpression = Atom(t.ThirdExpression);
                break;
            case InPredicate inPredicate:
                inPredicate.Expression = Atom(inPredicate.Expression);
                for (int i = 0; i < inPredicate.Values.Count; i++) inPredicate.Values[i] = Atom(inPredicate.Values[i]);
                break;
        }
    }

    // ---- module text ----------------------------------------------------------------------------------------

    /// <summary>
    /// A module's own text with the ignored tokens taken out. Works on the token stream, so a "--" inside a string or a
    /// bracket inside a comment is never mistaken for anything.
    /// </summary>
    internal static string ModuleText(string text, CompareOptions o)
    {
        var tokens = Sql.NewParser().GetTokenStream(new StringReader(text), out IList<ParseError> errors);
        if (tokens == null || tokens.Count == 0) return PlainText(text, o);

        var sb = new StringBuilder(text.Length);
        bool headerDone = false;

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            switch (t.TokenType)
            {
                case TSqlTokenType.EndOfFile:
                    continue;

                case TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment:
                    if (o.IgnoreComments)
                    {
                        // Keep the line break a multi-line comment may have been standing in for.
                        if (t.TokenType == TSqlTokenType.MultilineComment && t.Text.IndexOf('\n') >= 0) sb.Append('\n');
                        continue;
                    }

                    break;

                case TSqlTokenType.WhiteSpace:
                    if (o.IgnoreWhitespace)
                    {
                        sb.Append(t.Text.IndexOf('\n') >= 0 ? "\n" : " ");
                        continue;
                    }

                    break;

                case TSqlTokenType.Semicolon:
                    if (o.IgnoreSemicolons) continue;
                    break;

                case TSqlTokenType.QuotedIdentifier:
                    if (o.IgnoreBrackets)
                    {
                        sb.Append(Unbracket(t.Text));
                        continue;
                    }

                    break;
            }

            if (!headerDone && o.IgnoreCreateOrAlter && IsSignificant(t))
            {
                headerDone = true;
                if (t.TokenType == TSqlTokenType.Alter)
                {
                    sb.Append("CREATE");
                    continue;
                }

                if (t.TokenType == TSqlTokenType.Create)
                {
                    int or = NextSignificant(tokens, i + 1);
                    int alter = or < 0 ? -1 : NextSignificant(tokens, or + 1);
                    if (or > 0 && alter > 0 && tokens[or].TokenType == TSqlTokenType.Or && tokens[alter].TokenType == TSqlTokenType.Alter)
                    {
                        sb.Append("CREATE");
                        i = alter;
                        continue;
                    }
                }
            }
            else if (!headerDone && IsSignificant(t))
            {
                headerDone = true;
            }

            sb.Append(t.Text);
        }

        return Tidy(sb.ToString(), o);
    }

    private static bool IsSignificant(TSqlParserToken t)
        => t.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile);

    private static int NextSignificant(IList<TSqlParserToken> tokens, int from)
    {
        for (int i = from; i < tokens.Count; i++)
        {
            if (IsSignificant(tokens[i])) return i;
        }

        return -1;
    }

    /// <summary>Text that didn't parse: the same filters, done with regular expressions instead of tokens.</summary>
    internal static string PlainText(string text, CompareOptions o)
    {
        if (o.IgnoreComments)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", m => m.Value.IndexOf('\n') >= 0 ? "\n" : " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"--[^\r\n]*", "");
        }

        if (o.IgnoreBrackets) text = Regex.Replace(text, @"\[[^\]\r\n]+\]", m => Unbracket(m.Value));
        if (o.IgnoreSemicolons) text = text.Replace(";", "");
        if (o.IgnoreWhitespace) text = Regex.Replace(text, @"[ \t]+", " ");
        return Tidy(text, o);
    }

    private static string Tidy(string text, CompareOptions o)
    {
        var lines = NormalizeNewLines(text).Split(new[] { "\r\n" }, StringSplitOptions.None).Select(l => o.IgnoreWhitespace ? l.Trim() : l.TrimEnd());
        if (o.IgnoreWhitespace) lines = lines.Where(l => l.Length > 0);
        return string.Join("\r\n", lines).Trim('\r', '\n');
    }

    // ---- names ----------------------------------------------------------------------------------------------

    private static readonly Regex RegularIdentifier = new(@"^[A-Za-z_@#][A-Za-z0-9_@#$]*$", RegexOptions.Compiled);

    /// <summary>[Name] → Name, unless the brackets are needed (a reserved word, a space, a leading digit…).</summary>
    internal static string Unbracket(string quoted)
    {
        if (quoted.Length < 2) return quoted;
        char open = quoted[0], close = quoted[quoted.Length - 1];
        if (!((open == '[' && close == ']') || (open == '"' && close == '"'))) return quoted;

        string inner = quoted.Substring(1, quoted.Length - 2);
        return RegularIdentifier.IsMatch(inner) && !ReservedWords.Contains(inner) ? inner : quoted;
    }

    internal static string Bracket(string name) => "[" + (name ?? "").Replace("]", "]]") + "]";

    internal static string Name(SchemaObjectName name)
        => name == null ? "" : string.Join(".", name.Identifiers.Select(i => Bracket(i.Value)));

    private static string CollapseSpaces(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    internal static string NormalizeNewLines(string text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    /// <summary>T-SQL reserved keywords: a name that is one of these needs its brackets.</summary>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUTHORIZATION", "BACKUP", "BEGIN", "BETWEEN", "BREAK", "BROWSE", "BULK", "BY", "CASCADE",
        "CASE", "CHECK", "CHECKPOINT", "CLOSE", "CLUSTERED", "COALESCE", "COLLATE", "COLUMN", "COMMIT", "COMPUTE", "CONSTRAINT", "CONTAINS",
        "CONTAINSTABLE", "CONTINUE", "CONVERT", "CREATE", "CROSS", "CURRENT", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER",
        "CURSOR", "DATABASE", "DBCC", "DEALLOCATE", "DECLARE", "DEFAULT", "DELETE", "DENY", "DESC", "DISK", "DISTINCT", "DISTRIBUTED", "DOUBLE",
        "DROP", "DUMP", "ELSE", "END", "ERRLVL", "ESCAPE", "EXCEPT", "EXEC", "EXECUTE", "EXISTS", "EXIT", "EXTERNAL", "FETCH", "FILE", "FILLFACTOR",
        "FOR", "FOREIGN", "FREETEXT", "FREETEXTTABLE", "FROM", "FULL", "FUNCTION", "GOTO", "GRANT", "GROUP", "HAVING", "HOLDLOCK", "IDENTITY",
        "IDENTITY_INSERT", "IDENTITYCOL", "IF", "IN", "INDEX", "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "KEY", "KILL", "LEFT", "LIKE",
        "LINENO", "LOAD", "MERGE", "NATIONAL", "NOCHECK", "NONCLUSTERED", "NOT", "NULL", "NULLIF", "OF", "OFF", "OFFSETS", "ON", "OPEN",
        "OPENDATASOURCE", "OPENQUERY", "OPENROWSET", "OPENXML", "OPTION", "OR", "ORDER", "OUTER", "OVER", "PERCENT", "PIVOT", "PLAN", "PRECISION",
        "PRIMARY", "PRINT", "PROC", "PROCEDURE", "PUBLIC", "RAISERROR", "READ", "READTEXT", "RECONFIGURE", "REFERENCES", "REPLICATION", "RESTORE",
        "RESTRICT", "RETURN", "REVERT", "REVOKE", "RIGHT", "ROLLBACK", "ROWCOUNT", "ROWGUIDCOL", "RULE", "SAVE", "SCHEMA", "SECURITYAUDIT", "SELECT",
        "SEMANTICKEYPHRASETABLE", "SEMANTICSIMILARITYDETAILSTABLE", "SEMANTICSIMILARITYTABLE", "SESSION_USER", "SET", "SETUSER", "SHUTDOWN",
        "SOME", "STATISTICS", "SYSTEM_USER", "TABLE", "TABLESAMPLE", "TEXTSIZE", "THEN", "TO", "TOP", "TRAN", "TRANSACTION", "TRIGGER", "TRUNCATE",
        "TRY_CONVERT", "TSEQUAL", "UNION", "UNIQUE", "UNPIVOT", "UPDATE", "UPDATETEXT", "USE", "USER", "VALUES", "VARYING", "VIEW", "WAITFOR",
        "WHEN", "WHERE", "WHILE", "WITH", "WITHIN", "WRITETEXT",
    };
}
