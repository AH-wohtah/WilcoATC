using WilcoATC.Common;
using WilcoATC.Stations;

namespace WilcoATC.ViewModels;

/// <summary>
/// Une ligne du panneau « fréquences du terrain ». <see cref="IsTuned"/> change en cours de
/// vol (on tourne le bouton), d'où l'implémentation observable plutôt qu'un simple record.
/// </summary>
public sealed class AirportFrequencyViewModel : ObservableObject
{
    public AirportFrequencyViewModel(AirportFrequency f)
    {
        Label = f.Label;
        Type = f.Type;
        Mhz = f.Mhz;
        Frequency = f.Mhz.ToString("000.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    public string Label { get; }
    public ControllerType Type { get; }
    public double Mhz { get; }

    /// <summary>Fréquence formatée comme les afficheurs COM (« 118.700 »).</summary>
    public string Frequency { get; }

    private bool _isTuned;
    /// <summary>Vrai si COM1 ou COM2 est calé sur cette fréquence — mis en évidence dans l'UI.</summary>
    public bool IsTuned { get => _isTuned; set => SetProperty(ref _isTuned, value); }
}

/// <summary>
/// Un GROUPE de fréquences d'une même catégorie (Sol, Tour, Approche…), pour ranger le panneau
/// par sections plutôt qu'en une seule grille en vrac. Les tuiles réfèrent les MÊMES instances
/// que la liste plate, donc la mise en évidence « calé dessus » reste synchronisée.
/// </summary>
public sealed record FrequencyGroupViewModel(string Header, IReadOnlyList<AirportFrequencyViewModel> Items);
