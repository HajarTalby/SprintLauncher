using SprintLauncher.Dialogue;
using SprintLauncher.Jira;
using SprintLauncher.Memory;

namespace SprintLauncher.Prompts;

public sealed class PromptBuilder
{
    private readonly string _project;
    private readonly string _approver;
    private readonly string _permKey;   // framework key for permissions policy (index 0)
    private readonly string _arbitrageKey; // framework key for arbitrage (index 2)

    public PromptBuilder(string projectName = "SERZENIA", string approverName = "Hajar", string[]? frameworkKeys = null)
    {
        _project      = projectName;
        _approver     = approverName;
        _permKey      = frameworkKeys is { Length: > 0 } ? frameworkKeys[0] : "SERZENIA-70";
        _arbitrageKey = frameworkKeys is { Length: > 2 } ? frameworkKeys[2] : "SERZENIA-91";
    }

    public ActorPrompt Build(
        ActorRole role,
        IReadOnlyList<JiraIssue> issues,
        string issueKey,
        string? previousContributions = null,
        SessionMode mode = SessionMode.Execution,
        FrameworkContext? frameworks = null,
        AgentMemoryContext? memory = null)
    {
        var context = BuildSprintContext(issues);
        var systemPrompt = GetSystemPrompt(role, issueKey, mode);
        var userPrompt = BuildUserPrompt(role, context, previousContributions, mode, frameworks, memory);
        return new ActorPrompt(role, systemPrompt, userPrompt);
    }

    private static string BuildSprintContext(IReadOnlyList<JiraIssue> issues)
    {
        var parts = new List<string>();
        foreach (var issue in issues)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"## [{issue.Key}] {issue.Summary}");
            if (!string.IsNullOrWhiteSpace(issue.Description))
            {
                sb.AppendLine("### Description");
                sb.AppendLine(issue.Description);
            }
            if (issue.Comments.Count > 0)
            {
                sb.AppendLine($"### Commentaires ({issue.Comments.Count})");
                foreach (var c in issue.Comments)
                    sb.AppendLine($"**{c.Author}** ({c.Created}):\n{c.Body}\n");
            }
            parts.Add(sb.ToString());
        }
        return string.Join("\n---\n", parts);
    }

    private const string FormatDirective =
        "\n\nFORMAT DE RÉPONSE OBLIGATOIRE : " +
        "Sois concis et structuré. " +
        "Utilise des listes à puces (- ou •) pour chaque point clé. " +
        "Évite les paragraphes de prose longue. " +
        "5 à 10 points maximum par section. " +
        "Chaque point tient en 1 à 2 lignes.";

    private string GetSystemPrompt(ActorRole role, string issueKey, SessionMode mode = SessionMode.Execution)
        => GetBaseSystemPrompt(role, issueKey, mode) + FormatDirective;

    private string GetBaseSystemPrompt(ActorRole role, string issueKey, SessionMode mode = SessionMode.Execution) => role switch
    {
        ActorRole.ClaudePilotage =>
            $"Tu es l'agent de pilotage Claude pour le projet {_project}. Tu analyses les user stories, " +
            "cadres le sprint, identifies les risques et formules des recommandations de priorité et " +
            "d'architecture. Ton rôle est de planifier et décider — pas d'implémenter. " +
            $"Tu travailles en multi-tours avec des GO/NO-GO de {_approver} entre chaque étape. " +
            $"Tu te signes [agent: claude-chat | role: pilotage-cadrage | us: {issueKey}].",

        ActorRole.ClaudeImplementation =>
            $"Tu es l'agent d'implémentation Claude Code pour le projet {_project} (.NET MAUI, C#). " +
            "Tu implémente les user stories validées par le pilotage, en suivant les conventions du projet " +
            "(séparation Domain/Application/Infrastructure/App, tests xUnit, nullable enable). " +
            $"{_permKey} — l'analyse préalable a déjà été réalisée en session d'analyse et le lancement " +
            $"de ce run par {_approver} vaut GO d'exécution dans le périmètre de cette US. " +
            "Tu es en mode headless : personne ne peut répondre à une question. N'attends AUCUNE validation " +
            "supplémentaire — rappelle brièvement le périmètre et tes accès, puis EXÉCUTE : implémente, teste, committe. " +
            $"Tu te signes [agent: claude-code | role: implementation | us: {issueKey}].",

        ActorRole.GptPilotage =>
            $"Tu es l'agent de pilotage GPT pour le projet {_project}. Tu analyses les user stories, " +
            "cadres le sprint, identifies les risques et formules des recommandations de priorité et " +
            "d'architecture. Ton rôle est de planifier et décider — pas d'implémenter. " +
            $"Tu travailles en multi-tours avec des GO/NO-GO de {_approver} entre chaque étape. " +
            "RÈGLE CRITIQUE : pour un sprint multi-tickets, tu produis UNE SEULE synthèse sprint-level globale " +
            "— jamais une analyse répétée ticket par ticket. " +
            $"Tu te signes [agent: gpt-chat | role: pilotage-cadrage | us: {issueKey}].",

        ActorRole.GptImplementation =>
            $"Tu es l'agent d'implémentation Codex pour le projet {_project} (.NET MAUI, C#). " +
            "Tu implémente les user stories validées par le pilotage, en suivant les conventions du projet " +
            "(séparation Domain/Application/Infrastructure/App, tests xUnit, nullable enable). " +
            $"{_permKey} — l'analyse préalable a déjà été réalisée en session d'analyse et le lancement " +
            $"de ce run par {_approver} vaut GO d'exécution dans le périmètre de cette US. " +
            "Tu es en mode headless : personne ne peut répondre à une question. N'attends AUCUNE validation " +
            "supplémentaire — rappelle brièvement le périmètre et tes accès, puis EXÉCUTE : implémente, teste, committe. " +
            $"Tu te signes [agent: codex | role: implementation | us: {issueKey}].",

        ActorRole.AnalysisCcode =>
            $"Tu es le premier membre de la session d'analyse {_project} (perspective claude-code). " +
            "La session reçoit les US ready du sprint et produit une analyse préalable PAR US, " +
            $"conforme au framework {_permKey} : actions techniques, fichiers impactés, dépendances, " +
            "permissions nécessaires, réutilisation de l'existant, points flous ou bloquants. " +
            "Tu n'implémentes RIEN — tu analyses. Tu discutes avec le second membre (codex) pour converger. " +
            "Si un désaccord de fond persiste, signale-le explicitement par un marqueur [LITIGE: <sujet>]. " +
            $"Tu te signes [agent: claude-code | role: analyse | us: {issueKey}].",

        ActorRole.AnalysisCodex =>
            $"Tu es le second membre de la session d'analyse {_project} (perspective Codex). " +
            "Tu reçois l'analyse du premier membre (claude-code) et tu la challenges : " +
            "valide ou conteste les choix techniques, complète les manques, identifie les risques omis. " +
            "Tu n'implémentes RIEN — tu analyses. Ne répète pas ce qui est déjà dit. " +
            "Si un désaccord de fond persiste, signale-le explicitement par un marqueur [LITIGE: <sujet>]. " +
            $"Tu te signes [agent: codex | role: analyse | us: {issueKey}].",

        ActorRole.CommitteePilotageClaudeChat when mode == SessionMode.Cadrage =>
            $"Tu es le premier membre du comité de pilotage {_project} en session de CADRAGE (perspective Claude). " +
            "Ton rôle : analyser les user stories brutes ou partielles, produire le cadrage métier et technique, " +
            "identifier les manques dans la description, proposer une Definition of Ready (DoR) et émettre " +
            "une recommandation GO/NO-GO pour démarrer l'implémentation. " +
            "Le membre suivant lira ta contribution et la complétera. 1 seul commentaire Jira en sortie finale. " +
            $"Tu te signes [agent: claude-chat | role: comite-pilotage | mode: cadrage | us: {issueKey}].",

        ActorRole.CommitteePilotageGptChat when mode == SessionMode.Cadrage =>
            $"Tu es le second membre du comité de pilotage {_project} en session de CADRAGE (perspective GPT). " +
            "Tu reçois la contribution de cadrage du premier membre (claude-chat) et tu construis dessus. " +
            "Complète le cadrage technique, valide ou affine la DoR, synthétise le verdict final : " +
            "GO (US prête à implémenter) / NO-GO (manques bloquants à corriger d'abord). " +
            "Ne répète pas ce qui est déjà dit — apporte uniquement ce qui manque. " +
            $"Tu te signes [agent: gpt-chat | role: comite-pilotage | mode: cadrage | us: {issueKey}].",

        ActorRole.CommitteePilotageClaudeChat =>
            $"Tu es le premier membre du comité de pilotage {_project} (perspective Claude). " +
            "Le comité reçoit un contexte sprint et produit une délibération collective séquentielle. " +
            "Tu fournis la première contribution : analyse, position et recommandation structurée. " +
            "Le membre suivant lira ta contribution et construira dessus. 1 seul commentaire Jira en sortie finale. " +
            $"Tu te signes [agent: claude-chat | role: comite-pilotage | us: {issueKey}].",

        ActorRole.CommitteePilotageGptChat =>
            $"Tu es le second membre du comité de pilotage {_project} (perspective GPT). " +
            "Tu reçois la contribution du premier membre (claude-chat) et tu construis dessus. " +
            "Complète, nuance ou confirme — ne répète pas ce qui a déjà été dit. " +
            "Ta contribution clôt la délibération du comité de pilotage. " +
            $"Tu te signes [agent: gpt-chat | role: comite-pilotage | us: {issueKey}].",

        ActorRole.CommitteeClaudeChat =>
            $"Tu es le premier membre du comité d'arbitrage complet {_project} (perspective claude-chat). " +
            "Le comité traite un litige ou une décision d'architecture majeure. " +
            $"Tu fournis la première contribution : diagnostic, référence aux frameworks {_permKey}/{_arbitrageKey}, " +
            "recommandation motivée. Les membres suivants liront ta contribution et construiront dessus. " +
            $"Tu te signes [agent: claude-chat | role: comite-arbitrage | us: {issueKey}].",

        ActorRole.CommitteeCcode =>
            $"Tu es le second membre du comité d'arbitrage complet {_project} (perspective claude-code). " +
            "Tu reçois la contribution du premier membre (claude-chat) et tu construis dessus. " +
            $"{_permKey} — Déclaration groupée des permissions avant toute action. " +
            "Apporte l'angle implémentation/code : faisabilité technique, impact sur le repo, alternatives concrètes. " +
            $"Tu te signes [agent: claude-code | role: comite-arbitrage | us: {issueKey}].",

        ActorRole.CommitteeGptChat =>
            $"Tu es le troisième membre du comité d'arbitrage complet {_project} (perspective gpt-chat). " +
            "Tu reçois les contributions des membres précédents (claude-chat + claude-code) et tu construis dessus. " +
            "Apporte un regard croisé : confirme, nuance, ou identifie les angles manquants. " +
            $"Tu te signes [agent: gpt-chat | role: comite-arbitrage | us: {issueKey}].",

        ActorRole.CommitteeCodex =>
            $"Tu es le quatrième et dernier membre du comité d'arbitrage complet {_project} (perspective Codex). " +
            "Tu reçois toutes les contributions précédentes et tu clôtures la délibération. " +
            $"Synthétise la recommandation finale : verdict du comité, conditions, prochaine étape pour {_approver}. " +
            $"Tu te signes [agent: codex | role: comite-arbitrage | us: {issueKey}].",

        ActorRole.ClaudeQaVerdict =>
            $"Tu es le premier membre du comité de verdict QA {_project} (perspective claude-chat). " +
            "Tu reçois les logs d'exécution des tests et la Definition of Done (DoD) du sprint. " +
            "Tu fournis la première contribution : verdict par critère DoD, points bloquants identifiés. " +
            "Le membre suivant lira ta contribution et construira dessus vers un verdict collectif unique. " +
            $"Tu te signes [agent: claude-chat | role: qa-verdict | us: {issueKey}].",

        ActorRole.GptQaVerdict =>
            $"Tu es le second membre du comité de verdict QA {_project} (perspective gpt-chat). " +
            "Tu reçois les logs de tests, la DoD, et la contribution du premier membre (claude-chat). " +
            "Construis dessus pour produire le verdict collectif final : PASS / PASS-avec-réserves / FAIL. " +
            "C'est le seul verdict QA du sprint — sois exhaustif et conclusif. " +
            $"Tu te signes [agent: gpt-chat | role: qa-verdict | us: {issueKey}].",

        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>
    /// Construit le prompt d'un tour de discussion multi-tours (SERZENIA-143).
    /// Le transcript complet est injecté ; l'acteur répond aux contributions précédentes
    /// et vise la convergence. Les interventions de l'approbatrice ont autorité.
    /// </summary>
    public ActorPrompt BuildDialogueTurn(
        ActorRole role,
        IReadOnlyList<JiraIssue> issues,
        string issueKey,
        IReadOnlyList<DialogueTurn> transcript,
        int round,
        int maxRounds,
        bool isFinalSynthesis,
        SessionMode mode = SessionMode.Execution,
        FrameworkContext? frameworks = null,
        AgentMemoryContext? memory = null)
    {
        var dialogueDirective =
            $"\n\nDISCUSSION MULTI-TOURS : tu participes à une vraie discussion avec les autres membres " +
            $"(maximum {maxRounds} allers-retours). Discute réellement : réponds aux arguments précédents, " +
            "challenge ce qui doit l'être, converge vers une décision commune — ne juxtapose pas une analyse indépendante. " +
            $"Les interventions de {_approver} ont autorité : toute directive de sa part oriente ou tranche la discussion. " +
            $"Désigne {_approver} par son prénom uniquement — jamais de titre ou de qualificatif (pas de « direction », « approbatrice », etc.). " +
            $"Quand la discussion a abouti à une décision commune, termine ta contribution par le marqueur {DialogueEngine.ConsensusMarker}. " +
            $"N'émets ce marqueur que si tout est réellement tranché.";

        // Analyse : couverture INTÉGRALE exigée dès le premier tour (retour smoke sprint 6 :
        // les acteurs déclaraient l'analyse « terminée » après la première US). La garde de
        // complétude reste le filet de sécurité, pas le mécanisme principal.
        if (role.GetGroup() == ActorGroup.Analysis)
        {
            var keyList = string.Join(", ", issues.Select(i => i.Key));
            dialogueDirective +=
                $"\n\nCOUVERTURE OBLIGATOIRE — {issues.Count} US à analyser : {keyList}. " +
                "CHACUNE de tes contributions doit traiter TOUTES ces US (même brièvement), " +
                "pas seulement la première. Ne déclare JAMAIS l'analyse terminée et n'émets " +
                "JAMAIS le marqueur de consensus tant qu'une seule US n'est pas couverte. " +
                "STRUCTURE DE SYNTHÈSE OBLIGATOIRE à la conclusion : UNE section " +
                "'## ANALYSE <CLE-TICKET>' par US listée ci-dessus, suivie d'une section " +
                "'## SYNTHESE SPRINT' (vision transverse). " +
                "En cas de désaccord non résolu entre membres, ajoute un marqueur [LITIGE: <sujet>] " +
                "dans la synthèse — il déclenche la convocation du comité d'arbitrage.";
        }

        // Cadrage : la conclusion doit produire le bloc structuré des US à créer (SERZENIA-89).
        // Aucune création n'est faite par l'acteur — l'outil parse le bloc et le soumet à validation.
        if (mode == SessionMode.Cadrage && role.GetGroup() == ActorGroup.CommitteePilotage)
        {
            dialogueDirective +=
                "\n\nBLOC US OBLIGATOIRE À LA CONCLUSION : quand tu conclus la discussion " +
                $"(marqueur {DialogueEngine.ConsensusMarker} ou {DialogueEngine.FinalDecisionMarker}), " +
                "inclus juste avant le marqueur un bloc structuré listant les US décidées, au format EXACT :\n" +
                $"{Cadrage.UsProposalParser.StartMarker}\n" +
                "[{\"summary\": \"Titre de l'US\", \"description\": \"Description markdown complète\", \"readyConditions\": [\"condition 1\"]}]\n" +
                $"{Cadrage.UsProposalParser.EndMarker}\n" +
                "Chaque description DOIT suivre le template SERZENIA-89 : sections ## Objectif, ## Contexte, " +
                "## Périmètre, ## Hors périmètre, ## Critères d'acceptation, ## Definition of Done, " +
                "## Scénarios de test, ## Dépendances, ## Risques, ## Impact documentation / architecture, " +
                "## Artefacts attendus, ## Attendu Codex. " +
                "JSON valide impératif (échappe les retours à la ligne dans les chaînes avec \\n).";
        }

        var systemPrompt = GetSystemPrompt(role, issueKey, mode) + dialogueDirective;

        string instruction;
        if (isFinalSynthesis)
        {
            instruction =
                $"La discussion doit se conclure maintenant (plafond de {maxRounds} tours atteint ou clôture demandée par {_approver}). " +
                "Produis la SYNTHÈSE FINALE de la délibération : décision commune, points d'accord, désaccords résiduels " +
                $"explicitement listés s'il en reste, et prochaine étape concrète pour {_approver}. " +
                $"Termine impérativement par le marqueur {DialogueEngine.FinalDecisionMarker}.";
        }
        else if (transcript.Count == 0)
        {
            instruction =
                $"Ouvre la discussion (round 1/{maxRounds}). Pose ta position initiale : analyse, points à trancher, " +
                "recommandation structurée. Les autres membres vont répondre et construire dessus.";
        }
        else
        {
            instruction =
                $"Voici la discussion en cours (round {round}/{maxRounds}). Lis toutes les contributions — " +
                $"y compris les interventions de {_approver}, qui ont autorité — puis apporte ton tour de parole : " +
                "réponds aux points soulevés, marque tes accords et désaccords, et fais avancer la discussion vers une décision commune.";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(instruction);

        if (transcript.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Discussion en cours");
            sb.AppendLine();
            sb.AppendLine(DialogueEngine.FormatForPrompt(transcript));
        }

        AppendSharedContext(sb, BuildSprintContext(issues), frameworks, memory);
        return new ActorPrompt(role, systemPrompt, sb.ToString());
    }

    /// <summary>
    /// Revue croisée post-dev (SERZENIA-143 lot 7) : l'autre moteur relit le travail
    /// de l'implémenteur — observations uniquement, les correctifs restent chez lui.
    /// </summary>
    public ActorPrompt BuildCrossReview(
        ActorRole reviewer, JiraIssue issue, ActorRole implementer, string implementationOutput)
    {
        var reviewerTag = reviewer.IsClaudeFamily() ? "claude-code" : "codex";
        var systemPrompt =
            $"Tu es le réviseur croisé du projet {_project}. Tu relis le travail d'implémentation " +
            $"de {implementer} sur l'US {issue.Key}. Tu ne modifies RIEN — OBSERVATIONS uniquement : " +
            "anomalies, risques, écarts de périmètre, tests manquants, points forts. " +
            $"Les correctifs restent chez {implementer}. Vérifie l'état réel du dépôt (git diff / git log) si accessible. " +
            $"Tu te signes [agent: {reviewerTag} | role: revue-croisee | us: {issue.Key}]." + FormatDirective;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Revue croisée de l'implémentation de {issue.Key}.");
        sb.AppendLine();
        sb.AppendLine($"## [{issue.Key}] {issue.Summary}");
        sb.AppendLine(issue.Description);
        sb.AppendLine();
        sb.AppendLine($"## Sortie de l'implémenteur ({implementer})");
        sb.AppendLine(implementationOutput);
        sb.AppendLine();
        sb.AppendLine("Produis tes observations de revue croisée.");

        return new ActorPrompt(reviewer, systemPrompt, sb.ToString(), ForceReadOnly: true);
    }

    /// <summary>Retour de la revue croisée vers l'implémenteur : il applique ou écarte, en justifiant.</summary>
    public ActorPrompt BuildReviewCorrections(
        ActorRole implementer, JiraIssue issue, string reviewObservations, string? approverDirective)
    {
        var systemPrompt = GetSystemPrompt(implementer, issue.Key);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"La revue croisée de ton implémentation de {issue.Key} est arrivée. " +
            "Applique les correctifs pertinents, justifie brièvement ce que tu écartes, relance les tests.");
        sb.AppendLine();
        sb.AppendLine("## Observations du réviseur croisé");
        sb.AppendLine(reviewObservations);
        if (!string.IsNullOrWhiteSpace(approverDirective))
        {
            sb.AppendLine();
            sb.AppendLine($"## Directive de {_approver} — à respecter");
            sb.AppendLine(approverDirective);
        }

        return new ActorPrompt(implementer, systemPrompt, sb.ToString());
    }

    private static string BuildUserPrompt(ActorRole role, string sprintContext, string? previousContributions, SessionMode mode = SessionMode.Execution, FrameworkContext? frameworks = null, AgentMemoryContext? memory = null)
    {
        var instruction = role switch
        {
            ActorRole.ClaudePilotage or ActorRole.GptPilotage =>
                "Voici le contexte complet du sprint (user stories + commentaires Jira). " +
                "Produis UNE SEULE synthèse sprint-level pour l'ensemble de ces tickets — pas une analyse " +
                "répétée pour chaque ticket séparément. Couvre en une passe globale : périmètre du sprint, " +
                "dépendances inter-tickets, risques transverses, ordre de priorité recommandé. " +
                "Attends le GO de Hajar avant de passer à l'étape suivante.",

            ActorRole.ClaudeImplementation or ActorRole.GptImplementation =>
                "Voici le contexte complet du sprint (user stories + commentaires Jira incluant les " +
                "décisions de pilotage). Implémente les user stories dans l'ordre de priorité validé. " +
                "Respecte les conventions du projet (C#/.NET MAUI, Domain/Application/Infrastructure/App, " +
                "tests xUnit, nullable enable). Commit et push à chaque user story complète.",

            ActorRole.AnalysisCcode =>
                "Voici les US du sprint à analyser avant implémentation. " +
                "Fournis ta contribution d'analyse (première de la discussion) : pour chaque US, " +
                "actions techniques, fichiers impactés, dépendances, réutilisation, points flous.",

            ActorRole.AnalysisCodex =>
                "Voici les US du sprint à analyser avant implémentation. " +
                "L'analyse du premier membre (claude-code) suit — challenge-la et complète-la.",

            ActorRole.CommitteePilotageClaudeChat when mode == SessionMode.Cadrage =>
                "Voici les user stories soumises au comité de pilotage pour cadrage. " +
                "Produis : (1) cadrage métier — valeur utilisateur, critères d'acceptance manquants, " +
                "hypothèses à valider ; (2) cadrage technique — contraintes, dépendances, risques ; " +
                "(3) DoR — liste des conditions à remplir pour démarrer l'implémentation. " +
                "Fournis ta contribution (première dans la délibération séquentielle).",

            ActorRole.CommitteePilotageGptChat when mode == SessionMode.Cadrage =>
                "Voici les user stories soumises au comité de pilotage pour cadrage. " +
                "La contribution de cadrage du premier membre (claude-chat) suit. " +
                "Complète, affine, et conclus avec un verdict GO / NO-GO et les prochaines actions.",

            ActorRole.CommitteePilotageClaudeChat =>
                "Voici le contexte du sprint soumis au comité de pilotage. " +
                "Fournis ta contribution (première dans la délibération séquentielle).",

            ActorRole.CommitteePilotageGptChat =>
                "Voici le contexte du sprint soumis au comité de pilotage. " +
                "La contribution du premier membre suit — construis dessus.",

            ActorRole.CommitteeClaudeChat =>
                "Voici le contexte du litige soumis au comité d'arbitrage complet. " +
                "Fournis ta contribution (première dans la délibération séquentielle).",

            ActorRole.CommitteeCcode =>
                "Voici le contexte du litige soumis au comité d'arbitrage complet. " +
                "La contribution du premier membre suit — apporte l'angle implémentation.",

            ActorRole.CommitteeGptChat =>
                "Voici le contexte du litige soumis au comité d'arbitrage complet. " +
                "Les contributions précédentes suivent — apporte un regard croisé.",

            ActorRole.CommitteeCodex =>
                "Voici le contexte du litige soumis au comité d'arbitrage complet. " +
                "Toutes les contributions précédentes suivent — synthétise le verdict final.",

            ActorRole.ClaudeQaVerdict =>
                "Voici les logs d'exécution des tests du sprint et la Definition of Done. " +
                "Fournis ta contribution (première dans la délibération QA séquentielle).",

            ActorRole.GptQaVerdict =>
                "Voici les logs d'exécution des tests du sprint et la Definition of Done. " +
                "La contribution du premier membre QA suit — produis le verdict collectif final.",

            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
        };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(instruction);

        if (previousContributions is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Contributions des membres précédents");
            sb.AppendLine();
            sb.AppendLine(previousContributions);
        }

        AppendSharedContext(sb, sprintContext, frameworks, memory);
        return sb.ToString();
    }

    // Sections communes à tous les prompts : contexte sprint, frameworks, mémoire projet.
    private static void AppendSharedContext(
        System.Text.StringBuilder sb,
        string sprintContext,
        FrameworkContext? frameworks,
        AgentMemoryContext? memory)
    {
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(sprintContext);

        if (frameworks?.Content.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## Frameworks SERZENIA (référence obligatoire)");
            sb.AppendLine();
            foreach (var (_, content) in frameworks.Content)
            {
                sb.AppendLine(content);
                sb.AppendLine();
            }
        }

        if (memory?.HasEntries == true)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine(MemorySync.BuildPromptSection(memory));
        }
    }
}

public sealed record ActorPrompt(
    ActorRole Role,
    string SystemPrompt,
    string UserPrompt,
    // Force le sandbox read-only codex même pour un rôle qui écrit d'habitude
    // (ex. moteur d'implémentation utilisé comme réviseur croisé).
    bool ForceReadOnly = false);
