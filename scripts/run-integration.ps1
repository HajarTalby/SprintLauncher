# Wrapper sans argument : lance le codex d'integration sur le depot principal.
# Permet une commande schtasks pure (pas de guillemets imbriques). ASCII strict.
& "C:\Users\najwa\OneDrive\Desktop\SprintLauncher\scripts\run-codex-lot.ps1" `
    -Worktree "C:\Users\najwa\OneDrive\Desktop\SprintLauncher" `
    -BriefFile "C:\Users\najwa\OneDrive\Desktop\SprintLauncher\scripts\briefs\brief-integration.txt"
