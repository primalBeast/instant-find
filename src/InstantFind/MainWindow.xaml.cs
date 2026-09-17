using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InstantFind.Models;
using InstantFind.ViewModels;

namespace InstantFind;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private Point _dragStart;
    private bool _dragPending;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.FocusSearchRequested += () =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };
        _vm.PropertyChanged += Vm_PropertyChanged;
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm.Dispose();
        };
        Loaded += (_, _) =>
        {
            SyncColumnVisibility();
            SearchBox.Focus();
        };
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowExtensionColumn)
            or nameof(MainViewModel.ShowAttributesColumn)
            or nameof(MainViewModel.ExtensionColumnVisibility)
            or nameof(MainViewModel.AttributesColumnVisibility))
        {
            SyncColumnVisibility();
        }
    }

    private void SyncColumnVisibility()
    {
        ExtensionColumn.Visibility = _vm.ShowExtensionColumn ? Visibility.Visible : Visibility.Collapsed;
        AttributesColumn.Visibility = _vm.ShowAttributesColumn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _vm.OpenSelected();
    }

    private void ResultsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is FileEntry entry)
        {
            _vm.ContextTarget = entry;
            ResultsGrid.SelectedItem = entry;
            _vm.SelectedItem = entry;
        }
    }

    private void ResultsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragPending = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not null;
    }

    private void ResultsGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragPending || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(null);
        var dx = Math.Abs(pos.X - _dragStart.X);
        var dy = Math.Abs(pos.Y - _dragStart.Y);
        if (dx < SystemParameters.MinimumHorizontalDragDistance
            && dy < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragPending = false;

        // Prefer selected row; fall back to row under pointer
        var entry = _vm.SelectedItem;
        if (entry is null)
        {
            var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            entry = row?.Item as FileEntry;
        }

        if (entry is null || string.IsNullOrWhiteSpace(entry.FullPath))
            return;

        // OLE file drop so Explorer can copy/move
        var data = new DataObject(DataFormats.FileDrop, new[] { entry.FullPath });
        try
        {
            DragDrop.DoDragDrop(ResultsGrid, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch
        {
            // Soft-fail — drag cancelled or target rejected
        }
    }

    private void ResultsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (_vm.ContextTarget is null)
            _vm.ContextTarget = _vm.SelectedItem;
        _vm.IsContextMenuOpen = true;
    }

    private void ResultsContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _vm.IsContextMenuOpen = false;
        _vm.ContextTarget = null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }


    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow { Owner = this };
        help.ShowDialog();
        SearchBox.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && ResultsGrid.Items.Count > 0)
        {
            ResultsGrid.Focus();
            ResultsGrid.SelectedIndex = 0;
            var row = (DataGridRow)ResultsGrid.ItemContainerGenerator
                .ContainerFromIndex(0);
            row?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_vm.IsFilterPopupOpen)
            {
                _vm.IsFilterPopupOpen = false;
                e.Handled = true;
                return;
            }
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }
}
