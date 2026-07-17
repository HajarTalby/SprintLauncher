using SprintLauncher.Prompts;

namespace SprintLauncher.Dialogue;

/// <summary>
/// Adressage d'une directive de Hajar à un acteur ou à un groupe précis (retour du
/// 2026-07-16 : « je veux pouvoir définir dans mon intervention à quel acteur je
/// dirige mon retour »). Sans adressage, la directive s'applique au prochain acteur
/// qui parle — comportement historique conservé.
/// </summary>
public sealed record DirectiveAddress(ActorRole? Actor, ActorGroup? Group, string Text)
{
    public bool IsTargeted => Actor is not null || Group is not null;

    /// <summary>Libellé du destinataire pour l'UI, le journal et le registre.</summary>
    public string TargetLabel =>
        Actor?.ToString() ?? Group?.ToString() ?? "tous les acteurs";
}

public static class DirectiveAddressing
{
    // Alias parlants → rôle. Les noms d'enum exacts sont acceptés d'office (voir Parse).
    private static readonly Dictionary<string, ActorRole> _actorAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codex"]            = ActorRole.GptImplementation,
        ["gpt"]              = ActorRole.GptImplementation,
        ["gptimpl"]          = ActorRole.GptImplementation,
        ["ccode"]            = ActorRole.ClaudeImplementation,
        ["claude-code"]      = ActorRole.ClaudeImplementation,
        ["claudecode"]       = ActorRole.ClaudeImplementation,
        ["claudeimpl"]       = ActorRole.ClaudeImplementation,
        ["analyse-ccode"]    = ActorRole.AnalysisCcode,
        ["analyse-codex"]    = ActorRole.AnalysisCodex,
        ["qa-claude"]        = ActorRole.ClaudeQaVerdict,
        ["qa-gpt"]           = ActorRole.GptQaVerdict,
        ["pilotage-claude"]  = ActorRole.CommitteePilotageClaudeChat,
        ["pilotage-gpt"]     = ActorRole.CommitteePilotageGptChat,
    };

    private static readonly Dictionary<string, ActorGroup> _groupAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qa"]          = ActorGroup.Qa,
        ["pilotage"]    = ActorGroup.CommitteePilotage,
        ["comite"]      = ActorGroup.CommitteePilotage,
        ["comité"]      = ActorGroup.CommitteePilotage,
        ["analyse"]     = ActorGroup.Analysis,
        ["arbitrage"]   = ActorGroup.CommitteeArbitrage,
    };

    /// <summary>
    /// Adressage MULTIPLE (retour de Hajar, 2026-07-17 : « quand je dirige mon
    /// intervention à plusieurs acteurs, il n'y a que le premier qui est pris en
    /// compte ») : « @ccode et @codex fais X » → une directive pour CHAQUE cible,
    /// même texte. Les connecteurs entre cibles (et, +, virgule, &) sont absorbés.
    /// Sans aucun @ en tête : une seule adresse non ciblée (comportement historique).
    /// </summary>
    public static IReadOnlyList<DirectiveAddress> ParseMulti(string raw)
    {
        var remaining = (raw ?? "").Trim();
        var actors = new List<ActorRole>();
        var groups = new List<ActorGroup>();

        while (remaining.StartsWith('@'))
        {
            var probe = Parse(remaining);
            if (!probe.IsTargeted) break; // @inconnu : tout reste du texte libre
            if (probe.Actor is { } a && !actors.Contains(a)) actors.Add(a);
            if (probe.Group is { } g && !groups.Contains(g)) groups.Add(g);
            remaining = probe.Text;

            // Connecteurs entre deux cibles : « et », « + », « & », virgule.
            var before = remaining;
            while (true)
            {
                var trimmed = remaining.TrimStart(' ', ',', '+', '&');
                if (trimmed.StartsWith("et ", StringComparison.OrdinalIgnoreCase) &&
                    trimmed.Length > 3 && trimmed.TrimStart()[..1] == "e")
                {
                    var afterEt = trimmed[3..].TrimStart();
                    if (afterEt.StartsWith('@')) { remaining = afterEt; continue; }
                }
                remaining = trimmed;
                break;
            }
            if (remaining == before && !remaining.StartsWith('@')) break;
        }

        if (actors.Count == 0 && groups.Count == 0)
            return [new DirectiveAddress(null, null, (raw ?? "").Trim())];

        var text = remaining.Trim();
        var result = new List<DirectiveAddress>();
        result.AddRange(actors.Select(a => new DirectiveAddress(a, null, text)));
        result.AddRange(groups.Select(g => new DirectiveAddress(null, g, text)));
        return result;
    }

    /// <summary>
    /// Extrait un éventuel préfixe « @cible » du texte de la directive.
    /// Le @ doit ouvrir le texte : un @ au milieu d'une phrase reste du texte libre.
    /// </summary>
    public static DirectiveAddress Parse(string raw)
    {
        var text = (raw ?? "").Trim();
        if (!text.StartsWith('@')) return new DirectiveAddress(null, null, text);

        var sepIndex = text.IndexOfAny([' ', ':', '\n', '\t', ',']);
        var token = (sepIndex < 0 ? text[1..] : text[1..sepIndex]).Trim();
        var rest = (sepIndex < 0 ? "" : text[(sepIndex + 1)..]).TrimStart(' ', ':', ',').Trim();

        // Un @ suivi de rien d'exploitable : on ne mange pas le texte de Hajar.
        if (token.Length == 0) return new DirectiveAddress(null, null, text);

        if (Enum.TryParse<ActorRole>(token, ignoreCase: true, out var exactRole))
            return new DirectiveAddress(exactRole, null, rest);

        if (_actorAliases.TryGetValue(token, out var aliasRole))
            return new DirectiveAddress(aliasRole, null, rest);

        if (Enum.TryParse<ActorGroup>(token, ignoreCase: true, out var exactGroup))
            return new DirectiveAddress(null, exactGroup, rest);

        if (_groupAliases.TryGetValue(token, out var aliasGroup))
            return new DirectiveAddress(null, aliasGroup, rest);

        // Tolérance aux fautes de frappe (retour de Hajar, 2026-07-17 :
        // « @commiteepilotagegpt » — un 't' manquant — traité comme non adressé) :
        // rapprochement par distance d'édition sur les noms normalisés.
        if (FuzzyMatchActor(token) is { } fuzzyRole)
            return new DirectiveAddress(fuzzyRole, null, rest);
        if (FuzzyMatchGroup(token) is { } fuzzyGroup)
            return new DirectiveAddress(null, fuzzyGroup, rest);

        // Cible inconnue : la directive reste valable pour tous plutôt que d'être
        // silencieusement perdue (une faute de frappe ne doit jamais coûter un retour).
        return new DirectiveAddress(null, null, text);
    }

    // ── Rapprochement flou : normalisation (minuscules, lettres seules) puis
    // distance de Levenshtein bornée — 2 fautes tolérées, 3 sur les noms longs.
    private static string Normalize(string s) =>
        new([.. s.ToLowerInvariant().Where(char.IsLetter)]);

    private static bool CloseEnough(string a, string b)
    {
        var maxDist = Math.Max(a.Length, b.Length) >= 12 ? 3 : 2;
        if (Math.Abs(a.Length - b.Length) > maxDist) return false;
        return Levenshtein(a, b) <= maxDist;
    }

    // Le nom complet OU son préfixe de même longueur : « commiteepilotagegpt » doit
    // trouver « CommitteePilotageGptChat » malgré le suffixe Chat et la faute.
    private static bool MatchesFuzzy(string token, string candidate)
    {
        if (CloseEnough(token, candidate)) return true;
        if (candidate.Length > token.Length + 2)
            return CloseEnough(token, candidate[..Math.Min(candidate.Length, token.Length + 1)]);
        return false;
    }

    private static ActorRole? FuzzyMatchActor(string token)
    {
        var t = Normalize(token);
        if (t.Length < 4) return null; // trop court pour un rapprochement fiable
        var candidates = Enum.GetValues<ActorRole>()
            .Where(r => MatchesFuzzy(t, Normalize(r.ToString())))
            .ToList();
        // Alias connus aussi (ex. « claudecode » mal tapé)
        if (candidates.Count == 0)
            candidates = _actorAliases
                .Where(kv => MatchesFuzzy(t, Normalize(kv.Key)))
                .Select(kv => kv.Value).Distinct().ToList();
        return candidates.Count == 1 ? candidates[0] : null; // ambigu = pas de pari
    }

    private static ActorGroup? FuzzyMatchGroup(string token)
    {
        var t = Normalize(token);
        if (t.Length < 4) return null;
        var candidates = Enum.GetValues<ActorGroup>()
            .Where(g => MatchesFuzzy(t, Normalize(g.ToString())))
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>Liste des cibles adressables, pour l'aide et l'autocomplétion UI.</summary>
    public static IReadOnlyList<string> KnownTargets() =>
        [.. Enum.GetNames<ActorRole>().Concat(_actorAliases.Keys)
             .Concat(Enum.GetNames<ActorGroup>()).Concat(_groupAliases.Keys)
             .Distinct(StringComparer.OrdinalIgnoreCase).Order()];

    /// <summary>La directive vise-t-elle cet acteur ? (adressage acteur, groupe, ou non adressée)</summary>
    public static bool Matches(this DirectiveAddress address, ActorRole role)
    {
        if (address.Actor is { } a) return a == role;
        if (address.Group is { } g) return role.GetGroup() == g;
        return true;
    }
}
