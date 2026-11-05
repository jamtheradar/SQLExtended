using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLExtended.SchemaCompare.Engine;

/// <summary>
/// Reads one side's scripts into <see cref="SchemaObject"/>s. Each statement is attributed to the object it defines
/// or modifies, so a table's indexes, defaults, FKs, CHECK CONSTRAINT lines and extended properties end up on the
/// table whether they were written inline (a project) or as separate statements in the same file (an SMO export), or
/// in another file altogether. Triggers become objects of their own, since SMO scripts them inside the table's file
/// and a project keeps them in theirs.
/// </summary>
public static class ObjectExtractor
{
    public static SchemaSet Load(string path, Action<string> progress = null, CancellationToken ct = default)
    {
        var source = SourceLoader.Resolve(path);
        var set = new SchemaSet(source.RootPath, source.Kind) { FileCount = source.Files.Count };
        set.Warnings.AddRange(source.Warnings);

        progress?.Invoke($"Parsing {source.Files.Count} files in {source.RootPath}");
        var results = new List<Extracted>[source.Files.Count];
        var warnings = new List<string>[source.Files.Count];
        int done = 0;

        Parallel.For(0, source.Files.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var w = new List<string>();
            results[i] = ExtractFile(source.RootPath, source.Files[i], w);
            warnings[i] = w;
            int n = Interlocked.Increment(ref done);
            if (n % 250 == 0) progress?.Invoke($"Parsed {n} of {source.Files.Count} files in {source.RootPath}");
        });

        foreach (var w in warnings) set.Warnings.AddRange(w);
        Merge(set, results.SelectMany(r => r));
        return set;
    }

    /// <summary>Reads a set from in-memory scripts (relative path → text). Used by the tests.</summary>
    public static SchemaSet FromScripts(IEnumerable<KeyValuePair<string, string>> scripts, SourceKind kind = SourceKind.Folder)
    {
        var set = new SchemaSet("", kind);
        var all = new List<Extracted>();
        foreach (var script in scripts)
        {
            all.AddRange(ExtractText(script.Key, script.Key, script.Value, set.Warnings));
            set.FileCount++;
        }

        Merge(set, all);
        return set;
    }

    // ---- per file -------------------------------------------------------------------------------------------

    /// <summary>A definition (Owner set) or a statement that belongs to another object (OwnerCandidates set).</summary>
    private sealed class Extracted
    {
        public SchemaObjectType Type;
        public string Schema, Name, Parent;
        public SchemaObjectType[] OwnerCandidates;
        public readonly List<SourceStatement> Statements = new();
        public bool IsChild => OwnerCandidates != null;
    }

    private static List<Extracted> ExtractFile(string root, string file, List<string> warnings)
    {
        try
        {
            string text = File.ReadAllText(file);
            return ExtractText(file, SourceLoader.MakeRelative(root, file), text, warnings);
        }
        catch (Exception ex)
        {
            warnings.Add($"{SourceLoader.MakeRelative(root, file)}: {ex.Message}");
            return new List<Extracted>();
        }
    }

    private static readonly Regex GoLine = new(@"^[ \t]*GO(?:[ \t]+\d+)?[ \t]*(?:--[^\r\n]*)?\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<Extracted> ExtractText(string file, string relative, string text, List<string> warnings)
    {
        var items = new List<Extracted>();
        text = PrepareSqlCmd(text);

        var parser = Sql.NewParser();
        var script = (TSqlScript)parser.Parse(new StringReader(text), out IList<ParseError> errors);
        if (errors.Count == 0)
        {
            ExtractScript(script, file, relative, items);
            return items;
        }

        // Parse each GO batch on its own, so one bad batch costs that batch and not the file.
        var batches = GoLine.Split(text);
        int index = 0;
        foreach (string batch in batches)
        {
            index++;
            if (batch.Trim().Length == 0) continue;
            var batchScript = (TSqlScript)Sql.NewParser().Parse(new StringReader(batch), out IList<ParseError> batchErrors);
            if (batchErrors.Count == 0)
            {
                ExtractScript(batchScript, file, relative, items);
                continue;
            }

            var first = batchErrors[0];
            warnings.Add($"{relative}: batch {index} didn't parse (line {first.Line}: {first.Message}); compared as text.");
            items.Add(Unparsed(file, relative, batch, index));
        }

        return items;
    }

    /// <summary>
    /// SQLCMD syntax in project scripts (<c>:r</c>, <c>:setvar</c>, <c>$(Var)</c>) isn't T-SQL. Directives become
    /// comments and a bare variable becomes a bracketed name, which parses wherever an identifier can go.
    /// </summary>
    private static string PrepareSqlCmd(string text)
    {
        if (text.IndexOf(':') < 0 && text.IndexOf("$(", StringComparison.Ordinal) < 0) return text;
        text = Regex.Replace(text, @"^([ \t]*):(?=[A-Za-z])", "$1--:", RegexOptions.Multiline);
        return Regex.Replace(text, @"(?<![\[\w])\$\((\w+)\)(?!\])", "[$($1)]");
    }

    private static readonly Regex UnparsedHeader = new(
        @"^(?:\s|--[^\n]*\n|/\*.*?\*/)*(?:CREATE(?:\s+OR\s+ALTER)?|ALTER)\s+(?<kind>PROC(?:EDURE)?|VIEW|FUNCTION|TRIGGER|TABLE|SYNONYM|SEQUENCE)\s+(?<name>(?:\[[^\]]+\]|""[^""]+""|[\w@#$]+)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[\w@#$]+))*)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>A batch that didn't parse is still matched to its object by the header, when the header is readable.</summary>
    private static Extracted Unparsed(string file, string relative, string batch, int index)
    {
        var item = new Extracted { Type = SchemaObjectType.Other, Schema = "", Name = relative + " #" + index };
        var m = UnparsedHeader.Match(batch);
        if (m.Success)
        {
            var parts = Regex.Matches(m.Groups["name"].Value, @"\[[^\]]+\]|""[^""]+""|[\w@#$]+").Cast<Match>().Select(p => p.Value.Trim('[', ']', '"')).ToList();
            item.Name = parts[parts.Count - 1];
            item.Schema = parts.Count > 1 ? parts[parts.Count - 2] : Sql.DefaultSchema;
            item.Type = m.Groups["kind"].Value.ToUpperInvariant() switch
            {
                "VIEW" => SchemaObjectType.View,
                "FUNCTION" => SchemaObjectType.Function,
                "TRIGGER" => SchemaObjectType.Trigger,
                "TABLE" => SchemaObjectType.Table,
                "SYNONYM" => SchemaObjectType.Synonym,
                "SEQUENCE" => SchemaObjectType.Sequence,
                _ => SchemaObjectType.Procedure,
            };
        }

        item.Statements.Add(new SourceStatement(file, relative, batch.Trim(), false) { Parsed = false });
        return item;
    }

    private static void ExtractScript(TSqlScript script, string file, string relative, List<Extracted> items)
    {
        var tokens = script.ScriptTokenStream;
        int next = 0;
        var pendingSettings = new List<SourceStatement>();

        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                // The statement's text runs from just after the previous statement, so a comment block that leads it
                // (a procedure's header) is part of it. GO lines between batches are left out.
                var sb = new StringBuilder();
                for (int i = next; i <= statement.LastTokenIndex && i < tokens.Count; i++)
                {
                    if (tokens[i].TokenType != TSqlTokenType.Go) sb.Append(tokens[i].Text);
                }

                next = statement.LastTokenIndex + 1;
                string text = sb.ToString().Trim();

                if (statement is SetOnOffStatement or SetTextSizeStatement or SetCommandStatement)
                {
                    pendingSettings.Add(new SourceStatement(file, relative, text, isSessionSetting: true));
                    continue;
                }

                if (statement is UseStatement or PrintStatement) continue;

                var item = Classify(statement);
                item.Statements.AddRange(pendingSettings);
                pendingSettings.Clear();
                item.Statements.Add(new SourceStatement(file, relative, text, false));
                items.Add(item);
            }
        }
    }

    // ---- classification ------------------------------------------------------------------------------------

    private static (string Schema, string Name) NameOf(SchemaObjectName name, string defaultSchema = Sql.DefaultSchema)
        => (name?.SchemaIdentifier?.Value ?? defaultSchema, name?.BaseIdentifier?.Value ?? "");

    private static Extracted Def(SchemaObjectType type, SchemaObjectName name)
    {
        var (schema, n) = NameOf(name);
        return new Extracted { Type = type, Schema = schema, Name = n };
    }

    private static Extracted Def(SchemaObjectType type, string schema, string name) => new() { Type = type, Schema = schema ?? "", Name = name ?? "" };

    private static Extracted Child(SchemaObjectName owner, params SchemaObjectType[] candidates)
    {
        var (schema, n) = NameOf(owner);
        return new Extracted { Schema = schema, Name = n, OwnerCandidates = candidates };
    }

    private static Extracted Child(string schema, string name, params SchemaObjectType[] candidates)
        => new() { Schema = schema ?? "", Name = name ?? "", OwnerCandidates = candidates };

    private static readonly SchemaObjectType[] TableOrView = { SchemaObjectType.Table, SchemaObjectType.View };

    private static Extracted Classify(TSqlStatement statement)
    {
        switch (statement)
        {
            case CreateTableStatement t: return Def(SchemaObjectType.Table, t.SchemaObjectName);

            // ALTER TABLE … ENABLE/DISABLE TRIGGER belongs to the trigger, which is its own object.
            case AlterTableTriggerModificationStatement atm when atm.TriggerNames.Count == 1:
                return Child(atm.SchemaObjectName.SchemaIdentifier?.Value ?? Sql.DefaultSchema, atm.TriggerNames[0].Value, SchemaObjectType.Trigger);
            case AlterTableStatement a: return Child(a.SchemaObjectName, SchemaObjectType.Table);

            case CreateIndexStatement i: return Child(i.OnName, TableOrView);
            case CreateColumnStoreIndexStatement c: return Child(c.OnName, TableOrView);
            case CreateXmlIndexStatement x: return Child(x.OnName, TableOrView);
            case CreateSelectiveXmlIndexStatement sx: return Child(sx.OnName, TableOrView);
            case CreateSpatialIndexStatement sp: return Child(sp.Object, TableOrView);
            case CreateFullTextIndexStatement ft: return Child(ft.OnName, TableOrView);
            case AlterIndexStatement ai: return Child(ai.OnName, TableOrView);
            case CreateStatisticsStatement st: return Child(st.OnName, TableOrView);

            case ViewStatementBody v: return Def(SchemaObjectType.View, v.SchemaObjectName);
            case ProcedureStatementBody p: return Def(SchemaObjectType.Procedure, p.ProcedureReference?.Name);
            case FunctionStatementBody f: return Def(SchemaObjectType.Function, f.Name);

            case TriggerStatementBody tr when tr.TriggerObject?.TriggerScope == TriggerScope.Normal:
            {
                // An unqualified trigger name takes its table's schema.
                var (parentSchema, parentName) = NameOf(tr.TriggerObject.Name);
                var (schema, name) = NameOf(tr.Name, parentSchema);
                return new Extracted { Type = SchemaObjectType.Trigger, Schema = schema, Name = name, Parent = parentSchema + "." + parentName };
            }
            case TriggerStatementBody dtr: return Def(SchemaObjectType.DatabaseTrigger, "", dtr.Name?.BaseIdentifier?.Value);
            case EnableDisableTriggerStatement ed when ed.TriggerNames.Count == 1 && ed.TriggerObject?.TriggerScope == TriggerScope.Normal:
            {
                var (parentSchema, _) = NameOf(ed.TriggerObject.Name);
                var (schema, name) = NameOf(ed.TriggerNames[0], parentSchema);
                return Child(schema, name, SchemaObjectType.Trigger);
            }

            case CreateTypeTableStatement tt: return Def(SchemaObjectType.TableType, tt.Name);
            case CreateTypeStatement ct: return Def(SchemaObjectType.UserDefinedType, ct.Name);
            case CreateSequenceStatement cs: return Def(SchemaObjectType.Sequence, cs.Name);
            case AlterSequenceStatement aseq: return Child(aseq.Name, SchemaObjectType.Sequence);
            case CreateSynonymStatement syn: return Def(SchemaObjectType.Synonym, syn.Name);
            case CreateSchemaStatement sch: return Def(SchemaObjectType.Schema, "", sch.Name?.Value);
            case AlterSchemaStatement asch: return Child("", asch.Name?.Value, SchemaObjectType.Schema);

            case CreateUserStatement u: return Def(SchemaObjectType.User, "", u.Name?.Value);
            case AlterUserStatement au: return Child("", au.Name?.Value, SchemaObjectType.User);
            case CreateRoleStatement r: return Def(SchemaObjectType.Role, "", r.Name?.Value);
            case AlterRoleStatement ar: return Child("", ar.Name?.Value, SchemaObjectType.Role);
            case CreateLoginStatement l: return Def(SchemaObjectType.Login, "", l.Name?.Value);
            case SecurityStatement g:
            {
                var parts = g.SecurityTargetObject?.ObjectName?.MultiPartIdentifier?.Identifiers;
                if (parts == null || parts.Count == 0) return Def(SchemaObjectType.Permissions, "", "(database)");
                string name = parts[parts.Count - 1].Value;
                string schema = parts.Count > 1 ? parts[parts.Count - 2].Value : "";
                return Def(SchemaObjectType.Permissions, schema, name);
            }

            case ExecuteStatement exec:
                return ClassifyExec(exec);
        }

        return Other(statement);
    }

    private static Extracted ClassifyExec(ExecuteStatement exec)
    {
        if (ExtendedProperty.TryParse(exec, out var prop))
        {
            if (string.Equals(prop.Level2Type, "TRIGGER", StringComparison.OrdinalIgnoreCase))
                return Child(prop.Level0Name, prop.Level2Name, SchemaObjectType.Trigger);
            if (prop.Level1Name != null)
                return Child(prop.Level0Name ?? Sql.DefaultSchema, prop.Level1Name, ExtendedProperty.OwnerTypes(prop.Level1Type));
            if (prop.Level0Name != null && string.Equals(prop.Level0Type, "SCHEMA", StringComparison.OrdinalIgnoreCase))
                return Child("", prop.Level0Name, SchemaObjectType.Schema);
            return Def(SchemaObjectType.ExtendedProperties, "", "(database)");
        }

        string proc = ExtendedProperty.ProcedureName(exec) ?? "";
        if (proc.Equals("sp_addrolemember", StringComparison.OrdinalIgnoreCase))
        {
            var args = ExtendedProperty.Arguments(exec, new[] { "@rolename", "@membername" });
            string role = args.FirstOrDefault(a => a.Key.Equals("@rolename", StringComparison.OrdinalIgnoreCase)).Value;
            return Child("", role, SchemaObjectType.Role);
        }

        if (proc.Equals("sp_addrole", StringComparison.OrdinalIgnoreCase))
        {
            var args = ExtendedProperty.Arguments(exec, new[] { "@rolename" });
            return Def(SchemaObjectType.Role, "", args.FirstOrDefault().Value);
        }

        return Other(exec);
    }

    /// <summary>Anything else is keyed by its statement type and the first name in it, so the same statement on both sides still matches.</summary>
    private static Extracted Other(TSqlStatement statement)
    {
        var finder = new FirstNameFinder();
        statement.Accept(finder);
        string kind = statement.GetType().Name;
        if (kind.EndsWith("Statement", StringComparison.Ordinal)) kind = kind.Substring(0, kind.Length - "Statement".Length);
        return Def(SchemaObjectType.Other, finder.Schema ?? "", kind + " " + (finder.Name ?? "(unnamed)"));
    }

    private sealed class FirstNameFinder : TSqlFragmentVisitor
    {
        public string Schema, Name;

        public override void ExplicitVisit(SchemaObjectName node)
        {
            if (Name != null) return;
            Schema = node.SchemaIdentifier?.Value;
            Name = node.BaseIdentifier?.Value;
        }

        public override void ExplicitVisit(Identifier node)
        {
            if (Name == null) Name = node.Value;
        }
    }

    // ---- merge ----------------------------------------------------------------------------------------------

    private static void Merge(SchemaSet set, IEnumerable<Extracted> extracted)
    {
        var children = new List<Extracted>();
        foreach (var e in extracted)
        {
            if (e.IsChild)
            {
                children.Add(e);
                continue;
            }

            var obj = GetOrAdd(set, e.Type, e.Schema, e.Name);
            if (e.Parent != null) obj.Parent = e.Parent;
            obj.Statements.AddRange(e.Statements);
        }

        // Children after every definition, so an index in one file finds a table defined in another.
        foreach (var c in children)
        {
            SchemaObject owner = null;
            foreach (var type in c.OwnerCandidates)
            {
                if (set.Objects.TryGetValue(SchemaObject.MakeKey(type, c.Schema, c.Name), out owner)) break;
            }

            owner ??= GetOrAdd(set, c.OwnerCandidates[0], c.Schema, c.Name);
            owner.Statements.AddRange(c.Statements);
        }
    }

    private static SchemaObject GetOrAdd(SchemaSet set, SchemaObjectType type, string schema, string name)
    {
        string key = SchemaObject.MakeKey(type, schema, name);
        if (!set.Objects.TryGetValue(key, out var obj))
        {
            obj = new SchemaObject(type, schema, name);
            set.Objects.Add(key, obj);
        }

        return obj;
    }
}
