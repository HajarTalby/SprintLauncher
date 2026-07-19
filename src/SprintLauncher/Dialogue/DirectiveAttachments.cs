namespace SprintLauncher.Dialogue;

/// <summary>
/// Pièces jointes sur une intervention/directive de Hajar (SERZENIA-144 Lot 3 :
/// « pouvoir intervenir auprès de n'importe quel acteur en insérant des images ou
/// autres pièces jointes »). Une intervention reste une ligne de texte (protocole
/// fichier existant : pending-directive.txt, live-input-&lt;role&gt;.txt, stdin de
/// checkpoint) — les chemins de fichiers sont encodés en fin de ligne par un
/// marqueur, puis extraits et les fichiers copiés dans le dossier du run avant
/// d'être référencés dans le prompt de l'acteur. Aucune tentative d'OCR : l'acteur
/// reçoit un chemin de fichier (image, document, vidéo...) qu'il ouvre lui-même
/// s'il en a besoin.
/// </summary>
public static class DirectiveAttachments
{
    private const string StartMarker = "[[SL_ATTACH]]";
    private const string EndMarker = "[[/SL_ATTACH]]";

    // '|' est un caractère interdit dans un chemin Windows — séparateur sûr entre
    // plusieurs pièces jointes sur une même ligne.
    private const char Separator = '|';

    public const string EmptyTextPlaceholder = "(voir pièce(s) jointe(s) ci-dessous)";

    /// <summary>Encode des chemins de fichiers sources à la fin d'une ligne de directive.</summary>
    public static string Encode(string text, IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0) return text;
        return $"{text} {StartMarker}{string.Join(Separator, filePaths)}{EndMarker}";
    }

    /// <summary>
    /// Extrait le marqueur de pièces jointes d'une ligne brute — retourne le texte
    /// nettoyé (marqueur retiré) et les chemins SOURCES (poste de Hajar), pas encore copiés.
    /// Absence de marqueur : la ligne est rendue telle quelle, liste vide.
    /// </summary>
    public static (string Text, IReadOnlyList<string> SourcePaths) Extract(string raw)
    {
        var text = raw ?? "";
        var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start < 0 || end < 0 || end < start) return (text.Trim(), []);

        var segment = text[(start + StartMarker.Length)..end];
        var cleaned = (text[..start] + text[(end + EndMarker.Length)..]).Trim();
        var paths = segment.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (cleaned, paths);
    }

    /// <summary>
    /// Copie les fichiers sources dans le dossier de pièces jointes du run pour la
    /// cible visée. Best-effort : un fichier manquant ou inaccessible est ignoré et
    /// signalé sur la console — jamais bloquant pour l'intervention elle-même.
    /// </summary>
    public static IReadOnlyList<string> CopyToRunFolder(IReadOnlyList<string> sourcePaths, string attachmentsDir)
    {
        if (sourcePaths.Count == 0) return [];
        var copied = new List<string>();
        try { Directory.CreateDirectory(attachmentsDir); }
        catch (IOException ex) { Console.WriteLine($"  ⚠ Dossier de pièces jointes inaccessible ({attachmentsDir}) : {ex.Message}"); return []; }

        foreach (var src in sourcePaths)
        {
            try
            {
                if (!File.Exists(src))
                {
                    Console.WriteLine($"  ⚠ Pièce jointe introuvable, ignorée : {src}");
                    continue;
                }
                var fileName = Path.GetFileName(src);
                var dest = Path.Combine(attachmentsDir, fileName);
                // Collision (même nom déjà copié pour cette cible) : préfixe d'horodatage
                // court plutôt que d'écraser une pièce jointe précédente.
                if (File.Exists(dest))
                    dest = Path.Combine(attachmentsDir, $"{DateTime.UtcNow:HHmmssfff}-{fileName}");
                File.Copy(src, dest, overwrite: false);
                copied.Add(Path.GetFullPath(dest));
            }
            catch (IOException ex) { Console.WriteLine($"  ⚠ Pièce jointe non copiée ({src}) : {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Console.WriteLine($"  ⚠ Pièce jointe non copiée ({src}) : {ex.Message}"); }
        }
        return copied;
    }

    /// <summary>
    /// Section à ajouter au texte de la directive livrée à l'acteur — référence les
    /// chemins COPIÉS dans le dossier du run (jamais les chemins sources du poste de
    /// Hajar, potentiellement inaccessibles à l'acteur).
    /// </summary>
    public static string FormatForPrompt(IReadOnlyList<string> copiedPaths)
    {
        if (copiedPaths.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Pièce(s) jointe(s) — fichier(s) copié(s) dans le dossier du run, ouvre-le(s) toi-même si besoin " +
            "(image, document, vidéo...) :");
        foreach (var p in copiedPaths) sb.AppendLine($"- {p}");
        return sb.ToString().TrimEnd();
    }
}
