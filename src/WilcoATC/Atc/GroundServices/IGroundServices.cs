namespace WilcoATC.Atc.GroundServices;

/// <summary>
/// Services au sol (pushback…), déclenchés par l'ATC. Isolé et optionnel :
/// une implémentation inerte suffit si l'utilisateur n'a pas GSX.
/// </summary>
public interface IGroundServices
{
    /// <summary>Déclenche le pushback des services au sol (appelé quand l'ATC l'accorde).</summary>
    void RequestPushback();
}
