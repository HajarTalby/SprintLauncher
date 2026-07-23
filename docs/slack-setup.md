# Slack — brancher les notifications des acteurs (SERZENIA-146)

Design : **un seul bot token**. Hajar installe l'app une fois et fournit le token ;
l'agent crée les canaux et poste lui-même (`chat.postMessage`). Aucun clic webhook.

## Ta part (~3 étapes, une seule fois)

1. https://api.slack.com/apps → **Create New App** → **From an app manifest** →
   workspace SERZENIA → colle `docs/slack-app-manifest.json` → **Create**.
   (Scopes demandés : `chat:write`, `channels:manage`, `channels:read`, `channels:join`.)
2. Menu **OAuth & Permissions** → **Install to Workspace** → **Allow**.
3. Copie le **Bot User OAuth Token** (`xoxb-…`) et colle-le dans `SprintLauncher/.env` :

   ```
   SLACK_BOT_TOKEN=xoxb-…
   ```

   Ne le colle **pas** dans le chat (c'est un secret). Dis « c'est mis ».

## Ma part (automatique, après ton « c'est mis »)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/notify/provision-slack.ps1
```

Le script : crée les canaux publics `ccode`, `ag`, `codex`, `sl` (ou rejoint ceux qui
existent), écrit leurs ids dans `.env` (`SLACK_CHANNEL_*`), et poste un message de test
dans chacun. `notify.exe --check` confirme ensuite le câblage.

## Fonctionnement

Chaque événement d'acteur du SL (début/fin de tour, quota, blocage) est posté par
`notify.exe` dans le canal de l'acteur via `chat.postMessage`, **avec le modèle utilisé**.
Le canal par acteur : `SLACK_CHANNEL_<ACTEUR>` dans `.env` (id ou nom), défaut = nom de
l'acteur. Le token n'apparaît jamais en clair (masqué `xoxb-***` dans `--check`).
