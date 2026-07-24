# Brief — Watcher de sessions Codex → alertes Slack (point 3 SERZENIA-155)

> Statut : **à faire** (lot dédié, pas dans une session lourde). Décidé par Hajar le 2026-07-23.
> Prérequis livrés : outil `notify` (Slack par acteur) opérationnel ; points 1 (alertes de
> session Claude Code) et 2 (fin de délégation `run-codex-lot.ps1`) déjà câblés et prouvés en réel.

## Problème

Les sessions **codex interactives lancées par Hajar dans VS Code** ne préviennent pas sur Slack
(fin de tâche, demande d'approbation). Le mécanisme naturel — le hook `notify` de Codex — est
**inutilisable** :

- Le slot `notify` de `~/.codex/config.toml` est **déjà occupé par le runtime computer-use**
  (`runtimes/cua_node/.../codex-computer-use.exe "turn-ended"`). Le remplacer casserait le
  computer-use.
- `config.toml` est **réécrit en direct par l'app Codex Desktop** (timestamps `marketplaces`
  observés modifiés en pleine session) → un edit manuel du `notify` serait écrasé.

Conclusion : ne pas toucher au hook global. Surveiller à la place les logs que Codex écrit déjà.

## Mécanisme retenu : watcher de rollout

Codex écrit un fichier JSONL par session :
`~/.codex/sessions/<yyyy>/<MM>/<dd>/rollout-<timestamp>-<uuid>.jsonl`, en **append** au fil des
événements. Marqueurs identifiés (via un rollout réel du 2026-07-23) :

| event_msg `type`   | sens                         | action watcher                          |
|--------------------|------------------------------|-----------------------------------------|
| `task_started`     | début d'un tour              | (rien / mémoriser le début)             |
| `task_complete`    | **fin d'un tour**            | **ping Slack `notify --actor codex`**   |
| `*approval*`       | demande d'approbation (à confirmer en session interactive, absent des runs bypass-sandbox) | ping `--level warn` |
| `agent_message`    | dernier message de l'agent   | source du texte de contexte du ping     |

### Composant `codex-session-watcher`

- **Découverte** : surveiller l'arbre `~/.codex/sessions/**/rollout-*.jsonl` (FileSystemWatcher +
  scan initial du jour). Nouveau fichier = nouvelle session.
- **Tail incrémental** : lire chaque `.jsonl` depuis son dernier offset (curseur persistant par
  fichier, comme `LiveInputInbox`), parser chaque ligne JSON, réagir sur `task_complete` /
  approbation.
- **Ping** : appeler l'outil `notify --actor codex --level info|warn --text ... --context <dernier agent_message tronqué>`.
- **Anti-doublon avec le point 2** : les sessions lancées par `run-codex-lot.ps1` ont **déjà**
  leur ping de fin. Les exclure — piste : détecter le `cwd`/worktree de la session (les rollouts
  contiennent le répertoire) et ignorer ceux sous `SL-*` / worktrees de délégation, OU marquer les
  délégations via une variable d'env repérable dans le rollout.
- **Debounce / anti-spam** : un ping par `task_complete`, pas par event. Optionnel : ne pinguer que
  si la session est restée idle > N s après `task_complete` (= vraiment en attente d'Hajar).

### Exécution permanente

Process de fond qui survit au redémarrage : **tâche planifiée Windows** (`schtasks`) déclenchée au
logon (`ONLOGON`), relançant le watcher s'il tombe. À préparer en fichier `.xml`/commande — mais
**la création de la tâche schtasks reste la main de Hajar** (droits), lui fournir la commande prête.

## Tests (bloquants)

1. **Unitaire** : parseur d'événements sur un rollout figé (fixture `.jsonl` copiée) → détecte les
   N `task_complete`, extrait le bon `agent_message`, ignore le reste. Zéro réseau.
2. **Anti-doublon** : un rollout de session-délégation (cwd sous `SL-*`) → **aucun** ping.
3. **Réel (participation Hajar)** : watcher lancé, Hajar ouvre une session codex VS Code, fait un
   tour → un ping `#codex` à la fin, un seul. Puis provoquer une demande d'approbation → ping warn.

## Estimation

- Parseur + curseurs + tail incrémental + tests unitaires : **~cœur d'un lot** (comparable au lot 1
  listener, réutilise les patterns `LiveInputInbox` / `SlackSocketListener`).
- Anti-doublon + debounce : petite couche.
- Tâche planifiée + doc d'install : léger, mais nécessite un aller-retour Hajar (schtasks).
- **Total : ~1 lot codex terra effort high**, délégable via `run-codex-lot.ps1` pour le cœur +
  tests unitaires (2 premiers tests), le test réel (3) restant à faire avec Hajar.

## Pistes de démarrage

- Réutiliser `SlackSink.ActorFromRole` / l'outil `notify` déjà en place (ne pas ré-écrire l'envoi).
- Regarder un rollout complet pour figer le schéma exact des lignes (`payload` imbriqué vs plat) et
  le nom précis de l'événement d'approbation en mode interactif (les runs `--dangerously-bypass`
  ne le produisent pas).
- Curseur persistant : un petit fichier d'offsets à côté (ex. `~/.codex/.watcher-offsets.json`),
  jamais commité.
