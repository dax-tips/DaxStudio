using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DaxStudio.UI.ViewModels;

namespace DaxStudio.UI.Views
{
    /// <summary>
    /// Interaction logic for XmSqlErdView.xaml
    /// </summary>
    public partial class XmSqlErdView : Controls.ZoomableUserControl
    {
        private bool _isDragging;
        private Point _dragStartPoint;
        private Point _tableStartPosition;
        private ErdTableViewModel _draggedTable;
        private FrameworkElement _draggedElement;
        private UIElement _coordinateRoot;

        public XmSqlErdView()
        {
            InitializeComponent();
            
            // Subscribe to export requests from the ViewModel
            DataContextChanged += (s, e) =>
            {
                if (e.OldValue is XmSqlErdViewModel oldVm)
                {
                    oldVm.ExportRequested -= OnExportRequested;
                }
                if (e.NewValue is XmSqlErdViewModel newVm)
                {
                    newVm.ExportRequested += OnExportRequested;
                }
            };
        }

        /// <summary>
        /// Handles export request from ViewModel by rendering the canvas to a PNG file.
        /// </summary>
        private void OnExportRequested(object sender, string filePath)
        {
            try
            {
                // Find the canvas to render - use the DiagramCanvas named in XAML
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
                    dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, renderWidth, renderHeight));
                    
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
                System.Windows.MessageBox.Show($"Failed to export image: {ex.Message}", "Export Error", 
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
            if (sender is FrameworkElement element && element.DataContext is ErdTableViewModel tableVm)
            {
                _coordinateRoot = FindCoordinateRoot(element);
                if (_coordinateRoot == null) return;

                // Start drag operation - use position relative to the coordinate root
                _isDragging = true;
                _dragStartPoint = e.GetPosition(_coordinateRoot);
                _tableStartPosition = new Point(tableVm.X, tableVm.Y);
                _draggedTable = tableVm;
                _draggedElement = element;
                element.CaptureMouse();

                // Also select the table when clicked
                if (DataContext is XmSqlErdViewModel erdVm)
                {
                    erdVm.SelectTable(tableVm.TableName);
                }

                e.Handled = true;
            }
        }

        private void Table_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging && _draggedTable != null && _coordinateRoot != null)
            {
                var currentPosition = e.GetPosition(_coordinateRoot);
                
                // Calculate new position based on how far we've moved from the start
                var newX = _tableStartPosition.X + (currentPosition.X - _dragStartPoint.X);
                var newY = _tableStartPosition.Y + (currentPosition.Y - _dragStartPoint.Y);

                // Keep within canvas bounds (don't go negative)
                if (newX < 0) newX = 0;
                if (newY < 0) newY = 0;

                // Update table position
                _draggedTable.X = newX;
                _draggedTable.Y = newY;

                // Notify ViewModel to update relationship lines
                if (DataContext is XmSqlErdViewModel erdVm)
                {
                    erdVm.OnTablePositionChanged(_draggedTable);
                }

                e.Handled = true;
            }
        }

        private void Table_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging && sender is FrameworkElement element)
            {
                _isDragging = false;
                _draggedTable = null;
                _draggedElement = null;
                _coordinateRoot = null;
                element.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Click on empty canvas area clears selection
            if (e.OriginalSource is FrameworkElement fe && 
                !(fe.DataContext is ErdTableViewModel))
            {
                if (DataContext is XmSqlErdViewModel erdVm)
                {
                    erdVm.ClearSelection();
                }
            }
        }

        private void Column_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ErdColumnViewModel columnVm)
            {
                // Find the parent table view model
                var parent = element;
                while (parent != null)
                {
                    if (parent.DataContext is ErdTableViewModel tableVm)
                    {
                        if (DataContext is XmSqlErdViewModel erdVm)
                        {
                            erdVm.SelectColumn(tableVm, columnVm);
                        }
                        break;
                    }
                    parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
                }
                
                e.Handled = true;
            }
        }

        private void Relationship_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ErdRelationshipViewModel relVm)
            {
                if (DataContext is XmSqlErdViewModel erdVm)
                {
                    erdVm.SelectRelationship(relVm);
                }
                e.Handled = true;
            }
        }

        private void TableHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only respond to direct clicks on the header, not drag operations
            if (e.ClickCount == 2 && sender is FrameworkElement element && element.DataContext is ErdTableViewModel tableVm)
            {
                if (DataContext is XmSqlErdViewModel erdVm)
                {
                    erdVm.SelectTableDetails(tableVm);
                }
                e.Handled = true;
            }
        }

        private void CollapseToggle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ErdTableViewModel tableVm)
            {
                tableVm.ToggleCollapse();
                e.Handled = true;
            }
        }
    }
}
