namespace WilcoATC.ViewModels;

/// <summary>État affichable d'une radio COM (active, standby, émission, station).</summary>
public sealed class ComRadioViewModel : ObservableObject
{
    public ComRadioViewModel(string title) => Title = title;

    /// <summary>Libellé du panneau ("COM 1" / "COM 2").</summary>
    public string Title { get; }

    private string _active = "---.---";
    public string Active { get => _active; set => SetProperty(ref _active, value); }

    private string _standby = "---.---";
    public string Standby { get => _standby; set => SetProperty(ref _standby, value); }

    private bool _isTransmitting;
    public bool IsTransmitting { get => _isTransmitting; set => SetProperty(ref _isTransmitting, value); }

    private string? _station;
    public string? Station
    {
        get => _station;
        set { if (SetProperty(ref _station, value)) Raise(nameof(HasStation)); }
    }

    /// <summary>Vrai si une station a été résolue (stretch) — pilote la visibilité UI.</summary>
    public bool HasStation => !string.IsNullOrEmpty(_station);
}
