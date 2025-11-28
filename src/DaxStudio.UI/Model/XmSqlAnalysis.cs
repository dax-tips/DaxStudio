using System;
using System.Collections.Generic;
using System.Linq;

namespace DaxStudio.UI.Model
{
    /// <summary>
    /// Represents the complete analysis of xmSQL queries from Server Timing events.
    /// This is the top-level container for all parsed table, column, and relationship information.
    /// </summary>
    public class XmSqlAnalysis
    {
        /// <summary>
        /// Dictionary of tables found in the xmSQL queries, keyed by table name.
        /// </summary>
        public Dictionary<string, XmSqlTableInfo> Tables { get; } = new Dictionary<string, XmSqlTableInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// List of relationships (joins) found between tables.
        /// </summary>
        public List<XmSqlRelationship> Relationships { get; } = new List<XmSqlRelationship>();

        /// <summary>
        /// Total number of SE (Storage Engine) queries analyzed.
        /// </summary>
        public int TotalSEQueriesAnalyzed { get; set; }

        /// <summary>
        /// Number of SE queries that were successfully parsed.
        /// </summary>
        public int SuccessfullyParsedQueries { get; set; }

        /// <summary>
        /// Number of SE queries that failed to parse.
        /// </summary>
        public int FailedParseQueries { get; set; }

        /// <summary>
        /// Gets the count of unique tables found.
        /// </summary>
        public int UniqueTablesCount => Tables.Count;

        /// <summary>
        /// Gets the total count of unique columns across all tables.
        /// </summary>
        public int UniqueColumnsCount => Tables.Values.Sum(t => t.Columns.Count);

        /// <summary>
        /// Gets the count of unique relationships found.
        /// </summary>
        public int UniqueRelationshipsCount => Relationships.Count;

        /// <summary>
        /// Gets or adds a table to the analysis.
        /// </summary>
        public XmSqlTableInfo GetOrAddTable(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName)) return null;

            // Clean the table name (remove quotes if present)
            tableName = tableName.Trim().Trim('\'', '"');

            if (!Tables.TryGetValue(tableName, out var table))
            {
                table = new XmSqlTableInfo(tableName);
                Tables[tableName] = table;
            }
            return table;
        }

        /// <summary>
        /// Adds or updates a relationship between tables.
        /// </summary>
        public void AddRelationship(string fromTable, string fromColumn, string toTable, string toColumn, XmSqlJoinType joinType)
        {
            // Check if this relationship already exists
            var existing = Relationships.FirstOrDefault(r =>
                r.FromTable.Equals(fromTable, StringComparison.OrdinalIgnoreCase) &&
                r.FromColumn.Equals(fromColumn, StringComparison.OrdinalIgnoreCase) &&
                r.ToTable.Equals(toTable, StringComparison.OrdinalIgnoreCase) &&
                r.ToColumn.Equals(toColumn, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.HitCount++;
            }
            else
            {
                Relationships.Add(new XmSqlRelationship
                {
                    FromTable = fromTable,
                    FromColumn = fromColumn,
                    ToTable = toTable,
                    ToColumn = toColumn,
                    JoinType = joinType,
                    HitCount = 1
                });
            }
        }

        /// <summary>
        /// Clears all analysis data.
        /// </summary>
        public void Clear()
        {
            Tables.Clear();
            Relationships.Clear();
            TotalSEQueriesAnalyzed = 0;
            SuccessfullyParsedQueries = 0;
            FailedParseQueries = 0;
        }
    }

    /// <summary>
    /// Represents information about a table found in xmSQL queries.
    /// </summary>
    public class XmSqlTableInfo
    {
        public XmSqlTableInfo(string tableName)
        {
            TableName = tableName;
        }

        /// <summary>
        /// The name of the table.
        /// </summary>
        public string TableName { get; }

        /// <summary>
        /// Dictionary of columns found in this table, keyed by column name.
        /// </summary>
        public Dictionary<string, XmSqlColumnInfo> Columns { get; } = new Dictionary<string, XmSqlColumnInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Number of SE queries that referenced this table.
        /// </summary>
        public int HitCount { get; set; }

        /// <summary>
        /// Whether this table appeared in a FROM clause (base table).
        /// </summary>
        public bool IsFromTable { get; set; }

        /// <summary>
        /// Whether this table appeared in a JOIN clause.
        /// </summary>
        public bool IsJoinedTable { get; set; }

        /// <summary>
        /// Gets or adds a column to this table.
        /// </summary>
        public XmSqlColumnInfo GetOrAddColumn(string columnName)
        {
            if (string.IsNullOrWhiteSpace(columnName)) return null;

            // Clean the column name (remove brackets if present)
            columnName = columnName.Trim().Trim('[', ']');

            if (!Columns.TryGetValue(columnName, out var column))
            {
                column = new XmSqlColumnInfo(columnName);
                Columns[columnName] = column;
            }
            return column;
        }

        /// <summary>
        /// Gets columns that are used in joins (relationship keys).
        /// </summary>
        public IEnumerable<XmSqlColumnInfo> JoinColumns => Columns.Values.Where(c => c.UsageTypes.HasFlag(XmSqlColumnUsage.Join));

        /// <summary>
        /// Gets columns that are filtered.
        /// </summary>
        public IEnumerable<XmSqlColumnInfo> FilteredColumns => Columns.Values.Where(c => c.UsageTypes.HasFlag(XmSqlColumnUsage.Filter));

        /// <summary>
        /// Gets columns that are selected/output.
        /// </summary>
        public IEnumerable<XmSqlColumnInfo> SelectedColumns => Columns.Values.Where(c => c.UsageTypes.HasFlag(XmSqlColumnUsage.Select));
    }

    /// <summary>
    /// Represents information about a column found in xmSQL queries.
    /// </summary>
    public class XmSqlColumnInfo
    {
        public XmSqlColumnInfo(string columnName)
        {
            ColumnName = columnName;
        }

        /// <summary>
        /// The name of the column.
        /// </summary>
        public string ColumnName { get; }

        /// <summary>
        /// How this column is used in queries (flags can be combined).
        /// </summary>
        public XmSqlColumnUsage UsageTypes { get; set; }

        /// <summary>
        /// Number of times this column was referenced.
        /// </summary>
        public int HitCount { get; set; }

        /// <summary>
        /// Aggregation functions applied to this column (SUM, COUNT, etc.)
        /// </summary>
        public HashSet<string> AggregationTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Adds a usage type to this column.
        /// </summary>
        public void AddUsage(XmSqlColumnUsage usage)
        {
            UsageTypes |= usage;
            HitCount++;
        }

        /// <summary>
        /// Adds an aggregation function to this column.
        /// </summary>
        public void AddAggregation(string aggregationType)
        {
            if (!string.IsNullOrWhiteSpace(aggregationType))
            {
                AggregationTypes.Add(aggregationType.Trim().ToUpperInvariant());
                UsageTypes |= XmSqlColumnUsage.Aggregate;
            }
        }
    }

    /// <summary>
    /// Represents a relationship (join) between two tables.
    /// </summary>
    public class XmSqlRelationship
    {
        /// <summary>
        /// The source table in the relationship.
        /// </summary>
        public string FromTable { get; set; }

        /// <summary>
        /// The column in the source table used for the join.
        /// </summary>
        public string FromColumn { get; set; }

        /// <summary>
        /// The target table in the relationship.
        /// </summary>
        public string ToTable { get; set; }

        /// <summary>
        /// The column in the target table used for the join.
        /// </summary>
        public string ToColumn { get; set; }

        /// <summary>
        /// The type of join (LEFT OUTER, INNER, etc.)
        /// </summary>
        public XmSqlJoinType JoinType { get; set; }

        /// <summary>
        /// Number of times this relationship was used.
        /// </summary>
        public int HitCount { get; set; }

        /// <summary>
        /// Returns a unique key for this relationship.
        /// </summary>
        public string Key => $"{FromTable}.{FromColumn}->{ToTable}.{ToColumn}";
    }

    /// <summary>
    /// Flags representing how a column is used in xmSQL queries.
    /// </summary>
    [Flags]
    public enum XmSqlColumnUsage
    {
        None = 0,
        Select = 1,      // Column appears in SELECT clause
        Filter = 2,      // Column appears in WHERE clause
        Join = 4,        // Column is used in a JOIN/ON clause
        GroupBy = 8,     // Column is used for grouping
        Aggregate = 16,  // Column has an aggregation function applied
        OrderBy = 32     // Column is used in ORDER BY
    }

    /// <summary>
    /// Types of joins found in xmSQL.
    /// </summary>
    public enum XmSqlJoinType
    {
        Unknown,
        LeftOuterJoin,
        InnerJoin,
        RightOuterJoin,
        FullOuterJoin
    }
}
