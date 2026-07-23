# Slack — brancher les notifications des acteurs (SERZENIA-146)

Le code est prêt. Il ne manque que les **URLs** (côté ton compte Slack — c'est ta main,
comme les permissions). ~5 minutes. Deux options ; le design livré marche avec les deux.

---

## Option A — 4 webhooks entrants (design actuel, rien à recoder)

Un webhook = une URL liée à **un canal**. On en crée 4 (un par acteur).

1. Va sur https://api.slack.com/apps → **Create New App** → **From an app manifest**.
2. Choisis ton workspace SERZENIA, colle le contenu de `docs/slack-app-manifest.json`, crée.
3. Onglet **Incoming Webhooks** → active le toggle.
4. **Add New Webhook to Workspace** → choisis le canal `#sl-ccode` → copie l'URL.
5. Répète le bouton pour `#sl-ag`, `#sl-codex`, `#sl-sl` (4 URLs au total).
6. Colle dans `SprintLauncher/.env` (jamais commité) :

   ```
   SLACK_WEBHOOK_CCODE=https://hooks.slack.com/services/...
   SLACK_WEBHOOK_AG=https://hooks.slack.com/services/...
   SLACK_WEBHOOK_CODEX=https://hooks.slack.com/services/...
   SLACK_WEBHOOK_SL=https://hooks.slack.com/services/...
   SLACK_WEBHOOK_DEFAULT=      # optionnel, repli
   ```

7. Vérifie : `tools\notify\published\notify.exe --check` → chaque acteur doit afficher `yes`.

> Les noms de canaux (`#sl-ccode`…) sont libres : le webhook est lié au canal choisi à
> l'étape 4, pas à une clé. Choisis le canal qui correspond à l'acteur.

---

## Option B — 1 bot token `chat:write` (une seule URL secrète)

Plus propre côté secrets (un seul token), mais **change le design livré** : il faut poster
via `chat:write` avec un ID de canal au lieu d'un webhook par acteur. Recodage du
`WebhookResolver` + `SlackNotifier` (~1 lot). À choisir seulement si tu préfères gérer un
token unique plutôt que 4 URLs.

---

## Une fois les URLs collées

Je lance le test end-to-end moi-même (`notify.exe --check` + un message réel par canal) et
je te montre le résultat. Aucune action manuelle de ton côté après le collage.
