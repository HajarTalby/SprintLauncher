# SERZENIA-141 - Lot 0 - Spike de faisabilite Antigravity

Date d'investigation : 2026-07-19  
Branche : `sl-141-ag-spike`  
Perimetre : analyse uniquement, aucune ligne de code d'integration.

## Conclusion courte

Recommandation : **GO-CONDITIONNEL**.

Antigravity n'est **pas present sur cette machine** au moment du spike : aucun `agy.exe`, `antigravity.exe`, extension VS Code, installation registre, Appx ou entree `winget` exploitable n'a ete trouvee. En revanche, la documentation officielle Google Antigravity expose un binaire CLI `agy` avec un mode non interactif `-p`, utilisable pour des hooks/scripts :

```powershell
agy -p "Review this git diff and draft a conventional commit message" --cwd <repo>
```

Ce mode rend une integration SL plausible, mais pas encore validable ici. Les conditions minimales avant GO d'implementation sont :

1. installer `agy` localement ;
2. authentifier le compte via Windows Credential Manager ou flux navigateur ;
3. valider un smoke reel `agy -p ... --cwd <repo>` avec capture stdout/stderr/exit code ;
4. confirmer comment passer de longs prompts sans limite Windows 32767 caracteres, car la doc trouvee documente `-p "<prompt>"`, pas stdin ;
5. confirmer si une sortie JSON/JSONL ou streaming existe. Je n'en ai pas trouve dans la doc officielle consultee.

## 1. Presence locale d'AG

### Extensions VS Code

Dossier inspecte :

`C:\Users\najwa\.vscode\extensions`

Extensions presentes :

- `anthropic.claude-code-2.1.215-win32-x64`
- `ms-dotnettools.csdevkit-3.20.199-win32-x64`
- `ms-dotnettools.csharp-2.140.9-win32-x64`
- `ms-dotnettools.vscode-dotnet-runtime-3.1.0`
- `ms-vscode.powershell-2025.4.0`
- `openai.chatgpt-26.715.31925-win32-x64`

Resultat : **aucune extension Antigravity, Google Antigravity, Gemini ou AGY**.

### Emplacements d'installation utilisateur/systeme

Emplacements inspectes :

- `C:\Users\najwa\AppData\Local\Programs`
- `C:\Users\najwa\AppData\Local`
- `C:\Program Files`
- `C:\Program Files (x86)`
- `C:\Users\najwa\AppData\Local\Microsoft\WindowsApps`
- chemins specifiques attendus par la doc officielle :
  - `C:\Users\najwa\AppData\Local\agy`
  - `C:\Users\najwa\AppData\Local\agy\bin`
  - `C:\Users\najwa\AppData\Local\Programs\Antigravity`
  - `C:\Users\najwa\AppData\Local\Programs\Google\Antigravity`
  - `C:\Program Files\Google\Antigravity`
  - `C:\Program Files (x86)\Google\Antigravity`

Recherche de binaires :

- `agy.exe`
- `agy.cmd`
- `agy.ps1`
- `antigravity.exe`
- `antigravity.cmd`
- `google-antigravity.exe`
- `gemini.exe`
- `gemini.cmd`

Resultat : **aucun binaire trouve**.

### PATH

Commandes tentees :

- `Get-Command antigravity,ag,gemini,google-antigravity`
- `where.exe agy`
- `where.exe antigravity`
- `where.exe gemini`
- `where.exe google-antigravity`

Resultat : **aucune commande AG/AGY/Gemini disponible dans le PATH**.

Le PATH contient notamment `dotnet`, Git, Node.js, GitHub CLI, Python, WindowsApps et VS Code, mais aucun dossier `agy\bin`.

### Applications installees

Registre Windows inspecte :

- `HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*`
- `HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*`
- `HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*`

Resultats Google trouves :

- Android Studio `2025.3`
- Google Chrome `150.0.7871.125`

`winget list --disable-interactivity | Select-String 'Antigravity|Gemini|Google'` :

- Android Studio `Google.AndroidStudio`
- Google Chrome `Google.Chrome.EXE`

`Get-AppxPackage` avec filtre Antigravity/Gemini/Google : aucun resultat.

Resultat : **Antigravity n'est pas installe comme application Windows connue**.

### Dossiers de configuration

Emplacements inspectes :

- `C:\Users\najwa\.antigravity`
- `C:\Users\najwa\.agy`
- `C:\Users\najwa\.gemini`
- `C:\Users\najwa\AppData\Roaming\npm`
- `C:\Users\najwa\AppData\Roaming`
- `C:\Users\najwa\AppData\Local`

Resultat : **aucun dossier ou artefact de configuration Antigravity/AGY/Gemini trouve**.

## 2. Pilotage headless / programmatique

### Sources officielles consultees

La page HTML publique `https://antigravity.google/docs/cli/...` est une app web rendue cote client. J'ai donc extrait le bundle officiel `https://antigravity.google/main-THARYY64.js`, qui charge les markdowns depuis :

`https://antigravity.google/assets/docs/<path>/<filename>.md`

Markdowns officiels consultes :

- `https://antigravity.google/assets/docs/cli/cli-install.md`
- `https://antigravity.google/assets/docs/cli/cli-getting-started.md`
- `https://antigravity.google/assets/docs/cli/cli-best-practices.md`
- `https://antigravity.google/assets/docs/cli/cli-modes.md`
- `https://antigravity.google/assets/docs/cli/cli-sandbox.md`
- `https://antigravity.google/assets/docs/cli/cli-credits.md`
- `https://antigravity.google/assets/docs/cli/cli-reference.md`
- `https://antigravity.google/assets/docs/cli/cli-prompting.md`
- `https://antigravity.google/assets/docs/cli/cli-conversations.md`

### Ce qui est documente

Installation Windows :

- la doc officielle indique que le script d'installation enregistre `agy` dans `C:\Users\<Username>\AppData\Local\agy\bin`.
- Sur cette machine, `C:\Users\najwa\AppData\Local\agy\bin` n'existe pas.

Authentification :

- `agy` utilise le keyring natif de l'OS, notamment Windows Credential Manager.
- Si un profil valide existe, l'authentification est silencieuse.
- Sinon, le premier lancement declenche un flux interactif navigateur.
- La deconnexion se fait via `/logout` dans le prompt du CLI.

Mode non interactif :

La doc `cli-best-practices.md` documente explicitement :

```bash
agy -p "Review this git diff and draft a conventional commit message" --cwd $(pwd)
```

Interpretation : `agy -p` est le candidat equivalent a `claude -p` / `codex exec` pour une invocation one-shot. C'est le principal element qui rend l'integration faisable en principe.

Modes et permissions :

- `agy --mode=accept-edits` lance la CLI en mode acceptation automatique des edits standard.
- `--dangerously-skip-permissions` est mentionne comme mecanisme qui continue de gouverner les commandes shell (`run_command`) a travers les modes.
- La cle `toolPermission` accepte `request-review`, `proceed-in-sandbox`, `always-proceed`, `strict`.
- La cle `enableTerminalSandbox` vaut `false` par defaut et active une sandbox terminal.
- Sur Windows, la sandbox officielle est basee sur `AppContainer`.

Quotas / abonnement :

- la doc `cli-credits.md` indique que la CLI s'integre a l'abonnement Antigravity et suit les credits/quotas AI Premium ;
- `/usage` ou `/quota` affiche les quotas modele ;
- `/credits` affiche les credits et liens d'achat ;
- `useG1Credits` permet, pour certains builds, d'utiliser des credits personnels quand les quotas de plan sont epuises.

Concurrence :

- la page produit et la doc `cli-best-practices.md` indiquent la possibilite de sous-agents / taches paralleles et de sessions paralleles via `/fork`.
- Je n'ai pas trouve de garantie officielle que plusieurs processus `agy -p` independants peuvent tourner simultanement avec le meme profil authentifie, ni de limite documentee par compte/profil.

### Ce qui n'est pas trouve / non valide

Je n'ai trouve aucune preuve officielle, dans les markdowns consultes, de :

- lecture du prompt sur stdin ;
- flag de sortie JSON/JSONL comparable a `codex exec --json` ;
- flag `--output-last-message` ou equivalent ;
- API locale / JSON-RPC locale comparable a `codex app-server` ;
- mode streaming exploitable en machine par le SL ;
- contrat d'exit codes pour quota/auth/permission ;
- capacite documentee a lancer plusieurs `agy -p` en parallele sous le meme compte.

Comme le binaire `agy` est absent localement, je n'ai pas pu tester :

- `agy --version`
- `agy --help`
- `agy -p "..." --cwd ...`
- un passage de prompt via stdin ;
- le comportement stdout/stderr ;
- les erreurs de quota/auth ;
- les effets de `--mode=accept-edits` et `--dangerously-skip-permissions`.

## 3. Contraintes et risques

### Contraintes certaines

- Installation requise : `agy` n'est pas installe ici.
- Authentification utilisateur requise : la CLI depend d'un profil dans Windows Credential Manager ou d'un flux interactif initial.
- Abonnement/quotas : la CLI consomme des credits/quotas Antigravity AI Premium.
- Permissions : le mode par defaut demande review pour ecritures/commandes reseau/bash. Pour un acteur SL autonome, il faudra parametrer explicitement `accept-edits`, sandbox et/ou permissions.
- Sandbox Windows : AppContainer, avec impacts possibles sur acces fichiers, registre, reseau et execution de tests.

### Risques bloquants pour SL

- **Prompt long** : le SL passe les prompts Claude/Codex par stdin pour eviter la limite Windows de ligne de commande. `agy -p "<prompt>"` documente un prompt en argument, pas stdin. Si `agy` ne lit pas stdin, il faudra une strategie alternative validee (`-p @file`, fichier contexte, prompt court + fichier, ou API/SDK).
- **Sortie non structuree** : sans JSON/JSONL ni fichier "last message", l'UI live et l'extraction du livrable seraient moins fiables que Codex.
- **Streaming incertain** : le SL affiche aujourd'hui les evenements Codex/Claude en live. Avec `agy -p`, on ne sait pas si stdout streame du texte exploitable ou rend seulement une sortie finale.
- **Permissions non equivalentes** : `--mode=accept-edits` automatise les edits, mais les commandes shell restent gouvernees par les permissions. Le mode implementation/QA SL a besoin de build/test/app sans blocage interactif.
- **Concurrence non prouvee** : le SL sait lancer deux moteurs en parallele. AG ajoute un troisieme moteur potentiel, mais il faut valider collisions de keyring/session, quotas et lock de workspace.

## 4. Patron SL existant observe

Fichiers lus :

- `src/SprintLauncher/Runners/BinaryLocator.cs`
- `src/SprintLauncher/Runners/ActorRunner.cs`
- `src/SprintLauncher/Runners/CodexJsonInterpreter.cs`
- `src/SprintLauncher/Prompts/ActorRole.cs`
- `src/SprintLauncher/Config/SprintLauncherConfig.cs`
- `src/SprintLauncher/Runners/ActorTurnCoordinator.cs`
- `src/SprintLauncher/Runners/QuotaDetector.cs`
- `src/SprintLauncher/Prompts/PromptBuilder.cs`
- `src/SprintLauncher.UI/MainWindow.xaml.cs`
- `src/SprintLauncher.Tests/ImplementationSchedulerTests.cs`

Synthese :

- `BinaryLocator` localise `claude.exe` via Claude Desktop App puis PATH, et `codex.exe` via extension VS Code puis PATH.
- `ActorRunner` choisit Claude si `role.IsClaudeFamily()`, sinon Codex. Il retire `OPENAI_API_KEY` et `ANTHROPIC_API_KEY`, passe les prompts par stdin, applique sandbox/read-only/full-auto selon le role, et interprete les flux live.
- Codex utilise `codex exec --json --output-last-message <file>`, stdin, `--skip-git-repo-check`, `--sandbox read-only` ou `--dangerously-bypass-approvals-and-sandbox`.
- `CodexJsonInterpreter` transforme le JSONL en lignes lisibles et extrait un dernier message en secours.
- `ActorRole` et `ActorGroup` sont actuellement modeles autour de deux familles : Claude et GPT/Codex.
- `ImplementationRotation` est code pour exactement deux moteurs : `ClaudeImplementation` et `GptImplementation`; `PickRelief` renvoie "l'autre".
- `ActorTurnCoordinator` a deux locks d'engine : `claude` et `codex`.
- `ModelEngine` ne connait que `Claude` et `Codex`.
- L'UI connait explicitement les roles et alias `@ccode`, `@codex`, `@gpt`.

## 5. Esquisse d'integration si les conditions sont remplies

### Localisation

Ajouter dans `BinaryLocator` :

- `AG_BIN` en override ;
- chemin officiel Windows : `%LOCALAPPDATA%\agy\bin\agy.exe` ;
- eventuels chemins `AppData\Local\Programs\Antigravity` si l'IDE installe aussi la CLI ;
- fallback PATH : `agy`.

### Runner

Ajouter un chemin AG dans `ActorRunner` :

- champ `_agBin`, config `_agModel`;
- dispatch explicite par moteur/famille au lieu de `IsClaudeFamily() ? Claude : Codex`;
- methode `RunAgAsync`.

Pseudo-commande cible, a valider :

```powershell
agy -p "<prompt>" --cwd "<repo>" --mode=accept-edits
```

Pour les roles read-only, tester soit `--mode=plan`, soit settings sandbox/permissions temporaires. Pour les roles execution, tester `--mode=accept-edits` + politique permissions non bloquante. Si `--dangerously-skip-permissions` est necessaire, il devra etre limite aux roles deja `IsExecutionRole()`.

Point critique : si `agy` ne supporte pas stdin, ne pas injecter le prompt complet en argument. Il faudra une solution robuste avant integration :

- support officiel stdin ;
- support fichier (`@prompt.txt` ou flag equivalent) ;
- SDK/API locale ;
- ou prompt court demandant de lire un fichier prompt genere par SL dans le repo/artifacts.

### Interpretation de sortie

Deux options selon smoke :

- si `agy` sort du texte simple : interpreter pass-through equivalent a `ILiveInterpreter` minimal, avec stdout comme livrable ;
- si `agy` expose JSON/JSONL : creer `AgJsonInterpreter` equivalent a `CodexJsonInterpreter`, mapper les evenements agent/tool/edit/error, et chercher un "final message" fiable.

Sans sortie structuree, garder `output-<role>.txt` mais signaler une degradation par rapport a Codex : pas de progression fine, detection quota plus fragile, dernier message moins fiable.

### Roles / orchestration

Approche minimale :

- ajouter `AgImplementation` comme troisieme moteur d'implementation ;
- eventuellement `AnalysisAg`, `CommitteeAg`, `AgQaVerdict`, `RetrospectiveAg` si AG doit etre "au meme titre" dans toutes les phases, pas seulement implementation ;
- ajouter un `ActorGroup.FamilyAg` ou remplacer la notion de famille par `ActorEngine`;
- remplacer les tests "Claude vs non-Claude => Codex" par un mapping explicite role -> engine.

Fichiers a toucher :

- `src/SprintLauncher/Runners/BinaryLocator.cs`
- `src/SprintLauncher/Runners/ActorRunner.cs`
- `src/SprintLauncher/Runners/AgJsonInterpreter.cs` ou interpreter texte simple
- `src/SprintLauncher/Prompts/ActorRole.cs`
- `src/SprintLauncher/Prompts/PromptBuilder.cs`
- `src/SprintLauncher/Config/SprintLauncherConfig.cs`
- `src/SprintLauncher/Runners/QuotaDetector.cs` / `ImplementationRotation`
- `src/SprintLauncher/Runners/ActorTurnCoordinator.cs`
- `src/SprintLauncher/Runners/ModelRecommendationParser.cs`
- `src/SprintLauncher/Dialogue/DirectiveAddressing.cs`
- `src/SprintLauncher/Program.cs`
- `src/SprintLauncher.UI/MainWindow.xaml`
- `src/SprintLauncher.UI/MainWindow.xaml.cs`
- tests existants et nouveaux tests runner/locator/rotation/directives

### Estimation

Si `agy` valide stdin ou fichier prompt + stdout final fiable : **2 a 4 jours** pour une integration minimale implementation-only, tests inclus.

Si AG doit etre present dans toutes les phases comme Claude/Codex, avec UI, model switching, quotas, rotation a 3 moteurs et live output : **5 a 8 jours**.

Si aucun stdin/fichier prompt/sortie structuree n'existe : **NO-GO technique pour une integration headless robuste** dans le patron actuel du SL, sauf a accepter une integration degradee via prompt court + fichier contexte et sortie texte non structuree.

## 6. Tests a faire apres installation AG

Commandes de smoke recommandees :

```powershell
agy --version
agy --help
agy -p "Reponds uniquement OK-SL-AG-SMOKE" --cwd "C:\Users\najwa\OneDrive\Desktop\SL-wt-ag"
```

Tester le prompt long :

```powershell
$prompt = Get-Content .\artifacts\prompts\sample-long-prompt.txt -Raw
agy -p $prompt --cwd "C:\Users\najwa\OneDrive\Desktop\SL-wt-ag"
```

Tester stdin, meme si non documente :

```powershell
"Reponds uniquement OK-STDIN" | agy -p --cwd "C:\Users\najwa\OneDrive\Desktop\SL-wt-ag"
```

Tester permissions execution :

```powershell
agy -p "Dans ce repo, lance dotnet test --nologo puis resume le resultat. Ne modifie aucun fichier." --cwd "C:\Users\najwa\OneDrive\Desktop\SL-wt-ag" --mode=plan
```

Tester implementation sandbox :

```powershell
agy -p "Cree un fichier temporaire artifacts/ag-smoke.txt contenant OK, puis affiche son chemin." --cwd "C:\Users\najwa\OneDrive\Desktop\SL-wt-ag" --mode=accept-edits
```

Capturer pour chaque test :

- commande exacte ;
- exit code ;
- stdout ;
- stderr ;
- fichiers modifies ;
- presence d'un blocage interactif ;
- messages auth/quota/permission.

