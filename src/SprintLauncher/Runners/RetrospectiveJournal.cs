using System.Text;
using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

/// <summary>
/// Journal append-only des points de retrospective produits pendant un sprint.
/// Un nouveau processus peut relire le meme dossier apres une interruption.
/// </summary>
public sealed class RetrospectiveJournal
{
    public const string Marker = "##RETRO";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly SemaphoreSlim _appendGate = new(1, 1);

    public RetrospectiveJournal(string artifactsRoot, string sprint)
    {
        DirectoryPath = Path.Combine(
            artifactsRoot,
            "retro",
            ArtifactNaming.SanitizeSegment(sprint));
    }

    public string DirectoryPath { get; }

    /// <summary>
    /// Extrait les blocs dont la ligne d'ouverture est exactement ##RETRO.
    /// Le bloc se termine au prochain titre Markdown de niveau 2 ou a la fin.
    /// Un marqueur absent, vide ou place sur une ligne non conforme est ignore.
    /// </summary>
    public static IReadOnlyList<string> ExtractPoints(string? actorOutput)
    {
        if (string.IsNullOrWhiteSpace(actorOutput)) return [];

        var lines = actorOutput.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var points = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsMarker(lines[i])) continue;

            var captured = new List<string>();
            for (i++; i < lines.Length && !IsLevelTwoHeading(lines[i]); i++)
                captured.Add(lines[i]);

            i--;
            var content = string.Join("\n", captured).Trim();
            if (content.Length > 0)
                points.Add(content);
        }

        return points;
    }

    public Task<RetrospectiveWriteResult> CaptureAsync(
        ActorRole role,
        string? actorOutput,
        CancellationToken ct = default)
    {
        var points = ExtractPoints(actorOutput);
        return points.Count == 0
            ? Task.FromResult(RetrospectiveWriteResult.NoContent)
            : AppendAsync(role, "POINT AU FIL DU SPRINT", string.Join("\n\n", points), ct);
    }

    public Task<RetrospectiveWriteResult> AppendFinalSynthesisAsync(
        ActorRole role,
        string? actorOutput,
        CancellationToken ct = default)
    {
        return string.IsNullOrWhiteSpace(actorOutput)
            ? Task.FromResult(RetrospectiveWriteResult.NoContent)
            : AppendAsync(role, "SYNTHESE FINALE", actorOutput.Trim(), ct);
    }

    /// <summary>
    /// Construit la matiere premiere injectee dans la phase retrospective finale.
    /// La lecture porte sur tous les fichiers deja presents, y compris apres un
    /// redemarrage complet du Sprint Launcher.
    /// </summary>
    public async Task<string> ReadForFinalPhaseAsync(CancellationToken ct = default)
    {
        string[] files;
        try
        {
            files = Directory.Exists(DirectoryPath)
                ? Directory.GetFiles(DirectoryPath, "retro-*.md")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
        }
        catch (IOException)
        {
            files = [];
        }

        var entries = new List<(string File, string Content)>();
        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, ct);
                if (!string.IsNullOrWhiteSpace(content))
                    entries.Add((Path.GetFileName(file), content.Trim()));
            }
            catch (IOException)
            {
                // Un fichier momentanement verrouille ne doit pas bloquer la synthese.
            }
        }

        if (entries.Count == 0)
            return "## Points de retrospective consignes pendant le sprint\n\n" +
                   "(aucun point n'a ete consigne avant la phase finale)";

        var sb = new StringBuilder();
        sb.AppendLine("## Points de retrospective consignes pendant le sprint");
        sb.AppendLine();
        sb.AppendLine("Utilise ces traces persistantes comme matiere premiere de la synthese finale.");
        foreach (var (file, content) in entries)
        {
            sb.AppendLine();
            sb.AppendLine($"### {file}");
            sb.AppendLine();
            sb.AppendLine(content);
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<RetrospectiveWriteResult> AppendAsync(
        ActorRole role,
        string kind,
        string content,
        CancellationToken ct)
    {
        var file = Path.Combine(DirectoryPath, ArtifactNaming.Retrospective(role));
        await _appendGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var separator = File.Exists(file) && new FileInfo(file).Length > 0 ? "\n\n" : "";
            var block =
                $"{separator}## {kind} - {role} - {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC\n\n" +
                content.Trim() + "\n";
            await File.AppendAllTextAsync(file, block, Utf8WithoutBom, CancellationToken.None);
            return new RetrospectiveWriteResult(content.Trim(), file, null);
        }
        catch (IOException ex)
        {
            return new RetrospectiveWriteResult(content.Trim(), file, ex.Message);
        }
        finally
        {
            _appendGate.Release();
        }
    }

    private static bool IsMarker(string line) =>
        line.Trim().Equals(Marker, StringComparison.OrdinalIgnoreCase);

    private static bool IsLevelTwoHeading(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("##", StringComparison.Ordinal) &&
               (trimmed.Length == 2 || trimmed[2] != '#');
    }
}

public sealed record RetrospectiveWriteResult(string? Content, string? FilePath, string? Error)
{
    public static RetrospectiveWriteResult NoContent { get; } = new(null, null, null);

    public bool HasContent => Content is not null;
    public bool Persisted => HasContent && Error is null;
}
