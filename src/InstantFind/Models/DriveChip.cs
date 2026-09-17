using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace InstantFind.Models;

/// <summary>
/// Toggle chip for an indexed drive letter shown in the status bar.
/// </summary>
public sealed class DriveChip : INotifyPropertyChanged
{
    private bool _isEnabled;

    public DriveChip(string letter, bool isEnabled)
    {
        Letter = letter;
        _isEnabled = isEnabled;
    }

    /// <summary>Display label such as "C:".</summary>
    public string Letter { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
