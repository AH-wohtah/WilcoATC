namespace FreqWatch.Atc;

/// <summary>Ce qui déclenche une transmission ATC.</summary>
public enum AtcTrigger
{
    InitialContact, // le joueur se cale sur la fréquence d'une station connue
    ManualTest,     // bouton / touche de test
}

/// <summary>Langue des transmissions (choisit le banc de templates).</summary>
public enum AtcLanguage
{
    English,
    French,
    Auto, // déduite de la voix TTS sélectionnée
}
