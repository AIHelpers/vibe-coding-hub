using System.Threading.Tasks;
using AiCodeAgent.App.Services;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One file in the list of what a card changed; opening it shows the diff.</summary>
public sealed partial class ChangedFileViewModel
{
    private readonly MissionControlViewModel _owner;

    public ChangedFileViewModel(CardFileChange change, MissionControlViewModel owner)
    {
        Change = change;
        _owner = owner;
    }

    public CardFileChange Change { get; }
    public string Badge => Change.Badge;
    public string Path => Change.Kind == CardFileKind.Renamed ? $"{Change.OldPath} → {Change.Path}" : Change.Path;

    [RelayCommand]
    private Task Open() => _owner.OpenDiff(this);
}
