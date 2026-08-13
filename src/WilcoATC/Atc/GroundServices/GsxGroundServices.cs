using WilcoATC.Settings;
using WilcoATC.Sim;

namespace WilcoATC.Atc.GroundServices;

/// <summary>
/// Intégration GSX (FSDreamTeam), optionnelle et SANS module WASM.
///
/// Mécanisme : quand l'ATC accorde le pushback, on allume le PHARE ANTICOLLISION
/// (beacon) via SimConnect. Avec l'option <b>auto-pushback</b> de GSX activée et le
/// frein de parking serré, GSX demande alors automatiquement le pushback
/// (comportement documenté par FSDreamTeam). Si GSX/l'option ne sont pas présents,
/// l'effet se limite à allumer le beacon (inoffensif).
///
/// Désactivé par défaut (réglage <see cref="AppSettings.GsxIntegrationEnabled"/>).
/// </summary>
public sealed class GsxGroundServices : IGroundServices
{
    private readonly ISimConnectService _sim;
    private readonly SettingsService _settings;

    public GsxGroundServices(ISimConnectService sim, SettingsService settings)
    {
        _sim = sim;
        _settings = settings;
    }

    public void RequestPushback()
    {
        if (!_settings.Current.GsxIntegrationEnabled) return;
        _sim.SetBeaconLight(true); // -> déclenche l'auto-pushback GSX
    }
}
