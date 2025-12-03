using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DaxStudio.UI.ViewModels;

namespace DaxStudio.UI.Views
{
    /// <summary>
    /// Interaction logic for ModelDiagramView.xaml
    /// </summary>
    public partial class ModelDiagramView : UserControl
    {
        private bool _isDragging;
        private Point _dragStartPoint;
        private Point _tableStartPosition;
        private ModelDiagramTableViewModel _draggedTable;
        private FrameworkElement _draggedElement;
        private UIElement _coordinateRoot;

        public ModelDiagramView()
        {
            InitializeComponent();

            // Subscribe to export requests and scale changes from the ViewModel
            DataContextChanged += (s, e) =>
            {
                if (e.OldValue is ModelDiagramViewModel oldVm)
                {
                    oldVm.ExportRequested -= OnExportRequested;
                    oldVm.CopyImageRequested -= OnCopyImageRequested;
                    oldVm.OnScaleChanged -= OnScaleChanged;
                    oldVm.OnScrollToRequested -= OnScrollToRequested;
                }
                if (e.NewValue is ModelDiagramViewModel newVm)
                {
                    newVm.ExportRequested += OnExportRequested;
                    newVm.CopyImageRequested += OnCopyImageRequested;
                    newVm.OnScaleChanged += OnScaleChanged;
                    newVm.OnScrollToRequested += OnScrollToRequested;
                    // Set initial view dimensions
                    UpdateViewDimensions(newVm);
                    // Apply initial scale
                    UpdateCanvasScale(newVm.Scale);
                }
            };

            // Update view dimensions when size changes
            SizeChanged += (s, e) =>
            {
                if (DataContext is ModelDiagramViewModel vm)
                {
                    UpdateViewDimensions(vm);
                }
            };
        }

        /// <summary>
        /// Updates the ViewModel with current view dimensions for ZoomToFit calculation.
        /// </summary>
        private void UpdateViewDimensions(ModelDiagramViewModel vm)
        {
            if (MainScrollViewer != null)
            {
                vm.ViewWidth = MainScrollViewer.ActualWidth > 0 ? MainScrollViewer.ActualWidth : 800;
                vm.ViewHeight = MainScrollViewer.ActualHeight > 0 ? MainScrollViewer.ActualHeight : 600;
            }
        }

        /// <summary>
        /// Handles scale changes from the ViewModel to update the canvas transform.
        /// </summary>
        private void OnScaleChanged(object sender, System.EventArgs e)
        {
            if (DataContext is ModelDiagramViewModel vm)
            {
                UpdateCanvasScale(vm.Scale);
            }
        }

        /// <summary>
        /// Handles scroll requests from the ViewModel.
        /// </summary>
        private void OnScrollToRequested(double x, double y)
        {
            if (DataContext is ModelDiagramViewModel vm && MainScrollViewer != null)
            {
                // Calculate the scroll position to center on the target
                var scrollX = (x * vm.Scale) - (MainScrollViewer.ViewportWidth / 2);
                var scrollY = (y * vm.Scale) - (MainScrollViewer.ViewportHeight / 2);
                
                MainScrollViewer.ScrollToHorizontalOffset(System.Math.Max(0, scrollX));
                MainScrollViewer.ScrollToVerticalOffset(System.Math.Max(0, scrollY));
            }
        }

        /// <summary>
        /// Updates the canvas scale transform.
        /// </summary>
        private void UpdateCanvasScale(double scale)
        {
            if (CanvasScaleTransform != null)
            {
                CanvasScaleTransform.ScaleX = scale;
                CanvasScaleTransform.ScaleY = scale;
            }
        }

        /// <summary>
        /// Handles mouse wheel for zooming when Ctrl is held.
        /// </summary>
        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (DataContext is ModelDiagramViewModel vm)
                {
                    var factor = e.Delta / 1200.0;
                    vm.Scale += factor;
                    e.Handled = true;
                }
            }
        }

        /// <summary>
        /// Handles export request from ViewModel by rendering the canvas to a PNG file.
        /// </summary>
        private void OnExportRequested(object sender, string filePath)
        {
            try
            {
                // Find the canvas to render
                var canvas = DiagramCanvas;
                if (canvas == null) return;

                // Get the actual size of the content
                var bounds = VisualTreeHelper.GetDescendantBounds(canvas);
                if (bounds.IsEmpty)
                {
                    bounds = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
                }

                // Create a render target with proper DPI
                var dpi = 96d;
                var renderWidth = (int)System.Math.Max(bounds.Width + 40, canvas.ActualWidth);
                var renderHeight = (int)System.Math.Max(bounds.Height + 40, canvas.ActualHeight);

                var renderTarget = new RenderTargetBitmap(
                    renderWidth, renderHeight,
                    dpi, dpi,
                    PixelFormats.Pbgra32);

                // Create a visual brush to render from the canvas with background
                var drawingVisual = new DrawingVisual();
                using (var dc = drawingVisual.RenderOpen())
                {
                    // Draw white background
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, renderWidth, renderHeight));

                    // Draw the canvas content
                    var visualBrush = new VisualBrush(canvas);
                    dc.DrawRectangle(visualBrush, null, new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight));
                }

                renderTarget.Render(drawingVisual);

                // Encode to PNG
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderTarget));

                // Save to file
                using (var stream = File.Create(filePath))
                {
                    encoder.Save(stream);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Failed to export image: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles copy image to clipboard request from ViewModel.
        /// </summary>
        private void OnCopyImageRequested(object sender, System.EventArgs e)
        {
            try
            {
                var canvas = DiagramCanvas;
                if (canvas == null) return;

                var bounds = VisualTreeHelper.GetDescendantBounds(canvas);
                if (bounds.IsEmpty)
                {
                    bounds = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
                }

                var dpi = 96d;
                var renderWidth = (int)System.Math.Max(bounds.Width + 40, canvas.ActualWidth);
                var renderHeight = (int)System.Math.Max(bounds.Height + 40, canvas.ActualHeight);

                var renderTarget = new RenderTargetBitmap(
                    renderWidth, renderHeight,
                    dpi, dpi,
                    PixelFormats.Pbgra32);

                var drawingVisual = new DrawingVisual();
                using (var dc = drawingVisual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, renderWidth, renderHeight));
                    var visualBrush = new VisualBrush(canvas);
                    dc.DrawRectangle(visualBrush, null, new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight));
                }

                renderTarget.Render(drawingVisual);

                Clipboard.SetImage(renderTarget);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Failed to copy image to clipboard: {ex.Message}", "Copy Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Find the coordinate root - the Grid inside ScrollViewer that has the canvas dimensions.
        /// </summary>
        private UIElement FindCoordinateRoot(DependencyObject element)
        {
            // Walk up the tree to find the ScrollViewer, then get its content (the Grid)
            while (element != null)
            {
                if (element is ScrollViewer scrollViewer)
                {
                    return scrollViewer.Content as UIElement;
                }
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private void Table_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ModelDiagramTableViewModel tableVm)
            {
                // Double-click on table body jumps to Metadata Pane
                if (e.ClickCount == 2)
                {
                    if (DataContext is ModelDiagramViewModel viewModel)
                    {
                        viewModel.SelectSingleTable(tableVm);
                        viewModel.JumpToMetadataPane();
                    }
                    e.Handled = true;
                    return;
                }

                _coordinateRoot = FindCoordinateRoot(element);
                if (_coordinateRoot == null) return;

                var position = e.GetPosition(_coordinateRoot);

                if (DataContext is ModelDiagramViewModel vm)
                {
                    // Handle Ctrl+click for multi-select
                    if (Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        vm.ToggleTableSelection(tableVm);
                    }
                    else
                    {
                        // Check if clicking on already-selected table in multi-selection
                        if (vm.SelectedTables.Count > 1 && vm.SelectedTables.Contains(tableVm))
                        {
                            // Start multi-drag without changing selection
                            UpdateMultiDragStart(tableVm, position);
                        }
                        else
                        {
                            // Single selection
                            vm.SelectSingleTable(tableVm);
                        }
                    }
                }

                // Start drag operation
                _isDragging = true;
                _dragStartPoint = position;
                _tableStartPosition = new Point(tableVm.X, tableVm.Y);
                _draggedTable = tableVm;
                _draggedElement = element;
                element.CaptureMouse();

                // Focus the control for keyboard navigation
                this.Focus();

                e.Handled = true;
            }
        }

        private void Table_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging || _isMultiDragging)
            {
                _isDragging = false;
                _draggedElement?.ReleaseMouseCapture();
                EndMultiDrag();

                // Update relationship paths after drag and save layout
                if (DataContext is ModelDiagramViewModel vm)
                {
                    vm.RefreshLayout();
                    vm.SaveLayoutAfterDrag();
                }

                e.Handled = true;
            }
        }

        private void Table_MouseMove(object sender, MouseEventArgs e)
        {
            if (_coordinateRoot == null) return;
            
            var currentPosition = e.GetPosition(_coordinateRoot);

            // Handle multi-drag
            if (_isMultiDragging)
            {
                HandleMultiDrag(currentPosition);
                e.Handled = true;
                return;
            }

            if (_isDragging && _draggedTable != null)
            {
                var delta = currentPosition - _dragStartPoint;

                // Update table position with optional snap-to-grid
                if (DataContext is ModelDiagramViewModel vm)
                {
                    _draggedTable.X = System.Math.Max(0, vm.SnapToGridValue(_tableStartPosition.X + delta.X));
                    _draggedTable.Y = System.Math.Max(0, vm.SnapToGridValue(_tableStartPosition.Y + delta.Y));
                    vm.RefreshLayout();
                }

                e.Handled = true;
            }
        }

        private void TableHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Double-click on header toggles collapse
            if (e.ClickCount == 2)
            {
                if (sender is FrameworkElement element && element.DataContext is ModelDiagramTableViewModel tableVm)
                {
                    tableVm.IsCollapsed = !tableVm.IsCollapsed;
                    // Update relationships connected to this table
                    if (DataContext is ModelDiagramViewModel vm)
                    {
                        vm.UpdateRelationshipsForTable(tableVm);
                    }
                    e.Handled = true;
                }
            }
            else
            {
                // Single click - start drag from header
                Table_MouseLeftButtonDown(sender, e);
            }
        }

        private void CollapseToggle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                // Find the parent table view model
                var parent = VisualTreeHelper.GetParent(element);
                while (parent != null)
                {
                    if (parent is FrameworkElement fe && fe.DataContext is ModelDiagramTableViewModel tableVm)
                    {
                        tableVm.IsCollapsed = !tableVm.IsCollapsed;
                        // Update relationships connected to this table
                        if (DataContext is ModelDiagramViewModel vm)
                        {
                            vm.UpdateRelationshipsForTable(tableVm);
                        }
                        e.Handled = true;
                        return;
                    }
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }
        }

        #region Table Resize Handling

        private bool _isResizing;
        private Point _resizeStartPoint;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private ModelDiagramTableViewModel _resizingTable;
        private FrameworkElement _resizingElement;
        private ResizeMode _resizeMode;

        private enum ResizeMode
        {
            Right,
            Bottom,
            Corner
        }

        private ModelDiagramTableViewModel FindParentTableViewModel(FrameworkElement element)
        {
            var parent = VisualTreeHelper.GetParent(element);
            while (parent != null)
            {
                if (parent is FrameworkElement fe && fe.DataContext is ModelDiagramTableViewModel tableVm)
                {
                    return tableVm;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        private void ResizeRight_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            StartResize(sender as FrameworkElement, e, ResizeMode.Right);
        }

        private void ResizeBottom_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            StartResize(sender as FrameworkElement, e, ResizeMode.Bottom);
        }

        private void ResizeCorner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            StartResize(sender as FrameworkElement, e, ResizeMode.Corner);
        }

        private void StartResize(FrameworkElement element, MouseButtonEventArgs e, ResizeMode mode)
        {
            if (element == null) return;

            var tableVm = FindParentTableViewModel(element);
            if (tableVm == null) return;

            _coordinateRoot = FindCoordinateRoot(element);
            if (_coordinateRoot == null) return;

            _isResizing = true;
            _resizeMode = mode;
            _resizeStartPoint = e.GetPosition(_coordinateRoot);
            _resizeStartWidth = tableVm.Width;
            _resizeStartHeight = tableVm.Height;
            _resizingTable = tableVm;
            _resizingElement = element;
            element.CaptureMouse();
            e.Handled = true;
        }

        private void Resize_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResizing)
            {
                _isResizing = false;
                _resizingElement?.ReleaseMouseCapture();

                // Update relationship paths after resize and save layout
                if (DataContext is ModelDiagramViewModel vm)
                {
                    vm.RefreshLayout();
                    vm.SaveLayoutAfterDrag();
                }

                e.Handled = true;
            }
        }

        private void ResizeRight_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isResizing && _resizeMode == ResizeMode.Right && _resizingTable != null && _coordinateRoot != null)
            {
                var currentPosition = e.GetPosition(_coordinateRoot);
                var deltaX = currentPosition.X - _resizeStartPoint.X;
                _resizingTable.Width = System.Math.Max(150, _resizeStartWidth + deltaX);

                if (DataContext is ModelDiagramViewModel vm)
                {
                    vm.RefreshLayout();
                }

                e.Handled = true;
            }
        }

        private void ResizeBottom_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isResizing && _resizeMode == ResizeMode.Bottom && _resizingTable != null && _coordinateRoot != null)
            {
                var currentPosition = e.GetPosition(_coordinateRoot);
                var deltaY = currentPosition.Y - _resizeStartPoint.Y;
                _resizingTable.Height = System.Math.Max(80, _resizeStartHeight + deltaY);

                if (DataContext is ModelDiagramViewModel vm)
                {
                    vm.RefreshLayout();
                }

                e.Handled = true;
            }
        }

        private void ResizeCorner_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isResizing && _resizeMode == ResizeMode.Corner && _resizingTable != null && _coordinateRoot != null)
            {
                var currentPosition = e.GetPosition(_coordinateRoot);
                var deltaX = currentPosition.X - _resizeStartPoint.X;
                var deltaY = currentPosition.Y - _resizeStartPoint.Y;
                _resizingTable.Width = System.Math.Max(150, _resizeStartWidth + deltaX);
                _resizingTable.Height = System.Math.Max(80, _resizeStartHeight + deltaY);

                if (DataContext is ModelDiagramViewModel vm)
                {
                    vm.RefreshLayout();
                }

                e.Handled = true;
            }
        }

        #endregion

        #region Context Menu Event Handlers

        private void CopyTableName_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && 
                menuItem.Parent is ContextMenu contextMenu && 
                contextMenu.PlacementTarget is FrameworkElement element &&
                element.DataContext is ModelDiagramTableViewModel tableVm &&
                DataContext is ModelDiagramViewModel vm)
            {
                vm.CopyTableName(tableVm);
            }
        }

        private void CopyTableDaxName_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && 
                menuItem.Parent is ContextMenu contextMenu && 
                contextMenu.PlacementTarget is FrameworkElement element &&
                element.DataContext is ModelDiagramTableViewModel tableVm &&
                DataContext is ModelDiagramViewModel vm)
            {
                vm.CopyTableDaxName(tableVm);
            }
        }

        private void CopyColumnName_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && 
                menuItem.Parent is ContextMenu contextMenu && 
                contextMenu.PlacementTarget is FrameworkElement element &&
                element.DataContext is ModelDiagramColumnViewModel columnVm &&
                DataContext is ModelDiagramViewModel vm)
            {
                vm.CopyColumnName(columnVm);
            }
        }

        private void CopyColumnDaxName_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && 
                menuItem.Parent is ContextMenu contextMenu && 
                contextMenu.PlacementTarget is FrameworkElement element &&
                element.DataContext is ModelDiagramColumnViewModel columnVm &&
                element.Tag is ModelDiagramTableViewModel tableVm &&
                DataContext is ModelDiagramViewModel vm)
            {
                vm.CopyColumnDaxName(columnVm, tableVm);
            }
        }

        private void JumpToMetadata_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && 
                menuItem.Parent is ContextMenu contextMenu && 
                contextMenu.PlacementTarget is FrameworkElement element &&
                element.DataContext is ModelDiagramTableViewModel tableVm &&
                DataContext is ModelDiagramViewModel vm)
            {
                vm.SelectSingleTable(tableVm);
                vm.JumpToMetadataPane();
            }
        }

        private void CollapseTable_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && 
                menuItem.Parent is ContextMenu contextMenu && 
                contextMenu.PlacementTarget is FrameworkElement element &&
                element.DataContext is ModelDiagramTableViewModel tableVm)
            {
                tableVm.IsCollapsed = true;
                // Update relationships connected to this table
                if (DataContext is ModelDiagramViewModel vm)
                {
                    vm.UpdateRelationshipsForTable(tableVm);
                }
            }
        }

        private void ExpandTable_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && 
                menuItem.Parent is ContextMenu contextMenu && 
                contextMenu.PlacementTarget is FrameworkElement element &&
                element.DataContext is ModelDiagramTableViewModel tableVm)
            {
                tableVm.IsCollapsed = false;
                // Update relationships connected to this table
                if (DataContext is ModelDiagramViewModel vm)
                {
                    vm.UpdateRelationshipsForTable(tableVm);
                }
            }
        }

        #endregion

        #region Keyboard Navigation

        private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is ModelDiagramViewModel vm)
            {
                switch (e.Key)
                {
                    case Key.Left:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                            vm.NudgeSelectedTables(-10, 0);
                        else
                            vm.NavigateToTable("left");
                        e.Handled = true;
                        break;
                    case Key.Right:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                            vm.NudgeSelectedTables(10, 0);
                        else
                            vm.NavigateToTable("right");
                        e.Handled = true;
                        break;
                    case Key.Up:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                            vm.NudgeSelectedTables(0, -10);
                        else
                            vm.NavigateToTable("up");
                        e.Handled = true;
                        break;
                    case Key.Down:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                            vm.NudgeSelectedTables(0, 10);
                        else
                            vm.NavigateToTable("down");
                        e.Handled = true;
                        break;
                    case Key.Tab:
                        vm.CycleConnectedTables(Keyboard.Modifiers == ModifierKeys.Shift);
                        e.Handled = true;
                        break;
                    case Key.Escape:
                        vm.ClearSelection();
                        e.Handled = true;
                        break;
                    case Key.Delete:
                        vm.HideSelectedTables();
                        e.Handled = true;
                        break;
                    case Key.A:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            vm.SelectAllTables();
                            e.Handled = true;
                        }
                        break;
                    case Key.P:
                        if (Keyboard.Modifiers == ModifierKeys.Control && vm.CanHighlightPath)
                        {
                            vm.HighlightPath();
                            e.Handled = true;
                        }
                        break;
                    case Key.Z:
                        if (Keyboard.Modifiers == ModifierKeys.Control && vm.CanUndoLayout)
                        {
                            vm.UndoLayout();
                            e.Handled = true;
                        }
                        break;
                    case Key.Add:
                    case Key.OemPlus:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            vm.ZoomIn();
                            e.Handled = true;
                        }
                        break;
                    case Key.Subtract:
                    case Key.OemMinus:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            vm.ZoomOut();
                            e.Handled = true;
                        }
                        break;
                    case Key.D0:
                    case Key.NumPad0:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            vm.ResetZoom();
                            e.Handled = true;
                        }
                        break;
                }
            }
        }

        #endregion

        #region Relationship Click Handling

        private void Relationship_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ModelDiagramRelationshipViewModel relVm)
            {
                if (DataContext is ModelDiagramViewModel vm)
                {
                    vm.SelectRelationship(relVm);
                }
                e.Handled = true;
            }
        }

        private void Relationship_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ModelDiagramRelationshipViewModel relVm)
            {
                if (DataContext is ModelDiagramViewModel vm)
                {
                    vm.HoverRelationship(relVm);
                }
            }
        }

        private void Relationship_MouseLeave(object sender, MouseEventArgs e)
        {
            if (DataContext is ModelDiagramViewModel vm)
            {
                vm.UnhoverRelationship();
            }
        }

        #endregion

        #region Canvas Panning

        private bool _isPanning;
        private Point _panStartPoint;
        private double _panStartHorizontalOffset;
        private double _panStartVerticalOffset;

        /// <summary>
        /// Handles left-click on the canvas background to start panning or clear selection.
        /// </summary>
        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only handle if clicking directly on the canvas background (not on a table or relationship)
            if (e.OriginalSource is FrameworkElement element)
            {
                // Check if we clicked on the background Grid itself
                if (element.Name == "DiagramGrid" || element.DataContext == DataContext)
                {
                    // Start panning
                    _isPanning = true;
                    _panStartPoint = e.GetPosition(MainScrollViewer);
                    _panStartHorizontalOffset = MainScrollViewer.HorizontalOffset;
                    _panStartVerticalOffset = MainScrollViewer.VerticalOffset;
                    DiagramGrid.CaptureMouse();
                    DiagramGrid.Cursor = System.Windows.Input.Cursors.Hand;
                    
                    // Also clear selection
                    if (DataContext is ModelDiagramViewModel vm)
                    {
                        vm.ClearSelection();
                    }
                    
                    e.Handled = true;
                }
            }
        }

        /// <summary>
        /// Handles mouse up on canvas to end panning.
        /// </summary>
        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                DiagramGrid.ReleaseMouseCapture();
                DiagramGrid.Cursor = System.Windows.Input.Cursors.Arrow;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handles mouse move on canvas for panning.
        /// </summary>
        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(MainScrollViewer);
                var deltaX = currentPoint.X - _panStartPoint.X;
                var deltaY = currentPoint.Y - _panStartPoint.Y;

                // Pan in the opposite direction of mouse movement (like dragging a piece of paper)
                MainScrollViewer.ScrollToHorizontalOffset(_panStartHorizontalOffset - deltaX);
                MainScrollViewer.ScrollToVerticalOffset(_panStartVerticalOffset - deltaY);
            }
        }

        #endregion

        #region Mini-Map

        private void MiniMap_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is ModelDiagramViewModel vm && MiniMapCanvas != null)
            {
                // Get click position relative to mini-map canvas
                var pos = e.GetPosition(MiniMapCanvas);
                
                // Calculate the actual canvas position to scroll to
                var scrollToX = pos.X - (MainScrollViewer.ViewportWidth / 2 / vm.Scale);
                var scrollToY = pos.Y - (MainScrollViewer.ViewportHeight / 2 / vm.Scale);
                
                // Scroll to that position
                MainScrollViewer.ScrollToHorizontalOffset(System.Math.Max(0, scrollToX * vm.Scale));
                MainScrollViewer.ScrollToVerticalOffset(System.Math.Max(0, scrollToY * vm.Scale));
            }
        }

        #endregion

        #region Multi-Selection Drag Support

        private bool _isMultiDragging;
        private Point _multiDragStartPoint;
        private Dictionary<ModelDiagramTableViewModel, Point> _multiDragStartPositions;

        /// <summary>
        /// Updates Table_MouseLeftButtonDown to support multi-selection drag.
        /// </summary>
        private void UpdateMultiDragStart(ModelDiagramTableViewModel tableVm, Point position)
        {
            if (DataContext is ModelDiagramViewModel vm)
            {
                if (vm.SelectedTables.Count > 1 && vm.SelectedTables.Contains(tableVm))
                {
                    // Start multi-drag
                    _isMultiDragging = true;
                    _multiDragStartPoint = position;
                    _multiDragStartPositions = new Dictionary<ModelDiagramTableViewModel, Point>();
                    foreach (var table in vm.SelectedTables)
                    {
                        _multiDragStartPositions[table] = new Point(table.X, table.Y);
                    }
                }
            }
        }

        /// <summary>
        /// Handles multi-table drag during mouse move.
        /// </summary>
        private void HandleMultiDrag(Point currentPosition)
        {
            if (_isMultiDragging && _multiDragStartPositions != null && DataContext is ModelDiagramViewModel vm)
            {
                var delta = new Point(
                    currentPosition.X - _multiDragStartPoint.X,
                    currentPosition.Y - _multiDragStartPoint.Y);

                foreach (var kvp in _multiDragStartPositions)
                {
                    var table = kvp.Key;
                    var startPos = kvp.Value;
                    table.X = System.Math.Max(0, vm.SnapToGridValue(startPos.X + delta.X));
                    table.Y = System.Math.Max(0, vm.SnapToGridValue(startPos.Y + delta.Y));
                }

                vm.RefreshLayout();
            }
        }

        /// <summary>
        /// Ends multi-table drag.
        /// </summary>
        private void EndMultiDrag()
        {
            if (_isMultiDragging)
            {
                _isMultiDragging = false;
                _multiDragStartPositions = null;
                
                if (DataContext is ModelDiagramViewModel vm)
                {
                    vm.SaveLayoutAfterDrag();
                }
            }
        }

        #endregion
    }
}
