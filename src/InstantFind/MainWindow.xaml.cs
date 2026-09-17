using System.Windows;
using System.Windows.Input;
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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && ResultsGrid.Items.Count > 0)
        {
            ResultsGrid.Focus();
            ResultsGrid.SelectedIndex = 0;
            var row = (System.Windows.Controls.DataGridRow)ResultsGrid.ItemContainerGenerator
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
