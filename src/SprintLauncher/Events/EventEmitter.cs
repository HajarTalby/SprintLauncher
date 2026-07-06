using System.Text.Json;

namespace SprintLauncher.Events;

/// <summary>
/// Protocole d'événements structurés CLI→UI (SERZENIA-143 lot 2).
/// Chaque événement est une ligne JSON préfixée <c>@@EVENT </c> sur stdout,
/// émise uniquement quand la sortie est redirigée (mode UI) — le mode terminal
/// garde l'affichage texte lisible. L'UI parse ces lignes au lieu de deviner
/// l'état par regex sur le texte.
/// </summary>
public static class EventEmitter
{
    public const string Prefix = "@@EVENT ";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool Enabled => Console.IsOutputRedirected;

    public static void Emit(string type, object payload)
    {
        if (!Enabled) return;
        Console.WriteLine(Prefix + JsonSerializer.Serialize(new EventEnvelope(type, payload), _json));
    }

    private sealed record EventEnvelope(string Type, object Data);
}
