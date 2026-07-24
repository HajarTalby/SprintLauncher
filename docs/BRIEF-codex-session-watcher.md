# Brief — Watcher de sessions Codex → alertes Slack (point 3 SERZENIA-155)

> Statut : **à construire** (lot dédié codex terra high). Schéma figé sur rollouts réels du
> 2026-07-23 par Claude, dont la session interactive VS Code réelle d'Hajar (« session 98 »).
> Prérequis livrés : outil `notify` (Slack par acteur) opérationnel ; points 1 (alertes session
> Claude Code) et 2 (fin de délégation `run-codex-lot.ps1`) câblés+prouvés en réel.

## Objectif

Un process de fond qui surveille les logs des sessions **codex interactives d'Hajar dans VS Code**
et envoie un ping Slack `#codex` **à la fin de chaque tour**, SANS toucher au hook `notify` global
(pris par computer-use, réécrit par l'app — cf. « Pourquoi pas le hook »). On lit les rollouts que
codex écrit déjà.

## Schéma réel des rollouts (FIGÉ — vérifié sur fichiers 2026-07-23, dont la session 98)

Codex écrit un JSONL par session, en **append** :
`~/.codex/sessions/<yyyy>/<MM>/<dd>/rollout-<ts>-<uuid>.jsonl` (`~` = `%USERPROFILE%`).
Chaque ligne = un objet JSON `{"timestamp":...,"type":<type-haut-niveau>,"payload":{...}}`.

### `session_meta` (identité de la session → sert au FILTRAGE)
```json
{"type":"session_meta","payload":{
  "cwd":"C:\\...","originator":"codex_vscode","thread_source":"user",
  "source":{"subagent":{"other":"guardian"}}, ...}}
```
- `cwd` : répertoire de travail (chemin Windows, backslashes échappés).
- `originator` : origine. Valeurs RÉELLES observées :
  - `codex_vscode` = session VS Code (**cible** si `thread_source=user`).
  - `codex_exec` = délégation CLI (`run-codex-lot.ps1`), cwd sous `SL-*` → à exclure.
  - `codex_work_desktop` / `Codex Desktop` = app computer-use / desktop, cwd sous `Documents\Codex` → à exclure.
- `thread_source` : `user` (piloté par un humain) ou `subagent` (sous-agent interne, ex. guardian).
- `source.subagent` : **présent** ⇒ sous-agent interne à **exclure**.

⚠️ **Un rollout contient PLUSIEURS lignes `session_meta`** (observé 27 dans la session 98 :
compaction/reconnexions VS Code les ré-émettent), **toutes de même identité**. Le parseur doit
**prendre la 1re `session_meta` et ignorer les suivantes** (ne jamais planter dessus).

### Événements `event_msg` (`payload.type` = le vrai type)
- `task_started` : `{type,turn_id,started_at}`.
- `task_complete` : **fin d'un tour → PING**.
  `{type,turn_id,last_agent_message,started_at,completed_at,duration_ms}`.
  **`last_agent_message` porte déjà le texte du dernier message** → source du `--context` du ping.
  **Ne PAS corréler un `agent_message` séparé.** (La session 98 : 16 `task_complete` sur sa vie.)
- Approbation : **AUCUN type `*approval*` observé, même en session interactive réelle** — la session
  98 tourne en auto/bypass (patch_apply/exec sans gate) → pas d'événement d'approbation du tout.
  ⇒ **Ne pas investir dessus.** Fournir juste un point d'extension `bool IsApprovalRequest(line)`
  qui rend `false` (testé à vide) ; le vrai type sera câblé plus tard SI le test réel en révèle un.

## Règle de FILTRAGE (le cœur du lot — se tromper = spam ou silence)

Pour une session (identité = sa 1re `session_meta`), pinguer ses `task_complete` **UNIQUEMENT si
TOUTES ces conditions sont vraies** :
1. `originator == "codex_vscode"` ;
2. `thread_source == "user"` ;
3. `source.subagent` **absent** ;
4. `cwd` **hors** worktree de délégation : exclure si un segment du chemin matche `^SL-` (insensible
   casse), ex. `...\Desktop\SL-155-lot2` — ces sessions ont déjà leur ping (point 2).

Fixtures fournies dans `tools/codex-watcher.Tests/fixtures/` (données réalistes, attendus) :
| fixture                     | originator          | thread_source | subagent | cwd             | attendu     |
|-----------------------------|---------------------|---------------|----------|-----------------|-------------|
| `interactive-vscode.jsonl`  | codex_vscode        | user          | absent   | SERZENIA        | **2 pings** |
| `guardian-subagent.jsonl`   | codex_vscode        | subagent      | guardian | SERZENIA        | 0 ping      |
| `computer-use.jsonl`        | codex_work_desktop  | user          | absent   | Documents\Codex | 0 ping      |
| `delegation-worktree.jsonl` | codex_exec          | user          | absent   | SL-155-lot2     | 0 ping      |

`interactive-vscode.jsonl` contient **deux** lignes `session_meta` (cas réel) et 2 `task_complete` :
les 2 pings doivent avoir pour contexte les `last_agent_message` respectifs (« …276 verts. » puis
« Prochaine etape : … »).

## Architecture demandée

Nouveau **tool console autonome** (parallèle à `tools/notify/`), long-running :
`tools/codex-watcher/` + tests `tools/codex-watcher.Tests/`. Découper en classes **pures/testables**
(tests unitaires : NI réseau NI horloge NI écriture FS hors tmp) :

- `SessionMeta` / `RolloutEvent` : modèles System.Text.Json, parsing **tolérant** (champ manquant ⇒
  défaut, jamais d'exception sur une ligne inattendue ou non-JSON).
- `RolloutParser` : lit la 1re `session_meta`, puis rend un `CompletedTurn{TurnId,LastAgentMessage,
  CompletedAt}` par `task_complete`. Ignore silencieusement les autres types et les `session_meta`
  surnuméraires.
- `SessionFilter.ShouldNotify(SessionMeta) : bool` — applique les 4 règles. **Testé sur les 4 fixtures.**
- `OffsetStore` : curseur d'offset **persistant par fichier** (JSON `~/.codex/.watcher-offsets.json`,
  jamais commité). Même esprit que `LiveInputInbox` (`src/SprintLauncher/Runners/LiveInputInbox.cs`) :
  lire depuis le dernier offset, ne consommer que jusqu'au dernier `\n`, gérer troncature/rotation.
  **Essentiel** : les rollouts font plusieurs Mo et grossissent en direct — jamais de relecture complète.
- `RolloutWatcher` : orchestration. `FileSystemWatcher` sur `~/.codex/sessions/**` (filtre
  `rollout-*.jsonl`) **+ scan initial du jour**. Nouveauté → lire lignes neuves (offset), parser,
  et pour chaque `task_complete` d'une session qui passe `SessionFilter` → appeler `notify`.
  Dédup par `turn_id` déjà vu (mémoire + offset store) : **un ping par `task_complete`**, jamais deux.
- `NotifyInvoker` : lance `notify` en sous-process, **exactement comme le point 2** de
  `scripts/run-codex-lot.ps1` :
  `dotnet run --project tools/notify -- --actor codex --level info --text "<résumé court>" --context "<last_agent_message tronqué ~500 char>"`.
  Réutiliser `notify` (NE PAS réécrire l'envoi Slack). Exporter `SPRINTLAUNCHER_HOME` = racine du
  repo principal (pour que `notify` trouve `.env`). Échec du ping = loggé, **non bloquant**.

## Tests (BLOQUANTS — les 2 premiers, zéro réseau)

1. **Parseur** : `Parse(interactive-vscode.jsonl)` rend **2** `CompletedTurn` avec les bons
   `LastAgentMessage`, malgré les 2 `session_meta`. `Parse(guardian-subagent.jsonl)` rend 1
   `task_complete` (le parseur ne filtre pas), mais…
2. **Filtrage** : `SessionFilter.ShouldNotify` = **vrai uniquement** pour `interactive-vscode` ;
   **faux** pour `guardian-subagent`, `computer-use`, `delegation-worktree` (4 cas).
3. **Réel (participation Hajar, HORS lot)** : watcher lancé, Hajar fait un tour dans sa session codex
   VS Code → **un** ping `#codex` à la fin.

Lot « vert » = `dotnet build` OK (`tools/codex-watcher` + `.Tests`) **et**
`dotnet test tools/codex-watcher.Tests` vert (tests 1 & 2). Committer sur `sl-155-watcher`
en `--no-verify` (hook = acteur IA) et **pousser**.

## Exécution permanente (NE PAS créer la tâche — fournir la commande)

Process de fond survivant au logon : **tâche planifiée Windows `ONLOGON`** relançant le watcher.
Fournir dans `docs/` la **commande `schtasks` prête à copier** (+ `.xml` si utile), mais **la création
reste la main de Hajar** (droits). Ne PAS exécuter `schtasks` dans le lot.

## Pourquoi pas le hook `notify` de codex (rappel)

Le slot `notify` de `~/.codex/config.toml` est **déjà pris par computer-use**
(`codex-computer-use.exe "turn-ended"`) et l'app **réécrit** `config.toml` en direct → un edit manuel
serait écrasé et casserait le computer-use. **Ne jamais toucher ce fichier.**

## Contraintes repo (IMPÉRATIF)

- Lancer `dotnet` **depuis le repo SprintLauncher** (le `global.json` de SERZENIA épingle un SDK absent).
- `codex-watcher` = **projet .NET indépendant** (`.csproj` autonome comme `tools/notify`, ne pas casser
  le build global de la solution SL).
- Ne PAS committer `.watcher-offsets.json` ni aucun rollout réel (uniquement les fixtures synthétiques
  déjà fournies).
- Commits `--no-verify`, branche `sl-155-watcher`, **pousser** en fin de lot.
