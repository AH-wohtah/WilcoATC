using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WilcoATC.Converters;

/// <summary>
/// Visible quand la valeur liée vaut le paramètre (comparaison texte, insensible à la casse),
/// Collapsed sinon. Sert au sélecteur de section des réglages : chaque bloc est visible
/// uniquement quand le menu déroulant porte son nom.
/// </summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
