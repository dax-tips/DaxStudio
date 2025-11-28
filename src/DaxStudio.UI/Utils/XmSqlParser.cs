using DaxStudio.UI.Model;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DaxStudio.UI.Utils
{
    /// <summary>
    /// Parses xmSQL queries from Server Timing events to extract table, column, and relationship information.
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

        /// <summary>
        /// Parses a single xmSQL query and adds the results to the analysis.
        /// </summary>
        /// <param name="xmSql">The xmSQL query text to parse.</param>
        /// <param name="analysis">The analysis object to add results to.</param>
        /// <returns>True if parsing was successful, false otherwise.</returns>
        public bool ParseQuery(string xmSql, XmSqlAnalysis analysis)
        {
            if (string.IsNullOrWhiteSpace(xmSql))
                return false;

            try
            {
                analysis.TotalSEQueriesAnalyzed++;

                // Parse FROM clause (base table)
                ParseFromClause(xmSql, analysis);

                // Parse SELECT clause (selected columns)
                ParseSelectClause(xmSql, analysis);

                // Parse JOIN clauses and relationships
                ParseJoinClauses(xmSql, analysis);

                // Parse ON clauses for relationship details
                ParseOnClauses(xmSql, analysis);

                // Parse WHERE clause (filtered columns)
                ParseWhereClause(xmSql, analysis);

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
        /// </summary>
        private void ParseFromClause(string xmSql, XmSqlAnalysis analysis)
        {
            var matches = FromClausePattern.Matches(xmSql);
            foreach (Match match in matches)
            {
                var tableName = match.Groups["table"].Value;
                var table = analysis.GetOrAddTable(tableName);
                if (table != null)
                {
                    table.IsFromTable = true;
                    table.HitCount++;
                }
            }
        }

        /// <summary>
        /// Parses the SELECT clause to identify selected columns.
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
        /// </summary>
        private void ParseJoinClauses(string xmSql, XmSqlAnalysis analysis)
        {
            // Parse LEFT OUTER JOINs
            var leftJoinMatches = LeftOuterJoinPattern.Matches(xmSql);
            foreach (Match match in leftJoinMatches)
            {
                var tableName = match.Groups["table"].Value;
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

                // Add relationship
                analysis.AddRelationship(fromTable, fromColumn, toTable, toColumn, joinType);

                // Mark columns as used in joins
                var fromTableInfo = analysis.GetOrAddTable(fromTable);
                var fromColumnInfo = fromTableInfo?.GetOrAddColumn(fromColumn);
                fromColumnInfo?.AddUsage(XmSqlColumnUsage.Join);
                Log.Debug("  Marked {Table}[{Col}] as Join", fromTable, fromColumn);

                var toTableInfo = analysis.GetOrAddTable(toTable);
                var toColumnInfo = toTableInfo?.GetOrAddColumn(toColumn);
                toColumnInfo?.AddUsage(XmSqlColumnUsage.Join);
                Log.Debug("  Marked {Table}[{Col}] as Join", toTable, toColumn);
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
    }
}
