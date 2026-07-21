using FreqWatch.Atc.Context;

namespace FreqWatch.Immersion;

/// <summary>État de vol minimal nécessaire aux annonces du copilote (issu de SimConnect).</summary>
public readonly record struct CopilotState(
    bool OnGround,
    double IasKnots,
    double AglFeet,
    double MslFeet,
    double VerticalSpeedFpm,
    double GroundSpeedKnots,
    FlightPhase Phase);

/// <summary>Vitesses de référence + option checklists (réglages).</summary>
public readonly record struct CopilotConfig(int V1, int Vr, int V2, bool Checklists);

/// <summary>
/// Copilote virtuel : décide QUOI annoncer et QUAND, à partir de l'état du simulateur.
/// Logique PURE (aucun audio, aucun I/O) donc entièrement testable.
///
/// Chaque annonce est faite UNE SEULE FOIS par vol ; la mémoire est réarmée au parking.
/// Les seuils sont détectés par FRANCHISSEMENT (valeur précédente -> valeur courante) pour
/// ne pas répéter l'annonce tant qu'on reste au-dessus/en dessous du seuil.
/// </summary>
public sealed class CopilotDirector
{
    private readonly HashSet<string> _said = new();
    private CopilotState? _prev;
    private FlightPhase _lastPhase = FlightPhase.Unknown;

    public void Reset()
    {
        _said.Clear();
        _prev = null;
        _lastPhase = FlightPhase.Unknown;
    }

    /// <summary>Renvoie les clés d'annonces à dire pour cette frame (souvent vide).</summary>
    public IReadOnlyList<string> Update(CopilotState s, CopilotConfig cfg)
    {
        var say = new List<string>();

        // Nouveau vol : au parking on réarme toutes les annonces.
        if (s.Phase == FlightPhase.Parked && _lastPhase != FlightPhase.Parked) _said.Clear();

        // ---- Checklists sur transition de phase ----
        if (cfg.Checklists && s.Phase != _lastPhase)
        {
            string? chk = s.Phase switch
            {
                FlightPhase.Parked => _lastPhase is FlightPhase.TaxiIn or FlightPhase.Landing
                                      ? "chk_shutdown" : "chk_before_start",
                FlightPhase.TaxiOut => "chk_before_takeoff",
                FlightPhase.Airborne => "chk_after_takeoff",
                FlightPhase.Approach => "chk_approach",
                FlightPhase.TaxiIn => "chk_after_landing",
                _ => null,
            };
            if (chk is not null) Say(say, chk);
        }
        _lastPhase = s.Phase;

        // Première frame : aucun franchissement calculable.
        if (_prev is not { } p) { _prev = s; return say; }
        _prev = s;

        // ---- Roulage au décollage ----
        if (s.OnGround && Crossed(p.IasKnots, s.IasKnots, 80, up: true)) Say(say, "eighty");
        if (s.OnGround && cfg.V1 > 0 && Crossed(p.IasKnots, s.IasKnots, cfg.V1, up: true)) Say(say, "v1");
        if (s.OnGround && cfg.Vr > 0 && Crossed(p.IasKnots, s.IasKnots, cfg.Vr, up: true)) Say(say, "rotate");

        // ---- Après décollage ----
        bool departed = _said.Contains("rotate");
        if (!s.OnGround && cfg.V2 > 0 && s.IasKnots >= cfg.V2 && departed) Say(say, "v2");
        if (!s.OnGround && s.VerticalSpeedFpm > 300 && s.AglFeet > 20 && departed)
        {
            if (Say(say, "positive_rate")) Say(say, "gear_up");
        }
        if (!s.OnGround && s.VerticalSpeedFpm > 200 && Crossed(p.AglFeet, s.AglFeet, 1000, up: true))
            Say(say, "climb_1000");

        // ---- Passage 10 000 ft (montée / descente) ----
        if (Crossed(p.MslFeet, s.MslFeet, 10000, up: true)) Say(say, "ten_thousand_up");
        if (Crossed(p.MslFeet, s.MslFeet, 10000, up: false)) Say(say, "ten_thousand_down");

        // ---- Approche (en descente uniquement) ----
        if (!s.OnGround && s.VerticalSpeedFpm < -200)
        {
            if (Crossed(p.AglFeet, s.AglFeet, 1000, up: false)) Say(say, "app_1000");
            if (Crossed(p.AglFeet, s.AglFeet, 500, up: false)) Say(say, "app_500");
            if (Crossed(p.AglFeet, s.AglFeet, 200, up: false)) Say(say, "minimums");
            if (Crossed(p.AglFeet, s.AglFeet, 100, up: false)) Say(say, "app_100");
            if (Crossed(p.AglFeet, s.AglFeet, 50, up: false)) Say(say, "app_50");
        }

        // ---- Toucher des roues / décélération ----
        if (s.OnGround && !p.OnGround && s.GroundSpeedKnots > 40)
        {
            Say(say, "spoilers");
            Say(say, "reverse");
        }
        if (s.OnGround && _said.Contains("spoilers") && Crossed(p.GroundSpeedKnots, s.GroundSpeedKnots, 70, up: false))
            Say(say, "seventy");

        return say;
    }

    private bool Say(List<string> sink, string key)
    {
        if (!_said.Add(key)) return false;
        sink.Add(key);
        return true;
    }

    /// <summary>Franchissement d'un seuil entre deux mesures (montant ou descendant).</summary>
    private static bool Crossed(double before, double now, double threshold, bool up)
        => up ? before < threshold && now >= threshold
              : before > threshold && now <= threshold;
}
