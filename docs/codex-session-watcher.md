# Watcher de sessions Codex

Le watcher lit les rollouts append-only de `%USERPROFILE%\.codex\sessions` et alerte `#codex`
à chaque `task_complete` d'une session interactive VS Code éligible. Il ne modifie jamais
`%USERPROFILE%\.codex\config.toml` : le hook global reste réservé à computer-use.

Lancement manuel depuis la racine de SprintLauncher :

```powershell
dotnet run --project tools/codex-watcher
```

Les offsets et identités de session sont persistés dans
`%USERPROFILE%\.codex\.watcher-offsets.json`. Ce fichier local ne doit pas être committé.
Pour vérifier seulement les fichiers du jour puis quitter :

```powershell
dotnet run --project tools/codex-watcher -- --once
```

## Tâche planifiée au logon

À exécuter par Hajar dans PowerShell (le lot ne crée pas la tâche) :

```powershell
schtasks /Create /TN "SprintLauncher Codex Session Watcher" /SC ONLOGON /RL LIMITED /TR 'powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command "Set-Location -LiteralPath ''C:\Users\najwa\OneDrive\Desktop\SL-155-watcher''; dotnet run --project tools\codex-watcher"' /F
```

Adapter le chemin du dépôt si celui-ci est déplacé. La tâche lance le processus au logon ;
`FileSystemWatcher` traite les ajouts, et un scan du dossier de rollouts du jour est fait au démarrage.
