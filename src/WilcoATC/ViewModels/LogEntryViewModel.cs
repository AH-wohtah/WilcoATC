namespace WilcoATC.ViewModels;

/// <summary>Catégorie d'une ligne de journal, utilisée pour la coloration.</summary>
public enum LogKind
{
    Change,    // changement de fréquence
    Transmit,  // changement de radio émettrice
    Initial,   // état initial après connexion
    System,    // message système (connexion, attente…)
    Atc,       // transmission ATC (voix)
    Pilot,     // requête du pilote
    Refused,   // décision ATC : refus
    Copilot,   // annonce du copilote virtuel
    Chatter,   // trafic radio ambiant (équipages / contrôle « IA »)
    Atis,      // bulletin ATIS du terrain (diffusion en boucle)
}

/// <summary>Une ligne du journal horodaté (immuable).</summary>
public sealed class LogEntryViewModel
{
    public LogEntryViewModel(string time, string text, LogKind kind)
    {
        Time = time;
        Text = text;
        Kind = kind;
    }

    public string Time { get; }  // "HH:mm:ss"
    public string Text { get; }
    public LogKind Kind { get; }
}
