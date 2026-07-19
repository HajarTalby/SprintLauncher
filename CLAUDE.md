# SprintLauncher — Instructions pour Claude Code

Repo autonome, extrait de SERZENIA le 2026-07-02. Les règles générales du projet SERZENIA
(Jira, secrets, procédure de release) restent valables — voir le `CLAUDE.md` du repo SERZENIA.

## Composants

| Projet | Dossier | Rôle |
|---|---|---|
| CLI | `src/SprintLauncher/` | Orchestrateur IA, pipeline acteurs, Jira |
| UI WPF | `src/SprintLauncher.UI/` | Interface desktop : ticket/sprint, log live, GO/ARRÊT |
| Outils | `tools/gh-token/` | Lit le token GitHub depuis le gestionnaire d'identifiants Windows |
| Scripts | `scripts/` | Orchestration codex détaché (run-codex-lot, launch-all, watch) + briefs |

L'UI lance le CLI en sous-processus. **Les deux doivent être buildés et packagés à chaque release.**

## Creuser jusqu'à la cause racine — toujours

Devant un échec : reproduire, lire l'erreur **en entier**, identifier la cause exacte, corriger
**à la source et durablement**. Un contournement laisse le problème intact et il revient à chaque
session. Ne signaler un blocage à Hajar qu'après avoir cherché le pourquoi.

Deux cas réels du 2026-07-19 :

- **« Problème GitHub » à la release v1.2.0** → ni GitHub, ni token révoqué. L'outil `gh-release.exe`
  vivait dans le **scratchpad d'une session Claude**, nettoyé depuis : il ne restait que l'apphost
  `.exe` sans son `.dll`. Correctif durable : outil recréé **dans le repo** (`tools/gh-token/`).
- **« Le classifier bloque »** → les settings n'avaient pas bougé depuis le 07/07. Vraie cause :
  `defaultMode: "dontAsk"` renvoie au classifier (jugement LLM **non déterministe**) toute commande
  qui ne matche pas exactement un motif `allow`. Le préfixe `cd ...;` cassait le motif
  `PowerShell(git *)`. Sans le préfixe (`git -C <repo> ...`), ça passe.

**Conséquences pratiques :**
- Ne jamais préfixer une commande par `cd` — utiliser `git -C <repo>`, `--project <path>`.
- Commande bloquée sans raison évidente : réessayer telle quelle (le verdict varie), ou la simplifier
  pour qu'elle matche un motif `allow`.
- Ne jamais créer un outil durable dans un dossier temporaire.

## Rien ne reste en local

Tout — commits, branches, tags, outils, scripts — doit être remonté sur GitHub. En fin de lot :
`git status`, `git log origin/main..HEAD`, push des branches **et** des tags.

## Ne pas casser le dev des acteurs

Les acteurs du sprint launcher travaillent en parallèle, parfois sur le même working tree.
Lors des merges et nettoyages :
- `git add` ciblé, jamais `git add -A` à l'aveugle.
- Jamais de réécriture d'historique partagé, jamais de force-push.
- Ne jamais supprimer un worktree où un acteur tourne encore (`Get-Process codex` avant).
- Vérifier `git status` du worktree avant tout `worktree remove` : si autre chose que
  `codex-last.txt` / `codex-run.log` traîne, s'arrêter et le signaler.

## Hook pre-commit

Le hook pre-commit lance un vrai acteur IA (~5 min de quota). Committer en `--no-verify` et valider
par build + tests à la place.

## Scripts PowerShell — encodage

- Les `.ps1` doivent être **strictement ASCII** : PS 5.1 les lit en Windows-1252, un `—` casse le parsing.
- Ne jamais mélanger les encodages dans un log : `*>>` écrit en UTF-16 et `Add-Content` en ANSI
  donnent un log illisible. Tout écrire en UTF-8 (`Add-Content -Encoding UTF8`).

## Infra codex détaché

`scripts/run-codex-lot.ps1` lance un codex détaché sur un worktree + un brief :
`codex.exe exec --skip-git-repo-check --json --output-last-message <f>
--dangerously-bypass-approvals-and-sandbox`, prompt sur stdin UTF-8 sans BOM, clés API retirées
(mode abonnement). `codex.exe` = `.vscode\extensions\openai.chatgpt-*\bin\windows-x86_64\codex.exe`.
Résultat lisible par lot : `<worktree>\codex-last.txt`.

Les 6 lots de SERZENIA-144 ont été codés par codex en ~25 min, en parallèle, quasi sans quota Claude.
