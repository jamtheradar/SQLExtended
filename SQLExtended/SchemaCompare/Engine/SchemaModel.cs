using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLExtended.SchemaCompare.Engine;

public enum SchemaObjectType
{
    Schema,
    Table,
    View,
    Procedure,
    Function,
    Trigger,
    DatabaseTrigger,
    TableType,
    UserDefinedType,
    Sequence,
    Synonym,
    User,
    Role,
    Login,
    Permissions,
    ExtendedProperties,
    Other,
}

/// <summary>Where one side of a compare came from.</summary>
public enum SourceKind
{
    /// <summary>A folder of scripts in the SQLExtended export shape (Tables\, Views\, …).</summary>
    Export,

    /// <summary>An SSDT / SDK-style database project (.sqlproj).</summary>
    DatabaseProject,

    /// <summary>Any other folder of .sql files.</summary>
    Folder,

    /// <summary>A live database, scripted into a cache folder in the export shape and read from there.</summary>
    Database,
}

/// <summary>One statement (one GO batch member) as written in a source file.</summary>
public sealed class SourceStatement
{
    public SourceStatement(string file, string relativePath, string text, bool isSessionSetting)
    {
        File = file;
        RelativePath = relativePath;
        Text = text;
        IsSessionSetting = isSessionSetting;
    }

    public string File { get; }

    public string RelativePath { get; }

    /// <summary>The statement's own text, including any comments that lead it in the batch.</summary>
    public string Text { get; }

    /// <summary>SET ANSI_NULLS / QUOTED_IDENTIFIER / ANSI_PADDING and similar, attached to the object that follows.</summary>
    public bool IsSessionSetting { get; }

    /// <summary>False when the batch didn't parse; the text is then compared as plain text.</summary>
    public bool Parsed { get; set; } = true;
}

/// <summary>
/// One database object, gathered from every statement that defines it on one side, wherever those statements were
/// written. An SMO export puts a table's indexes, defaults and FKs in the table's file as separate statements and a
/// project puts them inline or in other files, so objects are matched by name, never by file path.
/// </summary>
public sealed class SchemaObject
{
    public SchemaObject(SchemaObjectType type, string schema, string name)
    {
        Type = type;
        Schema = schema ?? "";
        Name = name ?? "";
    }

    public SchemaObjectType Type { get; }

    public string Schema { get; }

    public string Name { get; }

    /// <summary>For a DML trigger, the table or view it is on ("schema.name").</summary>
    public string Parent { get; set; }

    public List<SourceStatement> Statements { get; } = new();

    public string DisplayName => Schema.Length == 0 ? Name : Schema + "." + Name;

    public string Key => MakeKey(Type, Schema, Name);

    public bool IsSecurity => Type is SchemaObjectType.User or SchemaObjectType.Role or SchemaObjectType.Login or SchemaObjectType.Permissions;

    public IEnumerable<string> Files => Statements.Select(s => s.File).Distinct(StringComparer.OrdinalIgnoreCase);

    public static string MakeKey(SchemaObjectType type, string schema, string name)
        => type + "|" + (schema ?? "").ToUpperInvariant() + "|" + (name ?? "").ToUpperInvariant();

    public override string ToString() => Type + " " + DisplayName;
}

/// <summary>Everything read from one side.</summary>
public sealed class SchemaSet
{
    public SchemaSet(string rootPath, SourceKind kind)
    {
        RootPath = rootPath;
        Kind = kind;
    }

    public string RootPath { get; }

    /// <summary>Settable so a live database, read through its export folder, can say it is one.</summary>
    public SourceKind Kind { get; set; }

    public int FileCount { get; set; }

    public Dictionary<string, SchemaObject> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Files or batches that didn't parse, and anything else the user should know about this side.</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>Shared ScriptDom settings, so the extractor and the normaliser read SQL the same way.</summary>
public static class Sql
{
    public static TSqlParser NewParser() => new TSql170Parser(initialQuotedIdentifiers: true);

    public static Sql170ScriptGenerator NewGenerator() => new(new SqlScriptGeneratorOptions
    {
        KeywordCasing = KeywordCasing.Uppercase,
        IncludeSemicolons = false,
        AlignClauseBodies = false,
        AlignColumnDefinitionFields = false,
        MultilineSelectElementsList = false,
        MultilineWherePredicatesList = false,
        MultilineViewColumnsList = false,
        MultilineInsertTargetsList = false,
        MultilineInsertSourcesList = false,
        AsKeywordOnOwnLine = false,
        NewLineBeforeFromClause = false,
        NewLineBeforeWhereClause = false,
        NewLineBeforeOpenParenthesisInMultilineList = false,
        NewLineBeforeCloseParenthesisInMultilineList = false,
        IndentationSize = 4,
    });

    public const string DefaultSchema = "dbo";
}
