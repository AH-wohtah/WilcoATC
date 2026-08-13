using WilcoATC.Atc.Localization;
using WilcoATC.Settings;

namespace WilcoATC.Atc;

/// <summary>
/// Décide en QUELLE LANGUE le contrôleur parle, à chaque instant.
///
/// Trois entrées, dans cet ordre de priorité :
///
///  1. le RÉGLAGE — l'utilisateur peut imposer l'anglais partout, ou une langue fixe ;
///  2. la LANGUE DU PILOTE — dès qu'on lui parle français, le contrôleur répond en
///     français et y reste. C'est le comportement réel : un contrôleur bascule sur la
///     langue de son interlocuteur et n'en change plus sans raison ;
///  3. le PAYS SURVOLÉ — à défaut, la langue de contrôle du terrain (préfixe OACI).
///
/// La langue du pilote est OUBLIÉE en changeant de fréquence : on aborde chaque nouveau
/// contrôleur dans la langue du pays, et c'est de nouveau le premier échange qui tranche.
///
/// Le contrôleur COMPREND toujours les deux : la reconnaissance vocale est multilingue et
/// la grammaire d'intentions est essayée dans toutes les langues (voir
/// <see cref="Understanding.GrammarIntentRecognizer"/>). Ce résolveur ne décide que de la
/// langue d'ÉMISSION.
/// </summary>
public sealed class LanguageResolver
{
    private readonly SettingsService _settings;

    /// <summary>
    /// Une voix est-elle installée pour cette langue ? Sans modèle vocal, le contrôleur
    /// RESTE EN ANGLAIS : la phraséologie OACI est valable partout, alors qu'un texte
    /// français lu par un modèle anglais est inécoutable.
    /// </summary>
    private readonly Func<AtcLanguage, bool> _hasVoice;

    /// <summary>ICAO du terrain courant, poussé par le contrôleur ATC à chaque mise à jour.</summary>
    private volatile string? _airportIcao;

    /// <summary>Langue employée par le pilote sur la fréquence courante, si on l'a identifiée.</summary>
    private AtcLanguage? _pilotLanguage;

    public LanguageResolver(SettingsService settings, Func<AtcLanguage, bool> hasVoice)
    {
        _settings = settings;
        _hasVoice = hasVoice;
    }

    /// <summary>Terrain courant (préfixe OACI) : c'est lui qui donne la langue du pays.</summary>
    public void SetAirport(string? icao)
        => _airportIcao = string.IsNullOrWhiteSpace(icao) ? null : icao!.Trim().ToUpperInvariant();

    /// <summary>
    /// Le pilote vient de parler dans cette langue : le contrôleur s'y aligne pour la suite
    /// de l'échange. Sans effet si le réglage impose une langue.
    /// </summary>
    public void NotePilotLanguage(AtcLanguage language) => _pilotLanguage = language;

    /// <summary>
    /// Changement de fréquence : on repart de la langue du pays. Sinon un pilote ayant parlé
    /// anglais au Sol s'entendrait répondre en anglais par la Tour sans l'avoir demandé.
    /// </summary>
    public void ResetPilotLanguage() => _pilotLanguage = null;

    /// <summary>Langue de CONTRÔLE du pays survolé, indépendamment de ce que dit le pilote.</summary>
    public AtcLanguage CountryLanguage() => AtcCountryLanguages.ForIcao(_airportIcao);

    /// <summary>
    /// Langue de l'utilisateur. Anglais tant que la compréhension multilingue est coupée :
    /// la grammaire n'essaie qu'une table, il n'y a donc rien d'autre à détecter.
    /// </summary>
    public AtcLanguage UserLanguage() => AtcLanguage.English;

    /// <summary>
    /// Langue dans laquelle le contrôleur émet.
    ///
    /// <b>ANGLAIS UNIQUEMENT POUR L'INSTANT.</b> Le multilingue est désactivé ici, en UN SEUL
    /// POINT et volontairement : c'est le standard OACI, et c'est ce qui a été demandé. La
    /// matière reste en place et n'attend que ce retour — phraséologie traduite dans
    /// <see cref="Localization.AtcPhrases"/> et les blocs « i18n » d'<c>atc-rules.json</c>,
    /// mots-clés pilote dans <see cref="Understanding.IntentKeywords"/>, langue de chaque pays
    /// dans <see cref="Localization.AtcCountryLanguages"/>.
    ///
    /// Pour le réactiver : rendre son corps à cette méthode (réglage, puis
    /// <c>_pilotLanguage ?? CountryLanguage()</c>, filtré par <c>_hasVoice</c>), remettre les
    /// voix locales au catalogue et rouvrir la grammaire aux autres langues.
    /// </summary>
    public AtcLanguage Effective() => AtcLanguage.English;
}
