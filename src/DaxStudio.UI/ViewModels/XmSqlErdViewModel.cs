using Caliburn.Micro;
using DaxStudio.Common.Enums;
using DaxStudio.QueryTrace;
using DaxStudio.UI.Interfaces;
using DaxStudio.UI.Model;
using DaxStudio.UI.Utils;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DaxStudio.UI.ViewModels
{
    /// <summary>
    /// ViewModel for the xmSQL ERD (Entity Relationship Diagram) visualization.
    /// This displays tables, columns, and relationships extracted from Server Timing events.
    /// This is a dockable tool window that can be resized, floated, or maximized.
    /// </summary>
    [PartCreationPolicy(CreationPolicy.NonShared)]
    [Export]
    public class XmSqlErdViewModel : ToolWindowBase
    {
        private readonly XmSqlParser _parser;
        private XmSqlAnalysis _analysis;

        [ImportingConstructor]
        public XmSqlErdViewModel()
        {
            _parser = new XmSqlParser();
            _analysis = new XmSqlAnalysis();
        }

        #region ToolWindowBase Implementation

        public override string Title => "Storage Engine Dependencies";
        public override string DefaultDockingPane => "DockBottom";
        public override string ContentId => "storage-engine-dependencies";
        public override bool CanHide => true;
        
        /// <summary>
        /// Icon for the tool window (used by AvalonDock).
        /// </summary>
        public ImageSource IconSource => null;

        #endregion

        #region Properties
        /// The analysis results containing all parsed table/column/relationship data.
        /// </summary>
        public XmSqlAnalysis Analysis
        {
            get => _analysis;
            private set
            {
                _analysis = value;
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(Tables));
                NotifyOfPropertyChange(nameof(Relationships));
                NotifyOfPropertyChange(nameof(SummaryText));
                NotifyOfPropertyChange(nameof(HasData));
            }
        }

        /// <summary>
        /// Collection of table view models for display.
        /// </summary>
        public BindableCollection<ErdTableViewModel> Tables { get; } = new BindableCollection<ErdTableViewModel>();

        /// <summary>
        /// Collection of relationship view models for display.
        /// </summary>
        public BindableCollection<ErdRelationshipViewModel> Relationships { get; } = new BindableCollection<ErdRelationshipViewModel>();

        /// <summary>
        /// Summary text showing counts.
        /// </summary>
        public string SummaryText => _analysis != null
            ? $"{Tables.Count} Tables, {Tables.SelectMany(t => t.Columns).Count()} Columns, {Relationships.Count} Relationships (from {_analysis.SuccessfullyParsedQueries:N0} SE queries)"
            : "No data";

        /// <summary>
        /// Whether there is data to display.
        /// </summary>
        public bool HasData => Tables.Count > 0;

        /// <summary>
        /// Whether there is no data to display (inverse of HasData, for binding).
        /// </summary>
        public bool NoData => !HasData;

        private string _searchText = string.Empty;
        /// <summary>
        /// Search text for filtering tables/columns.
        /// </summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                NotifyOfPropertyChange();
                ApplySearchFilter();
            }
        }
        
        /// <summary>
        /// Clears the search text.
        /// </summary>
        public void ClearSearch()
        {
            SearchText = string.Empty;
        }
        
        /// <summary>
        /// Collapses all tables to show only headers.
        /// </summary>
        public void CollapseAll()
        {
            foreach (var table in Tables)
            {
                table.IsCollapsed = true;
            }
        }
        
        /// <summary>
        /// Expands all tables to show columns.
        /// </summary>
        public void ExpandAll()
        {
            foreach (var table in Tables)
            {
                table.IsCollapsed = false;
            }
        }
        
        /// <summary>
        /// Applies the search filter to highlight matching tables/columns.
        /// </summary>
        private void ApplySearchFilter()
        {
            var search = _searchText?.Trim() ?? string.Empty;
            var hasSearch = !string.IsNullOrEmpty(search);
            
            foreach (var table in Tables)
            {
                if (!hasSearch)
                {
                    // No search - clear all search highlighting
                    table.IsSearchMatch = false;
                    table.IsSearchDimmed = false;
                    foreach (var col in table.Columns)
                    {
                        col.IsSearchMatch = false;
                    }
                }
                else
                {
                    // Check if table name matches
                    var tableMatches = table.TableName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
                    
                    // Check if any column matches
                    var anyColumnMatches = false;
                    foreach (var col in table.Columns)
                    {
                        col.IsSearchMatch = col.ColumnName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (col.IsSearchMatch) anyColumnMatches = true;
                    }
                    
                    table.IsSearchMatch = tableMatches || anyColumnMatches;
                    table.IsSearchDimmed = !table.IsSearchMatch;
                }
            }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; NotifyOfPropertyChange(); }
        }

        private double _canvasWidth = 800;
        public double CanvasWidth
        {
            get => _canvasWidth;
            set { _canvasWidth = value; NotifyOfPropertyChange(); }
        }

        private double _canvasHeight = 600;
        public double CanvasHeight
        {
            get => _canvasHeight;
            set { _canvasHeight = value; NotifyOfPropertyChange(); }
        }

        private double _viewWidth = 800;
        /// <summary>
        /// The actual width of the view (set by code-behind).
        /// </summary>
        public double ViewWidth
        {
            get => _viewWidth;
            set { _viewWidth = value; NotifyOfPropertyChange(); }
        }

        private double _viewHeight = 600;
        /// <summary>
        /// The actual height of the view (set by code-behind).
        /// </summary>
        public double ViewHeight
        {
            get => _viewHeight;
            set { _viewHeight = value; NotifyOfPropertyChange(); }
        }

        private bool _showBottlenecks;
        /// <summary>
        /// Whether to highlight bottleneck tables (slowest tables).
        /// </summary>
        public bool ShowBottlenecks
        {
            get => _showBottlenecks;
            set
            {
                _showBottlenecks = value;
                NotifyOfPropertyChange();
                UpdateBottleneckHighlighting();
            }
        }

        #region Query Plan Integration

        private List<int> _availableQueryIds = new List<int>();
        /// <summary>
        /// Gets the list of available query IDs from the analysis.
        /// </summary>
        public List<int> AvailableQueryIds
        {
            get => _availableQueryIds;
            private set
            {
                _availableQueryIds = value;
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(HasMultipleQueries));
                NotifyOfPropertyChange(nameof(MaxQueryId));
            }
        }

        /// <summary>
        /// Whether there are multiple queries to filter (shows the filter UI).
        /// </summary>
        public bool HasMultipleQueries => _availableQueryIds.Count > 1;

        /// <summary>
        /// Maximum query ID (for slider range).
        /// </summary>
        public int MaxQueryId => _availableQueryIds.Count > 0 ? _availableQueryIds.Max() : 1;

        private int? _selectedQueryId;
        /// <summary>
        /// The currently selected query ID for filtering. Null means show all queries.
        /// </summary>
        public int? SelectedQueryId
        {
            get => _selectedQueryId;
            set
            {
                _selectedQueryId = value;
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(QueryFilterText));
                NotifyOfPropertyChange(nameof(QueryFilterDetails));
                ApplyQueryFilter();
            }
        }

        /// <summary>
        /// Display text for the current query filter state (e.g., "3 of 7").
        /// </summary>
        public string QueryFilterText
        {
            get
            {
                if (!_selectedQueryId.HasValue)
                    return "All";
                
                var currentIndex = _availableQueryIds.IndexOf(_selectedQueryId.Value);
                return $"{currentIndex + 1} of {_availableQueryIds.Count}";
            }
        }

        /// <summary>
        /// Details about the currently selected query - which tables it accesses.
        /// </summary>
        public string QueryFilterDetails
        {
            get
            {
                if (!_selectedQueryId.HasValue)
                    return $"Showing all {Tables.Count} tables from {_availableQueryIds.Count} SE queries";
                
                var tablesInQuery = Tables.Where(t => t.TableInfo.QueryIds.Contains(_selectedQueryId.Value)).ToList();
                var tableNames = string.Join(", ", tablesInQuery.Select(t => t.TableName).Take(3));
                if (tablesInQuery.Count > 3)
                    tableNames += $" +{tablesInQuery.Count - 3} more";
                
                return $"SE Query #{_selectedQueryId.Value}: {tablesInQuery.Count} table(s) - {tableNames}";
            }
        }

        /// <summary>
        /// Clears the query filter to show all tables.
        /// </summary>
        public void ClearQueryFilter()
        {
            SelectedQueryId = null;
        }

        /// <summary>
        /// Filters to show only the previous query.
        /// </summary>
        public void PreviousQuery()
        {
            if (!_selectedQueryId.HasValue)
            {
                // Currently showing all - go to first query
                if (_availableQueryIds.Count > 0)
                    SelectedQueryId = _availableQueryIds.First();
            }
            else
            {
                var currentIndex = _availableQueryIds.IndexOf(_selectedQueryId.Value);
                if (currentIndex > 0)
                {
                    SelectedQueryId = _availableQueryIds[currentIndex - 1];
                }
            }
        }

        /// <summary>
        /// Filters to show only the next query.
        /// </summary>
        public void NextQuery()
        {
            if (!_selectedQueryId.HasValue)
            {
                // Currently showing all - go to first query
                if (_availableQueryIds.Count > 0)
                    SelectedQueryId = _availableQueryIds.First();
            }
            else
            {
                var currentIndex = _availableQueryIds.IndexOf(_selectedQueryId.Value);
                if (currentIndex >= 0 && currentIndex < _availableQueryIds.Count - 1)
                {
                    SelectedQueryId = _availableQueryIds[currentIndex + 1];
                }
            }
        }

        /// <summary>
        /// Applies query filter to show/hide tables based on which queries accessed them.
        /// </summary>
        private void ApplyQueryFilter()
        {
            foreach (var table in Tables)
            {
                if (!_selectedQueryId.HasValue)
                {
                    // Show all - no query filtering
                    table.IsQueryFiltered = false;
                    table.IsQueryHighlighted = false;
                }
                else
                {
                    // Check if this table was accessed by the selected query
                    var wasAccessedByQuery = table.TableInfo.QueryIds.Contains(_selectedQueryId.Value);
                    table.IsQueryFiltered = !wasAccessedByQuery;
                    table.IsQueryHighlighted = wasAccessedByQuery;
                }
            }

            // Also update relationships visibility
            foreach (var rel in Relationships)
            {
                if (!_selectedQueryId.HasValue)
                {
                    rel.IsQueryFiltered = false;
                }
                else
                {
                    // Show relationship only if both tables are visible
                    var fromTable = Tables.FirstOrDefault(t => t.TableName == rel.FromTable);
                    var toTable = Tables.FirstOrDefault(t => t.TableName == rel.ToTable);
                    rel.IsQueryFiltered = (fromTable?.IsQueryFiltered ?? true) || (toTable?.IsQueryFiltered ?? true);
                }
            }
        }

        #endregion

        #endregion

        #region Methods

        /// <summary>
        /// Analyzes a collection of SE events and builds the ERD model.
        /// Skips Internal and Batch subclass events for hit counting.
        /// </summary>
        public void AnalyzeEvents(IEnumerable<TraceStorageEngineEvent> events)
        {
            IsLoading = true;

            try
            {
                // Clear previous analysis
                _analysis = new XmSqlAnalysis();
                Tables.Clear();
                Relationships.Clear();

                // Parse each SE event's query
                foreach (var evt in events)
                {
                    // Only parse scan events that have query text
                    // Skip Internal and Batch events as they don't represent actual user queries
                    if (evt.IsScanEvent && !string.IsNullOrWhiteSpace(evt.Query) 
                        && !evt.IsInternalEvent && !evt.IsBatchEvent)
                    {
                        // Build full metrics including cache hit status and parallelism data
                        var metrics = new XmSqlParser.SeEventMetrics
                        {
                            QueryId = evt.RowNumber,  // Track which query this is for Query Plan Integration
                            EstimatedRows = evt.EstimatedRows,
                            DurationMs = evt.Duration,
                            IsCacheHit = evt.Class == DaxStudioTraceEventClass.VertiPaqSEQueryCacheMatch,
                            CpuTimeMs = evt.CpuTime,
                            CpuFactor = evt.CpuFactor,
                            NetParallelDurationMs = evt.NetParallelDuration
                        };
                        _parser.ParseQueryWithMetrics(evt.Query, _analysis, metrics);
                    }
                }

                // Create view models for tables
                CreateTableViewModels();

                // Calculate heat map levels
                CalculateHeatLevels();

                // Create view models for relationships
                CreateRelationshipViewModels();

                // Layout the diagram
                LayoutDiagram();

                // Gather available query IDs for query plan integration
                GatherAvailableQueryIds();

                NotifyOfPropertyChange(nameof(Analysis));
                NotifyOfPropertyChange(nameof(SummaryText));
                NotifyOfPropertyChange(nameof(HasData));
                NotifyOfPropertyChange(nameof(NoData));
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Creates table view models from the analysis.
        /// </summary>
        private void CreateTableViewModels()
        {
            Tables.Clear();

            foreach (var tableInfo in _analysis.Tables.Values
                .Where(t => !IsIntermediateTable(t.TableName))
                .OrderByDescending(t => t.HitCount))
            {
                var tableVm = new ErdTableViewModel(tableInfo);
                Tables.Add(tableVm);
            }
        }

        /// <summary>
        /// Determines if a table is an intermediate/temporary table (e.g., $TTable1, $TTable2).
        /// These are internal xmSQL constructs and typically not useful for the user to see.
        /// </summary>
        private bool IsIntermediateTable(string tableName)
        {
            // Filter out tables that match the pattern $TTable followed by digits
            // Also filter out tables starting with $ in general (internal tables)
            if (string.IsNullOrEmpty(tableName)) return true;
            
            // Match $TTable1, $TTable2, etc.
            if (tableName.StartsWith("$TTable", StringComparison.OrdinalIgnoreCase))
                return true;
            
            // Could add more patterns here in the future
            // e.g., $SemijoinResult, etc.
            
            return false;
        }

        /// <summary>
        /// Creates relationship view models from the analysis.
        /// </summary>
        private void CreateRelationshipViewModels()
        {
            Relationships.Clear();

            Log.Debug("Creating relationship view models. Analysis has {Count} relationships", _analysis.Relationships.Count);

            foreach (var rel in _analysis.Relationships)
            {
                Log.Debug("Processing relationship: {From}.{FromCol} -> {To}.{ToCol}", 
                    rel.FromTable, rel.FromColumn, rel.ToTable, rel.ToColumn);
                
                var fromTable = Tables.FirstOrDefault(t => t.TableName.Equals(rel.FromTable, StringComparison.OrdinalIgnoreCase));
                var toTable = Tables.FirstOrDefault(t => t.TableName.Equals(rel.ToTable, StringComparison.OrdinalIgnoreCase));

                Log.Debug("  FromTable found: {Found}, ToTable found: {ToFound}", 
                    fromTable != null, toTable != null);

                if (fromTable != null && toTable != null)
                {
                    var relVm = new ErdRelationshipViewModel(rel, fromTable, toTable);
                    Relationships.Add(relVm);
                    Log.Debug("  Added relationship VM. Path: {Path}", relVm.PathData);
                }
            }
            
            Log.Debug("Total relationship VMs created: {Count}", Relationships.Count);
        }

        /// <summary>
        /// Gathers all unique query IDs from the tables for query plan integration.
        /// </summary>
        private void GatherAvailableQueryIds()
        {
            var allQueryIds = new HashSet<int>();
            foreach (var table in Tables)
            {
                foreach (var queryId in table.TableInfo.QueryIds)
                {
                    allQueryIds.Add(queryId);
                }
            }
            AvailableQueryIds = allQueryIds.OrderBy(x => x).ToList();
            
            // Clear any previous query filter
            _selectedQueryId = null;
            NotifyOfPropertyChange(nameof(SelectedQueryId));
            NotifyOfPropertyChange(nameof(QueryFilterText));
        }

        /// <summary>
        /// Lays out the tables on the canvas using a relationship-aware algorithm.
        /// Tables connected by relationships are placed closer together.
        /// Fact tables (many relationships) go in center, dimension tables around them.
        /// </summary>
        private void LayoutDiagram()
        {
            if (Tables.Count == 0) return;

            const double tableWidth = 200;
            const double tableHeight = 180;
            const double horizontalSpacing = 100;
            const double verticalSpacing = 60;
            const double padding = 40;

            // Build relationship map to understand connectivity
            var relationshipMap = BuildRelationshipMap();

            // Find fact tables (tables with most relationships) vs dimension tables
            var tablesByConnections = Tables
                .OrderByDescending(t => relationshipMap.TryGetValue(t.TableName, out var connections) ? connections.Count : 0)
                .ThenByDescending(t => t.HitCount)
                .ToList();

            // Use a star/radial layout for better visualization
            // Most connected table goes in center, others radiate outward
            if (Tables.Count == 1)
            {
                // Single table - just center it
                Tables[0].X = padding;
                Tables[0].Y = padding;
                Tables[0].Width = tableWidth;
                Tables[0].Height = tableHeight;
            }
            else if (Tables.Count == 2)
            {
                // Two tables - place side by side
                LayoutHorizontal(tablesByConnections, tableWidth, tableHeight, horizontalSpacing, padding);
            }
            else
            {
                // 3+ tables - use star layout with most connected table in center
                LayoutStar(tablesByConnections, relationshipMap, tableWidth, tableHeight, horizontalSpacing, verticalSpacing, padding);
            }

            // Calculate canvas size to fit all tables
            var maxX = Tables.Max(t => t.X + t.Width);
            var maxY = Tables.Max(t => t.Y + t.Height);
            CanvasWidth = Math.Max(100, maxX + padding);
            CanvasHeight = Math.Max(100, maxY + padding);

            // Update relationship line positions
            foreach (var rel in Relationships)
            {
                rel.UpdatePath();
            }
        }

        /// <summary>
        /// Builds a map of table name to connected table names.
        /// </summary>
        private Dictionary<string, HashSet<string>> BuildRelationshipMap()
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var rel in _analysis.Relationships)
            {
                if (!map.ContainsKey(rel.FromTable))
                    map[rel.FromTable] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!map.ContainsKey(rel.ToTable))
                    map[rel.ToTable] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                map[rel.FromTable].Add(rel.ToTable);
                map[rel.ToTable].Add(rel.FromTable);
            }
            
            return map;
        }

        /// <summary>
        /// Layout tables horizontally in a single row (for small diagrams).
        /// </summary>
        private void LayoutHorizontal(List<ErdTableViewModel> tables, double tableWidth, double tableHeight, double spacing, double padding)
        {
            double x = padding;
            foreach (var table in tables)
            {
                table.X = x;
                table.Y = padding;
                table.Width = tableWidth;
                table.Height = tableHeight;
                x += tableWidth + spacing;
            }
        }

        /// <summary>
        /// Layout tables in a star pattern with most connected table(s) in center.
        /// </summary>
        private void LayoutStar(List<ErdTableViewModel> tables, Dictionary<string, HashSet<string>> relationshipMap, 
            double tableWidth, double tableHeight, double hSpacing, double vSpacing, double padding)
        {
            // Calculate center position for the most connected table
            var centerTable = tables[0];
            var outerTables = tables.Skip(1).ToList();
            
            // If there are many tables, arrange them in tiers
            var tier1 = new List<ErdTableViewModel>(); // Directly connected to center
            var tier2 = new List<ErdTableViewModel>(); // Not directly connected
            
            var centerConnections = relationshipMap.TryGetValue(centerTable.TableName, out var conn) ? conn : new HashSet<string>();
            
            foreach (var table in outerTables)
            {
                if (centerConnections.Contains(table.TableName))
                    tier1.Add(table);
                else
                    tier2.Add(table);
            }
            
            // Place center table
            double tier1Radius = tableWidth + hSpacing;
            double tier2Radius = tier1Radius + tableWidth + hSpacing;
            
            // Calculate center based on number of tiers needed
            double centerX = padding + (tier2.Any() ? tier2Radius : tier1Radius);
            double centerY = padding + (tier2.Any() ? tier2Radius : tier1Radius);
            
            centerTable.X = centerX;
            centerTable.Y = centerY;
            centerTable.Width = tableWidth;
            centerTable.Height = tableHeight;
            
            // Place tier 1 tables (directly connected) in a circle around center
            LayoutInCircle(tier1, centerX + tableWidth/2, centerY + tableHeight/2, tier1Radius, tableWidth, tableHeight);
            
            // Place tier 2 tables (not directly connected) in outer circle
            if (tier2.Any())
            {
                LayoutInCircle(tier2, centerX + tableWidth/2, centerY + tableHeight/2, tier2Radius, tableWidth, tableHeight);
            }
        }

        /// <summary>
        /// Layout tables in a circle around a center point.
        /// </summary>
        private void LayoutInCircle(List<ErdTableViewModel> tables, double centerX, double centerY, double radius, double tableWidth, double tableHeight)
        {
            if (tables.Count == 0) return;
            
            double angleStep = 2 * Math.PI / tables.Count;
            double startAngle = -Math.PI / 2; // Start from top
            
            for (int i = 0; i < tables.Count; i++)
            {
                double angle = startAngle + i * angleStep;
                tables[i].X = centerX + radius * Math.Cos(angle) - tableWidth / 2;
                tables[i].Y = centerY + radius * Math.Sin(angle) - tableHeight / 2;
                tables[i].Width = tableWidth;
                tables[i].Height = tableHeight;
            }
        }

        /// <summary>
        /// Clears all analysis data.
        /// </summary>
        public void Clear()
        {
            _analysis?.Clear();
            Tables.Clear();
            Relationships.Clear();
            NotifyOfPropertyChange(nameof(Analysis));
            NotifyOfPropertyChange(nameof(SummaryText));
            NotifyOfPropertyChange(nameof(HasData));
        }

        /// <summary>
        /// Copies the ERD summary to clipboard.
        /// </summary>
        public void CopyToClipboard()
        {
            if (_analysis == null) return;

            var text = new System.Text.StringBuilder();
            text.AppendLine("=== xmSQL Query Dependencies ===");
            text.AppendLine();
            text.AppendLine($"Tables: {_analysis.UniqueTablesCount}");
            text.AppendLine($"Columns: {_analysis.UniqueColumnsCount}");
            text.AppendLine($"Relationships: {_analysis.UniqueRelationshipsCount}");
            text.AppendLine($"SE Queries Analyzed: {_analysis.TotalSEQueriesAnalyzed}");
            text.AppendLine();

            text.AppendLine("=== Tables ===");
            foreach (var table in _analysis.Tables.Values.OrderByDescending(t => t.HitCount))
            {
                text.AppendLine($"\n[{table.TableName}] (Hit Count: {table.HitCount})");
                foreach (var col in table.Columns.Values.OrderByDescending(c => c.HitCount))
                {
                    var usages = new List<string>();
                    if (col.UsageTypes.HasFlag(XmSqlColumnUsage.Select)) usages.Add("Select");
                    if (col.UsageTypes.HasFlag(XmSqlColumnUsage.Filter)) usages.Add("Filter");
                    if (col.UsageTypes.HasFlag(XmSqlColumnUsage.Join)) usages.Add("Join");
                    if (col.UsageTypes.HasFlag(XmSqlColumnUsage.Aggregate)) usages.Add($"Aggregate({string.Join(",", col.AggregationTypes)})");

                    text.AppendLine($"  [{col.ColumnName}] - {string.Join(", ", usages)} (Hit Count: {col.HitCount})");
                }
            }

            text.AppendLine("\n=== Relationships ===");
            foreach (var rel in _analysis.Relationships.OrderByDescending(r => r.HitCount))
            {
                text.AppendLine($"  [{rel.FromTable}].[{rel.FromColumn}] -> [{rel.ToTable}].[{rel.ToColumn}] ({rel.JoinType}, Hit Count: {rel.HitCount})");
            }

            Clipboard.SetText(text.ToString());
        }

        /// <summary>
        /// Resets the layout by re-running the layout algorithm.
        /// Useful after dragging tables around to restore the auto-arranged layout.
        /// </summary>
        public void ResetLayout()
        {
            if (Tables.Count == 0) return;
            
            LayoutDiagram();
            CalculateHeatLevels();
        }
        
        /// <summary>
        /// Event raised when export to image is requested.
        /// The view handles the actual rendering.
        /// </summary>
        public event EventHandler<string> ExportRequested;
        
        /// <summary>
        /// Requests export of the diagram to an image file.
        /// </summary>
        public void ExportToImage()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG Image|*.png",
                Title = "Export Diagram to Image",
                FileName = "QueryDependencies.png"
            };
            
            if (dialog.ShowDialog() == true)
            {
                ExportRequested?.Invoke(this, dialog.FileName);
            }
        }

        #endregion

        #region Zoom

        /// <summary>
        /// Zooms in the diagram by increasing the scale.
        /// </summary>
        public void ZoomIn()
        {
            Scale = Math.Min(Scale + 0.1, 3.0); // Max 300%
        }

        /// <summary>
        /// Zooms out the diagram by decreasing the scale.
        /// </summary>
        public void ZoomOut()
        {
            Scale = Math.Max(Scale - 0.1, 0.25); // Min 25%
        }

        /// <summary>
        /// Resets the zoom level to 100%.
        /// </summary>
        public void ResetZoom()
        {
            Scale = 1.0;
        }

        /// <summary>
        /// Zooms to fit all tables within the visible view area.
        /// </summary>
        public void ZoomToFit()
        {
            if (Tables.Count == 0) return;

            // Calculate bounds of all tables
            var minX = Tables.Min(t => t.X);
            var minY = Tables.Min(t => t.Y);
            var maxX = Tables.Max(t => t.X + t.Width);
            var maxY = Tables.Max(t => t.Y + t.Height);

            var contentWidth = maxX - minX + 80; // Add padding
            var contentHeight = maxY - minY + 80;

            // Calculate scale to fit
            var scaleX = ViewWidth / contentWidth;
            var scaleY = ViewHeight / contentHeight;
            var newScale = Math.Min(scaleX, scaleY);

            // Clamp to reasonable bounds
            newScale = Math.Max(0.25, Math.Min(2.0, newScale));

            Scale = newScale;
        }

        #endregion

        #region Mini-map Navigation

        private bool _showMiniMap = true;
        /// <summary>
        /// Whether to show the mini-map overview panel.
        /// </summary>
        public bool ShowMiniMap
        {
            get => _showMiniMap;
            set 
            { 
                _showMiniMap = value; 
                NotifyOfPropertyChange(); 
            }
        }

        /// <summary>
        /// Toggles the mini-map visibility.
        /// </summary>
        public void ToggleMiniMap()
        {
            ShowMiniMap = !ShowMiniMap;
        }

        private double _viewportX;
        /// <summary>
        /// The X position of the viewport in content coordinates (for mini-map).
        /// </summary>
        public double ViewportX
        {
            get => _viewportX;
            set 
            { 
                _viewportX = value; 
                NotifyOfPropertyChange(); 
            }
        }

        private double _viewportY;
        /// <summary>
        /// The Y position of the viewport in content coordinates (for mini-map).
        /// </summary>
        public double ViewportY
        {
            get => _viewportY;
            set 
            { 
                _viewportY = value; 
                NotifyOfPropertyChange(); 
            }
        }

        private double _contentWidth = 1000;
        /// <summary>
        /// Total width of all content (for mini-map calculations).
        /// </summary>
        public double ContentWidth
        {
            get => _contentWidth;
            set 
            { 
                _contentWidth = value; 
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(MiniMapScaleX));
            }
        }

        private double _contentHeight = 1000;
        /// <summary>
        /// Total height of all content (for mini-map calculations).
        /// </summary>
        public double ContentHeight
        {
            get => _contentHeight;
            set 
            { 
                _contentHeight = value; 
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(MiniMapScaleY));
            }
        }

        // Mini-map dimensions
        private const double MiniMapWidth = 150;
        private const double MiniMapHeight = 100;

        /// <summary>
        /// Scale factor for X axis in mini-map.
        /// </summary>
        public double MiniMapScaleX => ContentWidth > 0 ? MiniMapWidth / ContentWidth : 1;

        /// <summary>
        /// Scale factor for Y axis in mini-map.
        /// </summary>
        public double MiniMapScaleY => ContentHeight > 0 ? MiniMapHeight / ContentHeight : 1;

        /// <summary>
        /// Updates content bounds for mini-map based on table positions.
        /// </summary>
        public void UpdateContentBounds()
        {
            if (Tables.Count == 0)
            {
                ContentWidth = 1000;
                ContentHeight = 800;
                return;
            }

            var minX = Tables.Min(t => t.X);
            var minY = Tables.Min(t => t.Y);
            var maxX = Tables.Max(t => t.X + t.Width);
            var maxY = Tables.Max(t => t.Y + t.Height);

            ContentWidth = Math.Max(maxX - minX + 100, ViewWidth);
            ContentHeight = Math.Max(maxY - minY + 100, ViewHeight);
        }

        /// <summary>
        /// Called when mini-map is clicked to navigate to that location.
        /// </summary>
        public event EventHandler<Point> MiniMapNavigationRequested;

        /// <summary>
        /// Navigates to a position on the mini-map (triggered by click).
        /// </summary>
        public void NavigateToMiniMapPosition(double miniMapX, double miniMapY)
        {
            // Convert mini-map coordinates to content coordinates
            var contentX = miniMapX / MiniMapScaleX;
            var contentY = miniMapY / MiniMapScaleY;

            MiniMapNavigationRequested?.Invoke(this, new Point(contentX, contentY));
        }

        #endregion

        #region Bottleneck Analysis

        /// <summary>
        /// Toggles bottleneck highlighting on/off.
        /// </summary>
        public void ToggleBottlenecks()
        {
            ShowBottlenecks = !ShowBottlenecks;
        }

        /// <summary>
        /// Updates bottleneck highlighting on all tables based on their duration.
        /// Tables in the top 20% of duration are marked as bottlenecks.
        /// </summary>
        private void UpdateBottleneckHighlighting()
        {
            if (!_showBottlenecks)
            {
                // Clear all bottleneck flags
                foreach (var table in Tables)
                {
                    table.IsBottleneck = false;
                    table.BottleneckRank = 0;
                }
                return;
            }

            // Calculate bottleneck threshold (top 20% or top 3, whichever is smaller)
            var tablesWithDuration = Tables
                .Where(t => t.TotalDurationMs > 0)
                .OrderByDescending(t => t.TotalDurationMs)
                .ToList();

            if (tablesWithDuration.Count == 0) return;

            var bottleneckCount = Math.Max(1, Math.Min(3, (int)Math.Ceiling(tablesWithDuration.Count * 0.2)));
            var threshold = tablesWithDuration.Count > bottleneckCount 
                ? tablesWithDuration[bottleneckCount - 1].TotalDurationMs 
                : 0;

            // Mark bottleneck tables
            var rank = 1;
            foreach (var table in Tables)
            {
                if (tablesWithDuration.Contains(table) && 
                    tablesWithDuration.IndexOf(table) < bottleneckCount)
                {
                    table.IsBottleneck = true;
                    table.BottleneckRank = rank++;
                }
                else
                {
                    table.IsBottleneck = false;
                    table.BottleneckRank = 0;
                }
            }
        }

        #endregion

        #region Selection and Highlighting

        private ErdTableViewModel _selectedTable;
        /// <summary>
        /// The currently selected table (if any).
        /// </summary>
        public ErdTableViewModel SelectedTable
        {
            get => _selectedTable;
            private set
            {
                _selectedTable = value;
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(HasSelectedTable));
            }
        }

        /// <summary>
        /// Whether a table is currently selected.
        /// </summary>
        public bool HasSelectedTable => _selectedTable != null;

        /// <summary>
        /// Selects a table and highlights related tables and relationships.
        /// </summary>
        public void SelectTable(ErdTableViewModel table)
        {
            // If clicking the same table, deselect
            if (_selectedTable == table)
            {
                ClearSelection();
                return;
            }

            // Clear previous selection
            ClearSelectionState();

            // Set new selection
            SelectedTable = table;
            table.IsSelected = true;

            // Find related tables and relationships
            var relatedTables = new HashSet<ErdTableViewModel>();

            foreach (var rel in Relationships)
            {
                bool isRelated = rel.FromTableViewModel == table || rel.ToTableViewModel == table;
                
                if (isRelated)
                {
                    rel.IsHighlighted = true;
                    rel.IsDimmed = false;
                    
                    // Add the other table to related set
                    if (rel.FromTableViewModel == table)
                        relatedTables.Add(rel.ToTableViewModel);
                    else
                        relatedTables.Add(rel.FromTableViewModel);
                }
                else
                {
                    rel.IsHighlighted = false;
                    rel.IsDimmed = true;
                }
            }

            // Update table highlighting
            foreach (var t in Tables)
            {
                if (t == table)
                {
                    t.IsSelected = true;
                    t.IsHighlighted = false;
                    t.IsDimmed = false;
                }
                else if (relatedTables.Contains(t))
                {
                    t.IsSelected = false;
                    t.IsHighlighted = true;
                    t.IsDimmed = false;
                }
                else
                {
                    t.IsSelected = false;
                    t.IsHighlighted = false;
                    t.IsDimmed = true;
                }
            }
        }

        /// <summary>
        /// Selects a table by name and highlights related tables and relationships.
        /// </summary>
        public void SelectTable(string tableName)
        {
            var table = Tables.FirstOrDefault(t => t.TableName == tableName);
            if (table != null)
            {
                SelectTable(table);
            }
        }

        /// <summary>
        /// Clears the current selection and all highlighting.
        /// </summary>
        public void ClearSelection()
        {
            ClearSelectionState();
            SelectedTable = null;
            SelectedDetailInfo = null;
        }

        private string _selectedDetailInfo;
        /// <summary>
        /// Detail information about the selected item (column or relationship).
        /// Displayed in a panel when user clicks on a column or relationship.
        /// </summary>
        public string SelectedDetailInfo
        {
            get => _selectedDetailInfo;
            set
            {
                _selectedDetailInfo = value;
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(HasSelectedDetail));
            }
        }

        public bool HasSelectedDetail => !string.IsNullOrEmpty(_selectedDetailInfo);

        private double _detailPanelHeight = 150;
        /// <summary>
        /// Height of the detail panel (resizable by user).
        /// </summary>
        public double DetailPanelHeight
        {
            get => _detailPanelHeight;
            set
            {
                // Clamp between min and max
                _detailPanelHeight = Math.Max(80, Math.Min(400, value));
                NotifyOfPropertyChange();
            }
        }

        /// <summary>
        /// Shows details for a clicked column.
        /// </summary>
        public void SelectColumn(ErdTableViewModel table, ErdColumnViewModel column)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"📊 Column: {table.TableName}[{column.ColumnName}]");
            sb.AppendLine();
            sb.AppendLine($"Hit Count: {column.HitCount}");
            sb.AppendLine();
            sb.AppendLine("Usage:");
            if (column.IsJoinColumn) sb.AppendLine("  🔑 Join Key");
            if (column.IsFilterColumn)
            {
                sb.AppendLine("  🔍 Filter");
                // Show filter values if available
                if (column.HasFilterValues)
                {
                    sb.AppendLine();
                    sb.AppendLine("Filter Values:");
                    var ops = column.FilterOperators.Any() 
                        ? string.Join("/", column.FilterOperators) 
                        : "";
                    if (!string.IsNullOrEmpty(ops))
                    {
                        sb.AppendLine($"  Operators: {ops}");
                    }
                    
                    // Show values (limit display to first 15)
                    var values = column.FilterValues.Take(15).ToList();
                    foreach (var value in values)
                    {
                        sb.AppendLine($"  • {value}");
                    }
                    if (column.FilterValues.Count > 15)
                    {
                        sb.AppendLine($"  ... and {column.FilterValues.Count - 15} more values");
                    }
                }
            }
            if (column.IsAggregateColumn) sb.AppendLine($"  📈 Aggregate ({column.AggregationText})");
            if (column.IsSelectColumn) sb.AppendLine("  ✓ Selected/Output");
            
            // Add table-level row count context
            if (table.HasRowCountData)
            {
                sb.AppendLine();
                sb.AppendLine($"Table Rows Scanned: {table.TotalRowsFormatted}");
                if (table.HasHighRowCount)
                {
                    sb.AppendLine($"⚠ Max single scan: {table.MaxEstimatedRows:N0} rows");
                }
            }
            
            // Add table cache info if available
            if (table.HasCacheData)
            {
                sb.AppendLine();
                sb.AppendLine($"Table Cache Rate: {table.CacheHitRateFormatted}");
            }
            
            SelectedDetailInfo = sb.ToString();
        }

        /// <summary>
        /// Shows details for a clicked table header.
        /// </summary>
        public void SelectTableDetails(ErdTableViewModel table)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"📊 Table: {table.TableName}");
            sb.AppendLine();
            
            // Basic stats
            sb.AppendLine("Overview:");
            sb.AppendLine($"  SE Query Hits: {table.HitCount}");
            sb.AppendLine($"  SE Queries: {table.QueryCount}");
            sb.AppendLine($"  Columns Used: {table.Columns.Count}");
            
            // Query IDs
            if (table.HasQueryIds)
            {
                sb.AppendLine($"  Query IDs: {table.QueryIdsFormatted}");
            }
            
            // Row statistics
            if (table.HasRowCountData)
            {
                sb.AppendLine();
                sb.AppendLine("Row Statistics:");
                sb.AppendLine($"  Total Rows Scanned: {table.TotalEstimatedRows:N0}");
                sb.AppendLine($"  Max Single Scan: {table.MaxEstimatedRows:N0}");
                
                if (table.HasHighRowCount)
                {
                    sb.AppendLine();
                    sb.AppendLine("⚠ High row count detected (100K+ in single scan)");
                    sb.AppendLine("  Consider adding filters or optimizing the query");
                }
            }
            
            // Duration
            if (table.HasDurationData)
            {
                sb.AppendLine();
                sb.AppendLine("Duration:");
                sb.AppendLine($"  Total: {table.TotalDurationFormatted}");
                sb.AppendLine($"  Max Single: {table.MaxDurationMs}ms");
                
                if (table.HasHighDuration)
                {
                    sb.AppendLine("  ⚠ High duration - potential bottleneck");
                }
            }
            
            // Cache
            if (table.HasCacheData)
            {
                sb.AppendLine();
                sb.AppendLine("Cache Statistics:");
                sb.AppendLine($"  Hits: {table.CacheHits}");
                sb.AppendLine($"  Misses: {table.CacheMisses}");
                sb.AppendLine($"  Hit Rate: {table.CacheHitRateFormatted}");
                
                if (table.HasPoorCacheRate)
                {
                    sb.AppendLine("  ⚠ Poor cache hit rate");
                }
            }
            
            // Parallelism
            if (table.HasParallelData)
            {
                sb.AppendLine();
                sb.AppendLine("Parallelism:");
                sb.AppendLine($"  Parallel Queries: {table.ParallelQueryCount}");
                sb.AppendLine($"  Max CPU Factor: {table.MaxCpuFactor:0.0}x");
                sb.AppendLine($"  Total CPU Time: {table.TotalCpuTimeMs}ms");
                
                if (table.HasHighParallelism)
                {
                    sb.AppendLine("  ℹ High parallelism detected");
                }
            }
            
            // Callbacks warning
            if (table.HasCallbacks)
            {
                sb.AppendLine();
                sb.AppendLine($"⚠ Callbacks Detected: {table.CallbackColumnCount} column(s)");
                sb.AppendLine("  DAX callbacks reduce SE efficiency");
                var callbackCols = table.Columns.Where(c => c.HasCallback).Take(3);
                foreach (var col in callbackCols)
                {
                    sb.AppendLine($"  • [{col.ColumnName}]");
                }
            }
            
            // List columns with filters
            var filterColumns = table.Columns.Where(c => c.IsFilterColumn && c.HasFilterValues).ToList();
            if (filterColumns.Any())
            {
                sb.AppendLine();
                sb.AppendLine("Filtered Columns:");
                foreach (var col in filterColumns.Take(5))
                {
                    var preview = col.FilterValues.Take(3);
                    var previewText = string.Join(", ", preview);
                    if (col.FilterValues.Count > 3)
                        previewText += "...";
                    sb.AppendLine($"  • [{col.ColumnName}]: {previewText}");
                }
                if (filterColumns.Count > 5)
                {
                    sb.AppendLine($"  ... and {filterColumns.Count - 5} more filtered columns");
                }
            }
            
            // List relationships this table participates in
            var tableRelationships = Relationships.Where(r => 
                r.FromTable == table.TableName || r.ToTable == table.TableName).ToList();
            if (tableRelationships.Any())
            {
                sb.AppendLine();
                sb.AppendLine("Relationships:");
                foreach (var rel in tableRelationships.Take(5))
                {
                    var direction = rel.FromTable == table.TableName ? "→" : "←";
                    var otherTable = rel.FromTable == table.TableName ? rel.ToTable : rel.FromTable;
                    sb.AppendLine($"  {direction} {otherTable} ({rel.JoinTypeText})");
                }
                if (tableRelationships.Count > 5)
                {
                    sb.AppendLine($"  ... and {tableRelationships.Count - 5} more relationships");
                }
            }
            
            SelectedDetailInfo = sb.ToString();
        }

        /// <summary>
        /// Shows details for a clicked relationship.
        /// </summary>
        public void SelectRelationship(ErdRelationshipViewModel relationship)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"🔗 Relationship: {relationship.FromTable} → {relationship.ToTable}");
            sb.AppendLine();
            sb.AppendLine("Connection:");
            sb.AppendLine($"  From: {relationship.FromTable}[{relationship.FromColumn}]");
            sb.AppendLine($"  To:   {relationship.ToTable}[{relationship.ToColumn}]");
            sb.AppendLine();
            sb.AppendLine("Properties:");
            sb.AppendLine($"  Join Type: {relationship.JoinTypeText}");
            sb.AppendLine($"  Hit Count: {relationship.HitCount}");
            
            // Cardinality info
            if (relationship.Cardinality != XmSqlCardinality.Unknown)
            {
                sb.AppendLine($"  Cardinality: {relationship.CardinalityText}");
                if (relationship.IsManyToMany)
                {
                    sb.AppendLine();
                    sb.AppendLine("⚠ Many-to-Many relationship detected");
                    sb.AppendLine("  This can impact query performance");
                }
            }
            
            // Cross-filter direction
            if (relationship.IsBidirectional)
            {
                sb.AppendLine($"  Cross-Filter: Bidirectional (Both)");
                sb.AppendLine();
                sb.AppendLine("ℹ Bidirectional cross-filtering enabled");
            }
            
            // Get stats from both tables
            var fromTable = Tables.FirstOrDefault(t => t.TableName == relationship.FromTable);
            var toTable = Tables.FirstOrDefault(t => t.TableName == relationship.ToTable);
            
            if (fromTable != null || toTable != null)
            {
                sb.AppendLine();
                sb.AppendLine("Table Statistics:");
                if (fromTable != null)
                {
                    sb.AppendLine($"  {fromTable.TableName}: {fromTable.HitCount} hits, {fromTable.TotalRowsFormatted} rows");
                }
                if (toTable != null)
                {
                    sb.AppendLine($"  {toTable.TableName}: {toTable.HitCount} hits, {toTable.TotalRowsFormatted} rows");
                }
            }
            
            SelectedDetailInfo = sb.ToString();
        }

        /// <summary>
        /// Clears the detail selection.
        /// </summary>
        public void ClearDetailSelection()
        {
            SelectedDetailInfo = null;
        }

        private void ClearSelectionState()
        {
            foreach (var t in Tables)
            {
                t.IsSelected = false;
                t.IsHighlighted = false;
                t.IsDimmed = false;
            }

            foreach (var rel in Relationships)
            {
                rel.IsHighlighted = false;
                rel.IsDimmed = false;
            }
        }

        /// <summary>
        /// Called when a table is dragged to a new position.
        /// Updates all relationship lines connected to the table.
        /// </summary>
        public void OnTablePositionChanged(ErdTableViewModel table)
        {
            // Update all relationships connected to this table
            foreach (var rel in Relationships)
            {
                if (rel.FromTableViewModel == table || rel.ToTableViewModel == table)
                {
                    rel.UpdatePath();
                }
            }

            // Expand canvas if table is dragged beyond current bounds
            double requiredWidth = table.X + table.Width + 50;  // 50px padding
            double requiredHeight = table.Y + 200 + 50;  // Approximate table height + padding

            if (requiredWidth > CanvasWidth)
            {
                CanvasWidth = requiredWidth;
            }
            if (requiredHeight > CanvasHeight)
            {
                CanvasHeight = requiredHeight;
            }
        }

        #endregion

        #region Heat Map

        /// <summary>
        /// Calculates heat levels for all tables based on their hit counts.
        /// </summary>
        private void CalculateHeatLevels()
        {
            if (Tables.Count == 0) return;

            int maxHitCount = Tables.Max(t => t.HitCount);
            int minHitCount = Tables.Min(t => t.HitCount);
            int range = maxHitCount - minHitCount;

            foreach (var table in Tables)
            {
                if (range > 0)
                {
                    // Normalize to 0.0 - 1.0
                    table.HeatLevel = (double)(table.HitCount - minHitCount) / range;
                }
                else
                {
                    // All tables have same hit count
                    table.HeatLevel = 0.5;
                }
            }
        }

        #endregion

        #region Table Dragging

        private ErdTableViewModel _draggingTable;
        private double _dragStartX;
        private double _dragStartY;
        private double _tableStartX;
        private double _tableStartY;

        /// <summary>
        /// Starts dragging a table.
        /// </summary>
        public void StartTableDrag(ErdTableViewModel table, double mouseX, double mouseY)
        {
            _draggingTable = table;
            _dragStartX = mouseX;
            _dragStartY = mouseY;
            _tableStartX = table.X;
            _tableStartY = table.Y;
            table.IsDragging = true;
        }

        /// <summary>
        /// Updates the position of the dragged table.
        /// </summary>
        public void UpdateTableDrag(double mouseX, double mouseY)
        {
            if (_draggingTable == null) return;

            double deltaX = mouseX - _dragStartX;
            double deltaY = mouseY - _dragStartY;

            _draggingTable.X = Math.Max(0, _tableStartX + deltaX);
            _draggingTable.Y = Math.Max(0, _tableStartY + deltaY);

            // Update relationship lines
            UpdateRelationshipsForTable(_draggingTable);

            // Update canvas size if needed
            UpdateCanvasSize();
        }

        /// <summary>
        /// Ends dragging a table.
        /// </summary>
        public void EndTableDrag()
        {
            if (_draggingTable != null)
            {
                _draggingTable.IsDragging = false;
                _draggingTable = null;
            }
        }

        /// <summary>
        /// Updates all relationships connected to a specific table.
        /// </summary>
        private void UpdateRelationshipsForTable(ErdTableViewModel table)
        {
            foreach (var rel in Relationships)
            {
                if (rel.FromTableViewModel == table || rel.ToTableViewModel == table)
                {
                    rel.UpdatePath();
                }
            }
        }

        /// <summary>
        /// Updates the canvas size to fit all tables.
        /// </summary>
        private void UpdateCanvasSize()
        {
            if (Tables.Count == 0) return;

            double maxX = Tables.Max(t => t.X + t.Width) + 50;
            double maxY = Tables.Max(t => t.Y + t.Height) + 50;

            CanvasWidth = Math.Max(800, maxX);
            CanvasHeight = Math.Max(600, maxY);
        }

        #endregion
    }

    /// <summary>
    /// ViewModel for a table in the ERD diagram.
    /// </summary>
    public class ErdTableViewModel : PropertyChangedBase
    {
        private readonly XmSqlTableInfo _tableInfo;

        public ErdTableViewModel(XmSqlTableInfo tableInfo)
        {
            _tableInfo = tableInfo;
            Columns = new BindableCollection<ErdColumnViewModel>(
                tableInfo.Columns.Values
                    .Where(c => !IsInternalColumn(c.ColumnName))
                    .OrderByDescending(c => c.UsageTypes.HasFlag(XmSqlColumnUsage.Join))
                    .ThenByDescending(c => c.HitCount)
                    .Select(c => new ErdColumnViewModel(c)));
        }

        /// <summary>
        /// Determines if a column is an internal/measure column that should be hidden.
        /// </summary>
        private static bool IsInternalColumn(string columnName)
        {
            if (string.IsNullOrEmpty(columnName)) return true;
            
            // Filter out $Measure0, $Measure1, etc.
            if (columnName.StartsWith("$Measure", StringComparison.OrdinalIgnoreCase))
                return true;
            
            // Filter out $Expr columns
            if (columnName.StartsWith("$Expr", StringComparison.OrdinalIgnoreCase))
                return true;
            
            return false;
        }

        public string TableName => _tableInfo.TableName;
        public int HitCount => _tableInfo.HitCount;
        public bool IsFromTable => _tableInfo.IsFromTable;
        public bool IsJoinedTable => _tableInfo.IsJoinedTable;
        
        /// <summary>
        /// Whether this table is accessed via DirectQuery (vs. VertiPaq).
        /// </summary>
        public bool IsDirectQuery => _tableInfo.IsDirectQuery;
        
        /// <summary>
        /// The DirectQuery source system.
        /// </summary>
        public string DirectQuerySource => _tableInfo.DirectQuerySource;
        
        /// <summary>
        /// Whether this table has columns with DAX callbacks.
        /// </summary>
        public bool HasCallbacks => _tableInfo.HasCallbacks;
        
        /// <summary>
        /// Number of columns with callbacks.
        /// </summary>
        public int CallbackColumnCount => Columns.Count(c => c.HasCallback);
        
        /// <summary>
        /// Total estimated rows scanned for this table across all SE queries.
        /// </summary>
        public long TotalEstimatedRows => _tableInfo.TotalEstimatedRows;
        
        /// <summary>
        /// Maximum rows returned by a single SE query for this table.
        /// </summary>
        public long MaxEstimatedRows => _tableInfo.MaxEstimatedRows;
        
        /// <summary>
        /// Formatted string for total estimated rows (e.g., "1.2M", "500K").
        /// </summary>
        public string TotalRowsFormatted => FormatRowCount(TotalEstimatedRows);
        
        /// <summary>
        /// Whether this table has row count data to display.
        /// </summary>
        public bool HasRowCountData => TotalEstimatedRows > 0;
        
        /// <summary>
        /// Whether this table has a high row count (100k+) which may indicate performance concerns.
        /// </summary>
        public bool HasHighRowCount => MaxEstimatedRows >= 100000;
        
        /// <summary>
        /// Total duration (ms) of all SE queries for this table.
        /// </summary>
        public long TotalDurationMs => _tableInfo.TotalDurationMs;
        
        /// <summary>
        /// Maximum duration (ms) of a single SE query for this table.
        /// </summary>
        public long MaxDurationMs => _tableInfo.MaxDurationMs;
        
        /// <summary>
        /// Formatted string for total duration.
        /// </summary>
        public string TotalDurationFormatted => FormatDuration(TotalDurationMs);
        
        /// <summary>
        /// Whether this table has duration data.
        /// </summary>
        public bool HasDurationData => TotalDurationMs > 0;
        
        /// <summary>
        /// Whether this table has high duration (>100ms total).
        /// </summary>
        public bool HasHighDuration => TotalDurationMs >= 100;

        #region Cache Hit/Miss Tracking
        
        /// <summary>
        /// Number of cache hits for queries on this table.
        /// </summary>
        public int CacheHits => _tableInfo.CacheHits;
        
        /// <summary>
        /// Number of cache misses for queries on this table.
        /// </summary>
        public int CacheMisses => _tableInfo.CacheMisses;
        
        /// <summary>
        /// Total queries (cache hits + misses) for this table.
        /// </summary>
        public int TotalCacheQueries => CacheHits + CacheMisses;
        
        /// <summary>
        /// Cache hit rate as a percentage (0-100).
        /// </summary>
        public double CacheHitRate => TotalCacheQueries > 0 ? (double)CacheHits / TotalCacheQueries * 100 : 0;
        
        /// <summary>
        /// Formatted cache hit rate string.
        /// </summary>
        public string CacheHitRateFormatted => $"{CacheHitRate:0}%";
        
        /// <summary>
        /// Whether this table has cache data to display.
        /// </summary>
        public bool HasCacheData => TotalCacheQueries > 0;
        
        /// <summary>
        /// Whether cache hit rate is good (>= 50%).
        /// </summary>
        public bool HasGoodCacheRate => CacheHitRate >= 50;
        
        /// <summary>
        /// Whether cache hit rate is poor (< 20%).
        /// </summary>
        public bool HasPoorCacheRate => CacheHitRate < 20 && TotalCacheQueries > 0;
        
        /// <summary>
        /// Cache indicator tooltip text.
        /// </summary>
        public string CacheTooltip => $"Cache: {CacheHits} hits, {CacheMisses} misses ({CacheHitRateFormatted} hit rate)";
        
        #endregion

        #region Query Count Tracking
        
        /// <summary>
        /// Number of distinct SE queries that accessed this table.
        /// </summary>
        public int QueryCount => _tableInfo.QueryCount;
        
        /// <summary>
        /// Whether this table has query count data.
        /// </summary>
        public bool HasQueryCountData => QueryCount > 0;
        
        /// <summary>
        /// Query count tooltip.
        /// </summary>
        public string QueryCountTooltip => $"{QueryCount} SE queries";
        
        #endregion

        #region Parallelism Tracking
        
        /// <summary>
        /// Total CPU time (ms) across all queries for this table.
        /// </summary>
        public long TotalCpuTimeMs => _tableInfo.TotalCpuTimeMs;
        
        /// <summary>
        /// Total parallel duration saved (ms) from parallel execution.
        /// </summary>
        public long TotalParallelDurationMs => _tableInfo.TotalParallelDurationMs;
        
        /// <summary>
        /// Maximum CPU factor observed for this table (higher = more parallelism).
        /// </summary>
        public double MaxCpuFactor => _tableInfo.MaxCpuFactor;
        
        /// <summary>
        /// Number of queries that ran in parallel for this table.
        /// </summary>
        public int ParallelQueryCount => _tableInfo.ParallelQueryCount;
        
        /// <summary>
        /// Whether this table had queries that ran in parallel.
        /// </summary>
        public bool HasParallelData => ParallelQueryCount > 0;
        
        /// <summary>
        /// Whether this table has high parallelism (CpuFactor >= 2).
        /// </summary>
        public bool HasHighParallelism => MaxCpuFactor >= 2.0;
        
        /// <summary>
        /// Formatted CPU factor for display.
        /// </summary>
        public string CpuFactorFormatted => MaxCpuFactor > 0 ? $"{MaxCpuFactor:0.0}x" : "";
        
        /// <summary>
        /// Parallelism indicator tooltip.
        /// </summary>
        public string ParallelismTooltip => HasParallelData 
            ? $"Parallelism: {ParallelQueryCount} parallel queries, max {MaxCpuFactor:0.0}x CPU factor, {FormatDuration(TotalParallelDurationMs)} saved"
            : "No parallel execution detected";
        
        #endregion

        private bool _isBottleneck;
        /// <summary>
        /// Whether this table is identified as a performance bottleneck.
        /// </summary>
        public bool IsBottleneck
        {
            get => _isBottleneck;
            set { _isBottleneck = value; NotifyOfPropertyChange(); }
        }

        private int _bottleneckRank;
        /// <summary>
        /// The rank of this bottleneck (1 = slowest, 2 = second slowest, etc.). 0 if not a bottleneck.
        /// </summary>
        public int BottleneckRank
        {
            get => _bottleneckRank;
            set { _bottleneckRank = value; NotifyOfPropertyChange(); }
        }
        
        private bool _isCollapsed;
        /// <summary>
        /// Whether this table's columns are collapsed (header only).
        /// </summary>
        public bool IsCollapsed
        {
            get => _isCollapsed;
            set
            {
                _isCollapsed = value;
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(IsExpanded));
            }
        }
        
        /// <summary>
        /// Inverse of IsCollapsed for binding.
        /// </summary>
        public bool IsExpanded => !_isCollapsed;
        
        /// <summary>
        /// Toggles the collapsed state.
        /// </summary>
        public void ToggleCollapse()
        {
            IsCollapsed = !IsCollapsed;
        }
        
        /// <summary>
        /// Formats a row count with K/M/B suffixes for compact display.
        /// </summary>
        private static string FormatRowCount(long rows)
        {
            if (rows >= 1_000_000_000)
                return $"{rows / 1_000_000_000.0:0.#}B";
            if (rows >= 1_000_000)
                return $"{rows / 1_000_000.0:0.#}M";
            if (rows >= 1_000)
                return $"{rows / 1_000.0:0.#}K";
            return rows.ToString();
        }
        
        /// <summary>
        /// Formats duration in ms with appropriate suffix.
        /// </summary>
        private static string FormatDuration(long ms)
        {
            if (ms >= 60000)
                return $"{ms / 60000.0:0.#}m";
            if (ms >= 1000)
                return $"{ms / 1000.0:0.#}s";
            return $"{ms}ms";
        }

        public BindableCollection<ErdColumnViewModel> Columns { get; }

        #region Search Highlighting
        
        private bool _isSearchMatch = true;
        /// <summary>
        /// Whether this table matches the current search query.
        /// </summary>
        public bool IsSearchMatch
        {
            get => _isSearchMatch;
            set { _isSearchMatch = value; NotifyOfPropertyChange(); }
        }
        
        private bool _isSearchDimmed;
        /// <summary>
        /// Whether this table should be dimmed (doesn't match search).
        /// </summary>
        public bool IsSearchDimmed
        {
            get => _isSearchDimmed;
            set { _isSearchDimmed = value; NotifyOfPropertyChange(); }
        }
        
        #endregion

        #region Heat Map

        private double _heatLevel;
        /// <summary>
        /// Heat level from 0.0 (cold) to 1.0 (hot) based on relative hit count.
        /// </summary>
        public double HeatLevel
        {
            get => _heatLevel;
            set 
            { 
                _heatLevel = value; 
                NotifyOfPropertyChange(); 
                NotifyOfPropertyChange(nameof(HeatColor));
            }
        }

        /// <summary>
        /// Gets the heat color brush based on heat level.
        /// Green (cool) to Red (hot) gradient.
        /// </summary>
        public System.Windows.Media.SolidColorBrush HeatColor
        {
            get
            {
                // Convert heat level to hue: 120 (green) to 0 (red)
                double hue = (1 - _heatLevel) * 120;
                var color = HslToRgb(hue, 0.7, 0.5);
                return new System.Windows.Media.SolidColorBrush(color);
            }
        }

        /// <summary>
        /// Converts HSL color values to RGB Color.
        /// </summary>
        private static System.Windows.Media.Color HslToRgb(double h, double s, double l)
        {
            double r, g, b;

            if (s == 0)
            {
                r = g = b = l;
            }
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = HueToRgb(p, q, h / 360.0 + 1.0 / 3.0);
                g = HueToRgb(p, q, h / 360.0);
                b = HueToRgb(p, q, h / 360.0 - 1.0 / 3.0);
            }

            return System.Windows.Media.Color.FromRgb(
                (byte)(r * 255),
                (byte)(g * 255),
                (byte)(b * 255));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }

        #endregion

        #region Selection and Highlighting

        private bool _isSelected;
        /// <summary>
        /// Whether this table is currently selected (clicked).
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; NotifyOfPropertyChange(); }
        }

        private bool _isHighlighted;
        /// <summary>
        /// Whether this table is highlighted (related to selected table).
        /// </summary>
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set { _isHighlighted = value; NotifyOfPropertyChange(); }
        }

        private bool _isDimmed;
        /// <summary>
        /// Whether this table is dimmed (not related to selected table).
        /// </summary>
        public bool IsDimmed
        {
            get => _isDimmed;
            set { _isDimmed = value; NotifyOfPropertyChange(); }
        }

        #endregion

        #region Query Plan Integration

        private bool _isQueryFiltered;
        /// <summary>
        /// Whether this table is filtered out (not accessed by selected query).
        /// </summary>
        public bool IsQueryFiltered
        {
            get => _isQueryFiltered;
            set { _isQueryFiltered = value; NotifyOfPropertyChange(); }
        }

        private bool _isQueryHighlighted;
        /// <summary>
        /// Whether this table is highlighted because it was accessed by the selected query.
        /// </summary>
        public bool IsQueryHighlighted
        {
            get => _isQueryHighlighted;
            set { _isQueryHighlighted = value; NotifyOfPropertyChange(); }
        }

        /// <summary>
        /// Gets the underlying table info (for accessing QueryIds).
        /// </summary>
        public XmSqlTableInfo TableInfo => _tableInfo;

        /// <summary>
        /// Formatted list of query IDs that accessed this table.
        /// </summary>
        public string QueryIdsFormatted
        {
            get
            {
                if (_tableInfo.QueryIds.Count == 0)
                    return "";
                if (_tableInfo.QueryIds.Count <= 5)
                    return string.Join(", ", _tableInfo.QueryIds.OrderBy(x => x).Select(x => $"#{x}"));
                return $"#{_tableInfo.QueryIds.Min()}...#{_tableInfo.QueryIds.Max()} ({_tableInfo.QueryIds.Count} queries)";
            }
        }

        /// <summary>
        /// Whether this table has query ID data to display.
        /// </summary>
        public bool HasQueryIds => _tableInfo.QueryIds.Count > 0;

        #endregion

        #region Dragging

        private bool _isDragging;
        /// <summary>
        /// Whether the table is currently being dragged.
        /// </summary>
        public bool IsDragging
        {
            get => _isDragging;
            set { _isDragging = value; NotifyOfPropertyChange(); }
        }

        #endregion

        #region Position on canvas

        private double _x;
        public double X
        {
            get => _x;
            set { _x = value; NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(CenterX)); }
        }

        private double _y;
        public double Y
        {
            get => _y;
            set { _y = value; NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(CenterY)); }
        }

        private double _width = 180;
        public double Width
        {
            get => _width;
            set { _width = value; NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(CenterX)); }
        }

        private double _height = 150;
        public double Height
        {
            get => _height;
            set { _height = value; NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(CenterY)); }
        }

        private double _columnsHeight = 120;
        /// <summary>
        /// Height of the columns area (resizable by user).
        /// </summary>
        public double ColumnsHeight
        {
            get => _columnsHeight;
            set 
            { 
                // Clamp between min and max
                _columnsHeight = Math.Max(40, Math.Min(400, value)); 
                NotifyOfPropertyChange(); 
            }
        }

        // Center point for drawing relationship lines
        public double CenterX => X + Width / 2;
        public double CenterY => Y + Height / 2;

        // Right edge for outgoing relationships
        public double RightEdgeX => X + Width;
        public double RightEdgeY => Y + Height / 2;

        // Left edge for incoming relationships
        public double LeftEdgeX => X;
        public double LeftEdgeY => Y + Height / 2;

        #endregion

        #region Mini-map Properties

        // Mini-map scale factor (set by parent ViewModel)
        private double _miniMapScaleX = 0.1;
        private double _miniMapScaleY = 0.1;

        /// <summary>
        /// Updates the mini-map scale factors.
        /// </summary>
        public void SetMiniMapScale(double scaleX, double scaleY)
        {
            _miniMapScaleX = scaleX;
            _miniMapScaleY = scaleY;
            NotifyOfPropertyChange(nameof(MiniMapX));
            NotifyOfPropertyChange(nameof(MiniMapY));
            NotifyOfPropertyChange(nameof(MiniMapWidth));
            NotifyOfPropertyChange(nameof(MiniMapHeight));
        }

        /// <summary>
        /// X position on mini-map.
        /// </summary>
        public double MiniMapX => X * _miniMapScaleX;

        /// <summary>
        /// Y position on mini-map.
        /// </summary>
        public double MiniMapY => Y * _miniMapScaleY;

        /// <summary>
        /// Width on mini-map.
        /// </summary>
        public double MiniMapWidth => Math.Max(4, Width * _miniMapScaleX);

        /// <summary>
        /// Height on mini-map.
        /// </summary>
        public double MiniMapHeight => Math.Max(3, Height * _miniMapScaleY);

        #endregion
    }

    /// <summary>
    /// ViewModel for a column in the ERD diagram.
    /// </summary>
    public class ErdColumnViewModel : PropertyChangedBase
    {
        private readonly XmSqlColumnInfo _columnInfo;

        public ErdColumnViewModel(XmSqlColumnInfo columnInfo)
        {
            _columnInfo = columnInfo;
        }

        public string ColumnName => _columnInfo.ColumnName;
        public int HitCount => _columnInfo.HitCount;

        public bool IsJoinColumn => _columnInfo.UsageTypes.HasFlag(XmSqlColumnUsage.Join);
        public bool IsFilterColumn => _columnInfo.UsageTypes.HasFlag(XmSqlColumnUsage.Filter);
        public bool IsSelectColumn => _columnInfo.UsageTypes.HasFlag(XmSqlColumnUsage.Select);
        public bool IsAggregateColumn => _columnInfo.UsageTypes.HasFlag(XmSqlColumnUsage.Aggregate);

        /// <summary>
        /// Whether this column has a DAX callback.
        /// </summary>
        public bool HasCallback => _columnInfo.HasCallback;

        /// <summary>
        /// The type of callback (CallbackDataID, EncodeCallback, etc.)
        /// </summary>
        public string CallbackType => _columnInfo.CallbackType;

        public string AggregationText => _columnInfo.AggregationTypes.Count > 0
            ? string.Join(", ", _columnInfo.AggregationTypes)
            : string.Empty;

        /// <summary>
        /// Filter values applied to this column.
        /// </summary>
        public IReadOnlyList<string> FilterValues => _columnInfo.FilterValues;

        /// <summary>
        /// Whether this column has filter values.
        /// </summary>
        public bool HasFilterValues => _columnInfo.FilterValues.Count > 0;

        /// <summary>
        /// Filter operators used on this column.
        /// </summary>
        public IEnumerable<string> FilterOperators => _columnInfo.FilterOperators;

        /// <summary>
        /// Formatted filter values for display (first few values).
        /// </summary>
        public string FilterValuesPreview
        {
            get
            {
                if (!HasFilterValues) return string.Empty;
                var values = _columnInfo.FilterValues.Take(3).ToList();
                var preview = string.Join(", ", values);
                if (_columnInfo.FilterValues.Count > 3)
                    preview += $", ... (+{_columnInfo.FilterValues.Count - 3} more)";
                return preview;
            }
        }

        /// <summary>
        /// All filter values formatted for detail panel display.
        /// </summary>
        public string FilterValuesFormatted
        {
            get
            {
                if (!HasFilterValues) return string.Empty;
                var ops = _columnInfo.FilterOperators.Any() 
                    ? string.Join("/", _columnInfo.FilterOperators) 
                    : "=";
                return $"[{ops}] {string.Join(", ", _columnInfo.FilterValues)}";
            }
        }

        /// <summary>
        /// Tooltip text explaining the column usage icons.
        /// </summary>
        public string UsageTooltip
        {
            get
            {
                var tips = new List<string>();
                if (IsJoinColumn) tips.Add("🔑 Join Key");
                if (IsFilterColumn) tips.Add("🔍 Filter");
                if (IsAggregateColumn) tips.Add("📊 Aggregate (" + AggregationText + ")");
                if (IsSelectColumn) tips.Add("✓ Selected");
                if (HasCallback) tips.Add("⚡ Callback (" + CallbackType + ")");
                return tips.Count > 0 ? string.Join("\n", tips) : "No usage detected";
            }
        }
        
        private bool _isSearchMatch = true;
        /// <summary>
        /// Whether this column matches the current search query.
        /// </summary>
        public bool IsSearchMatch
        {
            get => _isSearchMatch;
            set { _isSearchMatch = value; NotifyOfPropertyChange(); }
        }
    }

    /// <summary>
    /// ViewModel for a relationship line in the ERD diagram.
    /// </summary>
    public class ErdRelationshipViewModel : PropertyChangedBase
    {
        private readonly XmSqlRelationship _relationship;
        private readonly ErdTableViewModel _fromTable;
        private readonly ErdTableViewModel _toTable;

        public ErdRelationshipViewModel(XmSqlRelationship relationship, ErdTableViewModel fromTable, ErdTableViewModel toTable)
        {
            _relationship = relationship;
            _fromTable = fromTable;
            _toTable = toTable;
        }

        public ErdTableViewModel FromTableViewModel => _fromTable;
        public ErdTableViewModel ToTableViewModel => _toTable;

        public string FromTable => _relationship.FromTable;
        public string FromColumn => _relationship.FromColumn;
        public string ToTable => _relationship.ToTable;
        public string ToColumn => _relationship.ToColumn;
        public XmSqlJoinType JoinType => _relationship.JoinType;
        public int HitCount => _relationship.HitCount;
        public XmSqlCardinality Cardinality => _relationship.Cardinality;
        public XmSqlCrossFilterDirection CrossFilterDirection => _relationship.CrossFilterDirection;

        /// <summary>
        /// Whether this relationship is many-to-many.
        /// </summary>
        public bool IsManyToMany => Cardinality == XmSqlCardinality.ManyToMany;

        /// <summary>
        /// Whether this relationship has bi-directional cross-filtering.
        /// </summary>
        public bool IsBidirectional => CrossFilterDirection == XmSqlCrossFilterDirection.Both;

        /// <summary>
        /// Text representation of cardinality for display.
        /// </summary>
        public string CardinalityText => Cardinality switch
        {
            XmSqlCardinality.OneToOne => "1:1",
            XmSqlCardinality.OneToMany => "1:*",
            XmSqlCardinality.ManyToOne => "*:1",
            XmSqlCardinality.ManyToMany => "*:*",
            _ => ""
        };

        /// <summary>
        /// Symbol for "one" side of relationship.
        /// </summary>
        public string FromCardinalitySymbol => Cardinality switch
        {
            XmSqlCardinality.OneToOne => "1",
            XmSqlCardinality.OneToMany => "1",
            XmSqlCardinality.ManyToOne => "*",
            XmSqlCardinality.ManyToMany => "*",
            _ => ""
        };

        /// <summary>
        /// Symbol for "many" side of relationship.
        /// </summary>
        public string ToCardinalitySymbol => Cardinality switch
        {
            XmSqlCardinality.OneToOne => "1",
            XmSqlCardinality.OneToMany => "*",
            XmSqlCardinality.ManyToOne => "1",
            XmSqlCardinality.ManyToMany => "*",
            _ => ""
        };

        public string JoinTypeText => JoinType switch
        {
            XmSqlJoinType.LeftOuterJoin => "LEFT OUTER",
            XmSqlJoinType.InnerJoin => "INNER",
            XmSqlJoinType.RightOuterJoin => "RIGHT OUTER",
            XmSqlJoinType.FullOuterJoin => "FULL OUTER",
            _ => ""
        };

        #region Highlighting

        private bool _isHighlighted;
        /// <summary>
        /// Whether this relationship is highlighted (connected to selected table).
        /// </summary>
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set { _isHighlighted = value; NotifyOfPropertyChange(); }
        }

        private bool _isDimmed;
        /// <summary>
        /// Whether this relationship is dimmed (not connected to selected table).
        /// </summary>
        public bool IsDimmed
        {
            get => _isDimmed;
            set { _isDimmed = value; NotifyOfPropertyChange(); }
        }

        private bool _isQueryFiltered;
        /// <summary>
        /// Whether this relationship is filtered out by query filter.
        /// </summary>
        public bool IsQueryFiltered
        {
            get => _isQueryFiltered;
            set { _isQueryFiltered = value; NotifyOfPropertyChange(); }
        }

        #endregion

        // Line path coordinates
        private double _startX;
        public double StartX
        {
            get => _startX;
            set 
            { 
                _startX = value; 
                NotifyOfPropertyChange(); 
                NotifyOfPropertyChange(nameof(PathData)); 
                NotifyOfPropertyChange(nameof(LabelX));
            }
        }

        private double _startY;
        public double StartY
        {
            get => _startY;
            set 
            { 
                _startY = value; 
                NotifyOfPropertyChange(); 
                NotifyOfPropertyChange(nameof(PathData)); 
                NotifyOfPropertyChange(nameof(LabelY));
            }
        }

        private double _endX;
        public double EndX
        {
            get => _endX;
            set 
            { 
                _endX = value; 
                NotifyOfPropertyChange(); 
                NotifyOfPropertyChange(nameof(PathData)); 
                NotifyOfPropertyChange(nameof(LabelX));
            }
        }

        private double _endY;
        public double EndY
        {
            get => _endY;
            set 
            { 
                _endY = value; 
                NotifyOfPropertyChange(); 
                NotifyOfPropertyChange(nameof(PathData)); 
                NotifyOfPropertyChange(nameof(LabelY));
            }
        }

        /// <summary>
        /// Path data for drawing the relationship line (bezier curve).
        /// </summary>
        public string PathData
        {
            get
            {
                // Calculate control points for a smooth bezier curve
                // Use InvariantCulture to ensure decimal points (not commas) are used
                double midX = (StartX + EndX) / 2;
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "M {0},{1} C {2},{3} {4},{5} {6},{7}",
                    StartX, StartY, midX, StartY, midX, EndY, EndX, EndY);
            }
        }

        /// <summary>
        /// Label position (middle of the line).
        /// </summary>
        public double LabelX => (StartX + EndX) / 2;
        public double LabelY => (StartY + EndY) / 2 - 10;

        /// <summary>
        /// Updates the path based on table positions.
        /// </summary>
        public void UpdatePath()
        {
            // Determine which edges to connect based on relative positions
            if (_fromTable.CenterX < _toTable.CenterX)
            {
                // From table is to the left, connect right edge to left edge
                StartX = _fromTable.RightEdgeX;
                StartY = _fromTable.RightEdgeY;
                EndX = _toTable.LeftEdgeX;
                EndY = _toTable.LeftEdgeY;
            }
            else
            {
                // From table is to the right, connect left edge to right edge
                StartX = _fromTable.LeftEdgeX;
                StartY = _fromTable.LeftEdgeY;
                EndX = _toTable.RightEdgeX;
                EndY = _toTable.RightEdgeY;
            }
        }
    }
}
