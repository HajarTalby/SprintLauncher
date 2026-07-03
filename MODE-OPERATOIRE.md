# Mode opératoire — Sprint Launcher

Version : v1.0.6 (2026-07-03) | Repo : `SprintLauncher/` (repo autonome depuis v1.0.6)

---

## Principe fondamental

**L'outil ne remplace jamais la décision humaine.** Les GO/NO-GO sont pilotés par Hajar à chaque étape structurante. L'outil orchestre les acteurs IA, mais c'est Hajar qui décide : démarrer le sprint, valider l'analyse de pilotage, passer en mode écriture, publier les résultats. Aucune action irréversible n'est effectuée sans `--write` et sans validation consciente.

**Règle absolue — transitions Jira :** Aucune session ne doit transitionner un ticket Jira (Done, In Progress, etc.) sans confirmation explicite de Hajar Talby. L'outil ne fait jamais de transition automatique de statut.

---

## 1. Prérequis

### Installation rapide (depuis la release)

Extraire le zip de la release dans un dossier, copier `.env.example` → `.env`, remplir les credentials.

### Prérequis systèmes

| Composant | Rôle | Détection |
|---|---|---|
| `claude.exe` | Acteurs Claude (CLI headless) | Auto : `%LOCALAPPDATA%\Packages\Claude_*\...\claude.exe` puis PATH |
| `codex.exe` | Acteurs GPT (CLI headless) | Auto : `%USERPROFILE%\.vscode\extensions\openai.chatgpt-*\bin\windows-x86_64\codex.exe` puis PATH |

Aucun chemin versionné codé en dur. Si les binaires ne sont pas trouvés, l'outil l'indique explicitement et renvoie une erreur non-fatale pour cet acteur.

### Fichier `.env` (obligatoire, non committé)

Copier `.env.example` → `.env` dans le même dossier que les binaires (ou dans le dossier courant) :

```
JIRA_BASE_URL=https://your-domain.atlassian.net
JIRA_EMAIL=your@email.com
JIRA_API_TOKEN=<token Jira>

# Optionnels — valeurs par défaut si absents
CLAUDE_MODEL=claude-opus-4-8
CODEX_MODEL=gpt-5.5
ACTOR_TIMEOUT_SECONDS=600

# Identité projet (injectée dans tous les prompts)
# PROJECT_NAME=SERZENIA
# APPROVER_NAME=Hajar
# FRAMEWORK_KEYS=SERZENIA-70,SERZENIA-89,SERZENIA-91

# Chemin absolu du dépôt source (facultatif si lancé depuis le repo)
# SERZENIA_REPO=C:\Users\najwa\OneDrive\Desktop\SERZENIA
```

**Détection automatique du `.env`** : le launcher cherche dans cet ordre :
1. Répertoire courant (`.\env`)
2. Répertoire du binaire
3. Dossier parent du binaire
4. `tools\sprint-launcher\.env` (compatibilité ancienne structure SERZENIA)

### Authentification

- **Claude** : OAuth via Claude Desktop App (`~/.claude/.credentials.json`). Aucune clé API requise. Mode abonnement Claude Max.
- **Codex** : OAuth via extension VS Code ChatGPT (`~/.codex/auth.json`, `auth_mode: "chatgpt"`). Aucune clé API requise. Mode abonnement ChatGPT Plus.
- **Jira** : Basic Auth (`email:token`) via `.env`. Jamais transmis aux subprocessus claude/codex.

### Prérequis opérationnel critique — sessions concurrentes

**Fermer toutes les sessions Claude Desktop et Codex (extension VS Code) avant tout lancement.**

Sessions concurrentes ouvertes = conflit OAuth : le subprocess Codex attend indéfiniment un jeton qui n'arrive jamais, sans timeout ni message d'erreur. Ce n'est pas un bug de l'outil — c'est une contrainte de l'architecture OAuth.

---

## 2. Lancement

### Via l'interface desktop (mode normal)

```powershell
# Depuis le dossier release
.\sprint-launcher-ui.exe

# Ou en développement
dotnet run --project src/SprintLauncher.UI
```

L'UI permet de saisir le numéro de ticket ou de sprint, de suivre l'exécution des acteurs en temps réel (journal live, indicateur par acteur), et d'accéder aux artefacts générés.

### Via le CLI (mode avancé / automatisation)

```powershell
# Depuis le dossier release
.\sprint-launcher.exe SERZENIA-138

# Depuis le repo source
dotnet run --project src/SprintLauncher -- SERZENIA-138
```

---

## 3. Commandes disponibles

### Lister les acteurs disponibles

```powershell
.\sprint-launcher.exe --list-roles
```

### Lancer sur un ticket en dry-run (défaut — aucune écriture Jira)

```powershell
.\sprint-launcher.exe SERZENIA-138
```

- Lit tous les commentaires Jira exhaustivement (garde-fou anti-troncature + compteur live)
- Utilise le cache différentiel si disponible
- Génère et exécute les acteurs configurés
- Affiche ce qui serait posté sans écrire
- Écrit prompts et sorties dans `artifacts/run/<KEY>/`

### Lancer avec écriture réelle (commentaires Jira publiés)

```powershell
.\sprint-launcher.exe SERZENIA-138 --write
```

### Lancer sur un sprint complet (via sprints.json)

```powershell
.\sprint-launcher.exe --sprint 6
```

Le fichier `sprints.json` à la racine du dossier mappe les IDs de sprint vers les listes de tickets :
```json
{
  "6": ["SERZENIA-110", "SERZENIA-111", "..."],
  "7": ["SERZENIA-141", "SERZENIA-142", "..."]
}
```

### Reprendre après interruption

```powershell
.\sprint-launcher.exe SERZENIA-138 --resume
```

Lit `artifacts/run/<KEY>/state.json` et reprend depuis le dernier acteur non terminé.

### Forcer une lecture Jira complète (ignorer le cache)

```powershell
.\sprint-launcher.exe SERZENIA-138 --no-cache
```

### Plusieurs tickets en un passage

```powershell
.\sprint-launcher.exe SERZENIA-138 SERZENIA-139
```

### Mode cadrage (Comité Pilotage en premier)

```powershell
.\sprint-launcher.exe --sprint 6 --mode cadrage
```

### Mode interactif (GO/NO-GO manuel entre groupes d'acteurs)

```powershell
.\sprint-launcher.exe SERZENIA-138 --interactive
```

### Publication manuelle d'un résultat GPT Pilotage

```powershell
# Dry-run d'abord
.\sprint-launcher.exe --publish-manual GptPilotage --from-file response.txt SERZENIA-138

# Écriture réelle après validation
.\sprint-launcher.exe --publish-manual GptPilotage --from-file response.txt SERZENIA-138 --write
```

---

## 4. Paramétrisation avancée

### Identité projet (PROJECT_NAME, APPROVER_NAME)

Permet d'utiliser le launcher sur un projet autre que SERZENIA :

```
PROJECT_NAME=MON-PROJET
APPROVER_NAME=Alice
```

Ces valeurs sont injectées dans tous les prompts acteurs à la place de "SERZENIA" et "Hajar".

### Tickets framework (FRAMEWORK_KEYS)

Les tickets framework sont lus au démarrage et injectés dans tous les prompts comme contexte de référence :

```
FRAMEWORK_KEYS=SERZENIA-70,SERZENIA-89,SERZENIA-91
```

Un cache SHA-256 détecte les changements de description et recharge uniquement les tickets modifiés.

### Dépôt source (SERZENIA_REPO)

Chemin absolu du dépôt source passé en `--dir` aux agents d'implémentation :

```
SERZENIA_REPO=C:\Users\najwa\OneDrive\Desktop\SERZENIA
```

Si absent, le launcher tente de détecter automatiquement un dossier `.git` en remontant l'arborescence. Un avertissement est affiché si aucun repo n'est trouvé.

### Injection mémoire Claude Code (MemorySync)

Au démarrage, le launcher lit les fichiers mémoire Claude Code (`memory/*.md`) du repo source et injecte dans les prompts les entrées ayant :
- `type: project` (mémoires projet)
- ou `inject_to_agents: true` (entrées explicitement marquées)

Les mémoires `feedback` (spécifiques au comportement Claude Code) sont exclues.

---

## 5. Cycle complet d'un sprint

```
[HAJAR] GO : lancer le sprint
      │
      ▼
[1] Lecture Jira — cache différentiel + anti-troncature
[2] Sync frameworks (SERZENIA-70, 89, 91) — détection des changements
[3] Sync mémoire projet Claude Code (memory/*.md)
[4] Génération des prompts — system prompt par rôle + contexte complet
      │
      ├──► ClaudePilotage            → claude.exe (stdin)
      ├──► ClaudeImplementation      → claude.exe (stdin)
      ├──► GptImplementation         → codex exec (stdin)
      ├──► GptPilotage               → SEMI-MANUEL (voir §6)
      ├──► CommitteePilotageClaudeChat → claude.exe (délibération séquentielle)
      ├──► CommitteePilotageGptChat  → codex exec
      ├──► CommitteeClaudeChat       → claude.exe
      ├──► CommitteeCcode            → claude.exe
      ├──► CommitteeGptChat          → codex exec
      ├──► CommitteeCodex            → codex exec
      ├──► ClaudeQaVerdict           → claude.exe
      └──► GptQaVerdict              → codex exec (--sandbox read-only)
      │    Checkpoint state.json écrit après chaque acteur
      ▼
[5] Artefacts dans artifacts/run/<KEY>/
      │
      ▼
[HAJAR] Relire les sorties dry-run → valider → relancer avec --write
      │
      ▼
[6] Publication → commentaires Jira signés [agent: ... | us: <KEY>]
      │
      ▼
[HAJAR] Validation finale → GO Done (jamais automatique)
```

---

## 6. Mode semi-manuel GPT Pilotage

L'acteur GPT Pilotage implique une intervention manuelle de Hajar.

**Étape A** — lancer le dry-run : l'outil écrit le prompt dans `artifacts/run/<KEY>/prompt-GptPilotage.txt`

**Étape B** — conduire la session dans ChatGPT web (abonnement ChatGPT Plus) : copier le prompt, coller la réponse dans un fichier texte

**Étape C** — publier :
```powershell
.\sprint-launcher.exe --publish-manual GptPilotage --from-file reponse.txt SERZENIA-138 --write
```

---

## 7. Garde-fous actifs

| Garde-fou | Comportement |
|---|---|
| Anti-troncature Jira | Exception si `fetched != total` — abort explicite |
| Compteur live avant/après | Toute variation concurrente provoque un échec explicite |
| Cache différentiel | Compteur live toujours vérifié ; cache n'est jamais utilisé sans validation |
| Idempotence commentaires | Refuse le doublon d'un commentaire signé identique |
| Isolation API keys | `ANTHROPIC_API_KEY` et `OPENAI_API_KEY` retirés de chaque env subprocess |
| Dry-run par défaut | Aucune écriture Jira sans `--write` |
| Garde commentaire vague | Refuse si corps vide, < 20 chars ou placeholder |
| Détection dynamique binaires | Aucun chemin versionné codé en dur |
| Prompt via stdin | Bypass limite Windows 32 767 chars sur arguments CLI |
| Timeout acteur | Arrêt de l'arbre de processus après `ACTOR_TIMEOUT_SECONDS` |
| Sandbox Codex | Rôles comité/QA GPT utilisent `--sandbox read-only` |
| Checkpoint par acteur | `state.json` — `--resume` reprend sans réexécuter ce qui est fait |
| Transitions Jira interdites | Aucune transition de statut sans validation explicite de Hajar |

---

## 8. Artefacts produits

```
artifacts/
├── framework-cache.json          # Cache SHA-256 des tickets framework
├── jira-cache/
│   └── {timestamp}-{KEY}.json   # Cache différentiel Jira par ticket
└── run/
    └── <KEY>/
        ├── state.json            # Checkpoint acteurs (--resume)
        ├── session-handoff.md    # Résumé état sprint à l'interruption
        ├── report.html           # Rapport HTML post-run
        ├── prompt-<Acteur>.txt   # Prompt généré par acteur
        └── output-<Acteur>.txt   # Réponse acteur
```

---

## 9. Limites et risques opérationnels

### Sessions concurrentes — conflit OAuth (critique)

Claude Desktop ou Codex extension VS Code ouverts en parallèle = conflit OAuth. Fermer avant tout lancement.

### `.git` en lecture seule dans les sessions Codex agentiques

Dans une session Codex pilotée par MCP, le répertoire `.git` est inaccessible en écriture. Les commits doivent être effectués par Claude Code CLI ou directement par Hajar.

### Expiration des tokens OAuth

L'outil ne bascule jamais vers une clé API (`ANTHROPIC_API_KEY`/`OPENAI_API_KEY` sont supprimées des env subprocess). En cas d'expiration, l'acteur échoue explicitement.

### Doublons en cas de relancement sans `--resume`

Sans `--resume` après interruption, tous les acteurs repartent de zéro. Toujours utiliser `--resume` après une interruption en mode `--write`.

### Avertissement SERZENIA_REPO manquant

Si `SERZENIA_REPO` n'est pas défini et qu'aucun `.git` n'est trouvé dans les répertoires parents, l'outil affiche un avertissement et continue — les agents d'implémentation fonctionnent mais n'ont pas accès au code source du dépôt.

---

## 10. Créer une release

```powershell
# CLI
dotnet publish src/SprintLauncher -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o release/sprint-launcher-vX.Y.Z

# UI WPF
dotnet publish src/SprintLauncher.UI -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o release/sprint-launcher-vX.Y.Z

# Zip
Compress-Archive -Path "release/sprint-launcher-vX.Y.Z/*" -DestinationPath "release/sprint-launcher-vX.Y.Z.zip"
```

**Tests BLOQUANTS avant toute déclaration "terminé" :**
- **T1** : `sprint-launcher-ui.exe` double-clic → fenêtre UI s'ouvre et reste ouverte
- **T2** : `sprint-launcher.exe` sans argument → usage affiché (sans mojibake)
- **T3** : `.env` copié + `sprint-launcher.exe SERZENIA-138` → dry-run démarre, Jira lu

Ces tests doivent être réalisés depuis un dossier extrait du zip, jamais depuis le dossier source.
