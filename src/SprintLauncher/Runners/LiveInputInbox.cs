using System.Text;
using SprintLauncher.Notifications;
using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

/// <summary>
/// Boîte de réception des interventions live pour un acteur : un fichier texte
/// (live-input-&lt;role&gt;.txt) où l'UI AJOUTE une ligne par intervention pendant que
/// l'acteur travaille. Le runner draine les nouvelles lignes et les pousse dans le
/// stdin du process acteur, cadrées par LiveChatProtocol.
///
/// Suit une position de lecture : chaque appel à <see cref="DrainNewLines"/> ne rend
/// que ce qui a été ajouté depuis le précédent — pas de doublon, pas de perte. Le
/// fichier est le canal inter-process (UI ↔ CLI) le plus simple et robuste ici :
/// pas de pipe nommé à gérer, survit à un lag de l'UI, et se relit tel quel.
/// </summary>
public sealed class LiveInputInbox
{
    private readonly string _path;
    private long _position;

    public LiveInputInbox(string path)
    {
        _path = path;
        // On démarre à la FIN d'un fichier préexistant : une intervention d'un tour
        // précédent ne doit pas être rejouée au tour suivant.
        _position = SafeLength(path);
    }

    public string Path => _path;

    /// <summary>
    /// Rend les lignes non vides ajoutées depuis le dernier appel. Best-effort :
    /// un fichier momentanément verrouillé (écriture UI en cours) rend une liste vide,
    /// le prochain appel rattrapera. Une ligne partielle (sans \n final) n'est pas
    /// consommée tant qu'elle n'est pas complète.
    /// </summary>
    public IReadOnlyList<string> DrainNewLines()
    {
        if (!File.Exists(_path)) return [];

        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _position)
            {
                // Fichier tronqué/réécrit (nouveau run) : on repart de zéro.
                _position = 0;
            }
            if (fs.Length == _position) return [];

            fs.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var content = reader.ReadToEnd();

            // Ne consommer que jusqu'au dernier \n : une ligne en cours d'écriture reste
            // pour le prochain drain.
            var lastNewline = content.LastIndexOf('\n');
            if (lastNewline < 0) return []; // rien de complet encore

            var consumed = content[..(lastNewline + 1)];
            _position += Encoding.UTF8.GetByteCount(consumed);

            return consumed
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        catch (IOException) { return []; }        // verrouillé — prochain passage
        catch (UnauthorizedAccessException) { return []; }
    }

    private static long SafeLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    /// <summary>Chemin de l'inbox d'un acteur dans un dossier live donné.</summary>
    public static string PathFor(string liveDir, ActorRole role) =>
        System.IO.Path.Combine(liveDir, $"live-input-{role}.txt");

    /// <summary>Clé Slack canonique de la famille qui exécute ce rôle.</summary>
    public static string ActorKeyFor(ActorRole role) =>
        ActorKeyFor(role.ToString());

    /// <summary>Clé Slack canonique d'un nom de rôle ou d'orchestrateur.</summary>
    public static string ActorKeyFor(string role) =>
        SlackSink.ActorFromRole(role);

    /// <summary>Chemin de l'inbox Slack d'un acteur dans un dossier live donné.</summary>
    public static string PathForActorKey(string liveDir, string actorKey) =>
        System.IO.Path.Combine(liveDir, $"live-input-{actorKey}.txt");

    /// <summary>
    /// Les deux boîtes à surveiller pendant un tour : celle du rôle (UI historique)
    /// et celle de la famille Slack. Chaque instance conserve son propre curseur.
    /// </summary>
    public static IReadOnlyList<LiveInputInbox> ForRoleAndActor(string liveDir, ActorRole role) =>
    [
        new LiveInputInbox(PathFor(liveDir, role)),
        new LiveInputInbox(PathForActorKey(liveDir, ActorKeyFor(role))),
    ];
}
