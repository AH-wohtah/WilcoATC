namespace FreqWatch.Atc.Understanding;

/// <summary>
/// Transforme un texte (transcrit ou saisi) en intention structurée.
/// Défaut : grammaire/mots-clés (déterministe, gratuit). Optionnel : LLM.
/// </summary>
public interface IIntentRecognizer
{
    Task<RecognizedIntent> RecognizeAsync(string text, CancellationToken ct = default);
}
