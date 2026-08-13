using WilcoATC.Common;
using WilcoATC.Stations;

namespace WilcoATC.Traffic;

/// <summary>Ce qu'un appareil du trafic est en train de faire vis-à-vis du terrain.</summary>
public enum TrafficPhase
{
    /// <summary>Rien d'intéressant : trop loin, trop haut, ou en route.</summary>
    None,
    /// <summary>En approche : il descend vers le terrain, encore loin et pas forcément aligné.</summary>
    Inbound,
    /// <summary>En finale, aligné sur une piste, en descente vers le seuil.</summary>
    Final,
    /// <summary>Au sol et lancé sur la piste : il décolle.</summary>
    DepartureRoll,
    /// <summary>Vient de décoller : en montée, s'éloignant du terrain.</summary>
    Departing,
    /// <summary>Au sol, vitesse de roulage : il circule.</summary>
    Taxiing,
}

/// <summary>Verdict complet : la phase, et la piste concernée s'il y en a une.</summary>
public sealed record TrafficSituation(TrafficPhase Phase, string? Runway, double DistanceNm);

/// <summary>
/// Reconnaît, à partir d'une position et d'un cap, ce qu'un appareil du trafic est en train
/// de faire — et sur quelle piste.
///
/// POURQUOI C'EST ISOLÉ ICI : c'est de la géométrie pure, sans simulateur, sans voix, sans
/// horloge. C'est donc la seule partie de la chaîne qu'on peut éprouver au banc d'essai, et
/// c'est aussi celle où une erreur s'entend immédiatement — un contrôleur qui autorise à
/// l'atterrissage un appareil qui vient de décoller ruine l'illusion en une phrase.
///
/// LES SEUILS NE SONT PAS CONNUS. On ne dispose que du point de référence du terrain, pas des
/// coordonnées de chaque seuil de piste. Les distances sont donc comptées jusqu'au CENTRE du
/// terrain : à Roissy, l'écart avec le vrai seuil atteint deux milles. C'est sans importance
/// pour annoncer « en finale », qui n'a pas besoin d'être au dixième de mille près, mais il ne
/// faut pas s'appuyer là-dessus pour du séquencement fin.
/// </summary>
public static class ApproachDetector
{
    /// <summary>Au-delà, on ne s'intéresse plus à l'appareil : il n'est pas en approche finale.</summary>
    private const double MaxFinalNm = 12;

    /// <summary>Une finale se joue bas. Au-dessus, l'appareil est encore en approche ou en transit.</summary>
    private const double MaxFinalAglFeet = 4000;

    /// <summary>Tolérance d'alignement sur l'axe de piste, en degrés.</summary>
    private const double AlignmentToleranceDeg = 25;

    /// <summary>
    /// Tolérance sur le RELÈVEMENT vers le terrain. Plus large que l'alignement : à un mille
    /// du seuil, un léger décalage latéral fait déjà un angle important, sans que l'appareil
    /// cesse d'être en finale.
    /// </summary>
    private const double InboundToleranceDeg = 45;

    /// <summary>En dessous, l'appareil ne descend pas vraiment vers le terrain.</summary>
    private const double MinDescentFpm = -100;

    /// <summary>
    /// Portée de l'approche : au-delà de la finale, jusqu'où l'on considère qu'un appareil
    /// se présente au terrain. C'est la tranche où un contrôleur d'approche parle.
    /// </summary>
    private const double MaxInboundNm = 25;

    /// <summary>Plafond de l'approche. Au-dessus, l'appareil transite, il n'arrive pas.</summary>
    private const double MaxInboundAglFeet = 12_000;

    /// <summary>
    /// Tolérance de RAPPROCHEMENT en approche, plus large qu'en finale : à quinze milles, un
    /// appareil en vent arrière ou en base descend vers le terrain sans lui faire face.
    /// </summary>
    private const double InboundBroadToleranceDeg = 75;

    /// <summary>
    /// Tolérance dans le CIRCUIT, plus large encore. En vent arrière on longe la piste en
    /// sens inverse : le terrain est alors par le travers, donc à quatre-vingt-dix degrés du
    /// cap. Une tolérance de soixante-quinze excluait mécaniquement toute vent arrière — soit
    /// l'essentiel de l'activité d'un terrain tranquille. Cent dix englobe vent arrière et
    /// base tout en écartant les départs en ligne droite, qui s'éloignent à cent quatre-vingts.
    /// </summary>
    private const double CircuitToleranceDeg = 110;

    /// <summary>Au sol au-delà de cette vitesse, c'est une course au décollage, pas du roulage.</summary>
    private const double DepartureRollKnots = 40;

    /// <summary>Montée franche : c'est ce qui distingue un décollage d'un simple survol bas.</summary>
    private const double ClimbFpm = 500;

    /// <summary>En dessous, un appareil au sol est à l'arrêt ou en train de manœuvrer.</summary>
    private const double TaxiKnots = 5;

    /// <summary>
    /// Analyse un appareil vis-à-vis d'un terrain.
    /// </summary>
    /// <param name="runways">Pistes du terrain. Vide, aucune piste ne peut être nommée.</param>
    public static TrafficSituation Analyse(
        double lat, double lon, double aglFeet, double headingTrueDeg,
        double groundSpeedKnots, double verticalSpeedFpm, bool onGround,
        double airportLat, double airportLon, IReadOnlyList<RunwayEnd> runways)
    {
        double distanceNm = Geo.DistanceMeters(lat, lon, airportLat, airportLon) / 1852.0;

        if (onGround)
        {
            // Au sol, l'appareil n'est intéressant que s'il est SUR le terrain — sinon il
            // roule sur un aérodrome voisin dont ce contrôleur n'a pas la charge.
            if (distanceNm > 3) return new TrafficSituation(TrafficPhase.None, null, distanceNm);

            if (groundSpeedKnots >= DepartureRollKnots)
                return new TrafficSituation(TrafficPhase.DepartureRoll,
                                            Aligned(headingTrueDeg, runways), distanceNm);

            return groundSpeedKnots >= TaxiKnots
                ? new TrafficSituation(TrafficPhase.Taxiing, null, distanceNm)
                : new TrafficSituation(TrafficPhase.None, null, distanceNm);
        }

        // En vol. Le relèvement vers le terrain sépare CEUX QUI ARRIVENT de ceux qui partent :
        // sans lui, un appareil qui vient de décoller — même axe, même cap, même altitude —
        // serait pris pour un appareil en finale.
        double bearingToField = Geo.BearingDeg(lat, lon, airportLat, airportLon);
        double towardField = Geo.AngleDifference(bearingToField, headingTrueDeg);

        // DÉPART : il monte et il s'éloigne. Il est encore près du terrain, donc il vient
        // manifestement d'en décoller.
        if (verticalSpeedFpm >= ClimbFpm && distanceNm <= MaxFinalNm && towardField > 90)
            return new TrafficSituation(TrafficPhase.Departing,
                                        Aligned(headingTrueDeg, runways), distanceNm);

        bool descending = verticalSpeedFpm <= MinDescentFpm;

        // FINALE : proche, bas, aligné, face au terrain, ET en descente — on ne se pose pas
        // en montant.
        if (descending && distanceNm <= MaxFinalNm && aglFeet <= MaxFinalAglFeet
            && towardField <= InboundToleranceDeg)
        {
            string? runway = Aligned(headingTrueDeg, runways);
            if (runway is not null)
                return new TrafficSituation(TrafficPhase.Final, runway, distanceNm);
        }

        // CIRCUIT : proche et bas, MÊME EN PALIER. Exiger une descente ici était une erreur —
        // une vent arrière se vole à altitude constante, et un appareil en base ou en attente
        // de son tour se retrouvait invisible alors qu'il est précisément celui à qui la tour
        // parle. C'est ce qui rendait un aérodrome tranquille complètement muet.
        if (distanceNm <= MaxFinalNm && aglFeet <= MaxFinalAglFeet
            && towardField <= CircuitToleranceDeg)
            return new TrafficSituation(TrafficPhase.Inbound,
                                        Aligned(headingTrueDeg, runways), distanceNm);

        // APPROCHE LOINTAINE : plus loin et plus haut, donc il faut une descente franche pour
        // le distinguer d'un simple transit.
        if (descending && distanceNm <= MaxInboundNm && aglFeet <= MaxInboundAglFeet
            && towardField <= InboundBroadToleranceDeg)
            return new TrafficSituation(TrafficPhase.Inbound,
                                        Aligned(headingTrueDeg, runways), distanceNm);

        return new TrafficSituation(TrafficPhase.None, null, distanceNm);
    }

    /// <summary>
    /// Piste dont l'axe correspond le mieux au cap, ou null si aucune n'est dans la tolérance.
    /// On retient la MEILLEURE, pas la première : sur des pistes parallèles rapprochées, la
    /// première venue serait un tirage au sort.
    /// </summary>
    private static string? Aligned(double headingTrueDeg, IReadOnlyList<RunwayEnd> runways)
    {
        string? best = null;
        double bestDelta = double.MaxValue;

        foreach (var r in runways)
        {
            double delta = Geo.AngleDifference(r.HeadingTrue, headingTrueDeg);
            if (delta <= AlignmentToleranceDeg && delta < bestDelta)
            {
                bestDelta = delta;
                best = r.Ident;
            }
        }
        return best;
    }
}
