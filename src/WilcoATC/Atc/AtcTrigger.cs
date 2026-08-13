namespace WilcoATC.Atc;

/// <summary>Ce qui déclenche une transmission ATC.</summary>
public enum AtcTrigger
{
    InitialContact, // le joueur se cale sur la fréquence d'une station connue
    ManualTest,     // bouton / touche de test
}
