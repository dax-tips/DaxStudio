using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    }
}
