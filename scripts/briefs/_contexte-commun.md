# CONTEXTE COMMUN — à lire avant ta mission

Tu n'es pas seul sur ce projet. Trois agents travaillent sur SERZENIA : **Claude Code**
(orchestration, cadrage, revue), **codex** (dev et QA) et **toi, AGY** (lots délégués).
Vous devez partager le même état du monde. Un déphasage entre vous produit des rapports
faux et du travail refait pour rien.

Ce préambule est ajouté automatiquement à chaque brief par `run-agy-lot.ps1`.

## Ce que tu as sous la main dans ce workspace

| Fichier / dossier | Contenu |
|---|---|
| `CLAUDE.md` | Les règles du projet, communes aux trois agents. Elles s'appliquent à toi. |
| `ETAT-GIT.md` | L'état git réel du dépôt au moment où ton lot a démarré : HEAD, branches locales **et distantes**, commits récents. Généré automatiquement. |
| `memoire/` | La mémoire partagée : `MEMORY.md` est l'index, les autres fichiers sont les faits durables (décisions, pièges, retours de Hajar). |
| `memoire/HANDOFF-latest.md` | L'état de la dernière session Claude Code : tâche en cours, reste à faire, décisions en attente. |
| `agy-prompt.txt` | Ta mission, à la suite de ce préambule. |

## Règles de non-déphasage — non négociables

1. **Ton workspace est un `git worktree` détaché sur un commit donné.** Le répertoire de
   travail ne montre QUE ce commit. Le dépôt complet reste accessible : `git branch -a`,
   `git log <branche>`, `git show <branche>:<fichier>`, `git diff main...<branche>`.
2. **N'écris JAMAIS « ce travail n'existe pas » / « c'est à refaire » sur la seule base
   des fichiers présents.** Vérifie d'abord dans les branches (`ETAT-GIT.md` les liste).
   Erreur réelle du 2026-08-05 : un lot a conclu que tout le travail métier et UI de codex
   était perdu, alors qu'il vivait sur cinq branches `codex/*` poussées le jour même.
   Le verdict correct était « livré sur branche, non fusionné ».
3. **Distingue trois états, jamais deux** : *présent sur le HEAD courant* / *présent sur
   une branche non fusionnée* / *réellement absent partout*. Dis lequel, avec la preuve.
4. **Toute affirmation sur le code porte un `fichier:ligne`** — ou un `branche:fichier`
   si la preuve est sur une branche.
5. **Si une information te manque, cherche-la avant de conclure.** Un « à vérifier »
   assumé et expliqué vaut mieux qu'une conclusion fausse et affirmative.

## Interdits communs aux trois agents

- Ne lance pas l'application SERZENIA, ni les tests `tests/SERZENIA.Windows.UITests` :
  ils ouvrent des fenêtres sur la machine de Hajar.
- Ne transitionne aucun ticket Jira. N'écris rien sur Jira.
- Aucun secret, token ou mot de passe en clair dans un fichier, un log ou un rapport.
- Ne réécris pas l'historique git partagé et ne supprime aucun worktree.
- Respecte les interdits supplémentaires de ta mission ; en cas de conflit, le plus
  restrictif l'emporte.

---
# TA MISSION

