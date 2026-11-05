using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLExtended.SchemaCompare.Engine;

/// <summary>An <c>sp_addextendedproperty</c> call, read into its eight arguments.</summary>
public sealed class ExtendedProperty
{
    // Positional order of sp_addextendedproperty's parameters.
    private static readonly string[] ParameterOrder = { "@name", "@value", "@level0type", "@level0name", "@level1type", "@level1name", "@level2type", "@level2name" };

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Name => Get("@name");
    public string Value => Get("@value");
    public string Level0Type => Get("@level0type");
    public string Level0Name => Get("@level0name");
    public string Level1Type => Get("@level1type");
    public string Level1Name => Get("@level1name");
    public string Level2Type => Get("@level2type");
    public string Level2Name => Get("@level2name");

    private string Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public static bool IsProcedure(ExecuteStatement exec, string procedureName)
        => ProcedureName(exec)?.Equals(procedureName, StringComparison.OrdinalIgnoreCase) == true;

    public static string ProcedureName(ExecuteStatement exec)
        => (exec?.ExecuteSpecification?.ExecutableEntity as ExecutableProcedureReference)?.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value;

    /// <summary>The arguments of any EXEC, named or positional, as text (string literals unquoted, NULL as null).</summary>
    public static List<KeyValuePair<string, string>> Arguments(ExecuteStatement exec, string[] positionalNames)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (exec?.ExecuteSpecification?.ExecutableEntity is not ExecutableProcedureReference proc) return result;

        int position = 0;
        foreach (var p in proc.Parameters)
        {
            string key = p.Variable?.Name ?? (position < positionalNames.Length ? positionalNames[position] : "@" + position);
            position++;
            result.Add(new KeyValuePair<string, string>(key, ValueText(p.ParameterValue)));
        }

        return result;
    }

    public static bool TryParse(TSqlStatement statement, out ExtendedProperty property)
    {
        property = null;
        if (statement is not ExecuteStatement exec || !IsProcedure(exec, "sp_addextendedproperty")) return false;

        property = new ExtendedProperty();
        foreach (var pair in Arguments(exec, ParameterOrder)) property._values[pair.Key] = pair.Value;
        return true;
    }

    private static string ValueText(ScalarExpression value)
    {
        switch (value)
        {
            case null: return null;
            case NullLiteral: return null;
            case StringLiteral s: return s.Value;
            case Literal l: return l.Value;
            case CastCall c: return ValueText(c.Parameter);
            default:
                Sql.NewGenerator().GenerateScript(value, out string text);
                return text;
        }
    }

    /// <summary>Sort key: the column (or other level-2 item) first, then the property name.</summary>
    public string SortKey => (Level2Type ?? "") + "|" + (Level2Name ?? "") + "|" + (Name ?? "");

    /// <summary>One canonical line, the same however the call was written (named or positional, N'' or '', sys. or not).</summary>
    public string ToScript() => ToScript("sp_addextendedproperty");

    /// <summary>The same call to another of the extended-property procedures; <c>sp_dropextendedproperty</c> takes no value.</summary>
    public string ToScript(string procedure)
    {
        var sb = new StringBuilder("EXECUTE ").Append(procedure).Append(" @name = ").Append(Quote(Name));
        if (!procedure.Equals("sp_dropextendedproperty", StringComparison.OrdinalIgnoreCase)) sb.Append(", @value = ").Append(Quote(Value));
        string[] levels = { "@level0type", "@level0name", "@level1type", "@level1name", "@level2type", "@level2name" };
        foreach (var level in levels)
        {
            string v = Get(level);
            if (v != null) sb.Append(", ").Append(level).Append(" = ").Append(Quote(level.EndsWith("type") ? v.ToUpperInvariant() : v));
        }

        return sb.ToString();
    }

    private static string Quote(string s) => s == null ? "NULL" : "N'" + s.Replace("'", "''") + "'";

    /// <summary>The object type named by a level-1 type, as the compare's object types.</summary>
    public static SchemaObjectType[] OwnerTypes(string level1Type)
    {
        switch ((level1Type ?? "").ToUpperInvariant())
        {
            case "TABLE": return new[] { SchemaObjectType.Table };
            case "VIEW": return new[] { SchemaObjectType.View };
            case "PROCEDURE": return new[] { SchemaObjectType.Procedure };
            case "FUNCTION": return new[] { SchemaObjectType.Function };
            case "SEQUENCE": return new[] { SchemaObjectType.Sequence };
            case "SYNONYM": return new[] { SchemaObjectType.Synonym };
            case "TYPE": return new[] { SchemaObjectType.TableType, SchemaObjectType.UserDefinedType };
            default: return new[] { SchemaObjectType.Table, SchemaObjectType.View, SchemaObjectType.Procedure, SchemaObjectType.Function };
        }
    }
}
