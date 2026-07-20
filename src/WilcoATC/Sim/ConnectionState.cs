namespace FreqWatch.Sim;

/// <summary>État global de la liaison SimConnect, exposé à l'UI.</summary>
public enum ConnectionState
{
    /// <summary>Simu absent / connexion perdue : on réessaie en boucle.</summary>
    Waiting,

    /// <summary>Connecté au simulateur, données en réception.</summary>
    Connected,

    /// <summary>DLL SimConnect introuvable ou incompatible (état fatal, non récupérable).</summary>
    MissingDependency,
}
