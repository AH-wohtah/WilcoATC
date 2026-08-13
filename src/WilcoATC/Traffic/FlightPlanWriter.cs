using System.Globalization;
using System.IO;
using System.Text;

namespace WilcoATC.Traffic;

/// <summary>Un point du plan de vol : soit un aéroport (par code OACI), soit un point libre.</summary>
/// <param name="Id">Identifiant affiché — code OACI pour un aéroport, « WP1 »… sinon.</param>
/// <param name="Icao">Code OACI si c'est un aéroport ; null pour un point libre.</param>
public sealed record PlanWaypoint(string Id, double Lat, double Lon, double AltitudeFeet, string? Icao = null);

/// <summary>
/// Écrit un plan de vol au format .PLN du simulateur.
///
/// POURQUOI CE FICHIER EXISTE : <c>AICreateEnrouteATCAircraft</c> ne prend pas une liste de
/// points en mémoire, il prend un CHEMIN DE FICHIER. C'est le simulateur qui lit le plan et
/// qui pilote ensuite l'appareil — il le fait rouler, décoller, monter, descendre et atterrir
/// avec son propre moteur. C'est toute la différence avec un appareil déplacé de l'extérieur :
/// il ne peut pas être saccadé, puisque personne ne le pousse.
///
/// LE FORMAT N'EST PAS DEVINÉ. Il est repris d'un plan livré avec Microsoft Flight Simulator
/// 2024 (<c>asobo-discovery-everest/everest.pln</c>) : mêmes balises, même ordre, même
/// notation des coordonnées. Deviner ce format aurait été refaire l'erreur des titres de
/// conteneur — un fichier presque juste est un fichier rejeté.
/// </summary>
public static class FlightPlanWriter
{
    /// <summary>
    /// Coordonnée au format du simulateur : « N27° 54' 22.14",E86° 46' 39.92",+001500.00 ».
    ///
    /// TOUT EST EN CULTURE INVARIANTE. Sur un Windows français, un « F2 » ordinaire écrirait
    /// « 22,14 » — virgule décimale — et le simulateur rejetterait le fichier sans un mot.
    /// C'est le genre de défaut qui ne se voit que sur la machine de l'utilisateur.
    /// </summary>
    public static string FormatLla(double lat, double lon, double altitudeFeet)
    {
        string latPart = Dms(lat, 'N', 'S');
        string lonPart = Dms(lon, 'E', 'W');
        string alt = (altitudeFeet < 0 ? "-" : "+")
                   + Math.Abs(altitudeFeet).ToString("000000.00", CultureInfo.InvariantCulture);
        return $"{latPart},{lonPart},{alt}";
    }

    private static string Dms(double value, char positive, char negative)
    {
        char hemisphere = value < 0 ? negative : positive;
        double abs = Math.Abs(value);

        int degrees = (int)abs;
        double minutesTotal = (abs - degrees) * 60.0;
        int minutes = (int)minutesTotal;
        double seconds = (minutesTotal - minutes) * 60.0;

        // L'arrondi des secondes peut atteindre 60,00 : sans report, on écrirait « 59' 60.00" »,
        // qui n'est pas une coordonnée valide.
        if (Math.Round(seconds, 2) >= 60.0)
        {
            seconds = 0;
            minutes++;
        }
        if (minutes >= 60)
        {
            minutes = 0;
            degrees++;
        }

        string sec = seconds.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{hemisphere}{degrees.ToString(CultureInfo.InvariantCulture)}° " +
               $"{minutes.ToString(CultureInfo.InvariantCulture)}' {sec}\"";
    }

    /// <summary>
    /// Construit le contenu du plan. Le premier et le dernier point donnent le départ et la
    /// destination — le simulateur s'en sert pour raccorder l'appareil à son ATC.
    /// </summary>
    public static string Build(IReadOnlyList<PlanWaypoint> waypoints, int cruisingAltFeet,
                               string flightRules = "IFR")
    {
        if (waypoints.Count < 2)
            throw new ArgumentException("Un plan de vol demande au moins deux points.", nameof(waypoints));

        var first = waypoints[0];
        var last = waypoints[^1];

        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("""<SimBase.Document Type="AceXML" version="1,0">""");
        sb.AppendLine("    <Descr>AceXML Document</Descr>");
        sb.AppendLine("    <FlightPlan.FlightPlan>");
        sb.AppendLine($"        <Title>{Escape(first.Id)} to {Escape(last.Id)}</Title>");
        sb.AppendLine($"        <FPType>{Escape(flightRules)}</FPType>");
        sb.AppendLine("        <RouteType>Direct</RouteType>");
        sb.AppendLine($"        <CruisingAlt>{cruisingAltFeet.ToString(CultureInfo.InvariantCulture)}</CruisingAlt>");
        sb.AppendLine($"        <DepartureID>{Escape(first.Id)}</DepartureID>");
        sb.AppendLine($"        <DepartureLLA>{FormatLla(first.Lat, first.Lon, first.AltitudeFeet)}</DepartureLLA>");
        sb.AppendLine($"        <DestinationID>{Escape(last.Id)}</DestinationID>");
        sb.AppendLine($"        <DestinationLLA>{FormatLla(last.Lat, last.Lon, last.AltitudeFeet)}</DestinationLLA>");
        sb.AppendLine($"        <Descr>{Escape(first.Id)}, {Escape(last.Id)}</Descr>");
        sb.AppendLine($"        <DepartureName>{Escape(first.Id)}</DepartureName>");
        sb.AppendLine($"        <DestinationName>{Escape(last.Id)}</DestinationName>");
        sb.AppendLine("        <AppVersion>");
        sb.AppendLine("            <AppVersionMajor>11</AppVersionMajor>");
        sb.AppendLine("            <AppVersionBuild>282174</AppVersionBuild>");
        sb.AppendLine("        </AppVersion>");

        foreach (var wp in waypoints)
        {
            sb.AppendLine($"        <ATCWaypoint id=\"{Escape(wp.Id)}\">");
            sb.AppendLine($"            <ATCWaypointType>{(wp.Icao is null ? "User" : "Airport")}</ATCWaypointType>");
            sb.AppendLine($"            <WorldPosition>{FormatLla(wp.Lat, wp.Lon, wp.AltitudeFeet)}</WorldPosition>");
            if (wp.Icao is { Length: > 0 } icao)
            {
                sb.AppendLine("            <ICAO>");
                sb.AppendLine($"                <ICAOIdent>{Escape(icao)}</ICAOIdent>");
                sb.AppendLine("            </ICAO>");
            }
            sb.AppendLine("        </ATCWaypoint>");
        }

        sb.AppendLine("    </FlightPlan.FlightPlan>");
        sb.AppendLine("</SimBase.Document>");
        return sb.ToString();
    }

    /// <summary>
    /// Écrit le plan sur disque et renvoie le chemin SANS l'extension — c'est sous cette forme
    /// que <c>AICreateEnrouteATCAircraft</c> l'attend.
    /// </summary>
    public static string Save(string directory, string name,
                              IReadOnlyList<PlanWaypoint> waypoints, int cruisingAltFeet,
                              string flightRules = "IFR")
    {
        Directory.CreateDirectory(directory);
        string full = Path.Combine(directory, name + ".pln");
        File.WriteAllText(full, Build(waypoints, cruisingAltFeet, flightRules), new UTF8Encoding(false));
        return Path.Combine(directory, name);
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
