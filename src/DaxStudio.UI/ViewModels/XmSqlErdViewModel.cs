using Caliburn.Micro;
using DaxStudio.UI.Interfaces;
using DaxStudio.UI.Model;
using DaxStudio.UI.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;

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

        #endregion

        #region Methods

        /// <summary>
        /// Analyzes a collection of SE events and builds the ERD model.
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
                    if (evt.IsScanEvent && !string.IsNullOrWhiteSpace(evt.Query))
                    {
                        _parser.ParseQuery(evt.Query, _analysis);
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

            foreach (var rel in _analysis.Relationships)
            {
                var fromTable = Tables.FirstOrDefault(t => t.TableName.Equals(rel.FromTable, StringComparison.OrdinalIgnoreCase));
                var toTable = Tables.FirstOrDefault(t => t.TableName.Equals(rel.ToTable, StringComparison.OrdinalIgnoreCase));

                if (fromTable != null && toTable != null)
                {
                    var relVm = new ErdRelationshipViewModel(rel, fromTable, toTable);
                    Relationships.Add(relVm);
                }
            }
        }

        /// <summary>
        /// Lays out the tables on the canvas using a simple algorithm.
        /// </summary>
        private void LayoutDiagram()
        {
            if (Tables.Count == 0) return;

            const double tableWidth = 180;
            const double tableHeight = 150;
            const double horizontalSpacing = 60;
            const double verticalSpacing = 40;
            const double padding = 20;

            // Simple grid layout - calculate how many columns we can fit
            int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Tables.Count)));
            int rows = (int)Math.Ceiling((double)Tables.Count / columns);

            // Position tables in a grid
            for (int i = 0; i < Tables.Count; i++)
            {
                int col = i % columns;
                int row = i / columns;

                Tables[i].X = padding + col * (tableWidth + horizontalSpacing);
                Tables[i].Y = padding + row * (tableHeight + verticalSpacing);
                Tables[i].Width = tableWidth;
                Tables[i].Height = tableHeight;
            }

            // Calculate canvas size to fit all tables
            CanvasWidth = padding * 2 + columns * (tableWidth + horizontalSpacing);
            CanvasHeight = padding * 2 + rows * (tableHeight + verticalSpacing);

            // Update relationship line positions
            foreach (var rel in Relationships)
            {
                rel.UpdatePath();
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
                    .OrderByDescending(c => c.UsageTypes.HasFlag(XmSqlColumnUsage.Join))
                    .ThenByDescending(c => c.HitCount)
                    .Select(c => new ErdColumnViewModel(c)));
        }

        public string TableName => _tableInfo.TableName;
        public int HitCount => _tableInfo.HitCount;
        public bool IsFromTable => _tableInfo.IsFromTable;
        public bool IsJoinedTable => _tableInfo.IsJoinedTable;

        public BindableCollection<ErdColumnViewModel> Columns { get; }

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

        public string AggregationText => _columnInfo.AggregationTypes.Count > 0
            ? string.Join(", ", _columnInfo.AggregationTypes)
            : string.Empty;

        /// <summary>
        /// Icon/symbol to display based on column usage.
        /// </summary>
        public string UsageIcon
        {
            get
            {
                if (IsJoinColumn) return "🔑";
                if (IsAggregateColumn) return "📊";
                if (IsFilterColumn) return "🔍";
                if (IsSelectColumn) return "✓";
                return "";
            }
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
                double midX = (StartX + EndX) / 2;
                return $"M {StartX},{StartY} C {midX},{StartY} {midX},{EndY} {EndX},{EndY}";
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
