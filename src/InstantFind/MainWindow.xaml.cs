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
        Closed += (_, _) => _vm.Dispose();
        Loaded += (_, _) => SearchBox.Focus();
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
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }
}
