using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private ErdTableViewModel _draggedTable;

        public XmSqlErdView()
        {
            InitializeComponent();
        }

        private void Table_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ErdTableViewModel tableVm)
            {
                // Start drag operation
                _isDragging = true;
                _dragStartPoint = e.GetPosition(element.Parent as UIElement);
                _draggedTable = tableVm;
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
            if (_isDragging && _draggedTable != null && sender is FrameworkElement element)
            {
                var currentPosition = e.GetPosition(element.Parent as UIElement);
                var deltaX = currentPosition.X - _dragStartPoint.X;
                var deltaY = currentPosition.Y - _dragStartPoint.Y;

                // Update table position
                _draggedTable.X += deltaX;
                _draggedTable.Y += deltaY;

                // Keep within canvas bounds
                if (_draggedTable.X < 0) _draggedTable.X = 0;
                if (_draggedTable.Y < 0) _draggedTable.Y = 0;

                _dragStartPoint = currentPosition;

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
