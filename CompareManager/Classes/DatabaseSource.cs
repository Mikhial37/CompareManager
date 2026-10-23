using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;

namespace CompareManager.Classes
{
    /// <summary>
    /// Reads a database's procedures, functions, views, triggers and tables, with the date
    /// each last changed (<c>sys.objects.modify_date</c>).
    ///
    /// Everything with a body comes from <c>sys.sql_modules</c>, verbatim. A table has no
    /// stored text, so one is written for it from its columns — name, type, nullability,
    /// identity and default, in column order — always in the same shape, so two databases'
    /// copies of a table compare cleanly. Keys and indexes are not part of it.
    ///
    /// Only reads; nothing is ever sent that changes anything.
    /// </summary>
    public static class DatabaseSource
    {
        private const string ObjectsSql = @"
SELECT o.object_id, s.name, o.name, o.type, o.modify_date, m.definition
FROM   sys.objects o
JOIN   sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE  o.is_ms_shipped = 0
AND    o.type IN ('P', 'FN', 'IF', 'TF', 'V', 'TR', 'U')
ORDER BY s.name, o.name;";

        private const string ColumnsSql = @"
SELECT c.object_id, c.name, t.name, c.max_length, c.precision, c.scale,
       c.is_nullable, c.is_identity, dc.definition, cc.definition
FROM   sys.columns c
JOIN   sys.objects o ON o.object_id = c.object_id AND o.type = 'U' AND o.is_ms_shipped = 0
JOIN   sys.types   t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
LEFT JOIN sys.computed_columns   cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
ORDER BY c.object_id, c.column_id;";

        public static List<SqlObject> Load(CompareSource source, CancellationToken cancel = default)
        {
            var objects = new Dictionary<int, SqlObject>();
            var columns = new Dictionary<int, List<string>>();
            string origin = source.Describe();

            using (var connection = new SqlConnection(source.ConnectionString()))
            {
                connection.Open();

                using (var command = new SqlCommand(ObjectsSql, connection) { CommandTimeout = 120 })
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        cancel.ThrowIfCancellationRequested();
                        objects[reader.GetInt32(0)] = new SqlObject
                        {
                            Schema     = reader.GetString(1),
                            Name       = reader.GetString(2),
                            Kind       = KindOf(reader.GetString(3).Trim()),
                            Modified   = reader.GetDateTime(4),
                            Definition = reader.IsDBNull(5) ? "" : ScriptParser.TrimBlankLines(reader.GetString(5)),
                            Origin     = origin
                        };
                    }
                }

                using (var command = new SqlCommand(ColumnsSql, connection) { CommandTimeout = 120 })
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        cancel.ThrowIfCancellationRequested();
                        int id = reader.GetInt32(0);
                        if (!columns.TryGetValue(id, out var list)) columns[id] = list = new List<string>();

                        list.Add(ColumnLine(
                            name:       reader.GetString(1),
                            type:       reader.GetString(2),
                            maxLength:  reader.GetInt16(3),
                            precision:  reader.GetByte(4),
                            scale:      reader.GetByte(5),
                            nullable:   reader.GetBoolean(6),
                            identity:   reader.GetBoolean(7),
                            defaultSql: reader.IsDBNull(8) ? null : reader.GetString(8),
                            computed:   reader.IsDBNull(9) ? null : reader.GetString(9)));
                    }
                }
            }

            foreach (var pair in objects.Where(p => p.Value.Kind == ObjectKind.Table))
                pair.Value.Definition = TableScript(pair.Value.Schema, pair.Value.Name,
                                                    columns.TryGetValue(pair.Key, out var list) ? list : new List<string>());

            return objects.Values.ToList();
        }

        /// <summary>The databases on the server the login can open, system ones left out.</summary>
        public static List<string> ListDatabases(CompareSource server)
        {
            const string sql = "SELECT name FROM sys.databases " +
                               "WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name";

            var names = new List<string>();
            using (var connection = new SqlConnection(server.ConnectionString(15, database: "master")))
            using (var command = new SqlCommand(sql, connection))
            {
                connection.Open();
                using var reader = command.ExecuteReader();
                while (reader.Read()) names.Add(reader.GetString(0));
            }
            return names;
        }

        /// <summary>Opens and closes a connection; the error, if any, is the message to show.</summary>
        public static void Test(CompareSource source)
        {
            using var connection = new SqlConnection(source.ConnectionString(10));
            connection.Open();
        }

        private static ObjectKind KindOf(string type) => type switch
        {
            "P"                => ObjectKind.Procedure,
            "FN" or "IF" or "TF" => ObjectKind.Function,
            "V"                => ObjectKind.View,
            "TR"               => ObjectKind.Trigger,
            _                  => ObjectKind.Table
        };

        // ── Tables ────────────────────────────────────────────────────────────

        public static string TableScript(string schema, string name, IReadOnlyList<string> columnLines)
        {
            var text = new StringBuilder();
            text.Append("CREATE TABLE [").Append(schema).Append("].[").Append(name).Append("]\n(\n");
            for (int i = 0; i < columnLines.Count; i++)
                text.Append("    ").Append(columnLines[i]).Append(i < columnLines.Count - 1 ? ",\n" : "\n");
            text.Append(')');
            return text.ToString();
        }

        public static string ColumnLine(string name, string type, short maxLength, byte precision, byte scale,
                                        bool nullable, bool identity, string defaultSql, string computed)
        {
            if (computed != null) return $"[{name}] AS {computed}";

            var line = new StringBuilder($"[{name}] {TypeName(type, maxLength, precision, scale)}");
            if (identity) line.Append(" IDENTITY");
            line.Append(nullable ? " NULL" : " NOT NULL");
            if (defaultSql != null) line.Append(" DEFAULT ").Append(defaultSql);
            return line.ToString();
        }

        /// <summary>A column's type as it would be written: <c>nvarchar(50)</c>, <c>decimal(18,2)</c>, <c>varchar(max)</c>.</summary>
        public static string TypeName(string type, short maxLength, byte precision, byte scale)
        {
            string t = type.ToLowerInvariant();
            switch (t)
            {
                case "varchar": case "char": case "varbinary": case "binary":
                    return $"{t}({(maxLength == -1 ? "max" : maxLength.ToString())})";
                case "nvarchar": case "nchar":
                    return $"{t}({(maxLength == -1 ? "max" : (maxLength / 2).ToString())})";
                case "decimal": case "numeric":
                    return $"{t}({precision},{scale})";
                case "datetime2": case "time": case "datetimeoffset":
                    return $"{t}({scale})";
                default:
                    return t;
            }
        }
    }
}
