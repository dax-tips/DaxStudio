using ADOTabular;
using Caliburn.Micro;
using DaxStudio.UI.Events;
using DaxStudio.UI.Interfaces;
using DaxStudio.UI.Model;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Windows;
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
        private string _currentModelKey;

        /// <summary>
        /// Path to the layout cache file.
        /// </summary>
        private static string LayoutCacheFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxStudio",
            "ModelDiagramLayouts.json");

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
            UpdateAllRelationships();
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
            UpdateAllRelationships();
        }

        /// <summary>
        /// Updates all relationship paths (e.g., after collapse/expand all).
        /// </summary>
        public void UpdateAllRelationships()
        {
            foreach (var rel in Relationships)
            {
                rel.UpdatePath();
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

        private bool _snapToGrid = false;
        /// <summary>
        /// Whether to snap table positions to a grid when dragging.
        /// </summary>
        public bool SnapToGrid
        {
            get => _snapToGrid;
            set { _snapToGrid = value; NotifyOfPropertyChange(); }
        }

        /// <summary>
        /// The grid size for snapping (in pixels).
        /// </summary>
        public double GridSize => 20;

        /// <summary>
        /// Snaps a value to the grid if snap is enabled.
        /// </summary>
        public double SnapToGridValue(double value)
        {
            if (!SnapToGrid) return value;
            return Math.Round(value / GridSize) * GridSize;
        }

        private bool _showMiniMap = false;
        /// <summary>
        /// Whether to show the mini-map overview panel.
        /// </summary>
        public bool ShowMiniMap
        {
            get => _showMiniMap;
            set { _showMiniMap = value; NotifyOfPropertyChange(); }
        }

        /// <summary>
        /// Collection of selected tables for multi-selection.
        /// </summary>
        public BindableCollection<ModelDiagramTableViewModel> SelectedTables { get; } = new BindableCollection<ModelDiagramTableViewModel>();

        /// <summary>
        /// Whether multiple tables are currently selected.
        /// </summary>
        public bool HasMultipleSelection => SelectedTables.Count > 1;

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

        /// <summary>
        /// Called when mouse enters a table. Highlights related tables and dims others.
        /// </summary>
        public void OnTableMouseEnter(ModelDiagramTableViewModel hoveredTable)
        {
            if (hoveredTable == null) return;

            hoveredTable.IsHovered = true;

            // Find all tables connected to the hovered table
            var connectedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hoveredTable.TableName };
            foreach (var rel in Relationships)
            {
                if (rel.FromTable == hoveredTable.TableName)
                    connectedTables.Add(rel.ToTable);
                else if (rel.ToTable == hoveredTable.TableName)
                    connectedTables.Add(rel.FromTable);
            }

            // Dim unconnected tables and relationships
            foreach (var table in Tables)
            {
                table.IsDimmed = !connectedTables.Contains(table.TableName);
            }

            foreach (var rel in Relationships)
            {
                var isConnected = rel.FromTable == hoveredTable.TableName || rel.ToTable == hoveredTable.TableName;
                rel.IsHighlighted = isConnected;
                rel.IsDimmed = !isConnected;
            }
        }

        /// <summary>
        /// Called when mouse leaves a table. Restores normal highlighting.
        /// </summary>
        public void OnTableMouseLeave(ModelDiagramTableViewModel hoveredTable)
        {
            if (hoveredTable == null) return;

            hoveredTable.IsHovered = false;

            // Restore all tables to normal (unless there's a selection)
            if (_selectedTable == null)
            {
                foreach (var table in Tables)
                {
                    table.IsDimmed = false;
                }

                foreach (var rel in Relationships)
                {
                    rel.IsHighlighted = false;
                    rel.IsDimmed = false;
                }
            }
            else
            {
                // Restore selection-based highlighting
                UpdateRelationshipHighlighting();
                foreach (var table in Tables)
                {
                    table.IsDimmed = false; // Tables don't dim on selection, only relationships
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
                _currentModelKey = GenerateModelKey(model);
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

                // Try to load saved layout, otherwise use auto-layout
                if (!TryLoadSavedLayout())
                {
                    LayoutDiagram();
                }

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
        /// Generates a unique key for the model based on server and database.
        /// </summary>
        private string GenerateModelKey(ADOTabularModel model)
        {
            // Create a key from database name and table count/names hash
            // This identifies the same model even across different connections
            var tableNames = string.Join("|", model.Tables.OrderBy(t => t.Caption).Select(t => t.Caption));
            var hash = tableNames.GetHashCode().ToString("X8");
            return $"{model.Database?.Name ?? "Unknown"}_{hash}";
        }

        /// <summary>
        /// Layouts the diagram using a simple grid-based algorithm.
        /// Tables connected by relationships are placed closer together.
        /// Fact tables (many relationships, on the "many" side) go at the bottom.
        /// Dimension tables (fewer relationships, on the "one" side) go at the top.
        /// </summary>
        private void LayoutDiagram()
        {
            if (Tables.Count == 0) return;

            const double tableWidth = 200;
            const double tableHeight = 180;
            const double horizontalSpacing = 100;
            const double verticalSpacing = 80;
            const double padding = 40;

            // Build relationship map and categorize tables
            var relationshipMap = BuildRelationshipMap();
            var tableInfo = CategorizeTablesForLayout(relationshipMap);

            // Separate into tiers based on table role
            var factTables = tableInfo.Where(t => t.Value.Role == TableRole.Fact).Select(t => t.Key).ToList();
            var bridgeTables = tableInfo.Where(t => t.Value.Role == TableRole.Bridge).Select(t => t.Key).ToList();
            var dimensionTables = tableInfo.Where(t => t.Value.Role == TableRole.Dimension).Select(t => t.Key).ToList();
            var standaloneTables = tableInfo.Where(t => t.Value.Role == TableRole.Standalone).Select(t => t.Key).ToList();

            // Layout in tiers: Dimensions at top, Bridge/other in middle, Facts at bottom
            double currentY = padding;

            // Tier 1: Dimension tables (top)
            if (dimensionTables.Count > 0)
            {
                currentY = LayoutTier(dimensionTables, currentY, tableWidth, tableHeight, horizontalSpacing, padding);
                currentY += verticalSpacing;
            }

            // Tier 2: Bridge tables (middle)
            if (bridgeTables.Count > 0)
            {
                currentY = LayoutTier(bridgeTables, currentY, tableWidth, tableHeight, horizontalSpacing, padding);
                currentY += verticalSpacing;
            }

            // Tier 3: Fact tables (bottom center)
            if (factTables.Count > 0)
            {
                currentY = LayoutTierCentered(factTables, currentY, tableWidth, tableHeight, horizontalSpacing, padding, 
                    Math.Max(dimensionTables.Count, bridgeTables.Count));
                currentY += verticalSpacing;
            }

            // Tier 4: Standalone tables (bottom, if any)
            if (standaloneTables.Count > 0)
            {
                currentY = LayoutTier(standaloneTables, currentY, tableWidth, tableHeight, horizontalSpacing, padding);
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
        /// Categorizes tables based on their relationship patterns.
        /// </summary>
        private Dictionary<ModelDiagramTableViewModel, TableLayoutInfo> CategorizeTablesForLayout(
            Dictionary<string, HashSet<string>> relationshipMap)
        {
            var result = new Dictionary<ModelDiagramTableViewModel, TableLayoutInfo>();

            foreach (var table in Tables)
            {
                var info = new TableLayoutInfo
                {
                    ConnectionCount = relationshipMap.TryGetValue(table.TableName, out var connections) ? connections.Count : 0
                };

                // Count how many times this table is on the "many" side vs "one" side
                int manySideCount = 0;
                int oneSideCount = 0;

                foreach (var rel in Relationships)
                {
                    if (rel.FromTable == table.TableName)
                    {
                        // This table is on the "From" side
                        if (rel.FromCardinality == "*") manySideCount++;
                        else oneSideCount++;
                    }
                    else if (rel.ToTable == table.TableName)
                    {
                        // This table is on the "To" side
                        if (rel.ToCardinality == "*") manySideCount++;
                        else oneSideCount++;
                    }
                }

                // Determine role based on relationship patterns
                if (info.ConnectionCount == 0)
                {
                    info.Role = TableRole.Standalone;
                }
                else if (manySideCount > 0 && manySideCount >= oneSideCount)
                {
                    // Primarily on the "many" side = Fact table
                    info.Role = TableRole.Fact;
                }
                else if (info.ConnectionCount >= 3 && manySideCount > 0)
                {
                    // Many connections with some "many" sides = Bridge table
                    info.Role = TableRole.Bridge;
                }
                else
                {
                    // Primarily on the "one" side = Dimension table
                    info.Role = TableRole.Dimension;
                }

                result[table] = info;
            }

            return result;
        }

        /// <summary>
        /// Layouts a tier of tables in a row.
        /// </summary>
        private double LayoutTier(List<ModelDiagramTableViewModel> tables, double startY,
            double tableWidth, double tableHeight, double hSpacing, double padding)
        {
            if (tables.Count == 0) return startY;

            double x = padding;
            double maxHeight = tableHeight;

            foreach (var table in tables.OrderBy(t => t.TableName))
            {
                table.X = x;
                table.Y = startY;
                table.Width = tableWidth;
                table.Height = tableHeight;
                x += tableWidth + hSpacing;
            }

            return startY + maxHeight;
        }

        /// <summary>
        /// Layouts a tier of tables centered relative to the widest tier.
        /// </summary>
        private double LayoutTierCentered(List<ModelDiagramTableViewModel> tables, double startY,
            double tableWidth, double tableHeight, double hSpacing, double padding, int maxColsReference)
        {
            if (tables.Count == 0) return startY;

            // Calculate offset to center this tier
            double maxWidth = maxColsReference * (tableWidth + hSpacing) - hSpacing;
            double thisWidth = tables.Count * (tableWidth + hSpacing) - hSpacing;
            double offsetX = padding + Math.Max(0, (maxWidth - thisWidth) / 2);

            double x = offsetX;
            foreach (var table in tables.OrderBy(t => t.TableName))
            {
                table.X = x;
                table.Y = startY;
                table.Width = tableWidth;
                table.Height = tableHeight;
                x += tableWidth + hSpacing;
            }

            return startY + tableHeight;
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
        /// Updates relationship paths connected to a specific table.
        /// Call this after a table's position or size changes.
        /// </summary>
        public void UpdateRelationshipsForTable(ModelDiagramTableViewModel table)
        {
            if (table == null) return;
            
            foreach (var rel in Relationships)
            {
                if (rel.FromTableViewModel == table || rel.ToTableViewModel == table)
                {
                    rel.UpdatePath();
                }
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

        /// <summary>
        /// Sets zoom to a specific level.
        /// </summary>
        public void SetZoom(double scale)
        {
            Scale = Math.Max(0.1, Math.Min(2.0, scale));
        }

        /// <summary>
        /// Auto-arranges tables in a grid layout.
        /// </summary>
        public void AutoArrange()
        {
            LayoutDiagram();
            RefreshLayout();
            SaveCurrentLayout();
        }

        /// <summary>
        /// Clears the saved layout for this model and re-applies auto-arrange.
        /// </summary>
        public void ClearSavedLayout()
        {
            if (string.IsNullOrEmpty(_currentModelKey)) return;

            try
            {
                var layouts = LoadLayoutCache();
                if (layouts.Remove(_currentModelKey))
                {
                    // Save the updated cache
                    var directory = Path.GetDirectoryName(LayoutCacheFilePath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    var json = JsonConvert.SerializeObject(layouts, Formatting.None);
                    File.WriteAllText(LayoutCacheFilePath, json);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to clear saved layout");
            }

            // Re-apply auto layout
            LayoutDiagram();
            RefreshLayout();
        }

        /// <summary>
        /// Saves the current table positions after a drag operation.
        /// Called from the view when tables are moved.
        /// </summary>
        public void SaveLayoutAfterDrag()
        {
            SaveCurrentLayout();
        }

        #region Copy Operations

        /// <summary>
        /// Copies the table name to clipboard.
        /// </summary>
        public void CopyTableName(ModelDiagramTableViewModel table)
        {
            if (table == null) return;
            try
            {
                ClipboardManager.SetText(table.TableName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to copy table name to clipboard");
            }
        }

        /// <summary>
        /// Copies the table DAX reference to clipboard (e.g., 'TableName').
        /// </summary>
        public void CopyTableDaxName(ModelDiagramTableViewModel table)
        {
            if (table == null) return;
            try
            {
                ClipboardManager.SetText($"'{table.TableName}'");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to copy table DAX name to clipboard");
            }
        }

        /// <summary>
        /// Copies the column name to clipboard.
        /// </summary>
        public void CopyColumnName(ModelDiagramColumnViewModel column)
        {
            if (column == null) return;
            try
            {
                ClipboardManager.SetText(column.ColumnName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to copy column name to clipboard");
            }
        }

        /// <summary>
        /// Copies the column DAX reference to clipboard (e.g., 'TableName'[ColumnName]).
        /// </summary>
        public void CopyColumnDaxName(ModelDiagramColumnViewModel column, ModelDiagramTableViewModel table)
        {
            if (column == null || table == null) return;
            try
            {
                var daxName = column.IsMeasure
                    ? $"[{column.ColumnName}]"
                    : $"'{table.TableName}'[{column.ColumnName}]";
                ClipboardManager.SetText(daxName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to copy column DAX name to clipboard");
            }
        }

        #endregion

        #region Multi-Selection Operations

        /// <summary>
        /// Toggles selection of a table (for Ctrl+click multi-select).
        /// </summary>
        public void ToggleTableSelection(ModelDiagramTableViewModel table)
        {
            if (table == null) return;

            if (SelectedTables.Contains(table))
            {
                SelectedTables.Remove(table);
                table.IsSelected = false;
            }
            else
            {
                SelectedTables.Add(table);
                table.IsSelected = true;
            }
            NotifyOfPropertyChange(nameof(HasMultipleSelection));
        }

        /// <summary>
        /// Selects a single table, clearing any multi-selection.
        /// </summary>
        public void SelectSingleTable(ModelDiagramTableViewModel table)
        {
            ClearSelection();
            if (table != null)
            {
                SelectedTables.Add(table);
                table.IsSelected = true;
                _selectedTable = table;
                NotifyOfPropertyChange(nameof(SelectedTable));
            }
            NotifyOfPropertyChange(nameof(HasMultipleSelection));
            UpdateRelationshipHighlighting();
        }

        /// <summary>
        /// Clears all table selections.
        /// </summary>
        public void ClearSelection()
        {
            foreach (var table in SelectedTables)
            {
                table.IsSelected = false;
            }
            SelectedTables.Clear();
            _selectedTable = null;
            NotifyOfPropertyChange(nameof(SelectedTable));
            NotifyOfPropertyChange(nameof(HasMultipleSelection));
        }

        /// <summary>
        /// Selects all tables in the diagram.
        /// </summary>
        public void SelectAllTables()
        {
            ClearSelection();
            foreach (var table in Tables)
            {
                SelectedTables.Add(table);
                table.IsSelected = true;
            }
            NotifyOfPropertyChange(nameof(HasMultipleSelection));
        }

        /// <summary>
        /// Selects all tables within a rectangle (for drag-select).
        /// </summary>
        public void SelectTablesInRect(double left, double top, double right, double bottom)
        {
            ClearSelection();
            foreach (var table in Tables)
            {
                // Check if table overlaps with selection rectangle
                if (table.X < right && table.X + table.Width > left &&
                    table.Y < bottom && table.Y + table.Height > top)
                {
                    SelectedTables.Add(table);
                    table.IsSelected = true;
                }
            }
            if (SelectedTables.Count == 1)
            {
                _selectedTable = SelectedTables[0];
                NotifyOfPropertyChange(nameof(SelectedTable));
            }
            NotifyOfPropertyChange(nameof(HasMultipleSelection));
        }

        /// <summary>
        /// Moves all selected tables by the given delta.
        /// </summary>
        public void MoveSelectedTables(double deltaX, double deltaY)
        {
            foreach (var table in SelectedTables)
            {
                table.X = Math.Max(0, SnapToGridValue(table.X + deltaX));
                table.Y = Math.Max(0, SnapToGridValue(table.Y + deltaY));
            }
            RefreshLayout();
        }

        #endregion

        #region Grouping

        /// <summary>
        /// Available group names for tables.
        /// </summary>
        public BindableCollection<string> AvailableGroups { get; } = new BindableCollection<string>();

        /// <summary>
        /// Groups tables by their name prefix (schema-like grouping).
        /// Tables named like "Dim_Customer" or "Dim.Customer" will be grouped as "Dim".
        /// </summary>
        public void AutoGroupByPrefix()
        {
            AvailableGroups.Clear();
            var groups = new HashSet<string>();

            foreach (var table in Tables)
            {
                var name = table.TableName;
                var prefix = ExtractPrefix(name);
                if (!string.IsNullOrEmpty(prefix))
                {
                    table.Group = prefix;
                    groups.Add(prefix);
                }
                else
                {
                    table.Group = "Other";
                    groups.Add("Other");
                }
            }

            foreach (var group in groups.OrderBy(g => g))
            {
                AvailableGroups.Add(group);
            }
            NotifyOfPropertyChange(nameof(AvailableGroups));
        }

        /// <summary>
        /// Extracts a prefix from a table name (e.g., "Dim" from "Dim_Customer").
        /// </summary>
        private string ExtractPrefix(string tableName)
        {
            // Try common separators
            foreach (var sep in new[] { '_', '.', ' ' })
            {
                var idx = tableName.IndexOf(sep);
                if (idx > 0 && idx < tableName.Length - 1)
                {
                    var prefix = tableName.Substring(0, idx);
                    // Only use if it looks like a prefix (short, starts with letter)
                    if (prefix.Length <= 10 && char.IsLetter(prefix[0]))
                    {
                        return prefix;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Sets the group for all selected tables.
        /// </summary>
        public void SetGroupForSelectedTables(string groupName)
        {
            foreach (var table in SelectedTables)
            {
                table.Group = groupName;
            }

            if (!string.IsNullOrEmpty(groupName) && !AvailableGroups.Contains(groupName))
            {
                AvailableGroups.Add(groupName);
            }
        }

        /// <summary>
        /// Clears all table groups.
        /// </summary>
        public void ClearAllGroups()
        {
            foreach (var table in Tables)
            {
                table.Group = null;
            }
            AvailableGroups.Clear();
        }

        /// <summary>
        /// Arranges tables into rows by their group.
        /// </summary>
        public void ArrangeByGroup()
        {
            if (!Tables.Any(t => !string.IsNullOrEmpty(t.Group))) return;

            const double tableWidth = 200;
            const double tableHeight = 180;
            const double horizontalSpacing = 100;
            const double verticalSpacing = 100;
            const double groupSpacing = 50;
            const double padding = 40;

            var groupedTables = Tables
                .GroupBy(t => t.Group ?? "Ungrouped")
                .OrderBy(g => g.Key)
                .ToList();

            double currentY = padding;

            foreach (var group in groupedTables)
            {
                var tablesInGroup = group.OrderBy(t => t.TableName).ToList();
                double x = padding;

                foreach (var table in tablesInGroup)
                {
                    table.X = x;
                    table.Y = currentY;
                    table.Width = tableWidth;
                    table.Height = tableHeight;
                    x += tableWidth + horizontalSpacing;
                }

                currentY += tableHeight + verticalSpacing + groupSpacing;
            }

            // Recalculate canvas size
            RefreshLayout();
            SaveCurrentLayout();
        }

        #endregion

        #region Keyboard Navigation

        /// <summary>
        /// Navigates to the next/previous table using arrow keys.
        /// </summary>
        public void NavigateToTable(string direction)
        {
            if (Tables.Count == 0) return;

            var current = _selectedTable ?? Tables.FirstOrDefault();
            if (current == null) return;

            ModelDiagramTableViewModel target = null;
            double bestDistance = double.MaxValue;

            foreach (var table in Tables)
            {
                if (table == current) continue;

                double dx = table.CenterX - current.CenterX;
                double dy = table.CenterY - current.CenterY;

                bool isCandidate = false;
                switch (direction.ToLower())
                {
                    case "left":
                        isCandidate = dx < -10 && Math.Abs(dy) < Math.Abs(dx);
                        break;
                    case "right":
                        isCandidate = dx > 10 && Math.Abs(dy) < Math.Abs(dx);
                        break;
                    case "up":
                        isCandidate = dy < -10 && Math.Abs(dx) < Math.Abs(dy);
                        break;
                    case "down":
                        isCandidate = dy > 10 && Math.Abs(dx) < Math.Abs(dy);
                        break;
                }

                if (isCandidate)
                {
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        target = table;
                    }
                }
            }

            if (target != null)
            {
                SelectSingleTable(target);
            }
        }

        /// <summary>
        /// Cycles through connected tables (Tab key).
        /// </summary>
        public void CycleConnectedTables(bool reverse = false)
        {
            if (_selectedTable == null || Relationships.Count == 0)
            {
                // If nothing selected, select first table
                if (Tables.Count > 0)
                {
                    SelectSingleTable(Tables[0]);
                }
                return;
            }

            // Get all tables connected to current selection
            var connectedTables = new List<ModelDiagramTableViewModel>();
            foreach (var rel in Relationships)
            {
                if (rel.FromTable == _selectedTable.TableName)
                {
                    var toTable = Tables.FirstOrDefault(t => t.TableName == rel.ToTable);
                    if (toTable != null && !connectedTables.Contains(toTable))
                        connectedTables.Add(toTable);
                }
                else if (rel.ToTable == _selectedTable.TableName)
                {
                    var fromTable = Tables.FirstOrDefault(t => t.TableName == rel.FromTable);
                    if (fromTable != null && !connectedTables.Contains(fromTable))
                        connectedTables.Add(fromTable);
                }
            }

            if (connectedTables.Count == 0) return;

            // Sort by name for consistent ordering
            connectedTables = connectedTables.OrderBy(t => t.TableName).ToList();

            // Find current position and move to next
            int currentIndex = -1;
            for (int i = 0; i < connectedTables.Count; i++)
            {
                if (connectedTables[i] == _selectedTable)
                {
                    currentIndex = i;
                    break;
                }
            }

            int nextIndex = reverse
                ? (currentIndex <= 0 ? connectedTables.Count - 1 : currentIndex - 1)
                : ((currentIndex + 1) % connectedTables.Count);

            SelectSingleTable(connectedTables[nextIndex]);
        }

        /// <summary>
        /// Event to request navigation to a table in the metadata pane.
        /// </summary>
        public event EventHandler<string> NavigateToMetadataRequested;

        /// <summary>
        /// Navigates to the selected table in the metadata pane.
        /// </summary>
        public void JumpToMetadataPane()
        {
            if (_selectedTable != null)
            {
                NavigateToMetadataRequested?.Invoke(this, _selectedTable.TableName);
            }
        }

        #endregion

        /// <summary>
        /// Saves the current layout to the cache file.
        /// </summary>
        private void SaveCurrentLayout()
        {
            if (string.IsNullOrEmpty(_currentModelKey) || Tables.Count == 0) return;

            try
            {
                // Load existing layouts
                var layouts = LoadLayoutCache();

                // Create layout data for current model
                var layoutData = new ModelLayoutData
                {
                    ModelKey = _currentModelKey,
                    LastModified = DateTime.UtcNow,
                    TablePositions = Tables.ToDictionary(
                        t => t.TableName,
                        t => new TablePosition 
                        { 
                            X = t.X, 
                            Y = t.Y, 
                            Width = t.Width, 
                            Height = t.Height, 
                            IsCollapsed = t.IsCollapsed,
                            ExpandedHeight = t.ExpandedHeight
                        }
                    )
                };

                // Update or add
                layouts[_currentModelKey] = layoutData;

                // Prune old entries (keep last 50 models)
                if (layouts.Count > 50)
                {
                    var oldest = layouts.OrderBy(kvp => kvp.Value.LastModified).Take(layouts.Count - 50).Select(kvp => kvp.Key).ToList();
                    foreach (var key in oldest)
                    {
                        layouts.Remove(key);
                    }
                }

                // Save to file
                var directory = Path.GetDirectoryName(LayoutCacheFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(layouts, Formatting.None);
                File.WriteAllText(LayoutCacheFilePath, json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save model diagram layout");
            }
        }

        /// <summary>
        /// Tries to load a saved layout for the current model.
        /// </summary>
        /// <returns>True if layout was loaded, false otherwise.</returns>
        private bool TryLoadSavedLayout()
        {
            if (string.IsNullOrEmpty(_currentModelKey)) return false;

            try
            {
                var layouts = LoadLayoutCache();
                if (!layouts.TryGetValue(_currentModelKey, out var layoutData)) return false;

                // Apply saved positions
                bool anyApplied = false;
                foreach (var table in Tables)
                {
                    if (layoutData.TablePositions.TryGetValue(table.TableName, out var pos))
                    {
                        table.X = pos.X;
                        table.Y = pos.Y;
                        table.Width = pos.Width > 0 ? pos.Width : 200;
                        table.Height = pos.Height > 0 ? pos.Height : 180;
                        // Use SetCollapsedState to properly restore collapsed state with expanded height
                        if (pos.IsCollapsed)
                        {
                            table.SetCollapsedState(true, pos.ExpandedHeight > 0 ? pos.ExpandedHeight : pos.Height);
                        }
                        anyApplied = true;
                    }
                }

                if (anyApplied)
                {
                    // Calculate canvas size
                    var maxX = Tables.Max(t => t.X + t.Width);
                    var maxY = Tables.Max(t => t.Y + t.Height);
                    CanvasWidth = Math.Max(100, maxX + 40);
                    CanvasHeight = Math.Max(100, maxY + 40);

                    // Update relationship paths
                    foreach (var rel in Relationships)
                    {
                        rel.UpdatePath();
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load model diagram layout");
            }

            return false;
        }

        /// <summary>
        /// Loads the layout cache from disk.
        /// </summary>
        private Dictionary<string, ModelLayoutData> LoadLayoutCache()
        {
            try
            {
                if (File.Exists(LayoutCacheFilePath))
                {
                    var json = File.ReadAllText(LayoutCacheFilePath);
                    return JsonConvert.DeserializeObject<Dictionary<string, ModelLayoutData>>(json)
                           ?? new Dictionary<string, ModelLayoutData>();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read layout cache file");
            }
            return new Dictionary<string, ModelLayoutData>();
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

        private string _group;
        /// <summary>
        /// The group this table belongs to (for visual grouping).
        /// </summary>
        public string Group
        {
            get => _group;
            set 
            { 
                _group = value; 
                NotifyOfPropertyChange(); 
                NotifyOfPropertyChange(nameof(HasGroup));
                NotifyOfPropertyChange(nameof(GroupColor));
            }
        }

        /// <summary>
        /// Whether this table has a group assigned.
        /// </summary>
        public bool HasGroup => !string.IsNullOrEmpty(Group);

        /// <summary>
        /// Color for the group indicator bar.
        /// Uses a hash of the group name to generate consistent colors.
        /// </summary>
        public string GroupColor
        {
            get
            {
                if (string.IsNullOrEmpty(Group)) return "#CCCCCC";
                
                // Generate a consistent color from the group name
                var hash = Group.GetHashCode();
                var colors = new[]
                {
                    "#E91E63", "#9C27B0", "#673AB7", "#3F51B5", "#2196F3",
                    "#03A9F4", "#00BCD4", "#009688", "#4CAF50", "#8BC34A",
                    "#CDDC39", "#FFC107", "#FF9800", "#FF5722", "#795548"
                };
                return colors[Math.Abs(hash) % colors.Length];
            }
        }

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

        // Top edge for relationships
        public double TopEdgeX => X + Width / 2;
        public double TopEdgeY => Y;

        // Bottom edge for relationships
        public double BottomEdgeX => X + Width / 2;
        public double BottomEdgeY => Y + Height;

        #endregion

        #region State Properties

        private bool _isCollapsed;
        private double _expandedHeight;
        private const double CollapsedHeight = 32; // Height for just the header

        public bool IsCollapsed
        {
            get => _isCollapsed;
            set 
            { 
                if (_isCollapsed == value) return;
                
                if (value)
                {
                    // Collapsing: save current height and set to collapsed height
                    _expandedHeight = Height;
                    Height = CollapsedHeight;
                }
                else
                {
                    // Expanding: restore saved height (or calculate default)
                    Height = _expandedHeight > CollapsedHeight ? _expandedHeight : CalculateDefaultHeight();
                }
                
                _isCollapsed = value; 
                NotifyOfPropertyChange(); 
            }
        }

        /// <summary>
        /// The height when expanded (for saving/restoring collapse state).
        /// </summary>
        public double ExpandedHeight
        {
            get => _expandedHeight > CollapsedHeight ? _expandedHeight : Height;
            set => _expandedHeight = value;
        }

        /// <summary>
        /// Sets the collapsed state without triggering height changes (for loading saved state).
        /// </summary>
        public void SetCollapsedState(bool isCollapsed, double expandedHeight)
        {
            _expandedHeight = expandedHeight;
            _isCollapsed = isCollapsed;
            NotifyOfPropertyChange(nameof(IsCollapsed));
        }

        /// <summary>
        /// Calculate a reasonable default height based on column/measure count.
        /// </summary>
        private double CalculateDefaultHeight()
        {
            int itemCount = Columns?.Count ?? 0;
            return Math.Max(80, Math.Min(50 + itemCount * 20, 400));
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

        private bool _isDimmed;
        /// <summary>
        /// Whether this table should be visually dimmed (e.g., when another table is hovered).
        /// </summary>
        public bool IsDimmed
        {
            get => _isDimmed;
            set { _isDimmed = value; NotifyOfPropertyChange(); }
        }

        private bool _isHovered;
        /// <summary>
        /// Whether the mouse is currently hovering over this table.
        /// </summary>
        public bool IsHovered
        {
            get => _isHovered;
            set { _isHovered = value; NotifyOfPropertyChange(); }
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
        /// The "From" side cardinality symbol (1 or *)
        /// </summary>
        public string FromCardinality => _relationship.FromColumnMultiplicity == "*" ? "*" : "1";

        /// <summary>
        /// The "To" side cardinality symbol (1 or *)
        /// </summary>
        public string ToCardinality => _relationship.ToColumnMultiplicity == "*" ? "*" : "1";

        /// <summary>
        /// Tooltip for the "From" side cardinality.
        /// </summary>
        public string FromCardinalityTooltip => $"{FromTable}[{FromColumn}] ({(FromCardinality == "*" ? "Many" : "One")})";

        /// <summary>
        /// Tooltip for the "To" side cardinality.
        /// </summary>
        public string ToCardinalityTooltip => $"{ToTable}[{ToColumn}] ({(ToCardinality == "*" ? "Many" : "One")})";

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
        /// Inverse of IsActive for binding convenience.
        /// </summary>
        public bool IsInactive => !IsActive;

        /// <summary>
        /// Whether the center indicator panel should be shown (has BiDi, M:M, or Inactive).
        /// </summary>
        public bool HasCenterIndicators => IsBidirectional || IsManyToMany || IsInactive;

        /// <summary>
        /// Whether the filter flows from "From" to "To" (single direction or both)
        /// </summary>
        public bool FiltersToEnd => true; // Filter always flows from "From" side

        /// <summary>
        /// Whether the filter flows from "To" to "From" (bi-directional only)
        /// </summary>
        public bool FiltersToStart => IsBidirectional;

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

        /// <summary>
        /// Tracks whether UpdatePath has been called at least once.
        /// </summary>
        private bool _isPathInitialized;

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
                // Return empty if positions not initialized
                if (!HasValidPositions) return string.Empty;
                
                // Determine if this is a horizontal or vertical relationship
                bool isVertical = (_startEdge == EdgeType.Top || _startEdge == EdgeType.Bottom);
                
                if (isVertical)
                {
                    // Vertical bezier curve (for top/bottom connections)
                    double midY = (StartY + EndY) / 2;
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "M {0},{1} C {2},{3} {4},{5} {6},{7}",
                        StartX, StartY, StartX, midY, EndX, midY, EndX, EndY);
                }
                else
                {
                    // Horizontal bezier curve (for left/right connections)
                    double midX = (StartX + EndX) / 2;
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "M {0},{1} C {2},{3} {4},{5} {6},{7}",
                        StartX, StartY, midX, StartY, midX, EndY, EndX, EndY);
                }
            }
        }

        /// <summary>
        /// Label position (middle of the line).
        /// </summary>
        public double LabelX => (StartX + EndX) / 2;
        public double LabelY => (StartY + EndY) / 2;

        /// <summary>
        /// Rotation angle for filter direction arrow pointing toward "From" table (where filter originates).
        /// 0 = right, 90 = down, 180 = left, 270 = up
        /// </summary>
        public double FilterDirectionAngle
        {
            get
            {
                // Point toward the From table (filter flows from "one" side to "many" side)
                double dx = StartX - EndX;
                double dy = StartY - EndY;
                double angleRadians = Math.Atan2(dy, dx);
                return angleRadians * 180 / Math.PI;
            }
        }

        /// <summary>
        /// Rotation angle for reverse filter direction (BiDi) pointing toward "To" table.
        /// </summary>
        public double ReverseFilterDirectionAngle => FilterDirectionAngle + 180;

        /// <summary>
        /// Position for the "From" cardinality label (near start, offset along the curve).
        /// </summary>
        public double FromCardinalityX
        {
            get
            {
                bool isVertical = (_startEdge == EdgeType.Top || _startEdge == EdgeType.Bottom);
                if (isVertical)
                {
                    // For vertical connections, position to the side
                    return StartX + 5;
                }
                else
                {
                    // For horizontal connections, position along the line
                    double offset = EndX > StartX ? 8 : -20;
                    return StartX + offset;
                }
            }
        }
        
        public double FromCardinalityY
        {
            get
            {
                bool isVertical = (_startEdge == EdgeType.Top || _startEdge == EdgeType.Bottom);
                if (isVertical)
                {
                    // For vertical connections, position along the line
                    double offset = EndY > StartY ? 8 : -15;
                    return StartY + offset;
                }
                else
                {
                    // For horizontal connections, position above/below
                    double offset = EndY > StartY ? -15 : 5;
                    return StartY + offset;
                }
            }
        }

        /// <summary>
        /// Position for the "To" cardinality label (near end, offset along the curve).
        /// </summary>
        public double ToCardinalityX
        {
            get
            {
                bool isVertical = (_startEdge == EdgeType.Top || _startEdge == EdgeType.Bottom);
                if (isVertical)
                {
                    // For vertical connections, position to the side
                    return EndX + 5;
                }
                else
                {
                    // For horizontal connections, position along the line
                    double offset = EndX > StartX ? -20 : 8;
                    return EndX + offset;
                }
            }
        }
        
        public double ToCardinalityY
        {
            get
            {
                bool isVertical = (_startEdge == EdgeType.Top || _startEdge == EdgeType.Bottom);
                if (isVertical)
                {
                    // For vertical connections, position along the line
                    double offset = EndY > StartY ? -15 : 8;
                    return EndY + offset;
                }
                else
                {
                    // For horizontal connections, position above/below
                    double offset = EndY > StartY ? 5 : -15;
                    return EndY + offset;
                }
            }
        }

        /// <summary>
        /// Returns true when the relationship path has been initialized via UpdatePath().
        /// </summary>
        public bool HasValidPositions => _isPathInitialized;

        /// <summary>
        /// Arrow path data pointing toward the "To" table (filter direction).
        /// Returns empty string - arrows removed to avoid visual artifacts.
        /// Filter direction is indicated by the relationship flowing from "From" to "To" table.
        /// </summary>
        public string ArrowToPathData => string.Empty;

        /// <summary>
        /// Arrow path data pointing toward the "From" table (bi-directional filter).
        /// Returns empty string - arrows removed to avoid visual artifacts.
        /// BiDi is indicated by the "BiDi" label in the center of the relationship line.
        /// </summary>
        public string ArrowFromPathData => string.Empty;

        /// <summary>
        /// Enum for edge selection.
        /// </summary>
        private enum EdgeType { Left, Right, Top, Bottom }

        /// <summary>
        /// The edge type used for the start of the relationship line.
        /// </summary>
        private EdgeType _startEdge;

        /// <summary>
        /// The edge type used for the end of the relationship line.
        /// </summary>
        private EdgeType _endEdge;

        /// <summary>
        /// Updates the path based on table positions.
        /// Chooses the optimal edge (top, bottom, left, right) to minimize line crossings.
        /// </summary>
        public void UpdatePath()
        {
            // Calculate distances for each edge combination and pick the shortest
            var fromCenter = new Point(_fromTable.CenterX, _fromTable.CenterY);
            var toCenter = new Point(_toTable.CenterX, _toTable.CenterY);

            // Calculate the angle between centers to determine primary direction
            double dx = toCenter.X - fromCenter.X;
            double dy = toCenter.Y - fromCenter.Y;

            // Determine if the relationship is more horizontal or vertical
            bool isMoreHorizontal = Math.Abs(dx) > Math.Abs(dy);

            // Check for overlapping tables (use vertical edges for stacked tables)
            bool tablesOverlapHorizontally = 
                _fromTable.X < _toTable.X + _toTable.Width && 
                _fromTable.X + _fromTable.Width > _toTable.X;
            bool tablesOverlapVertically = 
                _fromTable.Y < _toTable.Y + _toTable.Height && 
                _fromTable.Y + _fromTable.Height > _toTable.Y;

            if (tablesOverlapHorizontally && !tablesOverlapVertically)
            {
                // Tables are stacked vertically - use top/bottom edges
                if (fromCenter.Y < toCenter.Y)
                {
                    // From is above To
                    SetEdges(_fromTable.BottomEdgeX, _fromTable.BottomEdgeY, EdgeType.Bottom,
                             _toTable.TopEdgeX, _toTable.TopEdgeY, EdgeType.Top);
                }
                else
                {
                    // From is below To
                    SetEdges(_fromTable.TopEdgeX, _fromTable.TopEdgeY, EdgeType.Top,
                             _toTable.BottomEdgeX, _toTable.BottomEdgeY, EdgeType.Bottom);
                }
            }
            else if (tablesOverlapVertically && !tablesOverlapHorizontally)
            {
                // Tables are side by side - use left/right edges
                if (fromCenter.X < toCenter.X)
                {
                    SetEdges(_fromTable.RightEdgeX, _fromTable.RightEdgeY, EdgeType.Right,
                             _toTable.LeftEdgeX, _toTable.LeftEdgeY, EdgeType.Left);
                }
                else
                {
                    SetEdges(_fromTable.LeftEdgeX, _fromTable.LeftEdgeY, EdgeType.Left,
                             _toTable.RightEdgeX, _toTable.RightEdgeY, EdgeType.Right);
                }
            }
            else if (isMoreHorizontal)
            {
                // Primarily horizontal relationship - prefer left/right edges
                if (dx > 0)
                {
                    SetEdges(_fromTable.RightEdgeX, _fromTable.RightEdgeY, EdgeType.Right,
                             _toTable.LeftEdgeX, _toTable.LeftEdgeY, EdgeType.Left);
                }
                else
                {
                    SetEdges(_fromTable.LeftEdgeX, _fromTable.LeftEdgeY, EdgeType.Left,
                             _toTable.RightEdgeX, _toTable.RightEdgeY, EdgeType.Right);
                }
            }
            else
            {
                // Primarily vertical relationship - prefer top/bottom edges
                if (dy > 0)
                {
                    SetEdges(_fromTable.BottomEdgeX, _fromTable.BottomEdgeY, EdgeType.Bottom,
                             _toTable.TopEdgeX, _toTable.TopEdgeY, EdgeType.Top);
                }
                else
                {
                    SetEdges(_fromTable.TopEdgeX, _fromTable.TopEdgeY, EdgeType.Top,
                             _toTable.BottomEdgeX, _toTable.BottomEdgeY, EdgeType.Bottom);
                }
            }

            // Mark as initialized so paths will render
            _isPathInitialized = true;

            // Notify about derived properties
            NotifyOfPropertyChange(nameof(HasValidPositions));
            NotifyOfPropertyChange(nameof(PathData));
            NotifyOfPropertyChange(nameof(FromCardinalityX));
            NotifyOfPropertyChange(nameof(FromCardinalityY));
            NotifyOfPropertyChange(nameof(ToCardinalityX));
            NotifyOfPropertyChange(nameof(ToCardinalityY));
            NotifyOfPropertyChange(nameof(ArrowToPathData));
            NotifyOfPropertyChange(nameof(ArrowFromPathData));
            NotifyOfPropertyChange(nameof(FilterDirectionAngle));
            NotifyOfPropertyChange(nameof(ReverseFilterDirectionAngle));
        }

        /// <summary>
        /// Helper to set edge positions.
        /// Sets edge types first to ensure derived properties use correct values.
        /// </summary>
        private void SetEdges(double startX, double startY, EdgeType startEdge,
                              double endX, double endY, EdgeType endEdge)
        {
            // Set edge types first (before positions trigger notifications)
            _startEdge = startEdge;
            _endEdge = endEdge;
            
            // Now set positions (which trigger notifications that use edge types)
            StartX = startX;
            StartY = startY;
            EndX = endX;
            EndY = endY;
        }

        /// <summary>
        /// Simple Point struct for calculations.
        /// </summary>
        private struct Point
        {
            public double X, Y;
            public Point(double x, double y) { X = x; Y = y; }
        }

        #endregion
    }

    #region Layout Persistence Classes

    /// <summary>
    /// Lightweight data structure for persisting model diagram layouts.
    /// </summary>
    public class ModelLayoutData
    {
        public string ModelKey { get; set; }
        public DateTime LastModified { get; set; }
        public Dictionary<string, TablePosition> TablePositions { get; set; } = new Dictionary<string, TablePosition>();
    }

    /// <summary>
    /// Position data for a single table.
    /// </summary>
    public class TablePosition
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool IsCollapsed { get; set; }
        public double ExpandedHeight { get; set; }
    }

    /// <summary>
    /// Information about a table's role in the model for layout purposes.
    /// </summary>
    internal class TableLayoutInfo
    {
        public int ConnectionCount { get; set; }
        public TableRole Role { get; set; }
    }

    /// <summary>
    /// Role of a table in the data model.
    /// </summary>
    internal enum TableRole
    {
        Dimension,  // Lookup table, typically on the "one" side
        Fact,       // Transaction table, typically on the "many" side
        Bridge,     // Many-to-many bridge table
        Standalone  // No relationships
    }

    #endregion
}
