using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CardForge.Services;

namespace CardForge.Views;

public sealed class AssetSection : INotifyPropertyChanged
{
    string _title = "";
    public string Title { get => _title; set { _title = value; Changed(); } }
    int _count;
    bool _isExpanded;
    public int Count { get => _count; set { _count = value; Changed(); } }
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; Changed(); } }
    public ObservableCollection<ReferenceAsset> Assets { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
