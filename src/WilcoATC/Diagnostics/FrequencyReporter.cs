using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WilcoATC.Diagnostics;

/// <summary>Un signalement de fréquence MANQUANTE : qui (Discord), quel aéroport, quelle fréquence.</summary>
public sealed record FrequencyReport(string DiscordUser, string Airport, string Frequency);

/// <summary>
/// Envoie les signalements de fréquence des utilisateurs vers un webhook Discord (collecte
/// communautaire des trous/erreurs du jeu de données). Un seul <see cref="HttpClient"/> partagé ;
/// tout échec est journalisé et remonté (bool) sans jamais lever vers l'UI.
/// </summary>
public sealed class FrequencyReporter
{
    // Webhook Discord de collecte des signalements (fourni par le projet).
    private const string WebhookUrl =
        "https://discordapp.com/api/webhooks/1530879339246391296/N2OxhqI34M4UpKWUWB2esvtN45Jr9TB99fZQVw58JvPdKz9BC9Nc9OVuBvGD1J1LqNEm";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<bool> SendAsync(FrequencyReport r)
    {
        try
        {
            var payload = new
            {
                username = "WilcoATC",
                embeds = new[]
                {
                    new
                    {
                        title = "Missing frequency report",
                        color = 0xD8804A, // orange
                        fields = new object[]
                        {
                            new { name = "Airport", value = Trim(r.Airport), inline = true },
                            new { name = "Frequency", value = Trim(r.Frequency), inline = true },
                            new { name = "Reported by", value = Trim(r.DiscordUser) },
                        },
                    },
                },
            };

            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(WebhookUrl, content).ConfigureAwait(false);
            return resp.IsSuccessStatusCode; // Discord renvoie 204 No Content en cas de succès
        }
        catch (Exception ex)
        {
            FileLog.Exception("signalement de fréquence", ex);
            return false;
        }
    }

    private static string Trim(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();
}
