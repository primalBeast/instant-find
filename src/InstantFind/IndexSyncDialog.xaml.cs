using System.Windows;
using System.Windows.Input;

namespace InstantFind;

public partial class IndexSyncDialog : Window
{
    public enum Choice { Yes, No, Cancel }

    public Choice ResultChoice { get; private set; } = Choice.Cancel;

    public IndexSyncDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    public static Choice Show(Window? owner, string message)
    {
        var dlg = new IndexSyncDialog(message)
        {
            Owner = owner
        };
        dlg.ShowDialog();
        return dlg.ResultChoice;
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        ResultChoice = Choice.Yes;
        DialogResult = true;
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        ResultChoice = Choice.No;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ResultChoice = Choice.Cancel;
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ResultChoice = Choice.Cancel;
            DialogResult = false;
            e.Handled = true;
        }
    }
}
