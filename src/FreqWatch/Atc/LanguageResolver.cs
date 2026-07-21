namespace FreqWatch.Atc;

/// <summary>
/// Langue parlée par l'ATC, le copilote et le trafic ambiant.
///
/// POUR L'INSTANT : <b>anglais uniquement</b>. Le français a été retiré (phraséologie,
/// trafic ambiant, voix). L'anglais est de toute façon le standard international de
/// l'aviation, donc c'est aussi le comportement le plus réaliste par défaut.
///
/// La classe est conservée (et non supprimée) parce que tout le reste — cerveau ATC,
/// grammaire de reconnaissance, immersion — s'y branche déjà : réintroduire une langue
/// se fera ici, sans toucher aux appelants.
/// </summary>
public sealed class LanguageResolver
{
    /// <summary>Langue de l'utilisateur (ses requêtes, son copilote).</summary>
    public AtcLanguage UserLanguage() => AtcLanguage.English;

    /// <summary>Langue de l'ATC qui vous répond.</summary>
    public AtcLanguage Effective() => AtcLanguage.English;
}
