using ADOTabular;
using Caliburn.Micro;
using DaxStudio.UI.Events;
using DaxStudio.UI.Interfaces;
using DaxStudio.UI.Model;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Media;

namespace DaxStudio.UI.ViewModels
{
    /// <summary>
    /// ViewModel for the Model Diagram visualization.
    /// This displays tables, columns, and relationships from the connected model's metadata.
    /// This is a dockable tool window that can be resized, floated, or maximized.
    /// </summary>
    [PartCreationPolicy(CreationPolicy.NonShared)]
    [Export]
    public class ModelDiagramViewModel : ToolWindowBase, IHandle<MetadataLoadedEvent>
    {
        private readonly IEventAggregator _eventAggregator;
        private ADOTabularModel _model;

        [ImportingConstructor]
        public ModelDiagramViewModel(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator;
            _eventAggregator.SubscribeOnPublishedThread(this);
        }

        #region ToolWindowBase Implementation

        public override string Title => "Model Diagram";
        public override string DefaultDockingPane => "DockBottom";
        public override string ContentId => "model-diagram";
        public override bool CanHide => true;

        /// <summary>
        /// Icon for the tool window (used by AvalonDock).
        /// </summary>
        public ImageSource IconSource => null;

        #endregion

        #region Properties

        /// <summary>
        /// Collection of table view models for display.
        /// </summary>
        public BindableCollection<ModelDiagramTableViewModel> Tables { get; } = new BindableCollection<ModelDiagramTableViewModel>();

        /// <summary>
        /// Collection of relationship view models for display.
        /// </summary>
        public BindableCollection<ModelDiagramRelationshipViewModel> Relationships { get; } = new BindableCollection<ModelDiagramRelationshipViewModel>();

        /// <summary>
        /// Summary text showing counts.
        /// </summary>
        public string SummaryText => _model != null
            ? $"{Tables.Count} Tables, {Tables.Sum(t => t.ColumnCount)} Columns, {Relationships.Count} Relationships"
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
                    table.IsSearchMatch = true;
                    foreach (var col in table.Columns)
                    {
                        col.IsSearchMatch = true;
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

        private bool _showDataTypes = false;
        /// <summary>
        /// Whether to show data types next to columns.
        /// </summary>
        public bool ShowDataTypes
        {
            get => _showDataTypes;
            set
            {
                _showDataTypes = value;
                NotifyOfPropertyChange();
                foreach (var table in Tables)
                {
                    foreach (var col in table.Columns)
                    {
                        col.ShowDataType = value;
                    }
                }
            }
        }

        private bool _showHiddenObjects = false;
        /// <summary>
        /// Whether to show hidden tables and columns.
        /// </summary>
        public bool ShowHiddenObjects
        {
            get => _showHiddenObjects;
            set
            {
                _showHiddenObjects = value;
                NotifyOfPropertyChange();
                if (_model != null)
                {
                    LoadFromModel(_model);
                }
            }
        }

        private ModelDiagramTableViewModel _selectedTable;
        /// <summary>
        /// The currently selected table.
        /// </summary>
        public ModelDiagramTableViewModel SelectedTable
        {
            get => _selectedTable;
            set
            {
                if (_selectedTable != null)
                {
                    _selectedTable.IsSelected = false;
                }
                _selectedTable = value;
                if (_selectedTable != null)
                {
                    _selectedTable.IsSelected = true;
                }
                NotifyOfPropertyChange();
                UpdateRelationshipHighlighting();
            }
        }

        /// <summary>
        /// Updates relationship highlighting based on selected table.
        /// </summary>
        private void UpdateRelationshipHighlighting()
        {
            foreach (var rel in Relationships)
            {
                if (_selectedTable == null)
                {
                    rel.IsHighlighted = false;
                    rel.IsDimmed = false;
                }
                else
                {
                    var isConnected = rel.FromTable == _selectedTable.TableName || rel.ToTable == _selectedTable.TableName;
                    rel.IsHighlighted = isConnected;
                    rel.IsDimmed = !isConnected;
                }
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Loads the diagram from an ADOTabularModel.
        /// </summary>
        public void LoadFromModel(ADOTabularModel model)
        {
            if (model == null) return;

            IsLoading = true;

            try
            {
                _model = model;
                Tables.Clear();
                Relationships.Clear();

                // Create view models for tables
                foreach (var table in model.Tables)
                {
                    if (!ShowHiddenObjects && !table.IsVisible) continue;

                    var tableVm = new ModelDiagramTableViewModel(table, ShowHiddenObjects);
                    Tables.Add(tableVm);
                }

                // Create view models for relationships
                // Relationships are stored on individual tables, so we need to gather from all tables
                var processedRelationships = new HashSet<string>();
                foreach (var table in model.Tables)
                {
                    foreach (var rel in table.Relationships)
                    {
                        // Create a unique key to avoid duplicates (relationships appear on both ends)
                        var relKey = $"{rel.FromTable?.Name}|{rel.FromColumn}|{rel.ToTable?.Name}|{rel.ToColumn}";
                        if (processedRelationships.Contains(relKey)) continue;
                        processedRelationships.Add(relKey);

                        var fromTableVm = Tables.FirstOrDefault(t => t.TableName == rel.FromTable?.Name);
                        var toTableVm = Tables.FirstOrDefault(t => t.TableName == rel.ToTable?.Name);

                        if (fromTableVm != null && toTableVm != null)
                        {
                            var relVm = new ModelDiagramRelationshipViewModel(rel, fromTableVm, toTableVm);
                            Relationships.Add(relVm);
                        }
                    }
                }

                // Layout the diagram
                LayoutDiagram();

                NotifyOfPropertyChange(nameof(SummaryText));
                NotifyOfPropertyChange(nameof(HasData));
                NotifyOfPropertyChange(nameof(NoData));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{class} {method} {message}", nameof(ModelDiagramViewModel), nameof(LoadFromModel), ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Layouts the diagram using a simple grid-based algorithm.
        /// Tables connected by relationships are placed closer together.
        /// </summary>
        private void LayoutDiagram()
        {
            if (Tables.Count == 0) return;

            const double tableWidth = 200;
            const double tableHeight = 180;
            const double horizontalSpacing = 100;
            const double verticalSpacing = 60;
            const double padding = 150;  // Extra padding to allow room for dragging tables left

            // Build relationship map to understand connectivity
            var relationshipMap = BuildRelationshipMap();

            // Find fact tables (tables with most relationships) vs dimension tables
            var tablesByConnections = Tables
                .OrderByDescending(t => relationshipMap.TryGetValue(t.TableName, out var connections) ? connections.Count : 0)
                .ThenBy(t => t.TableName)
                .ToList();

            // Choose layout based on table count
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
            else if (Tables.Count <= 4)
            {
                // 3-4 tables - use grid layout
                LayoutGrid(tablesByConnections, relationshipMap, tableWidth, tableHeight, horizontalSpacing, verticalSpacing, padding);
            }
            else
            {
                // 5+ tables - use star layout with most connected table in center
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

            foreach (var rel in Relationships)
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
        private void LayoutHorizontal(List<ModelDiagramTableViewModel> tables, double tableWidth, double tableHeight, double spacing, double padding)
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
        /// Layout 3-4 tables in a grid pattern based on relationships.
        /// </summary>
        private void LayoutGrid(List<ModelDiagramTableViewModel> tables, Dictionary<string, HashSet<string>> relationshipMap,
            double tableWidth, double tableHeight, double hSpacing, double vSpacing, double padding)
        {
            if (tables.Count == 3)
            {
                // Triangle layout
                var factTable = tables[0];
                var dim1 = tables[1];
                var dim2 = tables[2];

                double totalWidth = 2 * tableWidth + hSpacing;
                double startX = padding;

                // Top row - dimensions
                dim1.X = startX;
                dim1.Y = padding;
                dim1.Width = tableWidth;
                dim1.Height = tableHeight;

                dim2.X = startX + tableWidth + hSpacing;
                dim2.Y = padding;
                dim2.Width = tableWidth;
                dim2.Height = tableHeight;

                // Bottom row - fact table centered
                factTable.X = startX + (totalWidth - tableWidth) / 2;
                factTable.Y = padding + tableHeight + vSpacing;
                factTable.Width = tableWidth;
                factTable.Height = tableHeight;
            }
            else if (tables.Count == 4)
            {
                // 2x2 grid layout
                double startX = padding;
                double startY = padding;

                tables[0].X = startX;
                tables[0].Y = startY;
                tables[0].Width = tableWidth;
                tables[0].Height = tableHeight;

                tables[1].X = startX + tableWidth + hSpacing;
                tables[1].Y = startY;
                tables[1].Width = tableWidth;
                tables[1].Height = tableHeight;

                tables[2].X = startX;
                tables[2].Y = startY + tableHeight + vSpacing;
                tables[2].Width = tableWidth;
                tables[2].Height = tableHeight;

                tables[3].X = startX + tableWidth + hSpacing;
                tables[3].Y = startY + tableHeight + vSpacing;
                tables[3].Width = tableWidth;
                tables[3].Height = tableHeight;
            }
        }

        /// <summary>
        /// Layout with most connected table in center, others around it.
        /// </summary>
        private void LayoutStar(List<ModelDiagramTableViewModel> tables, Dictionary<string, HashSet<string>> relationshipMap,
            double tableWidth, double tableHeight, double hSpacing, double vSpacing, double padding)
        {
            // Calculate grid dimensions for a balanced layout
            int cols = (int)Math.Ceiling(Math.Sqrt(tables.Count));
            int rows = (int)Math.Ceiling((double)tables.Count / cols);

            double x = padding;
            double y = padding;
            int col = 0;

            foreach (var table in tables)
            {
                table.X = x;
                table.Y = y;
                table.Width = tableWidth;
                table.Height = tableHeight;

                col++;
                if (col >= cols)
                {
                    col = 0;
                    x = padding;
                    y += tableHeight + vSpacing;
                }
                else
                {
                    x += tableWidth + hSpacing;
                }
            }
        }

        /// <summary>
        /// Re-layouts the diagram (can be called after table positions are changed).
        /// </summary>
        public void RefreshLayout()
        {
            // Update relationship line positions
            foreach (var rel in Relationships)
            {
                rel.UpdatePath();
            }

            // Recalculate canvas size
            if (Tables.Count > 0)
            {
                var maxX = Tables.Max(t => t.X + t.Width);
                var maxY = Tables.Max(t => t.Y + t.Height);
                CanvasWidth = Math.Max(100, maxX + 40);
                CanvasHeight = Math.Max(100, maxY + 40);
            }
        }

        /// <summary>
        /// Zooms to fit all tables in the view.
        /// </summary>
        public void ZoomToFit()
        {
            if (Tables.Count == 0) return;

            var contentWidth = Tables.Max(t => t.X + t.Width) + 40;
            var contentHeight = Tables.Max(t => t.Y + t.Height) + 40;

            var scaleX = ViewWidth / contentWidth;
            var scaleY = ViewHeight / contentHeight;
            var newScale = Math.Min(scaleX, scaleY) * 0.95; // 95% to add some margin

            Scale = Math.Max(0.1, Math.Min(2.0, newScale));
        }

        /// <summary>
        /// Resets zoom to 100%.
        /// </summary>
        public void ResetZoom()
        {
            Scale = 1.0;
        }

        #endregion

        #region Event Handlers

        public System.Threading.Tasks.Task HandleAsync(MetadataLoadedEvent message, System.Threading.CancellationToken cancellationToken)
        {
            if (message?.Model != null && IsVisible)
            {
                LoadFromModel(message.Model);
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        #endregion

        #region Export

        /// <summary>
        /// Event raised when export is requested. The view subscribes to render the canvas to an image.
        /// </summary>
        public event EventHandler<string> ExportRequested;

        /// <summary>
        /// Exports the diagram to a PNG file.
        /// </summary>
        public void ExportToImage()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG Image|*.png",
                Title = "Export Model Diagram",
                FileName = $"ModelDiagram_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            };

            if (dialog.ShowDialog() == true)
            {
                ExportRequested?.Invoke(this, dialog.FileName);
            }
        }

        #endregion
    }

    /// <summary>
    /// ViewModel for a table in the Model Diagram.
    /// </summary>
    public class ModelDiagramTableViewModel : PropertyChangedBase
    {
        private readonly ADOTabularTable _table;

        public ModelDiagramTableViewModel(ADOTabularTable table, bool showHiddenObjects)
        {
            _table = table;
            Columns = new BindableCollection<ModelDiagramColumnViewModel>(
                table.Columns
                    .Where(c => c.Contents != "RowNumber" && (showHiddenObjects || c.IsVisible))
                    .OrderBy(c => c.ObjectType == ADOTabularObjectType.Column ? 0 : 1) // Columns first, then measures
                    .ThenBy(c => c.Caption)
                    .Select(c => new ModelDiagramColumnViewModel(c)));
        }

        public string TableName => _table.Name;
        public string Caption => _table.Caption;
        public string Description => _table.Description;
        public bool IsVisible => _table.IsVisible;
        public bool IsDateTable => _table.IsDateTable;
        public int ColumnCount => Columns.Count(c => c.ObjectType == ADOTabularObjectType.Column);
        public int MeasureCount => Columns.Count(c => c.ObjectType == ADOTabularObjectType.Measure);

        public BindableCollection<ModelDiagramColumnViewModel> Columns { get; }

        /// <summary>
        /// Tooltip with table details matching metadata pane style.
        /// </summary>
        public string Tooltip
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(Caption);
                
                if (Caption != TableName)
                    sb.AppendLine($"Name: {TableName}");
                
                sb.AppendLine($"Columns: {ColumnCount}");
                sb.AppendLine($"Measures: {MeasureCount}");
                
                if (IsDateTable)
                    sb.AppendLine("📅 Date Table");
                    
                if (!IsVisible)
                    sb.AppendLine("👁 Hidden");
                    
                if (!string.IsNullOrEmpty(Description))
                {
                    sb.AppendLine();
                    sb.Append(Description);
                }
                
                return sb.ToString().TrimEnd();
            }
        }

        /// <summary>
        /// Header background color based on table type.
        /// </summary>
        /// </summary>
        public string HeaderColor
        {
            get
            {
                if (IsDateTable) return "#4CAF50"; // Green for date tables
                if (!IsVisible) return "#9E9E9E"; // Gray for hidden tables
                if (MeasureCount > 0 && ColumnCount == 0) return "#9C27B0"; // Purple for measure tables
                return "#2196F3"; // Blue default
            }
        }

        #region Position Properties

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

        private double _width = 200;
        public double Width
        {
            get => _width;
            set 
            { 
                _width = value; 
                NotifyOfPropertyChange(); 
                NotifyOfPropertyChange(nameof(CenterX)); 
                NotifyOfPropertyChange(nameof(RightEdgeX)); 
            }
        }

        private double _height = 180;
        public double Height
        {
            get => _height;
            set 
            { 
                _height = value; 
                NotifyOfPropertyChange(); 
                NotifyOfPropertyChange(nameof(CenterY)); 
                NotifyOfPropertyChange(nameof(RightEdgeY));
                NotifyOfPropertyChange(nameof(LeftEdgeY));
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

        #region State Properties

        private bool _isCollapsed;
        public bool IsCollapsed
        {
            get => _isCollapsed;
            set { _isCollapsed = value; NotifyOfPropertyChange(); }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; NotifyOfPropertyChange(); }
        }

        private bool _isSearchMatch = true;
        public bool IsSearchMatch
        {
            get => _isSearchMatch;
            set { _isSearchMatch = value; NotifyOfPropertyChange(); }
        }

        #endregion
    }

    /// <summary>
    /// ViewModel for a column in the Model Diagram.
    /// </summary>
    public class ModelDiagramColumnViewModel : PropertyChangedBase
    {
        private readonly ADOTabularColumn _column;

        public ModelDiagramColumnViewModel(ADOTabularColumn column)
        {
            _column = column;
        }

        public string ColumnName => _column.Name;
        public string Caption => _column.Caption;
        public string Description => _column.Description;
        public bool IsVisible => _column.IsVisible;
        public ADOTabularObjectType ObjectType => _column.ObjectType;
        public string DataTypeName => _column.DataTypeName;
        public bool IsKey => _column.IsKey;

        /// <summary>
        /// Whether this is a measure (vs column/hierarchy).
        /// </summary>
        public bool IsMeasure => ObjectType == ADOTabularObjectType.Measure 
                              || ObjectType == ADOTabularObjectType.KPI
                              || ObjectType == ADOTabularObjectType.KPIGoal
                              || ObjectType == ADOTabularObjectType.KPIStatus;

        /// <summary>
        /// Whether this is a hierarchy.
        /// </summary>
        public bool IsHierarchy => ObjectType == ADOTabularObjectType.Hierarchy 
                                || ObjectType == ADOTabularObjectType.UnnaturalHierarchy;

        /// <summary>
        /// Icon resource key based on column type and data type.
        /// Uses the same icons as the metadata pane.
        /// </summary>
        public string IconResourceKey
        {
            get
            {
                var suffix = IsVisible ? "DrawingImage" : "HiddenDrawingImage";
                
                // Measures
                if (IsMeasure)
                    return $"measure{suffix}";

                // Hierarchies
                if (IsHierarchy)
                    return "hierarchyDrawingImage";

                // Regular columns - based on data type
                return _column.DataType switch
                {
                    Microsoft.AnalysisServices.Tabular.DataType.Boolean => $"boolean{suffix}",
                    Microsoft.AnalysisServices.Tabular.DataType.DateTime => $"datetime{suffix}",
                    Microsoft.AnalysisServices.Tabular.DataType.Double => $"double{suffix}",
                    Microsoft.AnalysisServices.Tabular.DataType.Decimal => $"double{suffix}",
                    Microsoft.AnalysisServices.Tabular.DataType.Int64 => $"number{suffix}",
                    Microsoft.AnalysisServices.Tabular.DataType.String => $"string{suffix}",
                    _ => $"column{suffix}"
                };
            }
        }

        /// <summary>
        /// Short data type indicator for compact display.
        /// </summary>
        public string DataTypeIndicator
        {
            get
            {
                if (IsMeasure) return "fx";
                if (IsHierarchy) return "H";
                
                return _column.DataType switch
                {
                    Microsoft.AnalysisServices.Tabular.DataType.Boolean => "T/F",
                    Microsoft.AnalysisServices.Tabular.DataType.DateTime => "📅",
                    Microsoft.AnalysisServices.Tabular.DataType.Double => "#.#",
                    Microsoft.AnalysisServices.Tabular.DataType.Decimal => "#.#",
                    Microsoft.AnalysisServices.Tabular.DataType.Int64 => "123",
                    Microsoft.AnalysisServices.Tabular.DataType.String => "abc",
                    _ => ""
                };
            }
        }

        /// <summary>
        /// Background color for the data type indicator.
        /// </summary>
        public string DataTypeColor
        {
            get
            {
                if (IsMeasure) return "#FF9800";  // Orange for measures
                if (IsHierarchy) return "#9C27B0";  // Purple for hierarchies
                if (IsKey) return "#F44336";  // Red for keys
                
                return _column.DataType switch
                {
                    Microsoft.AnalysisServices.Tabular.DataType.Boolean => "#4CAF50",  // Green
                    Microsoft.AnalysisServices.Tabular.DataType.DateTime => "#2196F3",  // Blue
                    Microsoft.AnalysisServices.Tabular.DataType.Double => "#9C27B0",  // Purple
                    Microsoft.AnalysisServices.Tabular.DataType.Decimal => "#9C27B0",  // Purple
                    Microsoft.AnalysisServices.Tabular.DataType.Int64 => "#673AB7",  // Deep Purple
                    Microsoft.AnalysisServices.Tabular.DataType.String => "#607D8B",  // Blue Grey
                    _ => "#9E9E9E"  // Grey
                };
            }
        }

        /// <summary>
        /// Tooltip with detailed column information matching metadata pane.
        /// </summary>
        public string Tooltip
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(Caption);
                
                if (Caption != ColumnName)
                    sb.AppendLine($"Name: {ColumnName}");
                
                if (IsKey) 
                    sb.AppendLine("🔑 Key Column");
                    
                if (IsMeasure) 
                    sb.AppendLine("📊 Measure");
                else if (IsHierarchy) 
                    sb.AppendLine("📚 Hierarchy");
                else 
                    sb.AppendLine($"📋 Column");
                
                if (!string.IsNullOrEmpty(DataTypeName))
                    sb.AppendLine($"Data Type: {DataTypeName}");
                
                if (!IsVisible)
                    sb.AppendLine("👁 Hidden");
                    
                if (!string.IsNullOrEmpty(Description))
                {
                    sb.AppendLine();
                    sb.Append(Description);
                }
                    
                return sb.ToString().TrimEnd();
            }
        }

        private bool _showDataType;
        public bool ShowDataType
        {
            get => _showDataType;
            set { _showDataType = value; NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(DisplayText)); }
        }

        public string DisplayText => ShowDataType && !string.IsNullOrEmpty(DataTypeName)
            ? $"{Caption} ({DataTypeName})"
            : Caption;

        private bool _isSearchMatch = true;
        public bool IsSearchMatch
        {
            get => _isSearchMatch;
            set { _isSearchMatch = value; NotifyOfPropertyChange(); }
        }
    }

    /// <summary>
    /// ViewModel for a relationship line in the Model Diagram.
    /// </summary>
    public class ModelDiagramRelationshipViewModel : PropertyChangedBase
    {
        private readonly ADOTabularRelationship _relationship;
        private readonly ModelDiagramTableViewModel _fromTable;
        private readonly ModelDiagramTableViewModel _toTable;

        public ModelDiagramRelationshipViewModel(ADOTabularRelationship relationship, ModelDiagramTableViewModel fromTable, ModelDiagramTableViewModel toTable)
        {
            _relationship = relationship;
            _fromTable = fromTable;
            _toTable = toTable;
        }

        public ModelDiagramTableViewModel FromTableViewModel => _fromTable;
        public ModelDiagramTableViewModel ToTableViewModel => _toTable;

        public string FromTable => _relationship.FromTable?.Name;
        public string FromColumn => _relationship.FromColumn;
        public string ToTable => _relationship.ToTable?.Name;
        public string ToColumn => _relationship.ToColumn;
        public bool IsActive => _relationship.IsActive;

        /// <summary>
        /// Whether this relationship is many-to-many.
        /// </summary>
        public bool IsManyToMany =>
            _relationship.FromColumnMultiplicity == "*" && _relationship.ToColumnMultiplicity == "*";

        /// <summary>
        /// Whether this relationship has bi-directional cross-filtering.
        /// </summary>
        public bool IsBidirectional =>
            string.Equals(_relationship.CrossFilterDirection, "Both", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Text representation of cardinality for display.
        /// </summary>
        public string CardinalityText
        {
            get
            {
                var from = _relationship.FromColumnMultiplicity == "*" ? "*" : "1";
                var to = _relationship.ToColumnMultiplicity == "*" ? "*" : "1";
                return $"{from}:{to}";
            }
        }

        /// <summary>
        /// Tooltip with relationship details.
        /// </summary>
        public string Tooltip => $"{FromTable}[{FromColumn}] → {ToTable}[{ToColumn}]\n" +
                                 $"Cardinality: {CardinalityText}\n" +
                                 $"Cross-filter: {_relationship.CrossFilterDirection}\n" +
                                 $"Active: {(IsActive ? "Yes" : "No")}";

        #region Highlighting

        private bool _isHighlighted;
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set { _isHighlighted = value; NotifyOfPropertyChange(); }
        }

        private bool _isDimmed;
        public bool IsDimmed
        {
            get => _isDimmed;
            set { _isDimmed = value; NotifyOfPropertyChange(); }
        }

        #endregion

        #region Line Path

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

        #endregion
    }
}
