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

    // Registre consolidé des décisions déjà actées par l'approbatrice (extrait des
    // commentaires Jira du sprint) — injecté dans TOUS les prompts pour que les
    // acteurs ne redemandent jamais une décision déjà prise.
    public string? DecisionsRegistry { get; set; }

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

    // Mandat d'audit du comité de pilotage (retour de Hajar, 2026-07-16) : le pilotage
    // ne se contente JAMAIS de relire le verdict QA — il audite le sprint sur toutes
    // ses sources. Injecté dans les prompts système des deux membres du comité.
    private const string PilotageAuditMandate =
        "TON AUDIT COUVRE OBLIGATOIREMENT, pour CHAQUE US du sprint : " +
        "(1) le verdict et les sorties QA ; " +
        "(2) les COMMENTAIRES JIRA de l'US — restitutions, décisions, blocages, réserves ; " +
        "(3) les RETOURS DES ACTEURS DE DEV — ce qu'ils déclarent livré, leurs réserves, leurs écarts assumés ; " +
        "(4) l'ÉCART entre le livré et la DESCRIPTION Jira de l'US — chaque critère d'acceptation, chaque section du périmètre ; " +
        "(5) l'ÉCART avec l'US DE PILOTAGE du sprint — décisions actées, ordre d'exécution, critères de sortie, principes (providers réels, règle coût/qualité) ; " +
        "(6) la CONFORMITÉ aux frameworks d'exécution et de validation fournis en référence — y compris les règles de restitution et d'écriture Jira (bon ticket, bon format, artefacts exigés). " +
        "Un point non vérifiable faute d'information est un CONSTAT à lister, pas un point à passer sous silence. ";

    private const string FormatDirective =
        "\n\nFORMAT DE RÉPONSE OBLIGATOIRE : " +
        "Sois concis et structuré. " +
        "Utilise des listes à puces (- ou •) pour chaque point clé. " +
        "Évite les paragraphes de prose longue. " +
        "5 à 10 points maximum par section. " +
        "Chaque point tient en 1 à 2 lignes.";

    private const string RetrospectiveCaptureDirective =
        "\n\nCAPTURE RÉTROSPECTIVE AU FIL DU SPRINT (OPTIONNELLE) : " +
        "si ce tour révèle un apprentissage concret à conserver (réussite, difficulté, incident ou amélioration), " +
        "ajoute tout à la fin de ta réponse un bloc dont la ligne d'ouverture est EXACTEMENT '##RETRO', " +
        "puis une liste à puces factuelle. Le Sprint Launcher persiste ce bloc immédiatement. " +
        "N'ajoute pas ce bloc si tu n'as aucun point utile ; n'invente jamais un point pour remplir.";

    private const string ModelChoiceDirective =
        "\n\nCHOIX DU MODÈLE PAR COMPLEXITÉ : évalue la complexité du traitement suivant " +
        "(simple / moyen / complexe / critique) et propose le modèle LLM à utiliser pour " +
        "le prochain développement. Termine ta sortie par une ligne parsable : " +
        "'Modèle dev recommandé : <claude|codex> <identifiant-du-modèle>'. " +
        "Si aucun surclassement n'est utile, recommande le modèle par défaut sonnet-5.";

    private string GetSystemPrompt(ActorRole role, string issueKey, SessionMode mode = SessionMode.Execution)
        => GetBaseSystemPrompt(role, issueKey, mode) + FormatDirective + RetrospectiveCaptureDirective;

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

        ActorRole.AgImplementation =>
            $"Tu es l'agent d'implémentation Antigravity pour le projet {_project} (.NET MAUI, C#). " +
            "Tu implémente les user stories validées par le pilotage, en suivant les conventions du projet " +
            "(séparation Domain/Application/Infrastructure/App, tests xUnit, nullable enable). " +
            $"{_permKey} — l'analyse préalable a déjà été réalisée en session d'analyse et le lancement " +
            $"de ce run par {_approver} vaut GO d'exécution dans le périmètre de cette US. " +
            "Tu es en mode headless : personne ne peut répondre à une question. N'attends AUCUNE validation " +
            "supplémentaire — rappelle brièvement le périmètre et tes accès, puis EXÉCUTE : implémente, teste, committe. " +
            $"Tu te signes [agent: agy | role: implementation | us: {issueKey}].",

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
            "Tu es un analyste À PART ENTIÈRE, pas un relecteur : tu produis TA propre analyse " +
            "(actions techniques, fichiers impactés, dépendances, permissions nécessaires, réutilisation, " +
            "points flous), puis tu la CROISES avec celle de l'autre membre quand elle t'est présentée — " +
            "accords, désaccords argumentés, et différentiel dans les deux sens. " +
            "Valider en bloc l'analyse de l'autre sans apport propre n'est PAS une contribution. " +
            "Tu n'implémentes RIEN — tu analyses. " +
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
            "Le comité PILOTE le sprint — son audit est BEAUCOUP plus large que la validation QA " +
            "(retour de Hajar, 2026-07-16 : le pilotage se limitait aux sorties QA). " +
            PilotageAuditMandate +
            "Tu fournis la première contribution : audit complet, position et recommandation structurée. " +
            $"Tu te signes [agent: claude-chat | role: comite-pilotage | us: {issueKey}].",

        ActorRole.CommitteePilotageGptChat =>
            $"Tu es le second membre du comité de pilotage {_project} (perspective GPT). " +
            "Tu es un auditeur À PART ENTIÈRE : tu produis TON propre audit (même périmètre que " +
            "le premier membre), puis tu croises avec sa contribution — accords, désaccords " +
            "argumentés, différentiel. Valider sa lecture sans audit propre n'est PAS une contribution. " +
            PilotageAuditMandate +
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
            "Tu formes TON PROPRE verdict à partir des logs de tests, de la DoD et des preuves réelles — " +
            "jamais en te reposant sur le verdict de l'autre membre. Quand sa contribution t'est présentée, " +
            "tu la croises avec la tienne : accords, désaccords argumentés, différentiel (ce qu'il a manqué, " +
            "ce que tu avais manqué). Un écart vu par un seul des deux membres reste un écart. " +
            "Tu portes ensuite le verdict collectif final : PASS / PASS-avec-réserves / FAIL. " +
            "C'est le seul verdict QA du sprint — sois exhaustif et conclusif. " +
            $"Tu te signes [agent: gpt-chat | role: qa-verdict | us: {issueKey}].",

        ActorRole.RetrospectiveClaude =>
            $"Tu es l'agent de rétrospective Claude pour le projet {_project}, en fin de sprint. " +
            "Ton rôle : un post-mortem honnête de TON travail et de ce que tu as observé pendant ce sprint " +
            "— pas une nouvelle analyse des US. Tu écris une trace destinée à être relue au sprint suivant. " +
            $"Tu te signes [agent: claude-code | role: retrospective | us: {issueKey}].",

        ActorRole.RetrospectiveGpt =>
            $"Tu es l'agent de rétrospective GPT pour le projet {_project}, en fin de sprint. " +
            "Ton rôle : un post-mortem honnête de TON travail et de ce que tu as observé pendant ce sprint " +
            "— pas une nouvelle analyse des US. Tu écris une trace destinée à être relue au sprint suivant. " +
            $"Tu te signes [agent: codex | role: retrospective | us: {issueKey}].",

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
        AgentMemoryContext? memory = null,
        bool blindRound = false)
    {
        var dialogueDirective =
            $"\n\nDISCUSSION MULTI-TOURS : tu participes à une vraie discussion avec les autres membres " +
            $"(maximum {maxRounds} allers-retours). Discute réellement : réponds aux arguments précédents, " +
            "challenge ce qui doit l'être, converge vers une décision commune — ne juxtapose pas une analyse indépendante. " +
            $"Les interventions de {_approver} ont autorité : toute directive de sa part oriente ou tranche la discussion. " +
            $"PORTÉE DES VALIDATIONS : un accusé ponctuel de {_approver} (« ok ») sur un point précis ne vaut JAMAIS " +
            "GO global du sprint ni validation de clôture — seule une décision explicite avec portée claire compte ; " +
            "en cas de doute, considère le point comme réservé. " +
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

        // QA : verdict par critère DoD + section ÉCARTS structurée — c'est elle qui
        // pilote la boucle de remédiation de l'outil (le sprint n'est pas terminé
        // tant qu'elle n'est pas vide).
        if (role.GetGroup() == ActorGroup.Qa)
        {
            var qaKeyList = string.Join(", ", issues.Select(i => i.Key));
            dialogueDirective +=
                $"\n\nCOUVERTURE OBLIGATOIRE — {issues.Count} US à vérifier : {qaKeyList}. " +
                "Rends un verdict PAR US pour CHACUNE (pas seulement une seule) : une US non mentionnée " +
                "est un TROU de QA, pas un succès. Ne conclus JAMAIS et n'émets JAMAIS le consensus tant " +
                "qu'une US n'a pas son verdict.\n" +
                "VERDICT STRUCTURÉ OBLIGATOIRE : verdict PAR CRITÈRE DoD (SERZENIA-91) en t'appuyant sur les " +
                "LOGS D'EXÉCUTION RÉELS et l'AUDIT DES PREUVES fournis — pas seulement sur les déclarations des acteurs.\n" +
                "STOP-CONDITIONS / INPUTS EXTERNES : une US « implémentée en code » mais dont la PREUVE RÉELLE " +
                "était bloquée faute d'input (ex. DSN Sentry, config provider) n'est PAS terminée. Vérifie si " +
                "l'input est désormais fourni (variables .env, secrets, décisions de Hajar au registre) : " +
                "s'il l'est, EXIGE la preuve réelle (intégration branchée + preuve d'exécution) — sinon c'est un " +
                "écart '[CLE-US] preuve réelle à produire (input fourni)'. S'il manque encore, écart " +
                "'[CLE-US] bloqué — <input> manquant'.\n" +
                "EXÉCUTION RÉELLE PAR TOI-MÊME : tu as les droits d'exécution — ne te limite PAS aux logs fournis. " +
                "La release réelle est générée (chemins, ffmpeg et commandes dans le contexte d'exécution QA fourni) : " +
                "lance l'application toi-même, DÉROULE les scénarios E2E de chaque US sur cette release, et produis " +
                "les preuves de ce que tu observes (captures, vidéos) dans artifacts/<sprint>/<US>/. Un scénario que tu " +
                "n'as pas pu exécuter = écart, pas une hypothèse.\n" +
                "Ta conclusion DOIT contenir une section '## ECARTS' listant chaque écart au format exact " +
                "'- [CLE-US] description → action requise' (une ligne par écart ; '[GLOBAL]' pour un écart transverse). " +
                "Si aucun écart sur AUCUNE des US : '## ECARTS' suivi de 'AUCUN'. Preuve visuelle absente sur une US " +
                "à critère visuel = écart. Sois exigeant : documenter un écart ne le résout pas.";
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
            // Ce tour est le SEUL livrable visible : c'est lui qui s'affiche dans le Sprint
            // Launcher et part sur Jira en un commentaire unique (exigence de Hajar).
            instruction =
                $"SYNTHÈSE FINALE — la discussion se conclut maintenant (plafond de {maxRounds} tours atteint " +
                $"ou clôture demandée par {_approver}). Tu portes la synthèse de TOUTE la délibération. " +
                "C'est la SEULE sortie qui sera affichée et publiée sur Jira : elle doit se suffire à elle-même, " +
                "sans que personne ait besoin de relire le transcript. " +
                "Elle DOIT contenir : la décision commune ; les points d'accord ; les désaccords résiduels " +
                "explicitement listés s'il en reste (ne les gomme pas pour faire propre) ; ce que chaque membre " +
                "a apporté en propre (le différentiel des analyses indépendantes) ; " +
                $"et la prochaine étape concrète pour {_approver}. " +
                $"Termine impérativement par le marqueur {DialogueEngine.FinalDecisionMarker}.";
        }
        else if (blindRound)
        {
            // Tour à l'aveugle (retour de Hajar, 2026-07-16) : chaque membre produit SA
            // propre analyse sans voir celle des autres. Sans ça, le second membre se
            // contentait de valider la lecture du premier au lieu d'analyser.
            instruction =
                $"ANALYSE PROPRE — round 1/{maxRounds}, À L'AVEUGLE. Tu ne vois PAS la contribution des autres " +
                "membres : c'est volontaire. Produis TA propre analyse, complète et autonome, à partir du " +
                "contexte fourni uniquement (US, commentaires Jira, frameworks, décisions actées). " +
                "Ne dis jamais « je rejoins l'analyse précédente » ni « rien à ajouter » : il n'y a rien à rejoindre à ce stade. " +
                "Va au bout de ton raisonnement — analyse, points à trancher, risques, recommandation structurée. " +
                "Au tour suivant tu découvriras l'analyse des autres membres et vous les croiserez. " +
                $"N'émets AUCUN marqueur de convergence ({DialogueEngine.ConsensusMarker} / {DialogueEngine.FinalDecisionMarker}) " +
                "à ce tour : on ne converge pas avant de s'être lus.";
        }
        else if (transcript.Count == 0)
        {
            instruction =
                $"Ouvre la discussion (round 1/{maxRounds}). Pose ta position initiale : analyse, points à trancher, " +
                "recommandation structurée. Les autres membres vont répondre et construire dessus.";
        }
        else if (round == 2)
        {
            // Premier tour où les analyses à l'aveugle se rencontrent : le croisement est
            // le livrable, pas une politesse. Le différentiel est ce que Hajar veut voir.
            instruction =
                $"CROISEMENT — round {round}/{maxRounds}. Tu découvres maintenant l'analyse des autres membres, " +
                "produite indépendamment de la tienne. Croise-la avec la tienne et structure ta réponse ainsi :\n" +
                "1. '## ACCORDS' — les points où vos analyses convergent (cite-les, ne les réécris pas).\n" +
                "2. '## DÉSACCORDS' — les points où vous divergez : dis lequel tu tiens, et POURQUOI (argument, pas autorité). " +
                $"Si un désaccord de fond ne peut pas être résolu entre vous, marque-le [LITIGE: <sujet>].\n" +
                "3. '## DIFFÉRENTIEL' — ce que TON analyse apporte et qui est ABSENT de la leur, et inversement " +
                "ce qu'ils ont vu que tu avais manqué (reconnais-le explicitement).\n" +
                $"Les interventions de {_approver} ont autorité et tranchent. " +
                "Interdit : valider en bloc l'analyse de l'autre sans différentiel — s'il n'y a réellement aucun " +
                "écart, dis-le et prouve-le en citant les points couverts de part et d'autre.";
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
        ActorRole reviewer, JiraIssue issue, ActorRole implementer, string implementationOutput,
        ActorRole? reliefFrom = null)
    {
        var reviewerTag = reviewer.IsClaudeFamily() ? "claude-code" : "codex";
        var reliefNote = reliefFrom is null ? "" :
            $" ATTENTION : cette US a été implémentée EN DEUX TEMPS ({reliefFrom} puis {implementer}, relève). " +
            "Revois l'ENSEMBLE des deux contributions : cohérence entre elles, doublons de code, " +
            "conflits, morceaux orphelins du premier moteur.";
        var systemPrompt =
            $"Tu es le réviseur croisé du projet {_project}. Tu relis le travail d'implémentation " +
            $"de {implementer} sur l'US {issue.Key}.{reliefNote} Tu ne modifies RIEN — OBSERVATIONS uniquement : " +
            "anomalies, risques, écarts de périmètre, tests manquants, points forts. " +
            $"Les correctifs restent chez {implementer}. Vérifie l'état réel du dépôt (git diff / git log) si accessible. " +
            $"Tu te signes [agent: {reviewerTag} | role: revue-croisee | us: {issue.Key}]." +
            FormatDirective + RetrospectiveCaptureDirective;

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

    /// <summary>
    /// Remédiation d'écarts (SERZENIA-143 lot 8) : le moteur traite INTÉGRALEMENT
    /// les écarts QA de son US — code, tests, preuves — pas de simple documentation.
    /// </summary>
    public ActorPrompt BuildRemediation(
        ActorRole engine, JiraIssue issue, IReadOnlyList<string> ecarts, string? approverDirective)
    {
        var systemPrompt = GetSystemPrompt(engine, issue.Key);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"REMÉDIATION — le verdict QA a identifié des écarts sur {issue.Key}. " +
            "Traite-les INTÉGRALEMENT : implémente les correctifs, complète les tests, produis les preuves manquantes " +
            "(captures du scénario réel dans artifacts/sprint6/" + issue.Key + "/screenshots/, résultats de tests). " +
            "Documenter un écart ne le résout pas. Commit préfixé de la clé US.");
        sb.AppendLine();
        sb.AppendLine("## Écarts à traiter");
        foreach (var e in ecarts) sb.AppendLine($"- {e}");
        if (!string.IsNullOrWhiteSpace(approverDirective))
        {
            sb.AppendLine();
            sb.AppendLine($"## Directive de {_approver} — à respecter");
            sb.AppendLine(approverDirective);
        }
        sb.AppendLine();
        sb.AppendLine($"## [{issue.Key}] {issue.Summary}");
        sb.AppendLine(issue.Description);

        return new ActorPrompt(engine, systemPrompt, sb.ToString());
    }

    /// <summary>
    /// Tour de travail déclenché par une directive de Hajar adressée à un moteur
    /// SANS revue à conduire (retour 2026-07-17 : « il devrait avancer sur le travail
    /// interrompu sans attendre »). Mission = la directive, rien d'autre — surtout pas
    /// le prompt d'implémentation complet, qui referait le sprint.
    /// </summary>
    public ActorPrompt BuildDirectiveTurn(ActorRole engine, IReadOnlyList<JiraIssue> issues, string directive)
    {
        var systemPrompt = GetSystemPrompt(engine, issues.Count > 0 ? issues[0].Key : "sprint");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"DIRECTIVE DE {_approver.ToUpperInvariant()} — À EXÉCUTER MAINTENANT. " +
            "Tu es sollicité en parallèle de la phase de revues croisées : exécute cette directive, rien d'autre. " +
            "AVANT d'agir : vérifie l'état réel du dépôt (git status, git log -10) — ne réimplémente RIEN qui existe, " +
            "ne touche pas aux US en cours de revue par l'autre moteur au-delà de ce que la directive demande. " +
            "Si la directive vise une étape ultérieure (ex. « à la fin des revues »), prépare ce qui peut l'être " +
            "et dis-le explicitement — n'exécute pas prématurément. " +
            "Commence ta sortie par « ⚑ Intervention de Hajar prise en compte : » suivi de ce que tu fais. " +
            "Commit préfixé de la clé US concernée si tu modifies du code.");
        sb.AppendLine();
        sb.AppendLine($"## Directive de {_approver}");
        sb.AppendLine(directive);
        sb.AppendLine();
        sb.AppendLine("## Périmètre du sprint (référence)");
        foreach (var i in issues) sb.AppendLine($"- [{i.Key}] {i.Summary}");
        if (!string.IsNullOrWhiteSpace(DecisionsRegistry))
        {
            sb.AppendLine();
            sb.AppendLine($"## DÉCISIONS DÉJÀ ACTÉES PAR {_approver.ToUpperInvariant()}");
            sb.AppendLine(DecisionsRegistry);
        }
        return new ActorPrompt(engine, systemPrompt, sb.ToString());
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

    private string BuildUserPrompt(ActorRole role, string sprintContext, string? previousContributions, SessionMode mode = SessionMode.Execution, FrameworkContext? frameworks = null, AgentMemoryContext? memory = null)
    {
        var instruction = role switch
        {
            ActorRole.ClaudePilotage or ActorRole.GptPilotage =>
                "Voici le contexte complet du sprint (user stories + commentaires Jira). " +
                "Produis UNE SEULE synthèse sprint-level pour l'ensemble de ces tickets — pas une analyse " +
                "répétée pour chaque ticket séparément. Couvre en une passe globale : périmètre du sprint, " +
                "dépendances inter-tickets, risques transverses, ordre de priorité recommandé. " +
                "Attends le GO de Hajar avant de passer à l'étape suivante.",

            ActorRole.ClaudeImplementation or ActorRole.GptImplementation or ActorRole.AgImplementation =>
                "Voici le contexte complet du sprint (user stories + commentaires Jira incluant les " +
                "décisions de pilotage). Implémente les user stories dans l'ordre de priorité validé. " +
                "Respecte les conventions du projet (C#/.NET MAUI, Domain/Application/Infrastructure/App, " +
                "tests xUnit, nullable enable). Commit et push à chaque user story complète. " +
                "DISCIPLINE GIT (un autre moteur peut travailler dans le MÊME dépôt en parallèle) : " +
                "`git add` UNIQUEMENT les fichiers de TON périmètre — jamais `git add -A` ni `git add .` ; " +
                "message de commit préfixé de la clé US ; ne réécris jamais l'historique ; " +
                "si un commit échoue (lock, droits), termine ta sortie par la liste exacte " +
                "'FICHIERS MODIFIÉS:' un par ligne, pour commit par l'orchestrateur. " +
                "PREUVES DoD OBLIGATOIRES (SERZENIA-91) : exécute réellement build + tests + smoke ; " +
                "pour toute US à critère visuel, lance l'application, joue le scénario fonctionnel et " +
                "enregistre des captures d'écran étape par étape dans artifacts/sprint6/<CLE-US>/screenshots/ " +
                "(PowerShell System.Drawing CopyFromScreen), résultats de tests dans test-results/, logs dans logs/. " +
                "VIDÉO : si la variable d'environnement FFMPEG est définie, enregistre le scénario en vidéo " +
                "(`& $env:FFMPEG -f gdigrab -framerate 10 -t <durée> -i desktop videos/<scenario>.mp4`) pendant que tu le joues. " +
                "Une US sans preuves n'est PAS terminée. " +
                "DÉCLARATION D'INSTALLATIONS (traçabilité, sans blocage — le GO du run les autorise) : toute " +
                "installation ou modification d'environnement (winget, npm/dotnet tool install, paquet, pilote, " +
                "service, variable machine) doit apparaître dans ta restitution sous une section '## INSTALLATIONS' " +
                "listant quoi, pourquoi, et comment le désinstaller. Aucune installation ? Ne mets pas la section. " +
                "Une installation non déclarée est un écart de process.",

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

            ActorRole.RetrospectiveClaude or ActorRole.RetrospectiveGpt =>
                "Le sprint se termine. Fais ta RÉTROSPECTIVE — un post-mortem honnête, pas une nouvelle " +
                "analyse des US. Structure ta réponse en 3 sections obligatoires :\n" +
                "## Ce qui a bien marché (à garder)\n" +
                "## Ce qui a mal marché\n" +
                "## Plan d'action\n" +
                "Chaque section : listes à puces concrètes et actionnables, pas de généralités. " +
                "Le plan d'action doit proposer des correctifs vérifiables au prochain sprint, pas des vœux pieux.",

            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
        };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(instruction);
        if (role.GetGroup() is ActorGroup.CommitteePilotage or ActorGroup.Analysis)
            sb.AppendLine(ModelChoiceDirective);

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

    // Sections communes à tous les prompts : décisions actées, contexte sprint,
    // frameworks, mémoire projet.
    private void AppendSharedContext(
        System.Text.StringBuilder sb,
        string sprintContext,
        FrameworkContext? frameworks,
        AgentMemoryContext? memory)
    {
        if (!string.IsNullOrWhiteSpace(DecisionsRegistry))
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"## DÉCISIONS DÉJÀ ACTÉES PAR {_approver.ToUpperInvariant()} — APPLIQUE-LES ET CITE-LES, NE LES REDEMANDE JAMAIS");
            sb.AppendLine();
            sb.AppendLine(DecisionsRegistry);
        }

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
