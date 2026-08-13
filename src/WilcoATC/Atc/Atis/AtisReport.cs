using System.Globalization;
using WilcoATC.Sim;

namespace WilcoATC.Atc.Atis;

/// <summary>
/// Conditions telles qu'un ATIS les annonce : déjà ramenées AU TERRAIN, arrondies comme
/// dans un vrai bulletin (vent aux 10°, QNH au hectopascal). Voir <see cref="AtisSurface"/>
/// pour le passage depuis la mesure brute du simulateur.
/// </summary>
/// <param name="WindDirectionDeg">D'où vient le vent, en degrés MAGNÉTIQUES (0 = calme non signifiant).</param>
/// <param name="QnhHectopascals">0 si le simulateur n'a pas donné de pression exploitable.</param>
public sealed record AtisConditions(
    int WindDirectionDeg,
    int WindSpeedKnots,
    int VisibilityMeters,
    int TemperatureC,
    int QnhHectopascals,
    PrecipKind Precipitation)
{
    /// <summary>Vent CALME : sous 3 kt on n'annonce pas de direction, personne ne la ressent.</summary>
    public bool IsCalm => WindSpeedKnots < 3;

    /// <summary>
    /// Empreinte des conditions ANNONCÉES. Elle décide de la publication d'un nouveau
    /// bulletin (lettre suivante), donc elle est volontairement GROSSIÈRE : la vitesse est
    /// mise par tranches de 5 kt et la visibilité au kilomètre, sinon la moindre rafale
    /// « publierait » un bulletin toutes les cinq secondes.
    /// </summary>
    public string Digest(string? runway) => string.Join('|', new[]
    {
        IsCalm ? "calm" : WindDirectionDeg.ToString(CultureInfo.InvariantCulture),
        (WindSpeedKnots / 5).ToString(CultureInfo.InvariantCulture),
        (VisibilityMeters / 1000).ToString(CultureInfo.InvariantCulture),
        TemperatureC.ToString(CultureInfo.InvariantCulture),
        QnhHectopascals.ToString(CultureInfo.InvariantCulture),
        Precipitation.ToString(),
        runway ?? "-",
    });
}

/// <summary>
/// Un bulletin ATIS PUBLIÉ : il est figé (conditions et heure d'observation comprises) et
/// rejoué à l'identique jusqu'au bulletin suivant — exactement comme un enregistrement qui
/// tourne en boucle sur la fréquence.
/// </summary>
/// <param name="AirportName">Nom prononçable du terrain (« Ninoy Aquino »).</param>
/// <param name="Icao">ICAO si on a su l'attribuer — sert à choisir la formule d'altimètre.</param>
/// <param name="Letter">Lettre du bulletin (A..Z), annoncée en phonétique OACI.</param>
/// <param name="Runway">Piste en service (« 24 », « 06L »), ou null si indéterminable.</param>
public sealed record AtisReport(
    string AirportName,
    string? Icao,
    char Letter,
    AtisConditions Conditions,
    string? Runway,
    TimeSpan ZuluTime);
