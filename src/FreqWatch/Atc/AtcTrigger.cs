namespace FreqWatch.Atc;

/// <summary>Ce qui déclenche une transmission ATC.</summary>
public enum AtcTrigger
{
    InitialContact, // le joueur se cale sur la fréquence d'une station connue
    ManualTest,     // bouton / touche de test
}

/// <summary>
/// Langue des transmissions. UNE SEULE pour l'instant : l'anglais, standard mondial de
/// la phraséologie (ATC, copilote, trafic ambiant, reconnaissance vocale et voix TTS).
/// L'énumération est conservée pour que l'ajout d'une autre langue reste localisé.
/// </summary>
public enum AtcLanguage
{
    English,
}
