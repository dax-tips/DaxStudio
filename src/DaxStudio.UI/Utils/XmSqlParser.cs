using DaxStudio.UI.Model;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DaxStudio.UI.Utils
{
    /// <summary>
    /// Parses xmSQL queries from Server Timing events to extract table, column, and relationship information.
    /// Includes lineage tracking to resolve temporary/intermediate tables back to physical tables.
    /// </summary>
    public class XmSqlParser
    {
        // Regex patterns for parsing xmSQL

        // Matches table and column references like 'TableName'[ColumnName] or 'Table Name'[Column Name]
        private static readonly Regex TableColumnPattern = new Regex(
            @"'(?<table>[^']+)'\s*\[(?<column>[^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches standalone table references like 'TableName' (without column)
        private static readonly Regex StandaloneTablePattern = new Regex(
            @"(?<!\[)'(?<table>[^']+)'(?!\s*\[)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches FROM clause - captures the table name after FROM
        private static readonly Regex FromClausePattern = new Regex(
            @"\bFROM\s+'(?<table>[^']+)'",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches LEFT OUTER JOIN clause
        private static readonly Regex LeftOuterJoinPattern = new Regex(
            @"\bLEFT\s+OUTER\s+JOIN\s+'(?<table>[^']+)'",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches INNER JOIN clause
        private static readonly Regex InnerJoinPattern = new Regex(
            @"\bINNER\s+JOIN\s+'(?<table>[^']+)'",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches ON clause for join conditions: ON 'Table1'[Col1]='Table2'[Col2]
        // Also handles ON 'Table1'[Col1] = 'Table2'[Col2] (with spaces)
        private static readonly Regex OnClausePattern = new Regex(
            @"\bON\s+'(?<fromTable>[^']+)'\s*\[(?<fromColumn>[^\]]+)\]\s*=\s*'(?<toTable>[^']+)'\s*\[(?<toColumn>[^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches WHERE clause start
        private static readonly Regex WhereClausePattern = new Regex(
            @"\bWHERE\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches SELECT clause - everything between SELECT and FROM
        private static readonly Regex SelectClausePattern = new Regex(
            @"\bSELECT\b(?<columns>.*?)\bFROM\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Matches aggregation functions: SUM(...), COUNT(...), DCOUNT(...), MIN(...), MAX(...)
        private static readonly Regex AggregationPattern = new Regex(
            @"\b(?<agg>SUM|COUNT|DCOUNT|MIN|MAX|AVG|SUMSQR)\s*\(\s*(?:'(?<table>[^']+)'\s*\[(?<column>[^\]]+)\]|\s*\(\s*\))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches COUNT() without column (count all rows)
        private static readonly Regex CountAllPattern = new Regex(
            @"\bCOUNT\s*\(\s*\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches VAND / VOR for additional filter conditions
        private static readonly Regex VandVorPattern = new Regex(
            @"\b(?:VAND|VOR)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ==================== LINEAGE TRACKING PATTERNS ====================

        // Matches DEFINE TABLE '$TTableX' := ... blocks
        // Captures the temp table name and the definition body
        private static readonly Regex DefineTablePattern = new Regex(
            @"DEFINE\s+TABLE\s+'(?<tempTable>\$T[^']+)'\s*:=\s*(?<definition>.*?)(?=DEFINE\s+TABLE|CREATE\s+SHALLOW|REDUCED\s+BY|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Matches REDUCED BY clause (contains nested temp table definitions)
        private static readonly Regex ReducedByPattern = new Regex(
            @"REDUCED\s+BY\s*'(?<tempTable>\$T[^']+)'\s*:=\s*(?<definition>.*?)(?=;|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Matches CREATE SHALLOW RELATION definitions
        private static readonly Regex CreateShallowRelationPattern = new Regex(
            @"CREATE\s+SHALLOW\s+RELATION\s+'(?<relationName>[^']+)'.*?FROM\s+'(?<fromTable>[^']+)'\s*\[(?<fromColumn>[^\]]+)\].*?TO\s+'(?<toTable>[^']+)'\s*\[(?<toColumn>[^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Matches ININDEX operator: 'Table'[Column] ININDEX '$TTableX'[$SemijoinProjection]
        private static readonly Regex InIndexPattern = new Regex(
            @"'(?<physTable>[^']+)'\s*\[(?<physColumn>[^\]]+)\]\s+ININDEX\s+'(?<tempTable>\$T[^']+)'\s*\[(?<tempColumn>[^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches REVERSE BITMAP JOIN: REVERSE BITMAP JOIN 'Table' ON 'TempTable'[Col]='Table'[Col]
        private static readonly Regex ReverseBitmapJoinPattern = new Regex(
            @"REVERSE\s+BITMAP\s+JOIN\s+'(?<table>[^']+)'\s+ON\s+'(?<leftTable>[^']+)'\s*\[(?<leftCol>[^\]]+)\]\s*=\s*'(?<rightTable>[^']+)'\s*\[(?<rightCol>[^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches column references with $ naming convention: 'SourceTable$ColumnName' -> SourceTable, ColumnName
        private static readonly Regex TempColumnNamePattern = new Regex(
            @"^(?<sourceTable>[^\$]+)\$(?<sourceColumn>.+)$",
            RegexOptions.Compiled);

        // ==================== LINEAGE DATA STRUCTURES ====================

        /// <summary>
        /// Tracks lineage of temporary tables back to their source physical tables.
        /// Key: temp table name (e.g., "$TTable1")
        /// Value: TempTableLineage containing source tables and column mappings
        /// </summary>
        private Dictionary<string, TempTableLineage> _tempTableLineage;

        /// <summary>
        /// Represents lineage information for a temporary table.
        /// </summary>
        private class TempTableLineage
        {
            public string TempTableName { get; set; }
            public HashSet<string> SourcePhysicalTables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SourceTempTables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, ColumnLineage> ColumnMappings { get; } = new Dictionary<string, ColumnLineage>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Represents lineage information for a column in a temp table.
        /// </summary>
        private class ColumnLineage
        {
            public string TempColumnName { get; set; }
            public string SourceTable { get; set; }
            public string SourceColumn { get; set; }
        }

        // Stores the estimated rows for the current query being parsed
        private long? _currentQueryEstimatedRows;
        
        // Stores the duration for the current query being parsed
        private long? _currentQueryDurationMs;

        /// <summary>
        /// Parses a single xmSQL query and adds the results to the analysis.
        /// </summary>
        /// <param name="xmSql">The xmSQL query text to parse.</param>
        /// <param name="analysis">The analysis object to add results to.</param>
        /// <param name="estimatedRows">Optional estimated rows returned by this SE query.</param>
        /// <param name="durationMs">Optional duration in milliseconds of this SE query.</param>
        /// <returns>True if parsing was successful, false otherwise.</returns>
        public bool ParseQuery(string xmSql, XmSqlAnalysis analysis, long? estimatedRows = null, long? durationMs = null)
        {
            if (string.IsNullOrWhiteSpace(xmSql))
                return false;

            try
            {
                analysis.TotalSEQueriesAnalyzed++;
                _currentQueryEstimatedRows = estimatedRows;
                _currentQueryDurationMs = durationMs;

                // Initialize lineage tracking for this query
                _tempTableLineage = new Dictionary<string, TempTableLineage>(StringComparer.OrdinalIgnoreCase);

                // First pass: Build temp table lineage map
                BuildTempTableLineage(xmSql);

                // Parse FROM clause (base table)
                ParseFromClause(xmSql, analysis);

                // Parse SELECT clause (selected columns)
                ParseSelectClause(xmSql, analysis);

                // Parse JOIN clauses and relationships
                ParseJoinClauses(xmSql, analysis);

                // Parse ON clauses for relationship details (with lineage resolution)
                ParseOnClauses(xmSql, analysis);

                // Parse WHERE clause (filtered columns)
                ParseWhereClause(xmSql, analysis);

                // Parse ININDEX operations (filter relationships via temp tables)
                ParseInIndexOperations(xmSql, analysis);

                // Parse CREATE SHALLOW RELATION definitions
                ParseShallowRelations(xmSql, analysis);

                // Parse aggregations
                ParseAggregations(xmSql, analysis);

                analysis.SuccessfullyParsedQueries++;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to parse xmSQL query: {Query}", xmSql.Substring(0, Math.Min(100, xmSql.Length)));
                analysis.FailedParseQueries++;
                return false;
            }
        }

        /// <summary>
        /// Parses the FROM clause to identify the base table.
        /// Skips temp tables - only tracks physical tables.
        /// </summary>
        private void ParseFromClause(string xmSql, XmSqlAnalysis analysis)
        {
            var matches = FromClausePattern.Matches(xmSql);
            foreach (Match match in matches)
            {
                var tableName = match.Groups["table"].Value;
                
                // Skip temp tables - we only want physical tables in our analysis
                if (IsTempTable(tableName))
                    continue;
                    
                var table = analysis.GetOrAddTable(tableName);
                if (table != null)
                {
                    table.IsFromTable = true;
                    table.HitCount++;
                    
                    // Track estimated rows for this table
                    if (_currentQueryEstimatedRows.HasValue && _currentQueryEstimatedRows.Value > 0)
                    {
                        table.TotalEstimatedRows += _currentQueryEstimatedRows.Value;
                        if (_currentQueryEstimatedRows.Value > table.MaxEstimatedRows)
                        {
                            table.MaxEstimatedRows = _currentQueryEstimatedRows.Value;
                        }
                    }
                    
                    // Track duration for this table
                    if (_currentQueryDurationMs.HasValue && _currentQueryDurationMs.Value > 0)
                    {
                        table.TotalDurationMs += _currentQueryDurationMs.Value;
                        if (_currentQueryDurationMs.Value > table.MaxDurationMs)
                        {
                            table.MaxDurationMs = _currentQueryDurationMs.Value;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Parses the SELECT clause to identify selected columns.
        /// Resolves temp table column references to physical tables.
        /// </summary>
        private void ParseSelectClause(string xmSql, XmSqlAnalysis analysis)
        {
            var selectMatch = SelectClausePattern.Match(xmSql);
            if (selectMatch.Success)
            {
                var selectClause = selectMatch.Groups["columns"].Value;
                
                // Find all table[column] references in the SELECT clause
                var columnMatches = TableColumnPattern.Matches(selectClause);
                foreach (Match match in columnMatches)
                {
                    var tableName = match.Groups["table"].Value;
                    var columnName = match.Groups["column"].Value;

                    // Resolve temp table references to physical tables
                    var resolved = ResolveToPhysicalColumn(tableName, columnName);
                    if (resolved.HasValue && !IsTempTable(resolved.Value.Table))
                    {
                        tableName = resolved.Value.Table;
                        columnName = resolved.Value.Column;
                    }
                    else if (IsTempTable(tableName))
                    {
                        // Couldn't resolve and it's a temp table - skip
                        continue;
                    }

                    var table = analysis.GetOrAddTable(tableName);
                    if (table != null)
                    {
                        var column = table.GetOrAddColumn(columnName);
                        column?.AddUsage(XmSqlColumnUsage.Select);
                    }
                }
            }
        }

        /// <summary>
        /// Parses JOIN clauses to identify joined tables.
        /// Skips temp tables - only tracks physical tables.
        /// </summary>
        private void ParseJoinClauses(string xmSql, XmSqlAnalysis analysis)
        {
            // Parse LEFT OUTER JOINs
            var leftJoinMatches = LeftOuterJoinPattern.Matches(xmSql);
            foreach (Match match in leftJoinMatches)
            {
                var tableName = match.Groups["table"].Value;
                
                // Skip temp tables
                if (IsTempTable(tableName))
                    continue;
                    
                var table = analysis.GetOrAddTable(tableName);
                if (table != null)
                {
                    table.IsJoinedTable = true;
                    table.HitCount++;
                }
            }

            // Parse INNER JOINs
            var innerJoinMatches = InnerJoinPattern.Matches(xmSql);
            foreach (Match match in innerJoinMatches)
            {
                var tableName = match.Groups["table"].Value;
                
                // Skip temp tables
                if (IsTempTable(tableName))
                    continue;
                    
                var table = analysis.GetOrAddTable(tableName);
                if (table != null)
                {
                    table.IsJoinedTable = true;
                    table.HitCount++;
                }
            }
        }

        /// <summary>
        /// Parses ON clauses to extract relationship details.
        /// Uses lineage resolution to convert temp table references to physical tables.
        /// </summary>
        private void ParseOnClauses(string xmSql, XmSqlAnalysis analysis)
        {
            // Determine the join type by looking at what precedes each ON clause
            var onMatches = OnClausePattern.Matches(xmSql);
            Log.Debug("ParseOnClauses: Found {Count} ON clause matches", onMatches.Count);
            
            foreach (Match match in onMatches)
            {
                var fromTable = match.Groups["fromTable"].Value;
                var fromColumn = match.Groups["fromColumn"].Value;
                var toTable = match.Groups["toTable"].Value;
                var toColumn = match.Groups["toColumn"].Value;

                Log.Debug("ParseOnClauses: {FromTable}[{FromCol}] = {ToTable}[{ToCol}]",
                    fromTable, fromColumn, toTable, toColumn);

                // Determine join type by looking at text before the ON
                var textBeforeOn = xmSql.Substring(0, match.Index);
                var joinType = DetermineJoinType(textBeforeOn);

                // Resolve temp table references to physical tables using lineage
                var resolvedFrom = ResolveToPhysicalColumn(fromTable, fromColumn);
                var resolvedTo = ResolveToPhysicalColumn(toTable, toColumn);

                // Use resolved physical tables if available, otherwise use original
                var physFromTable = resolvedFrom?.Table ?? fromTable;
                var physFromColumn = resolvedFrom?.Column ?? fromColumn;
                var physToTable = resolvedTo?.Table ?? toTable;
                var physToColumn = resolvedTo?.Column ?? toColumn;

                // Skip if either side is still a temp table (couldn't resolve)
                if (IsTempTable(physFromTable) || IsTempTable(physToTable))
                {
                    Log.Debug("  Skipping temp table relationship: {From}[{FromCol}] -> {To}[{ToCol}]",
                        physFromTable, physFromColumn, physToTable, physToColumn);
                    continue;
                }

                // Log resolution if it happened
                if (!fromTable.Equals(physFromTable, StringComparison.OrdinalIgnoreCase) ||
                    !toTable.Equals(physToTable, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug("  Resolved to physical: {From}[{FromCol}] -> {To}[{ToCol}]",
                        physFromTable, physFromColumn, physToTable, physToColumn);
                }

                // Add relationship between physical tables
                analysis.AddRelationship(physFromTable, physFromColumn, physToTable, physToColumn, joinType);

                // Mark columns as used in joins
                var fromTableInfo = analysis.GetOrAddTable(physFromTable);
                var fromColumnInfo = fromTableInfo?.GetOrAddColumn(physFromColumn);
                fromColumnInfo?.AddUsage(XmSqlColumnUsage.Join);
                Log.Debug("  Marked {Table}[{Col}] as Join", physFromTable, physFromColumn);

                var toTableInfo = analysis.GetOrAddTable(physToTable);
                var toColumnInfo = toTableInfo?.GetOrAddColumn(physToColumn);
                toColumnInfo?.AddUsage(XmSqlColumnUsage.Join);
                Log.Debug("  Marked {Table}[{Col}] as Join", physToTable, physToColumn);
            }
        }

        /// <summary>
        /// Determines the join type by examining text before the ON clause.
        /// </summary>
        private XmSqlJoinType DetermineJoinType(string textBeforeOn)
        {
            // Find the last JOIN keyword before ON
            var lastLeftOuterJoin = textBeforeOn.LastIndexOf("LEFT OUTER JOIN", StringComparison.OrdinalIgnoreCase);
            var lastInnerJoin = textBeforeOn.LastIndexOf("INNER JOIN", StringComparison.OrdinalIgnoreCase);

            if (lastLeftOuterJoin > lastInnerJoin)
                return XmSqlJoinType.LeftOuterJoin;
            if (lastInnerJoin > lastLeftOuterJoin)
                return XmSqlJoinType.InnerJoin;

            return XmSqlJoinType.Unknown;
        }

        /// <summary>
        /// Parses the WHERE clause to identify filtered columns.
        /// Resolves temp table column references to physical tables.
        /// </summary>
        private void ParseWhereClause(string xmSql, XmSqlAnalysis analysis)
        {
            var whereMatch = WhereClausePattern.Match(xmSql);
            if (whereMatch.Success)
            {
                // Get everything after WHERE
                var whereClause = xmSql.Substring(whereMatch.Index);

                // Find all table[column] references in the WHERE clause
                var columnMatches = TableColumnPattern.Matches(whereClause);
                foreach (Match match in columnMatches)
                {
                    var tableName = match.Groups["table"].Value;
                    var columnName = match.Groups["column"].Value;

                    // Resolve temp table references to physical tables
                    var resolved = ResolveToPhysicalColumn(tableName, columnName);
                    if (resolved.HasValue && !IsTempTable(resolved.Value.Table))
                    {
                        tableName = resolved.Value.Table;
                        columnName = resolved.Value.Column;
                    }
                    else if (IsTempTable(tableName))
                    {
                        // Couldn't resolve and it's a temp table - skip
                        continue;
                    }

                    var table = analysis.GetOrAddTable(tableName);
                    if (table != null)
                    {
                        var column = table.GetOrAddColumn(columnName);
                        column?.AddUsage(XmSqlColumnUsage.Filter);
                    }
                }
            }
        }

        /// <summary>
        /// Parses aggregation functions to identify aggregated columns.
        /// </summary>
        private void ParseAggregations(string xmSql, XmSqlAnalysis analysis)
        {
            var aggMatches = AggregationPattern.Matches(xmSql);
            foreach (Match match in aggMatches)
            {
                var aggFunction = match.Groups["agg"].Value;
                var tableName = match.Groups["table"].Value;
                var columnName = match.Groups["column"].Value;

                if (!string.IsNullOrEmpty(tableName) && !string.IsNullOrEmpty(columnName))
                {
                    // Resolve temp table references to physical tables
                    var resolved = ResolveToPhysicalColumn(tableName, columnName);
                    if (resolved.HasValue && !IsTempTable(resolved.Value.Table))
                    {
                        tableName = resolved.Value.Table;
                        columnName = resolved.Value.Column;
                    }
                    else if (IsTempTable(tableName))
                    {
                        // Couldn't resolve and it's a temp table - skip
                        continue;
                    }

                    var table = analysis.GetOrAddTable(tableName);
                    if (table != null)
                    {
                        var column = table.GetOrAddColumn(columnName);
                        column?.AddAggregation(aggFunction);
                    }
                }
            }
        }

        /// <summary>
        /// Parses multiple xmSQL queries and aggregates the results.
        /// </summary>
        /// <param name="queries">Collection of xmSQL query strings.</param>
        /// <returns>An XmSqlAnalysis containing the aggregated results.</returns>
        public XmSqlAnalysis ParseQueries(IEnumerable<string> queries)
        {
            var analysis = new XmSqlAnalysis();

            foreach (var query in queries)
            {
                ParseQuery(query, analysis);
            }

            return analysis;
        }

        /// <summary>
        /// Extracts just the table name from a 'TableName'[ColumnName] reference.
        /// </summary>
        public static string ExtractTableName(string reference)
        {
            var match = TableColumnPattern.Match(reference);
            return match.Success ? match.Groups["table"].Value : null;
        }

        /// <summary>
        /// Extracts just the column name from a 'TableName'[ColumnName] reference.
        /// </summary>
        public static string ExtractColumnName(string reference)
        {
            var match = TableColumnPattern.Match(reference);
            return match.Success ? match.Groups["column"].Value : null;
        }

        /// <summary>
        /// Checks if the given xmSQL is a scan event (contains SELECT/FROM structure).
        /// </summary>
        public static bool IsScanQuery(string xmSql)
        {
            if (string.IsNullOrWhiteSpace(xmSql))
                return false;

            return xmSql.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   xmSql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ==================== LINEAGE TRACKING METHODS ====================

        /// <summary>
        /// Builds the temp table lineage map by parsing DEFINE TABLE statements.
        /// This creates a mapping from temp tables to their source physical tables.
        /// </summary>
        private void BuildTempTableLineage(string xmSql)
        {
            // Parse DEFINE TABLE statements
            var defineMatches = DefineTablePattern.Matches(xmSql);
            foreach (Match match in defineMatches)
            {
                var tempTableName = match.Groups["tempTable"].Value;
                var definition = match.Groups["definition"].Value;
                
                ParseTempTableDefinition(tempTableName, definition);
            }

            // Parse REDUCED BY statements (nested temp table definitions)
            var reducedByMatches = ReducedByPattern.Matches(xmSql);
            foreach (Match match in reducedByMatches)
            {
                var tempTableName = match.Groups["tempTable"].Value;
                var definition = match.Groups["definition"].Value;
                
                ParseTempTableDefinition(tempTableName, definition);
            }

            // Resolve transitive lineage (temp tables derived from other temp tables)
            ResolveTransitiveLineage();

            Log.Debug("Built lineage for {Count} temp tables", _tempTableLineage.Count);
        }

        /// <summary>
        /// Parses a single temp table definition to extract source tables and column mappings.
        /// </summary>
        private void ParseTempTableDefinition(string tempTableName, string definition)
        {
            if (_tempTableLineage.ContainsKey(tempTableName))
                return;

            var lineage = new TempTableLineage { TempTableName = tempTableName };

            // Find FROM clause in definition
            var fromMatches = FromClausePattern.Matches(definition);
            foreach (Match fromMatch in fromMatches)
            {
                var sourceTable = fromMatch.Groups["table"].Value;
                if (IsTempTable(sourceTable))
                {
                    lineage.SourceTempTables.Add(sourceTable);
                }
                else
                {
                    lineage.SourcePhysicalTables.Add(sourceTable);
                }
            }

            // Find JOIN tables in definition
            var leftJoinMatches = LeftOuterJoinPattern.Matches(definition);
            foreach (Match joinMatch in leftJoinMatches)
            {
                var joinTable = joinMatch.Groups["table"].Value;
                if (IsTempTable(joinTable))
                {
                    lineage.SourceTempTables.Add(joinTable);
                }
                else
                {
                    lineage.SourcePhysicalTables.Add(joinTable);
                }
            }

            var innerJoinMatches = InnerJoinPattern.Matches(definition);
            foreach (Match joinMatch in innerJoinMatches)
            {
                var joinTable = joinMatch.Groups["table"].Value;
                if (IsTempTable(joinTable))
                {
                    lineage.SourceTempTables.Add(joinTable);
                }
                else
                {
                    lineage.SourcePhysicalTables.Add(joinTable);
                }
            }

            // Find REVERSE BITMAP JOIN tables
            var reverseBitmapMatches = ReverseBitmapJoinPattern.Matches(definition);
            foreach (Match rbMatch in reverseBitmapMatches)
            {
                var table = rbMatch.Groups["table"].Value;
                if (IsTempTable(table))
                {
                    lineage.SourceTempTables.Add(table);
                }
                else
                {
                    lineage.SourcePhysicalTables.Add(table);
                }
            }

            // Parse column mappings from SELECT clause
            // Look for columns with $ naming: 'Calendar$Date' means Calendar.Date
            var columnMatches = TableColumnPattern.Matches(definition);
            foreach (Match colMatch in columnMatches)
            {
                var table = colMatch.Groups["table"].Value;
                var column = colMatch.Groups["column"].Value;

                // Check if column name has the $ pattern (e.g., "Calendar$Date")
                var tempColMatch = TempColumnNamePattern.Match(column);
                if (tempColMatch.Success)
                {
                    var sourceTable = tempColMatch.Groups["sourceTable"].Value;
                    var sourceColumn = tempColMatch.Groups["sourceColumn"].Value;
                    
                    lineage.ColumnMappings[column] = new ColumnLineage
                    {
                        TempColumnName = column,
                        SourceTable = sourceTable,
                        SourceColumn = sourceColumn
                    };

                    // Also track this as a source physical table
                    if (!IsTempTable(sourceTable))
                    {
                        lineage.SourcePhysicalTables.Add(sourceTable);
                    }
                }
                else if (!IsTempTable(table))
                {
                    // Direct physical table reference
                    lineage.SourcePhysicalTables.Add(table);
                    
                    // Map the column
                    lineage.ColumnMappings[column] = new ColumnLineage
                    {
                        TempColumnName = column,
                        SourceTable = table,
                        SourceColumn = column
                    };
                }
            }

            _tempTableLineage[tempTableName] = lineage;
            
            Log.Debug("Parsed lineage for {TempTable}: Physical=[{Physical}], Temp=[{Temp}], Columns={ColCount}",
                tempTableName,
                string.Join(",", lineage.SourcePhysicalTables),
                string.Join(",", lineage.SourceTempTables),
                lineage.ColumnMappings.Count);
        }

        /// <summary>
        /// Resolves transitive lineage - when temp tables are derived from other temp tables,
        /// we need to trace back to the original physical tables.
        /// </summary>
        private void ResolveTransitiveLineage()
        {
            // Keep iterating until no more changes (handles multi-level nesting)
            bool changed;
            int maxIterations = 10; // Prevent infinite loops
            int iteration = 0;

            do
            {
                changed = false;
                iteration++;

                foreach (var lineage in _tempTableLineage.Values)
                {
                    // For each source temp table, add its physical sources to our physical sources
                    foreach (var sourceTempTable in lineage.SourceTempTables.ToList())
                    {
                        if (_tempTableLineage.TryGetValue(sourceTempTable, out var sourceLineage))
                        {
                            foreach (var physTable in sourceLineage.SourcePhysicalTables)
                            {
                                if (lineage.SourcePhysicalTables.Add(physTable))
                                {
                                    changed = true;
                                }
                            }
                        }
                    }
                }
            } while (changed && iteration < maxIterations);
        }

        /// <summary>
        /// Checks if a table name represents a temporary table.
        /// </summary>
        private static bool IsTempTable(string tableName)
        {
            return tableName != null && tableName.StartsWith("$T", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves a table name to its physical source(s).
        /// If it's a temp table, returns the underlying physical tables.
        /// If it's already a physical table, returns it as-is.
        /// </summary>
        private IEnumerable<string> ResolveToPhysicalTables(string tableName)
        {
            if (!IsTempTable(tableName))
            {
                return new[] { tableName };
            }

            if (_tempTableLineage != null && _tempTableLineage.TryGetValue(tableName, out var lineage))
            {
                if (lineage.SourcePhysicalTables.Count > 0)
                {
                    return lineage.SourcePhysicalTables;
                }
            }

            // Couldn't resolve - return empty
            return Enumerable.Empty<string>();
        }

        /// <summary>
        /// Resolves a column reference (table + column) to its physical source.
        /// Handles the $ naming convention (e.g., Calendar$Date -> Calendar.Date).
        /// </summary>
        private (string Table, string Column)? ResolveToPhysicalColumn(string tableName, string columnName)
        {
            // First, check if the column name has the $ pattern
            var tempColMatch = TempColumnNamePattern.Match(columnName);
            if (tempColMatch.Success)
            {
                var sourceTable = tempColMatch.Groups["sourceTable"].Value;
                var sourceColumn = tempColMatch.Groups["sourceColumn"].Value;
                return (sourceTable, sourceColumn);
            }

            // If the table is a temp table, try to resolve through lineage
            if (IsTempTable(tableName) && _tempTableLineage != null)
            {
                if (_tempTableLineage.TryGetValue(tableName, out var lineage))
                {
                    if (lineage.ColumnMappings.TryGetValue(columnName, out var colLineage))
                    {
                        return (colLineage.SourceTable, colLineage.SourceColumn);
                    }
                    
                    // If we have exactly one source physical table, use that
                    if (lineage.SourcePhysicalTables.Count == 1)
                    {
                        return (lineage.SourcePhysicalTables.First(), columnName);
                    }
                }
            }

            // Not a temp table or couldn't resolve
            if (!IsTempTable(tableName))
            {
                return (tableName, columnName);
            }

            return null;
        }

        /// <summary>
        /// Parses ININDEX operations which represent filter relationships through temp tables.
        /// Example: 'Sales'[DateKey] ININDEX '$TTable4'[$SemijoinProjection]
        /// This indicates Sales.DateKey is filtered by the temp table, which we can trace back.
        /// </summary>
        private void ParseInIndexOperations(string xmSql, XmSqlAnalysis analysis)
        {
            var inIndexMatches = InIndexPattern.Matches(xmSql);
            foreach (Match match in inIndexMatches)
            {
                var physTable = match.Groups["physTable"].Value;
                var physColumn = match.Groups["physColumn"].Value;
                var tempTable = match.Groups["tempTable"].Value;

                Log.Debug("Found ININDEX: {Table}[{Col}] ININDEX {TempTable}", physTable, physColumn, tempTable);

                // Mark the physical column as used in a filter
                if (!IsTempTable(physTable))
                {
                    var table = analysis.GetOrAddTable(physTable);
                    var column = table?.GetOrAddColumn(physColumn);
                    column?.AddUsage(XmSqlColumnUsage.Filter);
                }

                // Resolve the temp table to find what physical tables it relates to
                // This helps us understand the relationship chain
                var physicalSources = ResolveToPhysicalTables(tempTable);
                foreach (var sourceTable in physicalSources)
                {
                    if (!sourceTable.Equals(physTable, StringComparison.OrdinalIgnoreCase))
                    {
                        // There's an implicit relationship between physTable and sourceTable
                        // through this ININDEX operation
                        Log.Debug("  Resolved ININDEX lineage: {Source} -> {Target} via {Temp}",
                            sourceTable, physTable, tempTable);

                        // We could add an implicit relationship here if needed
                        // For now, just ensure both tables are tracked
                        analysis.GetOrAddTable(sourceTable);
                    }
                }
            }
        }

        /// <summary>
        /// Parses CREATE SHALLOW RELATION definitions which explicitly define relationships.
        /// </summary>
        private void ParseShallowRelations(string xmSql, XmSqlAnalysis analysis)
        {
            var matches = CreateShallowRelationPattern.Matches(xmSql);
            foreach (Match match in matches)
            {
                var fromTable = match.Groups["fromTable"].Value;
                var fromColumn = match.Groups["fromColumn"].Value;
                var toTable = match.Groups["toTable"].Value;
                var toColumn = match.Groups["toColumn"].Value;

                Log.Debug("Found SHALLOW RELATION: {From}[{FromCol}] -> {To}[{ToCol}]",
                    fromTable, fromColumn, toTable, toColumn);

                // Resolve any temp table references to physical tables
                var resolvedFrom = ResolveToPhysicalColumn(fromTable, fromColumn);
                var resolvedTo = ResolveToPhysicalColumn(toTable, toColumn);

                if (resolvedFrom.HasValue && resolvedTo.HasValue)
                {
                    var (physFromTable, physFromCol) = resolvedFrom.Value;
                    var (physToTable, physToCol) = resolvedTo.Value;

                    // Add the relationship between physical tables
                    if (!IsTempTable(physFromTable) && !IsTempTable(physToTable))
                    {
                        analysis.AddRelationship(physFromTable, physFromCol, physToTable, physToCol, XmSqlJoinType.Unknown);

                        // Mark columns as join keys
                        var fromTableInfo = analysis.GetOrAddTable(physFromTable);
                        fromTableInfo?.GetOrAddColumn(physFromCol)?.AddUsage(XmSqlColumnUsage.Join);

                        var toTableInfo = analysis.GetOrAddTable(physToTable);
                        toTableInfo?.GetOrAddColumn(physToCol)?.AddUsage(XmSqlColumnUsage.Join);

                        Log.Debug("  Added physical relationship: {From}[{FromCol}] -> {To}[{ToCol}]",
                            physFromTable, physFromCol, physToTable, physToCol);
                    }
                }
            }
        }
    }
}
