# Brief codex — Finalisation release v1.2.1 + ménage git (SERZENIA-147)

Repo : `C:\Users\najwa\OneDrive\Desktop\SprintLauncher`. Tu tournes dans le worktree
`C:\Users\najwa\OneDrive\Desktop\SL-wt-release`, sur une branche `sl-release-v1.2.1`
partant de `main`. Commits en `--no-verify` (le hook pre-commit est un acteur IA).

## Contexte
Les binaires v1.2.1 ont été packagés le 19/07 mais depuis un `.csproj` resté à **1.1.2** :
la version interne ne correspond pas au nom de la release. À corriger proprement, puis
faire le ménage des branches/worktrees obsolètes.

## Écart 1 — Version (bump + rebuild + tag)
1. Passer la version **1.1.2 → 1.2.1** dans les DEUX projets :
   `src/SprintLauncher/SprintLauncher.csproj` et
   `src/SprintLauncher.UI/SprintLauncher.UI.csproj`
   (balises `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`).
2. `dotnet test` — tous les tests doivent rester verts (lance depuis le worktree, PAS
   depuis un cwd où `global.json` épingle un SDK 9.0.300 absent).
3. Rebuild des DEUX binaires en Release, self-contained, single-file, win-x64, dans
   `release/sprint-launcher-v1.2.1/` (écrase l'existant) :
   - `dotnet publish src/SprintLauncher -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o release/sprint-launcher-v1.2.1`
   - `dotnet publish src/SprintLauncher.UI -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o release/sprint-launcher-v1.2.1`
4. **Tests T2 et T3 automatisables** (depuis le dossier release, pas le projet source) :
   - T2 : lancer `sprint-launcher.exe` sans argument → doit afficher l'usage et
     « Appuyez sur une touche pour fermer… » (capture stdout en UTF-8, exit non bloquant).
   - T3 : copier `.env` à côté, lancer `sprint-launcher.exe SERZENIA-138 --no-cache` →
     le dry-run doit démarrer (lire les premières lignes de stdout jusqu'à « ANALYSE »).
   - **T1 (UI visuelle) : NE PAS le tenter** — il exige un affichage réel, il sera fait
     par Claude. Note-le comme « T1 à valider par Claude » dans ton message final.
5. Re-zipper : `release/sprint-launcher-v1.2.1.zip` (écrase l'existant).
6. Commit `--no-verify` : « chore: SERZENIA-147 bump version interne 1.1.2 → 1.2.1
   (release cohérente) + rebuild binaires ». Push `sl-release-v1.2.1` sur origin.
7. Tag `v1.2.1` sur ce commit et `git push origin v1.2.1`. **Le tag local + push tag est
   autorisé. NE PAS créer de release GitHub** (`gh release create` / API REST interdits :
   ça attend la validation de Hajar).

## Écart 2 — Ménage git
Tout a été sauvegardé sur origin, tu peux nettoyer sans risque de perte.
Retirer ces worktrees (leur contenu SERZENIA-144 est déjà intégré dans `main` via
`cad6840`) :
```
git worktree remove C:\Users\najwa\OneDrive\Desktop\SL-wt-lot1
git worktree remove C:\Users\najwa\OneDrive\Desktop\SL-wt-lot2
git worktree remove C:\Users\najwa\OneDrive\Desktop\SL-wt-lot5
git worktree remove C:\Users\najwa\OneDrive\Desktop\SL-wt-lot6
```
Supprimer ces branches LOCALES (leur contenu est dans `main` et/ou sur origin) :
```
git branch -D sl-lot1-modele-complexite sl-lot2-pause-horodatage sl-lot5-interpretation sl-lot6-gardien
git branch -D sl-139-analyse-per-us sl-141-ag-integration sl-142-memoire-agents sl-fixes-2026-07-16 sl-quota-auto-resume sl-141-ag-spike
```

## INTERDICTIONS ABSOLUES (ne jamais faire)
- **NE PAS** toucher au worktree `SL-wt-notify` ni à la branche `sl-notify-slack`
  (sujet Slack en cours, séparé).
- **NE PAS** créer de release GitHub.
- **NE PAS** transitionner de ticket Jira, ni poster de commentaire Jira.
- **NE PAS** supprimer de branche sur origin (uniquement les branches LOCALES ci-dessus).
- **NE PAS** réécrire l'historique de `main` (pas de rebase/force-push sur main).

## Livraison
Message final : versions bumpées, résultat des tests (T2/T3 verts ou non, T1 laissé à
Claude), tag posé, worktrees/branches nettoyés, et tout ce qui n'a pas pu être fait avec
la raison précise.
