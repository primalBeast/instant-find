using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using InstantFind.Models;
using InstantFind.Services;
using InstantFind.ViewModels;

namespace InstantFind;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    public string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    private Point _dragStart;
    private bool _dragPending;
    private bool _customChromeApplied;

    public MainWindow()
    {
        StartupLog.Append("MainWindow.Ctor.Begin");
        try
        {
            InitializeComponent();
            StartupLog.Append("MainWindow.InitializeComponent.Done");

            if (VersionRun is not null)
                VersionRun.Text = AppVersion;

            // Prefer opaque brush from resources (WindowChrome may set HWND bg transparent).
            try
            {
                if (TryFindResource("BgBrush") is Brush bg)
                    Background = bg;
            }
            catch (Exception ex)
            {
                StartupLog.Append("MainWindow.BgBrush.Failed", ex.Message);
            }

            _vm = new MainViewModel();
            DataContext = _vm;
            StartupLog.Append("MainWindow.ViewModel.Ready");

            SizeChanged += (_, _) => UpdateFilterPopupMaxHeight();
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsFilterPopupOpen) && _vm.IsFilterPopupOpen)
                    UpdateFilterPopupMaxHeight();
            };

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
                StartupLog.Append("MainWindow.Closed");
            };
            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            ContentRendered += (_, _) => StartupLog.Append(
                "MainWindow.ContentRendered",
                $"state={WindowState} vis={Visibility} chrome={_customChromeApplied}");

            StartupLog.Append("MainWindow.Ctor.Complete", $"version={AppVersion}");
        }
        catch (Exception ex)
        {
            ErrorLog.AppendFailure("MainWindow.Ctor", ex);
            StartupLog.Append("MainWindow.Ctor.Failed", $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        StartupLog.Append("MainWindow.SourceInitialized.Begin", $"hwndReady style={WindowStyle}");
        ApplyCustomChrome();
        try
        {
            // Ensure we are visible even if chrome briefly left the HWND transparent.
            if (Visibility != Visibility.Visible)
                Visibility = Visibility.Visible;
            StartupLog.Append("MainWindow.SourceInitialized.Done", $"chrome={_customChromeApplied}");
        }
        catch (Exception ex)
        {
            ErrorLog.AppendFailure("MainWindow.SourceInitialized", ex);
            StartupLog.Append("MainWindow.SourceInitialized.Failed", ex.Message);
        }
    }

    private void ApplyCustomChrome()
    {
        try
        {
            // Robust WindowStyle=None + WindowChrome pattern (caption hit-test via IsHitTestVisibleInChrome).
            var chrome = new WindowChrome
            {
                CaptionHeight = 44,
                ResizeBorderThickness = new Thickness(6),
                // 0 = no glass extend; avoids DwmExtendFrameIntoClientArea failure paths when DWM blips.
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, chrome);
            _customChromeApplied = true;
            StartupLog.Append("MainWindow.WindowChrome.Applied", "CaptionHeight=44 GlassFrame=0");
        }
        catch (Exception ex)
        {
            _customChromeApplied = false;
            ErrorLog.AppendFailure("MainWindow.WindowChrome", ex);
            StartupLog.Append("MainWindow.WindowChrome.Failed", $"{ex.GetType().Name}: {ex.Message}");
            try
            {
                WindowChrome.SetWindowChrome(this, null!);
            }
            catch { /* ignore */ }

            // Fall back to system chrome so the window still appears and is usable.
            try
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                StartupLog.Append("MainWindow.WindowChrome.Fallback", "restored SingleBorderWindow system chrome");
            }
            catch (Exception fallbackEx)
            {
                ErrorLog.AppendFailure("MainWindow.WindowChrome.Fallback", fallbackEx);
                StartupLog.Append("MainWindow.WindowChrome.Fallback.Failed", fallbackEx.Message);
            }
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupLog.Append(
                "MainWindow.Loaded",
                $"state={WindowState} vis={Visibility} active={IsActive} chrome={_customChromeApplied}");
            SyncColumnVisibility();
            SearchBox.Focus();

            // If still not showing for any reason, force activate once.
            if (!IsVisible || !IsActive)
            {
                Show();
                Activate();
                StartupLog.Append("MainWindow.Loaded.ForceShowActivate");
            }
        }
        catch (Exception ex)
        {
            ErrorLog.AppendFailure("MainWindow.Loaded", ex);
            StartupLog.Append("MainWindow.Loaded.Failed", ex.Message);
        }
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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The window may have been closed while the drag was starting.
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
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

    /// <summary>
    /// Filter popup height = min(content, host client height − margins); ScrollViewer only if needed.
    /// </summary>
    private void UpdateFilterPopupMaxHeight()
    {
        try
        {
            if (FilterPopupBorder is null) return;
            var max = Math.Max(160, ActualHeight - 80);
            FilterPopupBorder.MaxHeight = max;
        }
        catch
        {
            // soft-fail — keep XAML MaxHeight
        }
    }

}
