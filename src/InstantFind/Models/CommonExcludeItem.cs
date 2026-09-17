using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace InstantFind.Models;

/// <summary>One common exclude row in the Filter popup (checkbox + label).</summary>
public sealed class CommonExcludeItem : INotifyPropertyChanged
{
    private bool _isChecked;

    public CommonExcludeItem(string id, string label, bool isChecked)
    {
        Id = id;
        Label = label;
        _isChecked = isChecked;
    }

    public string Id { get; }
    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
