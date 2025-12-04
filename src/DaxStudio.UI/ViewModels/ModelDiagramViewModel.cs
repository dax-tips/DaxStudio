using ADOTabular;
using Caliburn.Micro;
using DaxStudio.Interfaces;
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
using System.Threading;
using System.Threading.Tasks;
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
        private readonly IMetadataProvider _metadataProvider;
        private readonly IGlobalOptions _options;
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
        public ModelDiagramViewModel(IEventAggregator eventAggregator, IMetadataProvider metadataProvider, IGlobalOptions options)
        {
            _eventAggregator = eventAggregator;
            _metadataProvider = metadataProvider;
            _options = options;
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

        /// <summary>
        /// Unsubscribe from event aggregator when the window is closed to prevent memory leaks.
        /// </summary>
        protected override Task OnDeactivateAsync(bool close, CancellationToken cancellationToken)
        {
            if (close)
            {
                _eventAggregator.Unsubscribe(this);
            }
            return base.OnDeactivateAsync(close, cancellationToken);
        }

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
        /// Collection of text annotations for display.
        /// </summary>
        public BindableCollection<ModelDiagramAnnotationViewModel> Annotations { get; } = new BindableCollection<ModelDiagramAnnotationViewModel>();

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
            SaveLayoutForUndo();
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
            SaveLayoutForUndo();
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

        private string _loadingMessage = "Loading diagram...";
        /// <summary>
        /// Message shown during loading with progress info.
        /// </summary>
        public string LoadingMessage
        {
            get => _loadingMessage;
            set { _loadingMessage = value; NotifyOfPropertyChange(); }
        }

        private string _loadingStats = string.Empty;
        /// <summary>
        /// Statistics about the last load operation (timing info).
        /// </summary>
        public string LoadingStats
        {
            get => _loadingStats;
            set { _loadingStats = value; NotifyOfPropertyChange(); }
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
                    LoadFromModel(_model, forceReload: true);
                }
            }
        }

        private bool _sortKeyColumnsFirst = false;
        /// <summary>
        /// Whether to sort related columns first in the column list.
        /// </summary>
        public bool SortKeyColumnsFirst
        {
            get => _sortKeyColumnsFirst;
            set
            {
                _sortKeyColumnsFirst = value;
                NotifyOfPropertyChange();
                // Re-sort columns in all tables
                foreach (var table in Tables)
                {
                    table.UpdateColumnSort(_sortKeyColumnsFirst);
                }
            }
        }

        private int _tableFilter = 0;
        /// <summary>
        /// Filter tables by type: 0=All, 1=Date Tables Only
        /// </summary>
        public int TableFilter
        {
            get => _tableFilter;
            set
            {
                _tableFilter = value;
                NotifyOfPropertyChange();
                ApplyTableFilter();
            }
        }

        /// <summary>
        /// Applies the table filter to show/hide tables based on type.
        /// Also hides relationships between hidden tables.
        /// </summary>
        private void ApplyTableFilter()
        {
            // First, set table visibility
            foreach (var table in Tables)
            {
                switch (_tableFilter)
                {
                    case 0: // All tables
                        table.IsHidden = false;
                        break;
                    case 1: // Date tables only
                        table.IsHidden = !table.IsDateTable;
                        break;
                }
            }
            
            // Then, hide relationships where either table is hidden
            foreach (var rel in Relationships)
            {
                bool fromVisible = rel.FromTableViewModel != null && !rel.FromTableViewModel.IsHidden;
                bool toVisible = rel.ToTableViewModel != null && !rel.ToTableViewModel.IsHidden;
                rel.IsVisible = fromVisible && toVisible;
            }
            
            RefreshLayout();
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

        #region Layout Undo

        private Stack<Dictionary<string, (double X, double Y, bool IsCollapsed)>> _layoutUndoStack = 
            new Stack<Dictionary<string, (double X, double Y, bool IsCollapsed)>>();
        
        private const int MaxUndoLevels = 10;

        /// <summary>
        /// Whether there are layout changes that can be undone.
        /// </summary>
        public bool CanUndoLayout => _layoutUndoStack.Count > 0;

        /// <summary>
        /// Saves the current layout state for undo.
        /// </summary>
        private void SaveLayoutForUndo()
        {
            var state = new Dictionary<string, (double X, double Y, bool IsCollapsed)>();
            foreach (var table in Tables)
            {
                state[table.TableName] = (table.X, table.Y, table.IsCollapsed);
            }
            
            _layoutUndoStack.Push(state);
            
            // Limit stack size
            if (_layoutUndoStack.Count > MaxUndoLevels)
            {
                var items = _layoutUndoStack.ToArray();
                _layoutUndoStack.Clear();
                for (int i = 0; i < MaxUndoLevels; i++)
                {
                    _layoutUndoStack.Push(items[MaxUndoLevels - 1 - i]);
                }
            }
            
            NotifyOfPropertyChange(nameof(CanUndoLayout));
        }

        /// <summary>
        /// Undoes the last layout change (Auto Arrange, etc.).
        /// </summary>
        public void UndoLayout()
        {
            if (_layoutUndoStack.Count == 0) return;

            var state = _layoutUndoStack.Pop();
            
            foreach (var table in Tables)
            {
                if (state.TryGetValue(table.TableName, out var pos))
                {
                    table.X = pos.X;
                    table.Y = pos.Y;
                    table.IsCollapsed = pos.IsCollapsed;
                }
            }
            
            RefreshLayout();
            SaveCurrentLayout();
            NotifyOfPropertyChange(nameof(CanUndoLayout));
        }

        #endregion

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

        /// <summary>
        /// Whether at least one table is currently selected.
        /// </summary>
        public bool HasSelection => SelectedTables.Count > 0 || _selectedTable != null;

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
            if (_selectedTable == null)
            {
                // No selection - reset everything to normal
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
                // Find connected tables
                var connectedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _selectedTable.TableName };
                foreach (var rel in Relationships)
                {
                    if (rel.FromTable == _selectedTable.TableName)
                        connectedTables.Add(rel.ToTable);
                    else if (rel.ToTable == _selectedTable.TableName)
                        connectedTables.Add(rel.FromTable);
                }

                // Dim unconnected tables
                foreach (var table in Tables)
                {
                    table.IsDimmed = !connectedTables.Contains(table.TableName);
                }

                // Highlight connected relationships, dim others
                foreach (var rel in Relationships)
                {
                    var isConnected = rel.FromTable == _selectedTable.TableName || rel.ToTable == _selectedTable.TableName;
                    rel.IsHighlighted = isConnected;
                    rel.IsDimmed = !isConnected;
                }
            }
        }

        /// <summary>
        /// Calculates parallel offsets for relationships between the same table pairs.
        /// This prevents multiple relationships from overlapping by spacing them apart.
        /// </summary>
        private void CalculateParallelRelationshipOffsets()
        {
            const double ParallelGap = 30; // Gap between parallel relationship lines
            
            // Group relationships by table pair (order-independent)
            var tablePairGroups = Relationships
                .GroupBy(r => 
                {
                    // Create a consistent key regardless of direction
                    var tables = new[] { r.FromTable, r.ToTable }.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
                    return $"{tables[0]}|{tables[1]}";
                })
                .Where(g => g.Count() > 1) // Only process groups with multiple relationships
                .ToList();
            
            foreach (var group in tablePairGroups)
            {
                var rels = group.ToList();
                int count = rels.Count;
                
                // Calculate offsets to center the group
                // For 2 relationships: offsets are -10, +10
                // For 3 relationships: offsets are -20, 0, +20
                for (int i = 0; i < count; i++)
                {
                    double offset = (i - (count - 1) / 2.0) * ParallelGap;
                    rels[i].ParallelOffset = offset;
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
        /// <param name="model">The model to load.</param>
        /// <param name="forceReload">If true, bypasses duplicate load prevention.</param>
        public void LoadFromModel(ADOTabularModel model, bool forceReload = false)
        {
            if (model == null) return;
            
            // Prevent duplicate loads while already loading
            if (IsLoading) return;
            
            // Prevent reloading the same model (unless explicitly requested)
            if (!forceReload && _model == model && Tables.Count > 0) return;

            // Set loading flag BEFORE starting async work to prevent duplicate calls
            IsLoading = true;

            // For large models, load asynchronously
            var tableCount = model.Tables.Count(t => ShowHiddenObjects || t.IsVisible);
            if (tableCount > 20)
            {
                // Run async loading on background thread for large models
                _ = LoadFromModelAsync(model);
            }
            else
            {
                // Small models load synchronously
                LoadFromModelSync(model);
            }
        }

        /// <summary>
        /// Asynchronously loads the diagram from an ADOTabularModel with progress updates.
        /// Used for large models (>20 tables) to keep UI responsive.
        /// </summary>
        private async Task LoadFromModelAsync(ADOTabularModel model)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var stageStopwatch = new System.Diagnostics.Stopwatch();

            // IsLoading is already set by LoadFromModel() before calling this method
            LoadingMessage = "Initializing...";
            
            // Allow UI to render the loading indicator before starting work
            await Task.Delay(50);

            try
            {
                _model = model;
                _currentModelKey = GenerateModelKey(model);

                // Stage 1: Clear existing data (must be on UI thread)
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Tables.Clear();
                    Relationships.Clear();
                    Annotations.Clear();
                });

                // Stage 2: Materialize all model data on UI thread first (ADOTabular may log during lazy loading)
                stageStopwatch.Start();
                LoadingMessage = "Reading model metadata...";
                
                var visibleTables = model.Tables
                    .Where(t => ShowHiddenObjects || t.IsVisible)
                    .ToList();
                var totalTables = visibleTables.Count;

                // Pre-load all relationship data on UI thread to avoid logging issues on background thread
                // Also pre-extract all string values to avoid lazy loading on background thread
                var allRelationships = new List<(ADOTabularTable Table, List<ADOTabularRelationship> Relationships)>();
                var relationshipData = new List<(ADOTabularRelationship Rel, string FromTableName, string FromColumn, string ToTableName, string ToColumn)>();
                
                foreach (var table in model.Tables)
                {
                    // Access relationships on UI thread to trigger any lazy loading
                    var rels = table.Relationships.ToList();
                    allRelationships.Add((table, rels));
                    
                    // Pre-extract string values for fast background processing
                    foreach (var rel in rels)
                    {
                        relationshipData.Add((
                            rel,
                            rel.FromTable?.Name ?? "",
                            rel.FromColumn ?? "",
                            rel.ToTable?.Name ?? "",
                            rel.ToColumn ?? ""
                        ));
                    }
                }
                
                var metadataTime = stageStopwatch.ElapsedMilliseconds;

                // Capture values needed for background work
                var showHidden = ShowHiddenObjects;
                var metadataProvider = _metadataProvider;
                var options = _options;
                var sortKeyColumnsFirst = _sortKeyColumnsFirst;

                // Stage 3: Create table VMs (synchronously - faster than async overhead)
                stageStopwatch.Restart();
                LoadingMessage = $"Creating {totalTables} table views...";
                await Task.Yield(); // Allow UI to render progress
                
                // For large models (>50 tables), start with tables collapsed to improve rendering performance
                bool startCollapsed = totalTables > 50;
                
                var tableVms = new List<ModelDiagramTableViewModel>(totalTables);
                for (int i = 0; i < visibleTables.Count; i++)
                {
                    var table = visibleTables[i];
                    var tableVm = new ModelDiagramTableViewModel(table, showHidden, metadataProvider, options, sortKeyColumnsFirst);
                    if (startCollapsed)
                    {
                        tableVm.IsCollapsed = true;
                    }
                    tableVms.Add(tableVm);
                }

                var tablesTime = stageStopwatch.ElapsedMilliseconds;

                // Add all tables to collection - suppress notifications during bulk add
                // WPF rendering happens after this method completes
                stageStopwatch.Restart();
                LoadingMessage = $"Adding {tableVms.Count} tables to diagram...";
                await Task.Yield();
                
                Tables.IsNotifying = false;
                try
                {
                    Tables.AddRange(tableVms);
                }
                finally
                {
                    Tables.IsNotifying = true;
                    Tables.Refresh();
                }
                
                var addTablesTime = stageStopwatch.ElapsedMilliseconds;

                // Stage 4: Create relationships - fast enough to do on UI thread
                stageStopwatch.Restart();
                LoadingMessage = $"Processing {relationshipData.Count} relationships...";
                await Task.Yield(); // Allow UI to render progress

                // Build dictionaries for O(1) lookups
                var tableDict = tableVms.ToDictionary(t => t.TableName, StringComparer.OrdinalIgnoreCase);
                var columnDicts = new Dictionary<string, Dictionary<string, ModelDiagramColumnViewModel>>(StringComparer.OrdinalIgnoreCase);
                foreach (var tableVm in tableVms)
                {
                    columnDicts[tableVm.TableName] = tableVm.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
                }

                var relationshipVms = new List<ModelDiagramRelationshipViewModel>(relationshipData.Count / 2);
                var processedRelationships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (rel, fromTableName, fromColumn, toTableName, toColumn) in relationshipData)
                {
                    var relKey = $"{fromTableName}|{fromColumn}|{toTableName}|{toColumn}";
                    if (processedRelationships.Contains(relKey)) continue;
                    processedRelationships.Add(relKey);

                    tableDict.TryGetValue(fromTableName, out var fromTableVm);
                    tableDict.TryGetValue(toTableName, out var toTableVm);

                    if (fromTableVm != null && toTableVm != null)
                    {
                        var relVm = new ModelDiagramRelationshipViewModel(rel, fromTableVm, toTableVm);
                        relationshipVms.Add(relVm);

                        if (columnDicts.TryGetValue(fromTableName, out var fromColDict) &&
                            fromColDict.TryGetValue(fromColumn, out var fromCol))
                        {
                            fromCol.IsRelationshipColumn = true;
                        }
                        if (columnDicts.TryGetValue(toTableName, out var toColDict) &&
                            toColDict.TryGetValue(toColumn, out var toCol))
                        {
                            toCol.IsRelationshipColumn = true;
                        }
                    }
                }

                var relationshipsTime = stageStopwatch.ElapsedMilliseconds;

                // Add relationships to collection
                stageStopwatch.Restart();
                LoadingMessage = "Adding relationships to diagram...";
                
                Relationships.IsNotifying = false;
                try
                {
                    Relationships.AddRange(relationshipVms);
                }
                finally
                {
                    Relationships.IsNotifying = true;
                    Relationships.Refresh();
                }
                
                // Calculate parallel offsets for relationships between the same table pairs
                CalculateParallelRelationshipOffsets();
                
                var addRelsTime = stageStopwatch.ElapsedMilliseconds;

                // Re-sort columns and recalculate collapsed heights now that IsRelationshipColumn is set
                stageStopwatch.Restart();
                foreach (var table in Tables)
                {
                    // Notify that KeyColumns may have changed (IsRelationshipColumn was just set)
                    table.NotifyKeyColumnsChanged();
                    
                    // Recalculate collapsed height now that we know the key columns
                    if (table.IsCollapsed)
                    {
                        table.RecalculateCollapsedHeight();
                    }
                }
                var resortTime = stageStopwatch.ElapsedMilliseconds;

                // Stage 5: Layout
                stageStopwatch.Restart();
                LoadingMessage = $"Calculating layout for {tableVms.Count} tables...";
                await Task.Yield(); // Allow UI to render progress

                // Try to load saved layout first
                // Pass startCollapsed to ensure we don't override the collapsed state for large models
                bool layoutLoaded = TryLoadSavedLayout(preserveCollapsedState: startCollapsed);

                const int LargeModelThreshold = 50;

                if (!layoutLoaded)
                {
                    if (tableVms.Count <= LargeModelThreshold)
                    {
                        // For smaller models, use Sugiyama algorithm (better hierarchy)
                        LayoutDiagramSugiyama();
                    }
                    else
                    {
                        // For larger models, use cluster-based algorithm (more compact)
                        var layoutData = CalculateLayoutPositions(tableVms, allRelationships);
                        ApplyLayoutPositions(layoutData);
                        UpdateAllRelationships();
                    }
                }

                var layoutTime = stageStopwatch.ElapsedMilliseconds;

                stopwatch.Stop();
                var totalTime = stopwatch.ElapsedMilliseconds;

                // Update stats for display - comprehensive breakdown
                LoadingStats = $"Loaded in {totalTime:N0}ms (Meta:{metadataTime}ms, Create:{tablesTime}ms, Add:{addTablesTime}ms, Rel:{relationshipsTime}ms, AddRel:{addRelsTime}ms, Sort:{resortTime}ms, Layout:{layoutTime}ms)";

                // Capture values for logging on UI thread
                var tableCount = Tables.Count;
                var relCount = Relationships.Count;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // Log timing info on UI thread (Serilog sink uses IoC which requires UI thread)
                    Log.Information("{class} {method} Loaded {tables} tables, {relationships} relationships in {time}ms " +
                        "(Metadata:{metaTime}ms, CreateTables:{tablesTime}ms, AddTables:{addTablesTime}ms, " +
                        "CreateRels:{relTime}ms, AddRels:{addRelsTime}ms, Resort:{resortTime}ms, Layout:{layoutTime}ms)",
                        nameof(ModelDiagramViewModel), nameof(LoadFromModelAsync),
                        tableCount, relCount, totalTime, 
                        metadataTime, tablesTime, addTablesTime, 
                        relationshipsTime, addRelsTime, resortTime, layoutTime);

                    NotifyOfPropertyChange(nameof(SummaryText));
                    NotifyOfPropertyChange(nameof(HasData));
                    NotifyOfPropertyChange(nameof(NoData));
                });
            }
            catch (Exception ex)
            {
                // Log on UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Log.Error(ex, "{class} {method} {message}", nameof(ModelDiagramViewModel), nameof(LoadFromModelAsync), ex.Message);
                });
            }
            finally
            {
                // Use low priority to set IsLoading=false AFTER WPF finishes rendering
                // This keeps the loading indicator visible during the rendering phase
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsLoading = false;
                    LoadingMessage = "Loading diagram...";
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        /// <summary>
        /// Calculates layout positions using ViewModels only (no ADOTabular objects needed).
        /// Used by LayoutDiagram() when recalculating layout from existing data.
        /// </summary>
        private Dictionary<string, (double X, double Y, double Width)> CalculateLayoutPositionsFromViewModels()
        {
            var positions = new Dictionary<string, (double X, double Y, double Width)>();
            var tableVms = Tables.ToList();
            
            if (tableVms.Count == 0) return positions;

            const double tableWidth = 200;
            const double tableHeight = 180;
            const double horizontalSpacing = 80;
            const double verticalSpacing = 100;
            const double padding = 50;
            const double clusterGap = 150;

            // Build relationship graph from Relationships ViewModels
            var tableSet = new HashSet<string>(tableVms.Select(t => t.TableName), StringComparer.OrdinalIgnoreCase);
            var neighbors = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var tableVmDict = tableVms.ToDictionary(t => t.TableName, StringComparer.OrdinalIgnoreCase);
            
            foreach (var table in tableVms)
            {
                neighbors[table.TableName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            // Build neighbor relationships from Relationships collection
            foreach (var rel in Relationships)
            {
                var fromTable = rel.FromTable;
                var toTable = rel.ToTable;
                
                if (string.IsNullOrEmpty(fromTable) || string.IsNullOrEmpty(toTable)) continue;
                
                if (tableSet.Contains(fromTable) && tableSet.Contains(toTable))
                {
                    neighbors[fromTable].Add(toTable);
                    neighbors[toTable].Add(fromTable);
                }
            }

            // Find connected components using BFS
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clusters = new List<List<ModelDiagramTableViewModel>>();
            
            foreach (var table in tableVms)
            {
                if (visited.Contains(table.TableName)) continue;
                
                var cluster = new List<ModelDiagramTableViewModel>();
                var queue = new Queue<string>();
                queue.Enqueue(table.TableName);
                visited.Add(table.TableName);
                
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (tableVmDict.TryGetValue(current, out var tableVm))
                    {
                        cluster.Add(tableVm);
                    }
                    
                    foreach (var neighbor in neighbors[current])
                    {
                        if (!visited.Contains(neighbor))
                        {
                            visited.Add(neighbor);
                            queue.Enqueue(neighbor);
                        }
                    }
                }
                
                clusters.Add(cluster);
            }

            // Sort clusters by size (largest first)
            clusters = clusters.OrderByDescending(c => c.Count).ToList();

            // Calculate target aspect ratio
            int totalTables = tableVms.Count;
            int targetCols = (int)Math.Ceiling(Math.Sqrt(totalTables * 1.5));
            targetCols = Math.Max(8, Math.Min(20, targetCols));

            double currentX = padding;
            double currentY = padding;
            double rowMaxHeight = 0;

            foreach (var cluster in clusters)
            {
                int clusterSize = cluster.Count;
                int clusterCols, clusterRows;
                
                if (clusterSize <= 3)
                {
                    clusterCols = clusterSize;
                    clusterRows = 1;
                }
                else if (clusterSize <= 12)
                {
                    clusterCols = (int)Math.Ceiling(Math.Sqrt(clusterSize));
                    clusterRows = (int)Math.Ceiling((double)clusterSize / clusterCols);
                }
                else
                {
                    clusterCols = Math.Min(6, (int)Math.Ceiling(Math.Sqrt(clusterSize)));
                    clusterRows = (int)Math.Ceiling((double)clusterSize / clusterCols);
                }

                double clusterWidth = clusterCols * (tableWidth + horizontalSpacing) - horizontalSpacing;
                double clusterHeight = clusterRows * (tableHeight + verticalSpacing) - verticalSpacing;

                if (currentX + clusterWidth > targetCols * (tableWidth + horizontalSpacing) && currentX > padding)
                {
                    currentX = padding;
                    currentY += rowMaxHeight + clusterGap;
                    rowMaxHeight = 0;
                }

                int col = 0;
                int row = 0;
                
                var sortedCluster = cluster
                    .OrderByDescending(t => neighbors[t.TableName].Count)
                    .ThenBy(t => t.TableName)
                    .ToList();

                foreach (var tableVm in sortedCluster)
                {
                    double x = currentX + col * (tableWidth + horizontalSpacing);
                    double y = currentY + row * (tableHeight + verticalSpacing);
                    
                    positions[tableVm.TableName] = (x, y, tableWidth);

                    col++;
                    if (col >= clusterCols)
                    {
                        col = 0;
                        row++;
                    }
                }

                currentX += clusterWidth + clusterGap;
                rowMaxHeight = Math.Max(rowMaxHeight, clusterHeight);
            }

            return positions;
        }

        /// <summary>
        /// Calculates layout positions on a background thread without touching UI.
        /// Uses a relationship-aware layout that groups connected tables into clusters.
        /// Returns a dictionary of table name to (X, Y, Width) positions.
        /// </summary>
        private Dictionary<string, (double X, double Y, double Width)> CalculateLayoutPositions(
            List<ModelDiagramTableViewModel> tableVms,
            List<(ADOTabularTable Table, List<ADOTabularRelationship> Relationships)> allRelationships)
        {
            var positions = new Dictionary<string, (double X, double Y, double Width)>();
            
            if (tableVms.Count == 0) return positions;

            const double tableWidth = 200;
            const double tableHeight = 180;
            const double horizontalSpacing = 80;
            const double verticalSpacing = 100;
            const double padding = 50;
            const double clusterGap = 150; // Extra gap between clusters

            // Build relationship graph from the pre-loaded relationship data
            var tableSet = new HashSet<string>(tableVms.Select(t => t.TableName), StringComparer.OrdinalIgnoreCase);
            var neighbors = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var tableVmDict = tableVms.ToDictionary(t => t.TableName, StringComparer.OrdinalIgnoreCase);
            
            foreach (var table in tableVms)
            {
                neighbors[table.TableName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            // Build neighbor relationships from pre-loaded relationship data
            var processedRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (table, relationships) in allRelationships)
            {
                foreach (var rel in relationships)
                {
                    var fromTable = rel.FromTable?.Name;
                    var toTable = rel.ToTable?.Name;
                    
                    if (string.IsNullOrEmpty(fromTable) || string.IsNullOrEmpty(toTable)) continue;
                    
                    var relKey = $"{fromTable}|{toTable}";
                    if (processedRels.Contains(relKey)) continue;
                    processedRels.Add(relKey);
                    processedRels.Add($"{toTable}|{fromTable}");
                    
                    if (tableSet.Contains(fromTable) && tableSet.Contains(toTable))
                    {
                        neighbors[fromTable].Add(toTable);
                        neighbors[toTable].Add(fromTable);
                    }
                }
            }

            // Find connected components using BFS
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clusters = new List<List<ModelDiagramTableViewModel>>();
            
            foreach (var table in tableVms)
            {
                if (visited.Contains(table.TableName)) continue;
                
                // BFS to find all tables in this connected component
                var cluster = new List<ModelDiagramTableViewModel>();
                var queue = new Queue<string>();
                queue.Enqueue(table.TableName);
                visited.Add(table.TableName);
                
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (tableVmDict.TryGetValue(current, out var tableVm))
                    {
                        cluster.Add(tableVm);
                    }
                    
                    foreach (var neighbor in neighbors[current])
                    {
                        if (!visited.Contains(neighbor))
                        {
                            visited.Add(neighbor);
                            queue.Enqueue(neighbor);
                        }
                    }
                }
                
                clusters.Add(cluster);
            }

            // Sort clusters by size (largest first) to place important groups prominently
            clusters = clusters.OrderByDescending(c => c.Count).ToList();

            // Calculate target aspect ratio - aim for something reasonably square
            int totalTables = tableVms.Count;
            int targetCols = (int)Math.Ceiling(Math.Sqrt(totalTables * 1.5)); // Slightly wider than square
            targetCols = Math.Max(8, Math.Min(20, targetCols)); // Between 8 and 20 columns

            double currentX = padding;
            double currentY = padding;
            double rowMaxHeight = 0;
            double maxX = 0;

            foreach (var cluster in clusters)
            {
                // Calculate cluster dimensions
                int clusterSize = cluster.Count;
                int clusterCols, clusterRows;
                
                if (clusterSize <= 3)
                {
                    // Small clusters: single row
                    clusterCols = clusterSize;
                    clusterRows = 1;
                }
                else if (clusterSize <= 12)
                {
                    // Medium clusters: roughly square
                    clusterCols = (int)Math.Ceiling(Math.Sqrt(clusterSize));
                    clusterRows = (int)Math.Ceiling((double)clusterSize / clusterCols);
                }
                else
                {
                    // Large clusters: limit width, allow more rows
                    clusterCols = Math.Min(6, (int)Math.Ceiling(Math.Sqrt(clusterSize)));
                    clusterRows = (int)Math.Ceiling((double)clusterSize / clusterCols);
                }

                double clusterWidth = clusterCols * (tableWidth + horizontalSpacing) - horizontalSpacing;
                double clusterHeight = clusterRows * (tableHeight + verticalSpacing) - verticalSpacing;

                // Check if this cluster fits in the current row
                if (currentX + clusterWidth > targetCols * (tableWidth + horizontalSpacing) && currentX > padding)
                {
                    // Move to next row
                    currentX = padding;
                    currentY += rowMaxHeight + clusterGap;
                    rowMaxHeight = 0;
                }

                // Place tables within this cluster
                int col = 0;
                int row = 0;
                
                // Sort tables within cluster: more connected tables first (central position)
                var sortedCluster = cluster
                    .OrderByDescending(t => neighbors[t.TableName].Count)
                    .ThenBy(t => t.TableName)
                    .ToList();

                foreach (var tableVm in sortedCluster)
                {
                    double x = currentX + col * (tableWidth + horizontalSpacing);
                    double y = currentY + row * (tableHeight + verticalSpacing);
                    
                    positions[tableVm.TableName] = (x, y, tableWidth);
                    maxX = Math.Max(maxX, x + tableWidth);

                    col++;
                    if (col >= clusterCols)
                    {
                        col = 0;
                        row++;
                    }
                }

                // Update position for next cluster
                currentX += clusterWidth + clusterGap;
                rowMaxHeight = Math.Max(rowMaxHeight, clusterHeight);
            }

            return positions;
        }

        /// <summary>
        /// Applies pre-calculated layout positions to tables.
        /// Must be called on UI thread.
        /// </summary>
        private void ApplyLayoutPositions(Dictionary<string, (double X, double Y, double Width)> positions)
        {
            double maxX = 0, maxY = 0;

            foreach (var table in Tables)
            {
                if (positions.TryGetValue(table.TableName, out var pos))
                {
                    table.X = pos.X;
                    table.Y = pos.Y;
                    table.Width = pos.Width;

                    maxX = Math.Max(maxX, pos.X + pos.Width);
                    maxY = Math.Max(maxY, pos.Y + 180); // Approximate height
                }
            }

            CanvasWidth = Math.Max(800, maxX + 50);
            CanvasHeight = Math.Max(600, maxY + 50);
        }

        /// <summary>
        /// Synchronously loads the diagram from an ADOTabularModel.
        /// Used for small models (<=20 tables) where async overhead isn't needed.
        /// </summary>
        private void LoadFromModelSync(ADOTabularModel model)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // IsLoading is already set by LoadFromModel() before calling this method

            try
            {
                _model = model;
                _currentModelKey = GenerateModelKey(model);
                Tables.Clear();
                Relationships.Clear();
                Annotations.Clear();

                // Create view models for tables
                foreach (var table in model.Tables)
                {
                    if (!ShowHiddenObjects && !table.IsVisible) continue;

                    var tableVm = new ModelDiagramTableViewModel(table, ShowHiddenObjects, _metadataProvider, _options, _sortKeyColumnsFirst);
                    Tables.Add(tableVm);
                }

                // Create a dictionary for O(1) table lookups (performance optimization for large models)
                var tableDict = Tables.ToDictionary(t => t.TableName, StringComparer.OrdinalIgnoreCase);

                // Create view models for relationships
                // Relationships are stored on individual tables, so we need to gather from all tables
                var processedRelationships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var table in model.Tables)
                {
                    foreach (var rel in table.Relationships)
                    {
                        // Create a unique key to avoid duplicates (relationships appear on both ends)
                        var relKey = $"{rel.FromTable?.Name}|{rel.FromColumn}|{rel.ToTable?.Name}|{rel.ToColumn}";
                        if (processedRelationships.Contains(relKey)) continue;
                        processedRelationships.Add(relKey);

                        tableDict.TryGetValue(rel.FromTable?.Name ?? "", out var fromTableVm);
                        tableDict.TryGetValue(rel.ToTable?.Name ?? "", out var toTableVm);

                        if (fromTableVm != null && toTableVm != null)
                        {
                            var relVm = new ModelDiagramRelationshipViewModel(rel, fromTableVm, toTableVm);
                            Relationships.Add(relVm);

                            // Mark columns as relationship columns
                            var fromCol = fromTableVm.Columns.FirstOrDefault(c => c.ColumnName == rel.FromColumn);
                            var toCol = toTableVm.Columns.FirstOrDefault(c => c.ColumnName == rel.ToColumn);
                            if (fromCol != null) fromCol.IsRelationshipColumn = true;
                            if (toCol != null) toCol.IsRelationshipColumn = true;
                        }
                    }
                }

                // Calculate parallel offsets for relationships between the same table pairs
                CalculateParallelRelationshipOffsets();

                // Try to load saved layout, otherwise use auto-layout
                if (!TryLoadSavedLayout())
                {
                    LayoutDiagram();
                }

                stopwatch.Stop();
                Log.Information("{class} {method} Loaded {tables} tables, {relationships} relationships in {time}ms",
                    nameof(ModelDiagramViewModel), nameof(LoadFromModelSync),
                    Tables.Count, Relationships.Count, stopwatch.ElapsedMilliseconds);

                LoadingStats = $"Loaded in {stopwatch.ElapsedMilliseconds}ms";

                NotifyOfPropertyChange(nameof(SummaryText));
                NotifyOfPropertyChange(nameof(HasData));
                NotifyOfPropertyChange(nameof(NoData));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{class} {method} {message}", nameof(ModelDiagramViewModel), nameof(LoadFromModelSync), ex.Message);
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
        /// Layouts the diagram using the appropriate algorithm based on model size.
        /// - For smaller models (≤50 tables): Sugiyama-style layered algorithm (better hierarchy visualization)
        /// - For larger models (>50 tables): Cluster-based compact algorithm (better space utilization)
        /// </summary>
        private void LayoutDiagram()
        {
            if (Tables.Count == 0) return;

            const int LargeModelThreshold = 50;

            if (Tables.Count <= LargeModelThreshold)
            {
                // Use Sugiyama-style algorithm for smaller models - better hierarchy
                LayoutDiagramSugiyama();
            }
            else
            {
                // Use cluster-based algorithm for larger models - more compact
                LayoutDiagramClustered();
            }
        }

        /// <summary>
        /// Layouts the diagram using Sugiyama-style layered algorithm.
        /// Better for smaller models where relationship hierarchy is important.
        /// </summary>
        private void LayoutDiagramSugiyama()
        {
            const double tableWidth = 200;
            const double tableHeight = 180;
            const double horizontalSpacing = 100;
            const double verticalSpacing = 120;
            const double padding = 50;

            // Step 1: Assign tables to layers based on longest path from root nodes
            var layers = AssignLayers();
            
            // Step 2: Order tables within each layer to minimize edge crossings
            MinimizeCrossings(layers);
            
            // Step 3: Assign X coordinates using barycenter method
            AssignCoordinates(layers, tableWidth, tableHeight, horizontalSpacing, verticalSpacing, padding);

            // Calculate canvas size to fit all tables
            var maxX = Tables.Any() ? Tables.Max(t => t.X + t.Width) : 100;
            var maxY = Tables.Any() ? Tables.Max(t => t.Y + t.Height) : 100;
            CanvasWidth = Math.Max(100, maxX + padding);
            CanvasHeight = Math.Max(100, maxY + padding);

            // Update relationship line positions
            foreach (var rel in Relationships)
            {
                rel.UpdatePath();
            }
        }

        /// <summary>
        /// Layouts the diagram using cluster-based algorithm.
        /// Better for larger models where compact layout is more important.
        /// </summary>
        private void LayoutDiagramClustered()
        {
            // Calculate positions using the cluster-based algorithm
            var positions = CalculateLayoutPositionsFromViewModels();
            
            // Apply positions
            ApplyLayoutPositions(positions);

            // Update relationship line positions
            foreach (var rel in Relationships)
            {
                rel.UpdatePath();
            }
        }

        /// <summary>
        /// Assigns tables to layers using longest-path layering (Sugiyama Step 2).
        /// The "one" side of relationships (dimensions) are placed above the "many" side (facts).
        /// </summary>
        private List<List<ModelDiagramTableViewModel>> AssignLayers()
        {
            var layers = new List<List<ModelDiagramTableViewModel>>();
            var tableLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            // Build directed adjacency: from "one" side to "many" side (dimension -> fact)
            var adjacencyToMany = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var adjacencyFromMany = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var table in Tables)
            {
                adjacencyToMany[table.TableName] = new List<string>();
                adjacencyFromMany[table.TableName] = new List<string>();
                inDegree[table.TableName] = 0;
            }
            
            // Direction: from "1" side (dimension) to "*" side (fact)
            foreach (var rel in Relationships)
            {
                string fromTable, toTable;
                
                // Determine direction based on cardinality
                // "1" side points to "*" side (filter flows from one to many)
                if (rel.FromCardinality == "1" && (rel.ToCardinality == "*" || rel.ToCardinality == "M"))
                {
                    fromTable = rel.FromTable;
                    toTable = rel.ToTable;
                }
                else if (rel.ToCardinality == "1" && (rel.FromCardinality == "*" || rel.FromCardinality == "M"))
                {
                    fromTable = rel.ToTable;
                    toTable = rel.FromTable;
                }
                else
                {
                    // Same cardinality on both sides - use alphabetical order for consistency
                    if (string.Compare(rel.FromTable, rel.ToTable, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        fromTable = rel.FromTable;
                        toTable = rel.ToTable;
                    }
                    else
                    {
                        fromTable = rel.ToTable;
                        toTable = rel.FromTable;
                    }
                }
                
                if (adjacencyToMany.ContainsKey(fromTable) && adjacencyFromMany.ContainsKey(toTable))
                {
                    if (!adjacencyToMany[fromTable].Contains(toTable))
                    {
                        adjacencyToMany[fromTable].Add(toTable);
                        adjacencyFromMany[toTable].Add(fromTable);
                        inDegree[toTable]++;
                    }
                }
            }
            
            // Find root nodes (tables with no incoming edges - top-level dimensions)
            var rootNodes = Tables.Where(t => inDegree[t.TableName] == 0).ToList();
            
            // If no clear roots (cyclic), pick tables with most outgoing edges as roots
            if (!rootNodes.Any())
            {
                rootNodes = Tables
                    .OrderByDescending(t => adjacencyToMany[t.TableName].Count)
                    .ThenBy(t => adjacencyFromMany[t.TableName].Count)
                    .Take(Math.Max(1, Tables.Count / 3))
                    .ToList();
            }
            
            // Assign layers using BFS from root nodes (topological ordering)
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(ModelDiagramTableViewModel table, int layer)>();
            
            foreach (var root in rootNodes)
            {
                queue.Enqueue((root, 0));
                visited.Add(root.TableName);
            }
            
            // Process tables level by level
            while (queue.Count > 0)
            {
                var (table, layer) = queue.Dequeue();
                
                // Ensure layer exists
                while (layers.Count <= layer)
                    layers.Add(new List<ModelDiagramTableViewModel>());
                
                layers[layer].Add(table);
                tableLayer[table.TableName] = layer;
                
                // Add connected tables at next layer
                foreach (var childName in adjacencyToMany[table.TableName])
                {
                    if (!visited.Contains(childName))
                    {
                        visited.Add(childName);
                        var childTable = Tables.FirstOrDefault(t => t.TableName.Equals(childName, StringComparison.OrdinalIgnoreCase));
                        if (childTable != null)
                        {
                            queue.Enqueue((childTable, layer + 1));
                        }
                    }
                }
            }
            
            // Add any remaining unvisited tables (disconnected components) to a new layer
            var unvisited = Tables.Where(t => !visited.Contains(t.TableName)).ToList();
            if (unvisited.Any())
            {
                layers.Add(unvisited);
            }
            
            return layers;
        }

        /// <summary>
        /// Minimizes edge crossings using barycenter heuristic (Sugiyama Step 4).
        /// Multiple passes: sweep down then up repeatedly.
        /// </summary>
        private void MinimizeCrossings(List<List<ModelDiagramTableViewModel>> layers)
        {
            if (layers.Count < 2) return;
            
            // Build adjacency map
            var neighbors = BuildNeighborMap();
            
            // Multiple passes of crossing minimization using barycenter heuristic
            const int maxIterations = 4;
            
            for (int iter = 0; iter < maxIterations; iter++)
            {
                // Downward sweep: order layer i based on positions in layer i-1
                for (int i = 1; i < layers.Count; i++)
                {
                    OrderLayerByBarycenter(layers[i], layers[i - 1], neighbors);
                }
                
                // Upward sweep: order layer i based on positions in layer i+1
                for (int i = layers.Count - 2; i >= 0; i--)
                {
                    OrderLayerByBarycenter(layers[i], layers[i + 1], neighbors);
                }
            }
            
            // Post-processing: swap adjacent tables if it reduces crossings
            OptimizeCrossingsWithSwaps(layers, neighbors);
        }

        /// <summary>
        /// Post-processing step to reduce edge crossings by swapping adjacent tables within layers.
        /// </summary>
        private void OptimizeCrossingsWithSwaps(List<List<ModelDiagramTableViewModel>> layers, 
            Dictionary<string, HashSet<string>> neighbors)
        {
            bool improved = true;
            int maxPasses = 3;
            int pass = 0;
            
            while (improved && pass < maxPasses)
            {
                improved = false;
                pass++;
                
                // Try swapping adjacent pairs in each layer
                for (int layerIdx = 0; layerIdx < layers.Count; layerIdx++)
                {
                    var layer = layers[layerIdx];
                    
                    // Get adjacent layers for crossing calculation
                    var upperLayer = layerIdx > 0 ? layers[layerIdx - 1] : null;
                    var lowerLayer = layerIdx < layers.Count - 1 ? layers[layerIdx + 1] : null;
                    
                    // Try swapping each adjacent pair
                    for (int i = 0; i < layer.Count - 1; i++)
                    {
                        int currentCrossings = CountCrossingsForPair(layer, i, i + 1, upperLayer, lowerLayer, neighbors);
                        
                        // Swap and count
                        var temp = layer[i];
                        layer[i] = layer[i + 1];
                        layer[i + 1] = temp;
                        
                        int swappedCrossings = CountCrossingsForPair(layer, i, i + 1, upperLayer, lowerLayer, neighbors);
                        
                        if (swappedCrossings < currentCrossings)
                        {
                            // Keep the swap
                            improved = true;
                        }
                        else
                        {
                            // Revert the swap
                            temp = layer[i];
                            layer[i] = layer[i + 1];
                            layer[i + 1] = temp;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Counts edge crossings involving two specific positions in a layer.
        /// </summary>
        private int CountCrossingsForPair(List<ModelDiagramTableViewModel> layer, int pos1, int pos2,
            List<ModelDiagramTableViewModel> upperLayer, List<ModelDiagramTableViewModel> lowerLayer,
            Dictionary<string, HashSet<string>> neighbors)
        {
            int crossings = 0;
            
            var table1 = layer[pos1];
            var table2 = layer[pos2];
            
            // Check crossings with upper layer
            if (upperLayer != null)
            {
                crossings += CountCrossingsBetweenTables(table1, pos1, table2, pos2, upperLayer, neighbors);
            }
            
            // Check crossings with lower layer
            if (lowerLayer != null)
            {
                crossings += CountCrossingsBetweenTables(table1, pos1, table2, pos2, lowerLayer, neighbors);
            }
            
            return crossings;
        }

        /// <summary>
        /// Counts crossings between edges from two tables to an adjacent layer.
        /// </summary>
        private int CountCrossingsBetweenTables(ModelDiagramTableViewModel table1, int pos1,
            ModelDiagramTableViewModel table2, int pos2,
            List<ModelDiagramTableViewModel> adjacentLayer,
            Dictionary<string, HashSet<string>> neighbors)
        {
            int crossings = 0;
            
            // Get neighbors of table1 in adjacent layer
            var neighbors1 = new List<int>();
            if (neighbors.TryGetValue(table1.TableName, out var n1))
            {
                for (int i = 0; i < adjacentLayer.Count; i++)
                {
                    if (n1.Contains(adjacentLayer[i].TableName))
                        neighbors1.Add(i);
                }
            }
            
            // Get neighbors of table2 in adjacent layer
            var neighbors2 = new List<int>();
            if (neighbors.TryGetValue(table2.TableName, out var n2))
            {
                for (int i = 0; i < adjacentLayer.Count; i++)
                {
                    if (n2.Contains(adjacentLayer[i].TableName))
                        neighbors2.Add(i);
                }
            }
            
            // Count crossings: edge from table1 (at pos1) to adj[i] crosses edge from table2 (at pos2) to adj[j]
            // if (pos1 < pos2 and i > j) or (pos1 > pos2 and i < j)
            foreach (int adjPos1 in neighbors1)
            {
                foreach (int adjPos2 in neighbors2)
                {
                    // Crossing occurs when the relative order is different
                    if ((pos1 < pos2 && adjPos1 > adjPos2) || (pos1 > pos2 && adjPos1 < adjPos2))
                    {
                        crossings++;
                    }
                }
            }
            
            return crossings;
        }

        /// <summary>
        /// Orders a layer based on the average position of neighbors in the reference layer (barycenter).
        /// </summary>
        private void OrderLayerByBarycenter(List<ModelDiagramTableViewModel> layer, 
            List<ModelDiagramTableViewModel> referenceLayer, 
            Dictionary<string, HashSet<string>> neighbors)
        {
            // Calculate barycenter for each table in the layer
            var barycenters = new Dictionary<ModelDiagramTableViewModel, double>();
            
            for (int i = 0; i < referenceLayer.Count; i++)
            {
                // Position index of reference table
                var refTable = referenceLayer[i];
                if (!neighbors.ContainsKey(refTable.TableName)) continue;
                
                foreach (var neighborName in neighbors[refTable.TableName])
                {
                    var neighborTable = layer.FirstOrDefault(t => t.TableName.Equals(neighborName, StringComparison.OrdinalIgnoreCase));
                    if (neighborTable != null)
                    {
                        if (!barycenters.ContainsKey(neighborTable))
                            barycenters[neighborTable] = 0;
                        barycenters[neighborTable] += i;
                    }
                }
            }
            
            // Normalize by number of neighbors
            foreach (var table in layer)
            {
                if (barycenters.ContainsKey(table) && neighbors.ContainsKey(table.TableName))
                {
                    int neighborCount = neighbors[table.TableName].Count(n => 
                        referenceLayer.Any(r => r.TableName.Equals(n, StringComparison.OrdinalIgnoreCase)));
                    if (neighborCount > 0)
                        barycenters[table] /= neighborCount;
                }
                else
                {
                    // Tables with no neighbors get a high barycenter (placed at end)
                    barycenters[table] = double.MaxValue / 2;
                }
            }
            
            // Sort layer by barycenter
            var sorted = layer.OrderBy(t => barycenters.TryGetValue(t, out var bc) ? bc : double.MaxValue / 2)
                              .ThenBy(t => t.TableName)
                              .ToList();
            
            layer.Clear();
            layer.AddRange(sorted);
        }

        /// <summary>
        /// Builds a map of table name to all connected table names.
        /// </summary>
        private Dictionary<string, HashSet<string>> BuildNeighborMap()
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var table in Tables)
            {
                map[table.TableName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            
            foreach (var rel in Relationships)
            {
                if (map.ContainsKey(rel.FromTable))
                    map[rel.FromTable].Add(rel.ToTable);
                if (map.ContainsKey(rel.ToTable))
                    map[rel.ToTable].Add(rel.FromTable);
            }
            
            return map;
        }

        /// <summary>
        /// Assigns X coordinates to tables, trying to align connected tables (Sugiyama Step 5).
        /// Uses barycenter alignment with priority to minimize total edge length.
        /// </summary>
        private void AssignCoordinates(List<List<ModelDiagramTableViewModel>> layers,
            double tableWidth, double tableHeight, double hSpacing, double vSpacing, double padding)
        {
            var neighbors = BuildNeighborMap();
            
            // First pass: simple left-to-right placement
            for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                var layer = layers[layerIndex];
                double y = padding + layerIndex * (tableHeight + vSpacing);
                double x = padding;
                
                foreach (var table in layer)
                {
                    table.X = x;
                    table.Y = y;
                    table.Width = tableWidth;
                    table.Height = tableHeight;
                    x += tableWidth + hSpacing;
                }
            }
            
            // Second pass: adjust X positions based on connected tables (Brandes-Köpf inspired)
            // Pull tables toward their connected tables in adjacent layers
            const int refinementIterations = 3;
            
            for (int iter = 0; iter < refinementIterations; iter++)
            {
                // Downward pass: align with upper neighbors
                for (int layerIndex = 1; layerIndex < layers.Count; layerIndex++)
                {
                    AdjustLayerPositions(layers[layerIndex], layers[layerIndex - 1], neighbors, tableWidth, hSpacing, padding);
                }
                
                // Upward pass: align with lower neighbors
                for (int layerIndex = layers.Count - 2; layerIndex >= 0; layerIndex--)
                {
                    AdjustLayerPositions(layers[layerIndex], layers[layerIndex + 1], neighbors, tableWidth, hSpacing, padding);
                }
            }
            
            // Final centering: center all layers relative to the widest layer
            CenterAllLayers(layers, tableWidth, hSpacing, padding);
        }

        /// <summary>
        /// Adjusts positions of tables in a layer to align with connected tables.
        /// </summary>
        private void AdjustLayerPositions(List<ModelDiagramTableViewModel> layer,
            List<ModelDiagramTableViewModel> referenceLayer,
            Dictionary<string, HashSet<string>> neighbors,
            double tableWidth, double hSpacing, double padding)
        {
            // Calculate target X for each table based on average neighbor position
            var targetX = new Dictionary<ModelDiagramTableViewModel, double>();
            
            foreach (var table in layer)
            {
                if (neighbors.TryGetValue(table.TableName, out var tableNeighbors))
                {
                    var connectedInRef = referenceLayer
                        .Where(r => tableNeighbors.Contains(r.TableName))
                        .ToList();
                    
                    if (connectedInRef.Any())
                    {
                        // Target is average center X of connected tables
                        targetX[table] = connectedInRef.Average(t => t.X + tableWidth / 2) - tableWidth / 2;
                    }
                }
            }
            
            // Sort layer by current X position
            var sortedLayer = layer.OrderBy(t => t.X).ToList();
            
            // Adjust positions while maintaining order and minimum spacing
            for (int i = 0; i < sortedLayer.Count; i++)
            {
                var table = sortedLayer[i];
                double minX = (i == 0) ? padding : sortedLayer[i - 1].X + tableWidth + hSpacing;
                
                if (targetX.TryGetValue(table, out double target))
                {
                    // Move toward target, but not past minimum
                    double newX = Math.Max(minX, target);
                    
                    // Don't move too far (dampen to avoid oscillation)
                    double maxMove = (tableWidth + hSpacing) / 2;
                    double move = Math.Min(Math.Abs(newX - table.X), maxMove);
                    if (newX > table.X)
                        table.X = Math.Max(minX, table.X + move);
                    else if (newX < table.X)
                        table.X = Math.Max(minX, table.X - move);
                }
                else
                {
                    // No target - just ensure minimum spacing
                    if (table.X < minX)
                        table.X = minX;
                }
            }
        }

        /// <summary>
        /// Centers all layers relative to the widest layer.
        /// </summary>
        private void CenterAllLayers(List<List<ModelDiagramTableViewModel>> layers,
            double tableWidth, double hSpacing, double padding)
        {
            if (!layers.Any()) return;
            
            // Find the widest layer
            double maxWidth = 0;
            foreach (var layer in layers)
            {
                if (layer.Any())
                {
                    double layerWidth = layer.Max(t => t.X + tableWidth) - layer.Min(t => t.X);
                    maxWidth = Math.Max(maxWidth, layerWidth);
                }
            }
            
            // Center each layer
            foreach (var layer in layers)
            {
                if (!layer.Any()) continue;
                
                double layerWidth = layer.Max(t => t.X + tableWidth) - layer.Min(t => t.X);
                double offset = (maxWidth - layerWidth) / 2;
                double minX = layer.Min(t => t.X);
                double targetMinX = padding + offset;
                double shift = targetMinX - minX;
                
                foreach (var table in layer)
                {
                    table.X += shift;
                }
            }
        }

        /// <summary>
        /// Re-layouts the diagram (can be called after table positions are changed).
        /// </summary>
        public void RefreshLayout()
        {
            // Update relationship visibility based on table visibility
            UpdateRelationshipVisibility();
            
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
        /// Updates relationship visibility based on connected table visibility.
        /// Relationships are hidden if either connected table is hidden.
        /// </summary>
        private void UpdateRelationshipVisibility()
        {
            foreach (var rel in Relationships)
            {
                bool fromVisible = rel.FromTableViewModel != null && !rel.FromTableViewModel.IsHidden;
                bool toVisible = rel.ToTableViewModel != null && !rel.ToTableViewModel.IsHidden;
                rel.IsVisible = fromVisible && toVisible;
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
        /// Zooms to fit only selected tables in the view.
        /// </summary>
        public void ZoomToSelection()
        {
            var tablesToFit = SelectedTables.Count > 0 ? SelectedTables.ToList() : (_selectedTable != null ? new List<ModelDiagramTableViewModel> { _selectedTable } : null);
            if (tablesToFit == null || tablesToFit.Count == 0) return;

            var minX = tablesToFit.Min(t => t.X);
            var minY = tablesToFit.Min(t => t.Y);
            var maxX = tablesToFit.Max(t => t.X + t.Width);
            var maxY = tablesToFit.Max(t => t.Y + t.Height);

            var contentWidth = maxX - minX + 80;
            var contentHeight = maxY - minY + 80;

            var scaleX = ViewWidth / contentWidth;
            var scaleY = ViewHeight / contentHeight;
            var newScale = Math.Min(scaleX, scaleY) * 0.9; // 90% to add some margin

            Scale = Math.Max(0.1, Math.Min(2.0, newScale));
            
            // Request scroll to the selection center (handled by view)
            OnScrollToRequested?.Invoke(minX + contentWidth / 2 - 40, minY + contentHeight / 2 - 40);
        }

        /// <summary>
        /// Event raised when scrolling to a position is requested.
        /// </summary>
        public event Action<double, double> OnScrollToRequested;

        /// <summary>
        /// Resets zoom to 100%.
        /// </summary>
        public void ResetZoom()
        {
            Scale = 1.0;
        }

        /// <summary>
        /// Zooms in by 10%.
        /// </summary>
        public void ZoomIn()
        {
            Scale = Math.Min(2.0, Scale + 0.1);
        }

        /// <summary>
        /// Zooms out by 10%.
        /// </summary>
        public void ZoomOut()
        {
            Scale = Math.Max(0.1, Scale - 0.1);
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
            SaveLayoutForUndo();
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
            NotifyOfPropertyChange(nameof(HasSelection));
            NotifyOfPropertyChange(nameof(CanHighlightPath));
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
            NotifyOfPropertyChange(nameof(HasSelection));
            NotifyOfPropertyChange(nameof(CanHighlightPath));
            UpdateRelationshipHighlighting();
        }

        private ModelDiagramRelationshipViewModel _selectedRelationship;
        /// <summary>
        /// The currently selected relationship.
        /// </summary>
        public ModelDiagramRelationshipViewModel SelectedRelationship
        {
            get => _selectedRelationship;
            set
            {
                if (_selectedRelationship != null)
                {
                    _selectedRelationship.IsSelected = false;
                }
                _selectedRelationship = value;
                if (_selectedRelationship != null)
                {
                    _selectedRelationship.IsSelected = true;
                }
                NotifyOfPropertyChange();
            }
        }

        /// <summary>
        /// Selects a relationship and highlights the connected tables and columns.
        /// </summary>
        public void SelectRelationship(ModelDiagramRelationshipViewModel relationship)
        {
            // Clear table selection and column highlighting
            ClearSelection();
            ClearColumnHighlighting();
            
            // Select the relationship
            SelectedRelationship = relationship;
            
            if (relationship != null)
            {
                // Highlight the connected tables
                foreach (var table in Tables)
                {
                    var isConnected = table.TableName == relationship.FromTable || table.TableName == relationship.ToTable;
                    table.IsDimmed = !isConnected;
                    
                    // Highlight the specific columns used in this relationship
                    if (table.TableName == relationship.FromTable)
                    {
                        var col = FindColumnByName(table, relationship.FromColumn);
                        if (col != null) col.IsHighlighted = true;
                    }
                    // Note: Don't use else - a self-join would have both columns in the same table
                    if (table.TableName == relationship.ToTable)
                    {
                        var col = FindColumnByName(table, relationship.ToColumn);
                        if (col != null) col.IsHighlighted = true;
                    }
                }
                
                // Highlight this relationship, dim others
                foreach (var rel in Relationships)
                {
                    rel.IsHighlighted = rel == relationship;
                    rel.IsDimmed = rel != relationship;
                }
            }
        }

        /// <summary>
        /// Clears highlighting from all columns.
        /// </summary>
        private void ClearColumnHighlighting()
        {
            foreach (var table in Tables)
            {
                foreach (var col in table.Columns)
                {
                    col.IsHighlighted = false;
                }
            }
        }

        /// <summary>
        /// Finds a column in a table by name, trying multiple matching strategies.
        /// Handles differences between internal names (with underscores) and display names (with spaces).
        /// </summary>
        private ModelDiagramColumnViewModel FindColumnByName(ModelDiagramTableViewModel table, string columnName)
        {
            if (table == null || string.IsNullOrEmpty(columnName)) return null;
            
            // Try exact match on ColumnName first
            var col = table.Columns.FirstOrDefault(c => 
                string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
            if (col != null) return col;
            
            // Try matching on Caption
            col = table.Columns.FirstOrDefault(c => 
                string.Equals(c.Caption, columnName, StringComparison.OrdinalIgnoreCase));
            if (col != null) return col;
            
            // Try with underscores replaced by spaces (internal name -> display name)
            var nameWithSpaces = columnName.Replace("_", " ");
            col = table.Columns.FirstOrDefault(c => 
                string.Equals(c.ColumnName, nameWithSpaces, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Caption, nameWithSpaces, StringComparison.OrdinalIgnoreCase));
            if (col != null) return col;
            
            // Try with spaces replaced by underscores (display name -> internal name)
            var nameWithUnderscores = columnName.Replace(" ", "_");
            col = table.Columns.FirstOrDefault(c => 
                string.Equals(c.ColumnName, nameWithUnderscores, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Caption, nameWithUnderscores, StringComparison.OrdinalIgnoreCase));
            
            return col;
        }

        /// <summary>
        /// Highlights the columns involved in a relationship when hovering over it.
        /// Does not select the relationship or dim other tables.
        /// </summary>
        public void HoverRelationship(ModelDiagramRelationshipViewModel relationship)
        {
            // Don't change hover highlighting if a relationship is already selected
            if (SelectedRelationship != null) return;
            
            ClearColumnHighlighting();
            
            if (relationship != null)
            {
                // Highlight the specific columns used in this relationship
                foreach (var table in Tables)
                {
                    if (table.TableName == relationship.FromTable)
                    {
                        var col = FindColumnByName(table, relationship.FromColumn);
                        if (col != null) col.IsHighlighted = true;
                    }
                    // Note: Don't use else - a self-join would have both columns in the same table
                    if (table.TableName == relationship.ToTable)
                    {
                        var col = FindColumnByName(table, relationship.ToColumn);
                        if (col != null) col.IsHighlighted = true;
                    }
                }
            }
        }

        /// <summary>
        /// Clears column highlighting when mouse leaves a relationship.
        /// Only clears if no relationship is selected.
        /// </summary>
        public void UnhoverRelationship()
        {
            // Don't clear highlighting if a relationship is selected
            if (SelectedRelationship != null) return;
            
            ClearColumnHighlighting();
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
            SelectedRelationship = null; // Also clear relationship selection
            ClearColumnHighlighting(); // Clear column highlighting
            NotifyOfPropertyChange(nameof(SelectedTable));
            NotifyOfPropertyChange(nameof(HasMultipleSelection));
            NotifyOfPropertyChange(nameof(HasSelection));
            NotifyOfPropertyChange(nameof(CanHighlightPath));
            UpdateRelationshipHighlighting(); // Reset all dimming
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
            NotifyOfPropertyChange(nameof(HasSelection));
            NotifyOfPropertyChange(nameof(CanHighlightPath));
        }

        /// <summary>
        /// Nudges selected tables by the specified delta.
        /// </summary>
        public void NudgeSelectedTables(double deltaX, double deltaY)
        {
            if (SelectedTables.Count == 0 && _selectedTable != null)
            {
                _selectedTable.X = Math.Max(0, _selectedTable.X + deltaX);
                _selectedTable.Y = Math.Max(0, _selectedTable.Y + deltaY);
            }
            else
            {
                foreach (var table in SelectedTables)
                {
                    table.X = Math.Max(0, table.X + deltaX);
                    table.Y = Math.Max(0, table.Y + deltaY);
                }
            }
            RefreshLayout();
            SaveCurrentLayout();
        }

        /// <summary>
        /// Hides all selected tables from the diagram.
        /// </summary>
        public void HideSelectedTables()
        {
            var tablesToHide = SelectedTables.Count > 0 
                ? SelectedTables.ToList() 
                : (_selectedTable != null ? new List<ModelDiagramTableViewModel> { _selectedTable } : new List<ModelDiagramTableViewModel>());
            
            foreach (var table in tablesToHide)
            {
                table.IsHidden = true;
            }
            
            ClearSelection();
            RefreshLayout();
        }

        /// <summary>
        /// Hides a single table from the diagram.
        /// </summary>
        public void HideTable(ModelDiagramTableViewModel table)
        {
            if (table != null)
            {
                table.IsHidden = true;
                
                // Deselect if selected
                if (_selectedTable == table)
                {
                    _selectedTable = null;
                    NotifyOfPropertyChange(nameof(SelectedTable));
                }
                SelectedTables.Remove(table);
                
                RefreshLayout();
            }
        }

        /// <summary>
        /// Shows tables that are directly related to the specified table.
        /// </summary>
        public void ShowRelatedTables(ModelDiagramTableViewModel table)
        {
            if (table == null) return;

            // Find all tables connected via relationships
            var relatedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            relatedTables.Add(table.TableName);

            foreach (var rel in Relationships)
            {
                if (string.Equals(rel.FromTable, table.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    relatedTables.Add(rel.ToTable ?? "");
                }
                else if (string.Equals(rel.ToTable, table.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    relatedTables.Add(rel.FromTable ?? "");
                }
            }

            // Show all related tables
            foreach (var t in Tables)
            {
                if (relatedTables.Contains(t.TableName))
                {
                    t.IsHidden = false;
                }
            }

            RefreshLayout();
        }

        /// <summary>
        /// Hides all tables that are NOT selected, keeping only the selected tables visible.
        /// </summary>
        public void HideNonSelectedTables()
        {
            var selectedTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            // Collect selected table names
            if (SelectedTables.Count > 0)
            {
                foreach (var table in SelectedTables)
                {
                    selectedTableNames.Add(table.TableName);
                }
            }
            else if (_selectedTable != null)
            {
                selectedTableNames.Add(_selectedTable.TableName);
            }
            
            // Hide all non-selected tables
            if (selectedTableNames.Count > 0)
            {
                foreach (var table in Tables)
                {
                    if (!selectedTableNames.Contains(table.TableName))
                    {
                        table.IsHidden = true;
                    }
                }
                
                ClearSelection();
                RefreshLayout();
            }
        }

        /// <summary>
        /// Shows all hidden tables.
        /// </summary>
        public void ShowAllTables()
        {
            foreach (var table in Tables)
            {
                table.IsHidden = false;
            }
            // Reset filter to "All Tables"
            _tableFilter = 0;
            NotifyOfPropertyChange(nameof(TableFilter));
            RefreshLayout();
        }

        #region Annotations

        private ModelDiagramAnnotationViewModel _selectedAnnotation;
        /// <summary>
        /// The currently selected annotation.
        /// </summary>
        public ModelDiagramAnnotationViewModel SelectedAnnotation
        {
            get => _selectedAnnotation;
            set
            {
                if (_selectedAnnotation != null)
                    _selectedAnnotation.IsSelected = false;
                _selectedAnnotation = value;
                if (_selectedAnnotation != null)
                    _selectedAnnotation.IsSelected = true;
                NotifyOfPropertyChange();
            }
        }

        /// <summary>
        /// Adds a new annotation at the specified position.
        /// </summary>
        public void AddAnnotation(double x, double y)
        {
            var annotation = new ModelDiagramAnnotationViewModel
            {
                X = x,
                Y = y,
                Text = "Double-click to edit",
                Width = 150,
                Height = 60
            };
            Annotations.Add(annotation);
            SelectedAnnotation = annotation;
            annotation.IsEditing = true;
        }

        /// <summary>
        /// Adds a new annotation at the center of the visible area.
        /// Called from context menu.
        /// </summary>
        public void AddAnnotationAtCenter()
        {
            // Place in visible area - use a reasonable default position
            double x = 100;
            double y = 100;
            
            // Try to offset from existing annotations to avoid overlap
            if (Annotations.Count > 0)
            {
                var lastAnnotation = Annotations.Last();
                x = lastAnnotation.X + 20;
                y = lastAnnotation.Y + 20;
            }
            
            AddAnnotation(x, y);
        }

        /// <summary>
        /// Deletes the selected annotation.
        /// </summary>
        public void DeleteSelectedAnnotation()
        {
            if (_selectedAnnotation != null)
            {
                Annotations.Remove(_selectedAnnotation);
                SelectedAnnotation = null;
            }
        }

        /// <summary>
        /// Deletes a specific annotation.
        /// </summary>
        public void DeleteAnnotation(ModelDiagramAnnotationViewModel annotation)
        {
            if (annotation != null)
            {
                if (_selectedAnnotation == annotation)
                    SelectedAnnotation = null;
                Annotations.Remove(annotation);
            }
        }

        /// <summary>
        /// Clears all annotations.
        /// </summary>
        public void ClearAllAnnotations()
        {
            SelectedAnnotation = null;
            Annotations.Clear();
        }

        /// <summary>
        /// Sets the background color of the specified annotation.
        /// </summary>
        public void SetAnnotationColor(ModelDiagramAnnotationViewModel annotation, string color)
        {
            if (annotation != null)
            {
                annotation.BackgroundColor = color;
                SaveLayoutAfterDrag();
            }
        }

        #endregion

        /// <summary>
        /// Highlights the path between two selected tables.
        /// Uses BFS to find the shortest relationship path.
        /// </summary>
        public void HighlightPath()
        {
            if (SelectedTables.Count != 2) return;
            
            var startTable = SelectedTables[0].TableName;
            var endTable = SelectedTables[1].TableName;
            
            // Build adjacency list
            var adjacency = new Dictionary<string, List<(string neighbor, ModelDiagramRelationshipViewModel rel)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in Tables)
            {
                adjacency[table.TableName] = new List<(string, ModelDiagramRelationshipViewModel)>();
            }
            foreach (var rel in Relationships)
            {
                if (adjacency.ContainsKey(rel.FromTable) && adjacency.ContainsKey(rel.ToTable))
                {
                    adjacency[rel.FromTable].Add((rel.ToTable, rel));
                    adjacency[rel.ToTable].Add((rel.FromTable, rel));
                }
            }
            
            // BFS to find shortest path
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string table, List<string> path, List<ModelDiagramRelationshipViewModel> rels)>();
            queue.Enqueue((startTable, new List<string> { startTable }, new List<ModelDiagramRelationshipViewModel>()));
            visited.Add(startTable);
            
            List<string> foundPath = null;
            List<ModelDiagramRelationshipViewModel> foundRels = null;
            
            while (queue.Count > 0)
            {
                var (current, path, rels) = queue.Dequeue();
                
                if (current.Equals(endTable, StringComparison.OrdinalIgnoreCase))
                {
                    foundPath = path;
                    foundRels = rels;
                    break;
                }
                
                foreach (var (neighbor, rel) in adjacency[current])
                {
                    if (!visited.Contains(neighbor))
                    {
                        visited.Add(neighbor);
                        var newPath = new List<string>(path) { neighbor };
                        var newRels = new List<ModelDiagramRelationshipViewModel>(rels) { rel };
                        queue.Enqueue((neighbor, newPath, newRels));
                    }
                }
            }
            
            if (foundPath != null)
            {
                // Highlight the path tables and relationships
                var pathTables = new HashSet<string>(foundPath, StringComparer.OrdinalIgnoreCase);
                var pathRels = new HashSet<ModelDiagramRelationshipViewModel>(foundRels);
                
                foreach (var table in Tables)
                {
                    table.IsDimmed = !pathTables.Contains(table.TableName);
                    table.IsSelected = pathTables.Contains(table.TableName);
                }
                
                foreach (var rel in Relationships)
                {
                    rel.IsHighlighted = pathRels.Contains(rel);
                    rel.IsDimmed = !pathRels.Contains(rel);
                }
            }
        }

        /// <summary>
        /// Whether path highlighting is available (exactly 2 tables selected).
        /// </summary>
        public bool CanHighlightPath => SelectedTables.Count == 2;

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
            NotifyOfPropertyChange(nameof(HasSelection));
            NotifyOfPropertyChange(nameof(CanHighlightPath));
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
        /// Navigates to the selected table in the metadata pane.
        /// </summary>
        public void JumpToMetadataPane()
        {
            if (_selectedTable != null)
            {
                _eventAggregator.PublishOnUIThreadAsync(new NavigateToMetadataItemEvent(_selectedTable.TableName));
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
                    ),
                    Annotations = Annotations.Select(a => a.ToData()).ToList()
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
        private bool TryLoadSavedLayout(bool preserveCollapsedState = false)
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
                        
                        // If preserveCollapsedState is true (large model), keep tables collapsed
                        // Otherwise, restore from saved layout
                        if (preserveCollapsedState)
                        {
                            // Keep current collapsed state, just update position
                            // Height was already set when IsCollapsed was set
                        }
                        else
                        {
                            table.Height = pos.Height > 0 ? pos.Height : 180;
                            // Use SetCollapsedState to properly restore collapsed state with expanded height
                            if (pos.IsCollapsed)
                            {
                                table.SetCollapsedState(true, pos.ExpandedHeight > 0 ? pos.ExpandedHeight : pos.Height);
                            }
                        }
                        anyApplied = true;
                    }
                }

                // Restore annotations
                if (layoutData.Annotations != null && layoutData.Annotations.Count > 0)
                {
                    Annotations.Clear();
                    foreach (var annotData in layoutData.Annotations)
                    {
                        Annotations.Add(new ModelDiagramAnnotationViewModel(annotData));
                    }
                }

                if (anyApplied)
                {
                    // Calculate canvas size including annotations
                    var maxX = Tables.Max(t => t.X + t.Width);
                    var maxY = Tables.Max(t => t.Y + t.Height);
                    
                    if (Annotations.Count > 0)
                    {
                        maxX = Math.Max(maxX, Annotations.Max(a => a.X + a.Width));
                        maxY = Math.Max(maxY, Annotations.Max(a => a.Y + a.Height));
                    }
                    
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

        /// <summary>
        /// Event raised when copy image to clipboard is requested.
        /// The view handles the actual rendering.
        /// </summary>
        public event EventHandler CopyImageRequested;
        
        /// <summary>
        /// Copies the diagram as an image to the clipboard.
        /// </summary>
        public void CopyImageToClipboard()
        {
            CopyImageRequested?.Invoke(this, EventArgs.Empty);
        }

        #endregion
    }

    /// <summary>
    /// ViewModel for a table in the Model Diagram.
    /// </summary>
    public class ModelDiagramTableViewModel : PropertyChangedBase
    {
        private readonly ADOTabularTable _table;
        private readonly bool _showHiddenObjects;
        private readonly IMetadataProvider _metadataProvider;
        private readonly IGlobalOptions _options;
        private bool _sortKeyColumnsFirst = false;

        public ModelDiagramTableViewModel(ADOTabularTable table, bool showHiddenObjects, IMetadataProvider metadataProvider, IGlobalOptions options, bool sortKeyColumnsFirst = false)
        {
            _table = table;
            _showHiddenObjects = showHiddenObjects;
            _metadataProvider = metadataProvider;
            _options = options;
            _sortKeyColumnsFirst = sortKeyColumnsFirst;
            Columns = new BindableCollection<ModelDiagramColumnViewModel>(GetSortedColumns());
        }

        /// <summary>
        /// Gets columns sorted based on the current sort setting.
        /// Note: IsRelationshipColumn is NOT set here during initial load - it's set later
        /// by the relationship processing loop for much better performance.
        /// </summary>
        private IEnumerable<ModelDiagramColumnViewModel> GetSortedColumns()
        {
            var filtered = _table.Columns
                .Where(c => c.Contents != "RowNumber" && (_showHiddenObjects || c.IsVisible));

            IEnumerable<ADOTabularColumn> sorted;
            if (_sortKeyColumnsFirst)
            {
                // During initial load, we can't sort by relationship columns yet (not determined)
                // After relationships are processed, UpdateColumnSort can be called to re-sort
                sorted = filtered
                    .OrderBy(c => c.ObjectType == ADOTabularObjectType.Column ? 0 : 1) // Columns first, then measures
                    .ThenBy(c => c.Caption);
            }
            else
            {
                sorted = filtered
                    .OrderBy(c => c.ObjectType == ADOTabularObjectType.Column ? 0 : 1) // Columns first, then measures
                    .ThenBy(c => c.Caption);
            }

            // Create ViewModels - IsRelationshipColumn will be set later by relationship processing
            return sorted.Select(c => new ModelDiagramColumnViewModel(c, _metadataProvider, _options));
        }

        /// <summary>
        /// Checks if a column is used in a relationship (foreign key or referenced key).
        /// WARNING: This is expensive - iterates all relationships. Use sparingly.
        /// </summary>
        private bool IsRelationshipColumn(string columnName)
        {
            // Check if this column participates in any relationship (either side)
            return _table.Model?.Relationships?.Any(r => 
                (r.FromTable?.Name == _table.Name && r.FromColumn == columnName) ||
                (r.ToTable?.Name == _table.Name && r.ToColumn == columnName)) ?? false;
        }

        /// <summary>
        /// Updates the column sort order.
        /// </summary>
        public void UpdateColumnSort(bool sortKeyColumnsFirst)
        {
            _sortKeyColumnsFirst = sortKeyColumnsFirst;
            
            // Re-sort existing columns (preserves IsRelationshipColumn that was set during load)
            List<ModelDiagramColumnViewModel> sortedColumns;
            if (sortKeyColumnsFirst)
            {
                sortedColumns = Columns
                    .OrderBy(c => c.ObjectType == ADOTabularObjectType.Column ? 0 : 1)
                    .ThenByDescending(c => c.IsRelationshipColumn)
                    .ThenBy(c => c.Caption)
                    .ToList();
            }
            else
            {
                sortedColumns = Columns
                    .OrderBy(c => c.ObjectType == ADOTabularObjectType.Column ? 0 : 1)
                    .ThenBy(c => c.Caption)
                    .ToList();
            }
            
            // Clear and re-add to force UI update
            Columns.Clear();
            foreach (var col in sortedColumns)
            {
                Columns.Add(col);
            }
            
            NotifyOfPropertyChange(nameof(Columns));
            NotifyOfPropertyChange(nameof(KeyColumns));
        }

        public string TableName => _table.Name;
        public string Caption => _table.Caption;
        public string Description => _table.Description;
        public bool IsVisible => _table.IsVisible;
        public bool IsDateTable => _table.IsDateTable;
        public int ColumnCount => Columns.Count(c => c.ObjectType == ADOTabularObjectType.Column);
        public int MeasureCount => Columns.Count(c => c.ObjectType == ADOTabularObjectType.Measure);

        private bool _isHidden;
        /// <summary>
        /// Whether this table is manually hidden from the diagram (separate from model IsVisible).
        /// </summary>
        public bool IsHidden
        {
            get => _isHidden;
            set { _isHidden = value; NotifyOfPropertyChange(); }
        }

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
        /// Key and relationship columns (for collapsed view).
        /// Shows columns marked as keys OR columns used in relationships.
        /// </summary>
        public IEnumerable<ModelDiagramColumnViewModel> KeyColumns => Columns.Where(c => c.IsKey || c.IsRelationshipColumn);

        /// <summary>
        /// Whether this table has any key or relationship columns.
        /// </summary>
        public bool HasKeyColumns => Columns.Any(c => c.IsKey || c.IsRelationshipColumn);

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
        private const double CollapsedHeaderHeight = 40; // Height for just the header when collapsed
        private const double KeyColumnRowHeight = 18; // Height per key column row

        /// <summary>
        /// Calculate collapsed height based on header + key columns + relationship columns.
        /// </summary>
        private double CalculateCollapsedHeight()
        {
            // Count key columns AND relationship columns (shown in collapsed view)
            int keyCount = Columns.Count(c => c.IsKey || c.IsRelationshipColumn);
            if (keyCount == 0) return CollapsedHeaderHeight;
            return CollapsedHeaderHeight + 6 + (keyCount * KeyColumnRowHeight); // 6 = padding
        }

        /// <summary>
        /// Recalculates the collapsed height after IsRelationshipColumn has been set.
        /// Should be called after relationships are processed.
        /// </summary>
        public void RecalculateCollapsedHeight()
        {
            if (_isCollapsed)
            {
                Height = CalculateCollapsedHeight();
            }
        }

        /// <summary>
        /// Notifies that KeyColumns collection may have changed.
        /// Should be called after IsRelationshipColumn has been set on columns.
        /// </summary>
        public void NotifyKeyColumnsChanged()
        {
            NotifyOfPropertyChange(nameof(KeyColumns));
            NotifyOfPropertyChange(nameof(HasKeyColumns));
        }

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
                    Height = CalculateCollapsedHeight();
                }
                else
                {
                    // Expanding: restore saved height (or calculate default)
                    Height = _expandedHeight > CalculateCollapsedHeight() ? _expandedHeight : CalculateDefaultHeight();
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
            get => _expandedHeight > CalculateCollapsedHeight() ? _expandedHeight : Height;
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
        private readonly IMetadataProvider _metadataProvider;
        private readonly IGlobalOptions _options;
        private List<string> _sampleData;
        private bool _updatingSampleData;
        private bool _sampleDataLoadFailed; // Prevent retrying on error
        private const int SAMPLE_ROWS = 10;

        public ModelDiagramColumnViewModel(ADOTabularColumn column, IMetadataProvider metadataProvider, IGlobalOptions options)
        {
            _column = column;
            _metadataProvider = metadataProvider;
            _options = options;
            _sampleData = new List<string>();
        }

        public string ColumnName => _column.Name;
        public string Caption => _column.Caption;
        public string Description => _column.Description;
        public bool IsVisible => _column.IsVisible;
        public ADOTabularObjectType ObjectType => _column.ObjectType;
        public string DataTypeName => _column.DataTypeName;
        public bool IsKey => _column.IsKey;
        
        private bool _isRelationshipColumn;
        /// <summary>
        /// Whether this column is used in a relationship.
        /// Set by the parent table when relationships are loaded.
        /// </summary>
        public bool IsRelationshipColumn
        {
            get => _isRelationshipColumn;
            set { _isRelationshipColumn = value; NotifyOfPropertyChange(); }
        }

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
                // Trigger sample data loading if enabled and not already loaded/failed
                if (ShowSampleData && !HasSampleData && !_updatingSampleData && !_sampleDataLoadFailed)
                {
                    LoadSampleDataAsync();
                }

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
                
                // Add format string if available
                if (!string.IsNullOrEmpty(FormatString))
                    sb.AppendLine($"Format: {FormatString}");
                
                // Add statistics from metadata (like the metadata pane tooltip)
                if (!IsMeasure && !IsHierarchy)
                {
                    if (DistinctValues > 0)
                        sb.AppendLine($"Distinct Values: {DistinctValues:N0}");
                    
                    if (!string.IsNullOrEmpty(MinValue))
                        sb.AppendLine($"Min Value: {MinValue}");
                    
                    if (!string.IsNullOrEmpty(MaxValue))
                        sb.AppendLine($"Max Value: {MaxValue}");
                }
                
                // Add sample data if available and enabled
                if (ShowSampleData)
                {
                    if (_updatingSampleData)
                    {
                        sb.AppendLine();
                        sb.AppendLine("Sample Data: Loading...");
                    }
                    else if (HasSampleData)
                    {
                        sb.AppendLine();
                        sb.AppendLine("Sample Data:");
                        foreach (var sample in _sampleData)
                        {
                            sb.AppendLine($"  {sample}");
                        }
                    }
                }
                
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
        /// Whether to show sample data in the tooltip (based on options).
        /// For Model Diagram, we disable sample data loading to avoid performance issues
        /// and errors with calculated columns. Users can see sample data in the metadata pane.
        /// </summary>
        private bool ShowSampleData => false; // Disabled for Model Diagram - too many columns to query

        /// <summary>
        /// Whether sample data has been loaded.
        /// </summary>
        private bool HasSampleData => _sampleData != null && _sampleData.Count > 0;

        /// <summary>
        /// Asynchronously loads sample data for the column.
        /// This is intentionally async void as it's a fire-and-forget operation triggered by the Tooltip getter.
        /// Errors are caught and logged to prevent application crashes.
        /// </summary>
        private async void LoadSampleDataAsync()
        {
            // Skip sample data for hierarchies and measures - they don't have queryable data
            // Also skip if we already tried and failed (prevents retry loops)
            if (_metadataProvider == null || _updatingSampleData || _sampleDataLoadFailed || IsHierarchy || IsMeasure) return;
            
            _updatingSampleData = true;
            
            try
            {
                // Notify tooltip that we're loading (shows "Loading..." state)
                NotifyOfPropertyChange(nameof(Tooltip));
                
                var samples = await _metadataProvider.GetColumnSampleData(_column, SAMPLE_ROWS).ConfigureAwait(false);
                _sampleData = samples ?? new List<string>();
            }
            catch (Exception ex)
            {
                // Log but don't crash - sample data is not critical
                // Mark as failed to prevent retry loops on error columns
                Log.Warning(ex, "Error loading sample data for column {ColumnName}", ColumnName);
                _sampleData = new List<string>();
                _sampleDataLoadFailed = true;
            }
            finally
            {
                _updatingSampleData = false;
                // Marshal back to UI thread for property change notification
                await Application.Current.Dispatcher.InvokeAsync(() => NotifyOfPropertyChange(nameof(Tooltip)));
            }
        }

        // Expose additional column metadata properties for tooltip
        public string FormatString => _column.FormatString;
        public long DistinctValues => _column.DistinctValues;
        public string MinValue => _column.MinValue;
        public string MaxValue => _column.MaxValue;

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

        private bool _isHighlighted;
        /// <summary>
        /// Whether this column is highlighted (e.g., when its relationship is selected).
        /// </summary>
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set { _isHighlighted = value; NotifyOfPropertyChange(); }
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
        /// Font size for the "From" cardinality - larger for * to make it more visible
        /// </summary>
        public double FromCardinalityFontSize => FromCardinality == "*" ? 16 : 12;

        /// <summary>
        /// The "To" side cardinality symbol (1 or *)
        /// </summary>
        public string ToCardinality => _relationship.ToColumnMultiplicity == "*" ? "*" : "1";

        /// <summary>
        /// Font size for the "To" cardinality - larger for * to make it more visible
        /// </summary>
        public double ToCardinalityFontSize => ToCardinality == "*" ? 16 : 12;

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

        #region Visibility

        private bool _isVisible = true;
        /// <summary>
        /// Whether this relationship line should be visible (both connected tables are visible).
        /// </summary>
        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; NotifyOfPropertyChange(); }
        }

        #endregion

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

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; NotifyOfPropertyChange(); }
        }

        /// <summary>
        /// Tracks whether UpdatePath has been called at least once.
        /// </summary>
        private bool _isPathInitialized;

        /// <summary>
        /// Offset for parallel relationships between the same table pair.
        /// 0 = centered, negative = offset left/up, positive = offset right/down.
        /// </summary>
        private double _parallelOffset;
        public double ParallelOffset
        {
            get => _parallelOffset;
            set { _parallelOffset = value; NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(PathData)); }
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
                // Return empty if positions not initialized
                if (!HasValidPositions) return string.Empty;
                
                // Determine if this is a horizontal or vertical relationship
                bool isVertical = (_startEdge == EdgeType.Top || _startEdge == EdgeType.Bottom);
                
                // Apply parallel offset perpendicular to the line direction
                double offsetX = isVertical ? _parallelOffset : 0;
                double offsetY = isVertical ? 0 : _parallelOffset;
                
                double startX = StartX + offsetX;
                double startY = StartY + offsetY;
                double endX = EndX + offsetX;
                double endY = EndY + offsetY;
                
                if (isVertical)
                {
                    // Vertical bezier curve (for top/bottom connections)
                    double midY = (startY + endY) / 2;
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "M {0},{1} C {2},{3} {4},{5} {6},{7}",
                        startX, startY, startX, midY, endX, midY, endX, endY);
                }
                else
                {
                    // Horizontal bezier curve (for left/right connections)
                    double midX = (startX + endX) / 2;
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "M {0},{1} C {2},{3} {4},{5} {6},{7}",
                        startX, startY, midX, startY, midX, endY, endX, endY);
                }
            }
        }

        /// <summary>
        /// Label position (middle of the line), accounting for parallel offset.
        /// </summary>
        public double LabelX
        {
            get
            {
                bool isVertical = (_startEdge == EdgeType.Top || _startEdge == EdgeType.Bottom);
                double offset = isVertical ? _parallelOffset : 0;
                return (StartX + EndX) / 2 + offset;
            }
        }
        
        public double LabelY
        {
            get
            {
                bool isVertical = (_startEdge == EdgeType.Top || _startEdge == EdgeType.Bottom);
                double offset = isVertical ? 0 : _parallelOffset;
                return (StartY + EndY) / 2 + offset;
            }
        }

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
        /// Position for the "From" cardinality label (near start, offset based on edge type).
        /// </summary>
        public double FromCardinalityX
        {
            get
            {
                bool isVertical = (_startEdge == EdgeType.Top || _startEdge == EdgeType.Bottom);
                double parallelOffsetX = isVertical ? _parallelOffset : 0;
                
                // Position based on which edge the line exits from
                switch (_startEdge)
                {
                    case EdgeType.Top:
                    case EdgeType.Bottom:
                        // Vertical edge - position to the right of the connection point
                        return StartX + 4 + parallelOffsetX;
                    case EdgeType.Left:
                        // Left edge - position to the left
                        return StartX - 28;
                    case EdgeType.Right:
                        // Right edge - position to the right
                        return StartX + 6;
                    default:
                        return StartX + 5 + parallelOffsetX;
                }
            }
        }
        
        public double FromCardinalityY
        {
            get
            {
                bool isVertical = (_startEdge == EdgeType.Top || _startEdge == EdgeType.Bottom);
                double parallelOffsetY = isVertical ? 0 : _parallelOffset;
                
                // Position based on which edge the line exits from
                switch (_startEdge)
                {
                    case EdgeType.Top:
                        // Top edge - position above the connection point
                        return StartY - 28;
                    case EdgeType.Bottom:
                        // Bottom edge - position below the connection point
                        return StartY + 6;
                    case EdgeType.Left:
                    case EdgeType.Right:
                        // Horizontal edge - position above the line
                        return StartY - 12 + parallelOffsetY;
                    default:
                        return StartY - 12 + parallelOffsetY;
                }
            }
        }

        /// <summary>
        /// Position for the "To" cardinality label (near end, offset based on edge type).
        /// </summary>
        public double ToCardinalityX
        {
            get
            {
                bool isVertical = (_endEdge == EdgeType.Top || _endEdge == EdgeType.Bottom);
                double parallelOffsetX = isVertical ? _parallelOffset : 0;
                
                // Position based on which edge the line enters
                switch (_endEdge)
                {
                    case EdgeType.Top:
                    case EdgeType.Bottom:
                        // Vertical edge - position to the right of the connection point
                        return EndX + 4 + parallelOffsetX;
                    case EdgeType.Left:
                        // Left edge - position to the left
                        return EndX - 28;
                    case EdgeType.Right:
                        // Right edge - position to the right
                        return EndX + 6;
                    default:
                        return EndX + 5 + parallelOffsetX;
                }
            }
        }
        
        public double ToCardinalityY
        {
            get
            {
                bool isVertical = (_endEdge == EdgeType.Top || _endEdge == EdgeType.Bottom);
                double parallelOffsetY = isVertical ? 0 : _parallelOffset;
                
                // Position based on which edge the line enters
                switch (_endEdge)
                {
                    case EdgeType.Top:
                        // Top edge - position above the connection point
                        return EndY - 28;
                    case EdgeType.Bottom:
                        // Bottom edge - position below the connection point
                        return EndY + 6;
                    case EdgeType.Left:
                    case EdgeType.Right:
                        // Horizontal edge - position above the line
                        return EndY - 12 + parallelOffsetY;
                    default:
                        return EndY - 12 + parallelOffsetY;
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
            // Null check to prevent NullReferenceException
            if (_fromTable == null || _toTable == null)
            {
                _isPathInitialized = false;
                return;
            }

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
        public List<AnnotationData> Annotations { get; set; } = new List<AnnotationData>();
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
    /// Data for a single annotation.
    /// </summary>
    public class AnnotationData
    {
        public string Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Text { get; set; }
        public string BackgroundColor { get; set; }
        public double FontSize { get; set; } = 11;
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
    }

    #endregion

    #region Annotation ViewModel

    /// <summary>
    /// ViewModel for a text annotation in the Model Diagram.
    /// </summary>
    public class ModelDiagramAnnotationViewModel : PropertyChangedBase
    {
        public ModelDiagramAnnotationViewModel()
        {
            Id = Guid.NewGuid().ToString("N");
        }

        public ModelDiagramAnnotationViewModel(AnnotationData data)
        {
            Id = data.Id ?? Guid.NewGuid().ToString("N");
            _x = data.X;
            _y = data.Y;
            _width = data.Width > 0 ? data.Width : 150;
            _height = data.Height > 0 ? data.Height : 60;
            _text = data.Text ?? "";
            _backgroundColor = data.BackgroundColor ?? "#FFFDE7"; // Light yellow default
            _fontSize = data.FontSize > 0 ? data.FontSize : 11;
            _isBold = data.IsBold;
            _isItalic = data.IsItalic;
        }

        public string Id { get; }

        private double _x;
        public double X
        {
            get => _x;
            set { _x = value; NotifyOfPropertyChange(); }
        }

        private double _y;
        public double Y
        {
            get => _y;
            set { _y = value; NotifyOfPropertyChange(); }
        }

        private double _width = 150;
        public double Width
        {
            get => _width;
            set { _width = Math.Max(80, value); NotifyOfPropertyChange(); }
        }

        private double _height = 60;
        public double Height
        {
            get => _height;
            set { _height = Math.Max(30, value); NotifyOfPropertyChange(); }
        }

        private string _text = "";
        public string Text
        {
            get => _text;
            set { _text = value; NotifyOfPropertyChange(); }
        }

        private string _backgroundColor = "#FFFDE7"; // Light yellow
        public string BackgroundColor
        {
            get => _backgroundColor;
            set { _backgroundColor = value; NotifyOfPropertyChange(); }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; NotifyOfPropertyChange(); }
        }

        private bool _isEditing;
        public bool IsEditing
        {
            get => _isEditing;
            set { _isEditing = value; NotifyOfPropertyChange(); }
        }

        private double _fontSize = 11;
        public double FontSize
        {
            get => _fontSize;
            set { _fontSize = value; NotifyOfPropertyChange(); }
        }

        private bool _isBold;
        public bool IsBold
        {
            get => _isBold;
            set { _isBold = value; NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(FontWeight)); }
        }

        public System.Windows.FontWeight FontWeight => _isBold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;

        private bool _isItalic;
        public bool IsItalic
        {
            get => _isItalic;
            set { _isItalic = value; NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(FontStyle)); }
        }

        public System.Windows.FontStyle FontStyle => _isItalic ? System.Windows.FontStyles.Italic : System.Windows.FontStyles.Normal;

        /// <summary>
        /// Converts this annotation to persistence data.
        /// </summary>
        public AnnotationData ToData()
        {
            return new AnnotationData
            {
                Id = Id,
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                Text = Text,
                BackgroundColor = BackgroundColor,
                FontSize = FontSize,
                IsBold = IsBold,
                IsItalic = IsItalic
            };
        }
    }

    #endregion
}
