namespace FreqWatch.Sim;

/// <summary>
/// Contrat de la couche SimConnect. C'est la seule chose dont dépend l'UI :
/// elle ne référence jamais les types SimConnect directement.
///
/// Tous les événements sont levés depuis le thread de pompage SimConnect :
/// l'abonné (le ViewModel) est responsable du marshalling vers le thread UI.
/// </summary>
public interface ISimConnectService : IDisposable
{
    ConnectionState State { get; }
    string? StatusDetail { get; }

    /// <summary>Changement d'état de connexion (Waiting / Connected / MissingDependency).</summary>
    event Action<ConnectionState, string?>? StateChanged;

    /// <summary>Nouvel instantané radio complet (pour rafraîchir les afficheurs).</summary>
    event Action<RadioSnapshot>? RadioSnapshotReceived;

    /// <summary>Changement atomique détecté (pour le journal horodaté).</summary>
    event Action<RadioChange>? RadioChanged;

    /// <summary>Nouveau contexte de vol (position, vitesses, altitudes, aéroport proche…).</summary>
    event Action<ContextSnapshot>? ContextReceived;

    /// <summary>Identité de l'avion (renvoyée à la connexion et à chaque changement d'appareil).</summary>
    event Action<AircraftSnapshot>? AircraftReceived;

    /// <summary>Démarre la boucle de connexion + pompage (non bloquant).</summary>
    void Start();

    /// <summary>Arrête proprement la boucle et libère SimConnect.</summary>
    void Stop();

    /// <summary>Allume/éteint le beacon (déclencheur de l'auto-pushback GSX). Thread-safe.</summary>
    void SetBeaconLight(bool on);
}
