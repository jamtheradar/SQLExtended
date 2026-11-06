using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLExtended.ScriptCast;

/// <summary>What <see cref="ScriptCastAnalyzer"/> found: the kinds of statement that may change data or schema.</summary>
internal sealed class ScriptChangeReport
{
    /// <summary>Distinct statement kinds in script order, e.g. <c>UPDATE</c>, <c>CREATE PROCEDURE</c>, <c>EXEC</c>.</summary>
    public List<string> Kinds { get; } = new();

    /// <summary>The script didn't parse (or is SQLCMD), so nothing could be ruled out. Treated as changing.</summary>
    public bool ParseFailed { get; set; }

    public bool MayChange => ParseFailed || Kinds.Count > 0;
}

/// <summary>
/// Decides whether a script may change data or schema, for the confirm before a Select All cast (issue #13).
///
/// <para>An allow-list, not a deny-list: a statement is read-only only when it is one of the kinds below, so a
/// statement type nobody thought of counts as a change. <c>EXEC</c> counts too, since a procedure can do anything.
/// Scratch work is exempt: DML into a <c>#temp</c> table or table variable, <c>SELECT … INTO #t</c>, and
/// creating or dropping a <c>#temp</c> table. A script that doesn't parse counts as a change: being asked once too
/// often is cheaper than a silent DELETE on every database.</para>
///
/// Pure; linked into the tests.
/// </summary>
internal static class ScriptCastAnalyzer
{
    public static ScriptChangeReport Analyze(string sql)
    {
        var report = new ScriptChangeReport();
        if (string.IsNullOrWhiteSpace(sql)) return report;

        foreach (var batch in BatchSplitter.Split(sql))
        {
            var parser = new TSql170Parser(initialQuotedIdentifiers: true);
            TSqlFragment fragment;
            IList<ParseError> errors;
            using (var reader = new StringReader(batch.Text))
                fragment = parser.Parse(reader, out errors);

            if (errors.Count > 0 || fragment == null)
            {
                report.ParseFailed = true;
                continue;
            }

            fragment.Accept(new Visitor(report.Kinds));
        }

        return report;
    }

    /// <summary>
    /// Why the script can't run in transaction mode, or empty when it can (issue #13, stage 2). Two kinds of reason:
    /// the script manages its own transaction (<c>BEGIN TRAN</c>, <c>COMMIT</c>, <c>ROLLBACK</c>), which would commit
    /// or end the one ScriptCast opened behind its back, or it holds a statement SQL Server refuses inside a user
    /// transaction (<c>CREATE DATABASE</c>, <c>BACKUP</c>, full-text index DDL…). <c>SAVE TRAN</c> is allowed. A script
    /// that doesn't parse is refused: nothing in it could be ruled out. A procedure that commits can't be seen from
    /// here; the runner's <c>@@TRANCOUNT</c> check after every batch catches that one.
    /// </summary>
    public static List<string> TransactionBlockers(string sql)
    {
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(sql)) return reasons;

        foreach (var batch in BatchSplitter.Split(sql))
        {
            var parser = new TSql170Parser(initialQuotedIdentifiers: true);
            TSqlFragment fragment;
            IList<ParseError> errors;
            using (var reader = new StringReader(batch.Text))
                fragment = parser.Parse(reader, out errors);

            if (errors.Count > 0 || fragment == null)
            {
                string reason = $"The batch at line {batch.StartLine} couldn't be parsed, so it can't be checked for its own transactions.";
                if (!reasons.Contains(reason)) reasons.Add(reason);
                continue;
            }

            fragment.Accept(new BlockerVisitor(reasons, batch.StartLine));
        }

        return reasons;
    }

    private sealed class BlockerVisitor(List<string> reasons, int batchStartLine) : TSqlFragmentVisitor
    {
        public override void Visit(TSqlStatement node)
        {
            string reason = BlockerFor(node);
            if (reason == null) return;
            string line = $"{reason} (line {batchStartLine + node.StartLine - 1})";
            if (!reasons.Contains(line)) reasons.Add(line);
        }
    }

    private static string BlockerFor(TSqlStatement statement) => statement switch
    {
        BeginTransactionStatement => "BEGIN TRANSACTION: the script manages its own transaction",
        CommitTransactionStatement => "COMMIT: the script would commit the cast's transaction",
        RollbackTransactionStatement => "ROLLBACK: the script would end the cast's transaction",
        CreateDatabaseStatement or AlterDatabaseStatement or DropDatabaseStatement
            or BackupStatement or RestoreStatement
            or CreateFullTextCatalogStatement or AlterFullTextCatalogStatement or DropFullTextCatalogStatement
            or CreateFullTextIndexStatement or AlterFullTextIndexStatement or DropFullTextIndexStatement
            or ReconfigureStatement
            => KindName(statement) + ": not allowed inside a transaction",
        _ => null,
    };

    private sealed class Visitor(List<string> kinds) : TSqlFragmentVisitor
    {
        public override void Visit(TSqlStatement node)
        {
            string kind = Classify(node);
            if (kind != null && !kinds.Contains(kind)) kinds.Add(kind);
        }
    }

    /// <summary>Null when the statement can't change anything outside the session; otherwise its kind for the confirm.</summary>
    private static string Classify(TSqlStatement statement)
    {
        switch (statement)
        {
            // Containers: their statements are visited on their own.
            case BeginEndBlockStatement or IfStatement or WhileStatement or TryCatchStatement:
            // Reads, variables, flow and session options.
            case SelectStatement { Into: null }:
            case DeclareVariableStatement or SetVariableStatement or DeclareTableVariableStatement or PrintStatement:
            case ReturnStatement or BreakStatement or ContinueStatement or GoToStatement or LabelStatement or WaitForStatement:
            case ThrowStatement or RaiseErrorStatement or UseStatement:
            case SetOnOffStatement or SetRowCountStatement or SetTransactionIsolationLevelStatement or SetCommandStatement:
            case SetTextSizeStatement or SetErrorLevelStatement:
            case DeclareCursorStatement or OpenCursorStatement or FetchCursorStatement or CloseCursorStatement or DeallocateCursorStatement:
            case BeginTransactionStatement or CommitTransactionStatement or RollbackTransactionStatement or SaveTransactionStatement:
                return null;

            case SelectStatement select:
                return IsScratch(select.Into) ? null : "SELECT INTO";
            case InsertStatement insert:
                return IsScratch(insert.InsertSpecification?.Target) ? null : "INSERT";
            case UpdateStatement update:
                return IsScratch(update.UpdateSpecification?.Target) ? null : "UPDATE";
            case DeleteStatement delete:
                return IsScratch(delete.DeleteSpecification?.Target) ? null : "DELETE";
            case MergeStatement merge:
                return IsScratch(merge.MergeSpecification?.Target) ? null : "MERGE";
            case TruncateTableStatement truncate:
                return IsScratch(truncate.TableName) ? null : "TRUNCATE TABLE";
            case CreateTableStatement create:
                return IsScratch(create.SchemaObjectName) ? null : "CREATE TABLE";
            case DropTableStatement drop:
                return drop.Objects.Count > 0 && drop.Objects.All(IsScratch) ? null : "DROP TABLE";
            case ExecuteStatement:
                return "EXEC";
            default:
                return KindName(statement);
        }
    }

    private static bool IsScratch(TableReference target) => target switch
    {
        VariableTableReference => true,
        NamedTableReference named => IsScratch(named.SchemaObject),
        _ => false,
    };

    private static bool IsScratch(SchemaObjectName name) => name?.BaseIdentifier?.Value?.StartsWith("#") == true;

    /// <summary>
    /// The verb and object: <c>AlterProcedureStatement</c> → <c>ALTER PROCEDURE</c>,
    /// <c>AlterTableAddTableElementStatement</c> → <c>ALTER TABLE</c>, <c>CreateOrAlterViewStatement</c> → <c>CREATE OR ALTER VIEW</c>.
    /// </summary>
    private static string KindName(TSqlStatement statement)
    {
        string name = statement.GetType().Name;
        if (name.EndsWith("Statement")) name = name.Substring(0, name.Length - "Statement".Length);
        name = name.Replace("FullText", "Fulltext");   // one word in T-SQL: CREATE FULLTEXT INDEX, not CREATE FULL
        var words = Regex.Split(name, "(?<=[a-z])(?=[A-Z])");
        int take = words.Length >= 4 && words[1] == "Or" ? 4 : 2;
        return string.Join(" ", words.Take(take)).ToUpperInvariant();
    }
}
