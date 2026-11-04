using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Smo;
using SQLExtended.Export;
using System;
using System.Security.Cryptography;
using System.Text;

namespace SQLExtended.SchemaExporter;

/// <summary>
/// A hash of everything in a database that can change what the export writes, read with one query. When it matches the
/// fingerprint of the last clean export, the database can be skipped.
///
/// <c>modify_date</c> covers most of it. ALTER changes it, and Microsoft documents that a table's or view's also changes
/// when an index on it is created or altered. Drops, creates and renames change the object rows themselves. The other
/// sources fill the gaps <c>modify_date</c> leaves: extended properties, schemas and their owners, user-defined types,
/// enabled/disabled triggers, indexes and constraints, and compression. Each source is a row count plus
/// <c>CHECKSUM_AGG(BINARY_CHECKSUM(...))</c> over its rows. BINARY_CHECKSUM is case-sensitive, so a rename that only
/// changes case still shows. Anything this misses is caught by the periodic full scan (<c>fullScanEveryDays</c>).
///
/// Nothing that changes with data or use is included: no row counts, and no sequence current_value. Those would make
/// every database look changed every night.
/// </summary>
internal static class SchemaFingerprint
{
    private const string Sql = @"
SELECT 'objects' AS src, COUNT_BIG(*) AS n,
       CHECKSUM_AGG(BINARY_CHECKSUM(o.object_id, o.name, o.schema_id, o.principal_id, o.parent_object_id, o.type, o.modify_date)) AS chk,
       MAX(o.modify_date) AS latest
FROM sys.objects AS o WHERE o.is_ms_shipped = 0
UNION ALL
SELECT 'columns', COUNT_BIG(*),
       CHECKSUM_AGG(BINARY_CHECKSUM(c.object_id, c.column_id, c.name, c.user_type_id, c.max_length, c.precision, c.scale, c.is_nullable,
                                    c.collation_name, c.is_identity, c.is_computed, c.is_sparse, c.is_rowguidcol, c.is_column_set)), NULL
FROM sys.columns AS c JOIN sys.objects AS o ON o.object_id = c.object_id WHERE o.is_ms_shipped = 0
UNION ALL
SELECT 'indexes', COUNT_BIG(*),
       CHECKSUM_AGG(BINARY_CHECKSUM(i.object_id, i.index_id, i.name, i.type, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.is_disabled,
                                    i.ignore_dup_key, i.fill_factor, i.is_padded, i.allow_row_locks, i.allow_page_locks, i.has_filter, i.data_space_id)), NULL
FROM sys.indexes AS i JOIN sys.objects AS o ON o.object_id = i.object_id WHERE o.is_ms_shipped = 0
UNION ALL
SELECT 'index_columns', COUNT_BIG(*),
       CHECKSUM_AGG(BINARY_CHECKSUM(ic.object_id, ic.index_id, ic.index_column_id, ic.column_id, ic.key_ordinal, ic.partition_ordinal,
                                    ic.is_descending_key, ic.is_included_column)), NULL
FROM sys.index_columns AS ic JOIN sys.objects AS o ON o.object_id = ic.object_id WHERE o.is_ms_shipped = 0
UNION ALL
SELECT 'partitions', COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(p.object_id, p.index_id, p.partition_number, p.data_compression)), NULL
FROM sys.partitions AS p JOIN sys.objects AS o ON o.object_id = p.object_id WHERE o.is_ms_shipped = 0
UNION ALL
SELECT 'foreign_keys', COUNT_BIG(*),
       CHECKSUM_AGG(BINARY_CHECKSUM(fk.object_id, fk.is_disabled, fk.is_not_trusted, fk.is_not_for_replication,
                                    fk.delete_referential_action, fk.update_referential_action)), NULL
FROM sys.foreign_keys AS fk
UNION ALL
SELECT 'check_constraints', COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(cc.object_id, cc.is_disabled, cc.is_not_trusted, cc.is_not_for_replication)), NULL
FROM sys.check_constraints AS cc
UNION ALL
-- DML and database-level DDL triggers; the latter are not in sys.objects at all.
SELECT 'triggers', COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(t.object_id, t.name, t.parent_class, t.parent_id, t.is_disabled, t.modify_date)), MAX(t.modify_date)
FROM sys.triggers AS t WHERE t.is_ms_shipped = 0
UNION ALL
SELECT 'schemas', COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(s.schema_id, s.name, s.principal_id)), NULL
FROM sys.schemas AS s
UNION ALL
SELECT 'types', COUNT_BIG(*),
       CHECKSUM_AGG(BINARY_CHECKSUM(ty.user_type_id, ty.name, ty.schema_id, ty.principal_id, ty.system_type_id, ty.max_length, ty.precision, ty.scale,
                                    ty.is_nullable, ty.collation_name, ty.is_table_type)), NULL
FROM sys.types AS ty WHERE ty.is_user_defined = 1
UNION ALL
SELECT 'sequences', COUNT_BIG(*),
       CHECKSUM_AGG(BINARY_CHECKSUM(sq.object_id, sq.start_value, sq.increment, sq.minimum_value, sq.maximum_value, sq.is_cycling, sq.is_cached, sq.cache_size)), NULL
FROM sys.sequences AS sq
UNION ALL
SELECT 'extended_properties', COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(ep.class, ep.major_id, ep.minor_id, ep.name, ep.value)), NULL
FROM sys.extended_properties AS ep
UNION ALL
-- The export writes COLLATE only where a column differs from the database default, so the default is part of the output.
SELECT 'collation:' + CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), NULL, NULL, NULL;";

    /// <summary>
    /// What the exporter itself contributes: its own build (a rebuilt exporter may script differently), SMO's version,
    /// and whether encrypted modules are decrypted. A change to any of them makes every database export once.
    /// </summary>
    private static readonly string ExporterIdentity =
        $"exporter:{typeof(SchemaExportService).Assembly.ManifestModule.ModuleVersionId}|smo:{typeof(Scripter).Assembly.GetName().Version}";

    public static string Compute(string connectionString, string database, bool decrypting)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database };

        var text = new StringBuilder(ExporterIdentity).Append("|decrypt:").Append(decrypting).Append('\n');

        using (var connection = new SqlConnection(builder.ConnectionString))
        {
            connection.Open();
            using var command = new SqlCommand(Sql, connection) { CommandTimeout = 300 };
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                text.Append(reader.GetString(0)).Append('|')
                    .Append(reader.IsDBNull(1) ? "" : reader.GetInt64(1).ToString()).Append('|')
                    .Append(reader.IsDBNull(2) ? "" : reader.GetInt32(2).ToString()).Append('|')
                    .Append(reader.IsDBNull(3) ? "" : reader.GetDateTime(3).ToString("O")).Append('\n');
            }
        }

        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
