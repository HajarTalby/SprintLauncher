# Brief codex — Notificateur Slack multi-acteurs (SERZENIA-146)

## Objectif
Un seul mini-outil de notification, appelé par TOUS les acteurs (ccode, antigravity,
codex, SL), qui publie dans un canal Slack privé dédié par acteur. Doit fonctionner
même quand le Sprint Launcher est arrêté (appel direct du webhook, aucun démon).

## Contraintes non négociables
- **Le webhook est un secret.** Lu depuis `SprintLauncher/.env` uniquement. Jamais en
  clair dans le code, les logs, un commit, Jira, ou la sortie console.
- Le message affiché en log doit masquer l'URL (`https://hooks.slack.com/services/***`).
- Aucune dépendance externe : `System.Net.Http` suffit. Pas de SDK Slack.
- Doit être un outil durable **dans le repo** (`SprintLauncher/tools/notify/`), jamais
  dans un dossier temporaire.

## Interface
```
notify --actor <ccode|ag|codex|sl> --level <info|warn|blocked> --text "..."
       [--context "SERZENIA-112"]
```
Résolution du webhook : variable `SLACK_WEBHOOK_<ACTOR>` dans `.env`
(ex. `SLACK_WEBHOOK_CCODE`). Si absente → fallback `SLACK_WEBHOOK_DEFAULT`.
Si aucune → **exit 0 silencieux** (ne jamais faire échouer un hook parce que les
notifications ne sont pas configurées).

Payload Slack : `{"text": "..."}` en POST JSON. Format du texte :
`[<ACTOR>] <niveau> — <texte>` + ligne contexte si fourni. UTF-8 explicite.

## Robustesse
- Timeout 10 s, 2 retries avec backoff. Un échec réseau ne doit jamais bloquer l'appelant :
  exit 0 + message sur stderr.
- Tests unitaires : résolution du webhook par acteur, fallback, absence de config,
  masquage du secret dans les logs, format du payload. Le POST réel doit être mockable
  (injecter `HttpMessageHandler`).

## Câblage ccode (à livrer aussi)
Proposer — **sans éditer `.claude/settings.json`** (garde légitime, c'est la main de
Hajar) — un fichier `SprintLauncher/docs/146-hooks-proposes.json` contenant les hooks :
- `Notification` → level `blocked` (Claude attend une permission/un input)
- `StopFailure` avec matcher `rate_limit|billing_error` → level `blocked` (quota atteint)
- `Stop` → level `info` (tâche terminée)
- `PostToolUseFailure` → level `warn`
Les scripts wrapper PowerShell vont dans `SprintLauncher/scripts/notify/`, lisent le JSON
du hook sur stdin, en extraient le champ utile, et appellent `notify`.
**Impératif** : ces scripts doivent sortir immédiatement si `SPRINTLAUNCHER_ACTOR=1`
est présent (même garde que `handoff-load.ps1`), sinon chaque acteur du SL spammera le
canal ccode.

## Câblage Antigravity
Hooks `.agents/hooks.json` (`Stop`, `PreToolUse`). Pas d'événement quota nommé : détecter
via `terminationReason: error` + motif du message d'erreur. **À valider par un test réel**
sur la version installée (`agy` 1.1.4) — si non vérifiable, le signaler comme dette, ne
pas supposer que ça marche.

## Hors périmètre
Ne pas créer les canaux ni l'app Slack (Hajar le fait, elle fournira les webhooks).
Ne pas transitionner de ticket Jira. Ne pas publier de release.

## Livraison
Branche `sl-notify-slack`, commits en `--no-verify`, poussée sur origin. Tests verts.
