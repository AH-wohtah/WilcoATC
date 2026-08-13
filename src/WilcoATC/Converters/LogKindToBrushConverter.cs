using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WilcoATC.ViewModels;

namespace WilcoATC.Converters;

/// <summary>Associe une couleur au texte du journal selon la nature de la ligne.</summary>
public sealed class LogKindToBrushConverter : IValueConverter
{
    // Teintes cohérentes avec le thème cockpit (voir Themes/Cockpit.xaml).
    private static readonly Brush Change   = Freeze("#FFC24B"); // ambre : changement de fréquence
    private static readonly Brush Transmit = Freeze("#FF8A3D"); // orange : émission
    private static readonly Brush Initial  = Freeze("#8FA3B0"); // gris-bleu : état initial
    private static readonly Brush System   = Freeze("#57C7D4"); // cyan : messages système
    private static readonly Brush Atc      = Freeze("#37D67A"); // vert : transmission ATC
    private static readonly Brush Pilot    = Freeze("#E6EDF3"); // blanc : requête pilote
    private static readonly Brush Refused  = Freeze("#FF5C5C"); // rouge : refus
    private static readonly Brush Copilot  = Freeze("#5A97F8"); // bleu : copilote virtuel
    private static readonly Brush Chatter  = Freeze("#69768A"); // gris : trafic ambiant (en retrait)
    private static readonly Brush Atis     = Freeze("#9C8CF0"); // violet : bulletin ATIS (diffusion, pas un échange)

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            LogKind.Change => Change,
            LogKind.Transmit => Transmit,
            LogKind.Initial => Initial,
            LogKind.Atc => Atc,
            LogKind.Pilot => Pilot,
            LogKind.Refused => Refused,
            LogKind.Copilot => Copilot,
            LogKind.Chatter => Chatter,
            LogKind.Atis => Atis,
            _ => System,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
