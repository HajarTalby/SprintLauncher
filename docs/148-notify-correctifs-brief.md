# Lot — Correctifs bloquants du notificateur Slack (SERZENIA-146)

Tu travailles dans le worktree courant, sur la branche `sl-notify-slack`.
Le notificateur `tools/notify/` a été livré au commit `e6eb90b`. Il compile, ses 189 tests
passent, aucun secret n'est en clair. **Mais il ne notifie jamais rien en conditions
réelles.** Deux bugs bloquants prouvés par exécution. Corrige-les à la source.

## Contexte d'exécution réel (important)

Les hooks tournent dans le **repo cible** (ex. `C:\Users\najwa\OneDrive\Desktop\SERZENIA`),
pas dans le repo SprintLauncher. Le `.env` qui contient les webhooks vit uniquement à la
racine du repo SprintLauncher (`C:\Users\najwa\OneDrive\Desktop\SprintLauncher\.env`).
C'est cet écart qui casse tout.

## Bug 1 — `dotnet run` échoue dans le repo cible (BLOQUANT)

`scripts/notify/common.ps1` → `Invoke-SlNotify` appelle `dotnet run --project <notify.csproj>`
sans fixer le répertoire de travail. Le repo SERZENIA contient un `global.json` qui épingle
le SDK `9.0.300`, absent de la machine (seul `8.0.419` est installé). Reproduction faite :

```
> cd C:\Users\najwa\OneDrive\Desktop\SERZENIA
> dotnet run --project <...>\tools\notify\notify.csproj -- --actor ccode --level blocked --text test
Install the [9.0.300] .NET SDK or update [...\SERZENIA\global.json] to match an installed SDK.
exit -2147450735
```

Le hook meurt donc avant même d'atteindre le code C#. Aucune alerte ne part.

Correctif attendu : **ne pas dépendre du SDK ni du cwd au moment du hook.** Publie l'outil
une fois (binaire ou DLL sous un chemin stable dans le repo SprintLauncher) et fais que le
wrapper invoque directement ce binaire. Si un build à la volée reste nécessaire en secours,
il doit s'exécuter avec le cwd forcé sur le repo SprintLauncher. Un hook doit rester rapide
(pas de restore/build à chaque appel) et supporter plusieurs acteurs en parallèle sans se
disputer `obj/`+`bin/`.

## Bug 2 — le `.env` est cherché au mauvais endroit (BLOQUANT)

`tools/notify/Program.cs` fait `EnvFile.FindRepoEnvFile(Directory.GetCurrentDirectory())`.
Depuis le repo cible, la remontée trouve le `.git` de SERZENIA et retourne
`SERZENIA\.env` — qui n'existe pas. `Load` rend un dictionnaire vide, `WebhookResolver`
rend `null`, et `Program` fait `return 0` **en silence**. Même une fois le bug 1 corrigé,
rien ne partirait, et rien ne le signalerait.

Correctif attendu : résoudre le `.env` à partir de l'**installation de l'outil**
(`AppContext.BaseDirectory` remonté jusqu'à la racine du repo SprintLauncher), avec priorité
à une variable d'environnement explicite `SPRINTLAUNCHER_HOME` si elle est définie, et le
cwd seulement en dernier recours. Garde le comportement « pas de config → exit 0 » (ne pas
faire échouer un hook), mais **distingue** « pas de webhook configuré » (silencieux, normal)
de « fichier .env introuvable » (une ligne sur stderr, sans jamais afficher d'URL).

## Bug 3 — `.env.example` ne documente pas les webhooks

Aucune entrée `SLACK_*` dans `.env.example`. Hajar doit savoir quoi remplir. Ajoute les
clés attendues (`SLACK_WEBHOOK_CCODE`, `_AG`, `_CODEX`, `_SL`, `SLACK_WEBHOOK_DEFAULT`)
avec des valeurs **vides ou factices**, jamais de vraie URL, plus un commentaire court.

## Ajout demandé — `--check`

Ajoute un mode `--check` qui n'envoie rien et rapporte : chemin du `.env` retenu, présence
(oui/non) d'un webhook par acteur, **URL masquée uniquement**. Il doit sortir en code 0 si
la config est exploitable, 1 sinon. C'est le test que Hajar et moi pourrons rejouer sans
webhook réel et sans spammer Slack.

## Contraintes

- Ne modifie **jamais** `.claude/settings.json` (garde volontaire). Les hooks proposés
  restent dans `docs/146-hooks-proposes.json`.
- Aucun webhook réel ni token en clair dans le code, les tests ou les commits.
- Ne touche pas au repo SERZENIA : un autre agent y travaille en ce moment.
- Scripts `.ps1` en ASCII strict (PS 5.1 les lit en Windows-1252).

## Validation exigée avant de rendre la main

1. `dotnet test SprintLauncher.sln` — tout vert (189 tests aujourd'hui, plus les tiens).
2. Tests neufs couvrant : résolution du `.env` depuis un cwd étranger, absence de `.env`,
   priorité `SPRINTLAUNCHER_HOME`, et sortie de `--check`.
3. **Preuve d'exécution réelle** : lance le wrapper `ccode-stop.ps1` (ou `--check`) avec
   pour cwd `C:\Users\najwa\OneDrive\Desktop\SERZENIA` et montre dans ton résumé la sortie
   obtenue — elle ne doit plus contenir d'erreur SDK, et doit désigner le `.env` du repo
   SprintLauncher. Sans cette preuve, le lot n'est pas fini.
4. Commit sur `sl-notify-slack` et push sur `origin`.

Résume à la fin : ce que tu as changé, la sortie exacte du test du point 3, et toute dette
que tu laisses.
