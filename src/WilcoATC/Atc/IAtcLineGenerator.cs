namespace FreqWatch.Atc;

/// <summary>
/// Décide QUOI dire. Implémentations : templates déterministes (par défaut, gratuit,
/// hors-ligne) et adaptateur LLM optionnel (Ollama local / BYOK cloud), tous deux
/// derrière cette même interface. Le LLM n'est jamais obligatoire.
/// </summary>
public interface IAtcLineGenerator
{
    /// <summary>Produit le texte d'une transmission ATC (incluant l'indicatif du joueur).</summary>
    Task<string> GenerateAsync(FlightSnapshot flight, AtcTrigger trigger, CancellationToken ct = default);
}
