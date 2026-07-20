# SERZENIA-141 — Smoke reel Antigravity (agy)

Date : 2026-07-19. Machine : poste de Hajar (Windows 11).
Ce document remplace les hypotheses du spike (`docs/141-ag-spike.md`) par des constats
directs. Le spike avait ete fait sans pouvoir installer AG (winget etait en `deny`), il
contient deux affirmations fausses, corrigees ici.

## Installation

Le paquet existe bien, contrairement a ce qu'annoncait le spike :

```powershell
winget install --id Google.AntigravityCLI
```

Version installee : **1.1.4**. Le binaire est depose ici, et un alias `agy` est ajoute au
PATH (necessite un redemarrage du shell) :

```
C:\Users\najwa\AppData\Local\Microsoft\WinGet\Links\agy.exe
```

## Authentification

OAuth Google, obligatoire au premier appel. Sans elle, `agy` sort en **code 1** avec
`Authentication required` et une URL `accounts.google.com`. Le flux expire au bout de
**60 secondes** ; il accepte aussi un code d'autorisation colle sur stdin.

C'est une etape **interactive** : elle ne peut pas etre automatisee ni deleguee a un
acteur. Faite par Hajar le 2026-07-19. Une fois faite, elle est persistee : les appels
suivants sortent en code 0 sans redemander.

## Options reelles (`agy --help`)

```
-p / --print                     prompt unique non interactif, imprime la reponse
--add-dir <dir>                  ajoute un repertoire au workspace (repetable)
--model <m>                      modele de la session
--mode <accept-edits|plan>       mode d'execution de l'agent
--dangerously-skip-permissions   auto-approbation des outils
--print-timeout <duree>          timeout du mode print (defaut 5m)
--continue / --conversation      reprise de conversation
--project / --new-project        projet de la session
```

Sous-commandes : `agent(s)`, `models`, `plugin(s)`, `install`, `update`, `changelog`, `help`.

**`--cwd` n'existe pas** — le spike l'annoncait. C'est `--add-dir`.

## Constats de smoke

### 1. `--add-dir` fonctionne

```powershell
& $agy -p "Lis le fichier sprints.json a la racine du workspace et donne son contenu exact." --add-dir $repo
```

Exit 0, contenu exact du fichier restitue. AG lit bien le repo pointe.

> Piege de mesure : un premier test semblait montrer qu'AG ignorait `--add-dir` et
> partait dans son repertoire scratch. C'etait un bug de la commande de test, pas d'AG —
> `Start-Process -ArgumentList` en PS 5.1 concatene les arguments **sans les requoter**,
> donc le prompt etait decoupe et `agy` ne recevait que son premier mot. Utiliser
> l'operateur d'appel `&`, qui quote correctement.

### 2. Pas de stdin — c'est LA contrainte structurante

```powershell
"prompt" | & $agy -p     # -> exit 2 : "flag needs an argument: -p"
```

`-p` exige son argument sur la ligne de commande. Or les prompts du SL depassent
largement la limite Windows de 32767 caracteres pour une ligne de commande : le
`prompt-ClaudePilotage.txt` du run sprint 6 fait **534 Ko**. Passer le prompt en argument
est donc impossible en pratique.

### 3. Contournement valide : le prompt par fichier

Ecrire le prompt dans un fichier du workspace, et n'envoyer en argument qu'une consigne
courte qui pointe dessus :

```powershell
Set-Content "$dir\sl-prompt.txt" -Value $prompt -Encoding UTF8
& $agy -p "Lis le fichier sl-prompt.txt a la racine du workspace et execute la consigne qu'il contient." --add-dir $dir
```

Teste avec un prompt de **75 Ko** se terminant par une consigne verifiable : exit 0,
sortie exacte attendue, aucune troncature. C'est le mecanisme a retenir pour
l'integration.

Restent a valider pour de vrais prompts d'acteurs : le comportement au-dela de la fenetre
de contexte du modele, et le `--print-timeout` (defaut 5m) qui sera probablement trop
court pour une tache d'implementation.

### 4. Sortie

Texte brut (markdown), pas de JSON ni de JSONL, pas de flux evenementiel. L'integration
doit traiter AG comme un moteur one-shot, sans live comme celui de codex.
