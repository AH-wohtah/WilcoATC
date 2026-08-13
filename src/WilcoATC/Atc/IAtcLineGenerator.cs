namespace WilcoATC.Atc;

/// <summary>
/// Décide QUOI dire. Une seule implémentation : <see cref="TemplateAtcLineGenerator"/>,
/// déterministe, gratuite et hors-ligne. L'adaptateur LLM a été supprimé — il faisait
/// attendre une réponse réseau à chaque transmission.
/// </summary>
public interface IAtcLineGenerator
{
    /// <summary>Produit le texte d'une transmission ATC (incluant l'indicatif du joueur).</summary>
    Task<string> GenerateAsync(FlightSnapshot flight, AtcTrigger trigger, CancellationToken ct = default);
}
