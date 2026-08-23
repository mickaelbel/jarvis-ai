# Auto-développement de Jarvis (`autodev`)

Jarvis peut travailler sur son propre code : construire, tester, committer,
publier, redémarrer et se réparer tout seul.

## Outil vocal / chat

| Commande | Effet |
|---|---|
| `autodev status` | État du dépôt git + réglages de la boucle |
| `autodev build` | Compile la solution Release |
| `autodev test [filtre]` | Lance les tests (filtre dotnet `--filter`) |
| `autodev fix` | **Auto-réparation par IA** : diagnostic → patch → build+tests → commit ou rollback |
| `autodev checkpoint <msg>` | Commit git de tous les changements |
| `autodev annuler` | Rollback complet au dernier checkpoint |
| `autodev publier` | `publish.ps1 -SkipDependencies` vers dist\JarvisAI |
| `autodev installateur` | Génère le setup via scripts/build-installer.ps1 |
| `autodev redemarrer` | Redémarre l'application proprement |
| `autodev logs web 80` | Dernières lignes du journal |
| `autodev boucle off` / `on` / `120` | Configure la vérification périodique |
| `autodev boucle config autofix on autopub off` | Réglages fins |

## Boucle de maintenance autonome

Un service d'arrière-plan vérifie build+tests toutes les N minutes
(défaut : 12 h, première passe 10 min après démarrage) :

- **Vert** → rien (publication auto si activée).
- **Rouge** → déclenche le cycle d'auto-réparation ci-dessous.

## Cycle d'auto-réparation (garde-fous)

1. Checkpoint git « avant auto-fix » (rollback garanti).
2. Le diagnostic (échecs de tests / erreurs CS) est envoyé à l'IA locale.
3. L'IA répond un JSON `{file, content}` — **5 fichiers max**, uniquement sous
   `src/`, `Tests/`, `Plugins/`, `scripts/`. Toute autre écriture est rejetée.
4. Rebuild + retests complets.
   - Verts → commit « auto-fix IA » + leçon mémorisée.
   - Rouges → rollback intégral (`git checkout -- . && git clean`) + leçon.
5. Plafond : 3 tentatives/jour (réglable).

## Fichiers

- `src/Infrastructure/Dev/DotnetOutputParser.cs` — parsing FR/EN des sorties dotnet
- `src/Infrastructure/Dev/SelfDevService.cs` — moteur, réglages (`%LOCALAPPDATA%\JarvisAI\selfdev.json`), boucle
- `src/Infrastructure/Tools/SelfDevTool.cs` — outil `autodev`
- `Tests/DotnetOutputParserTests.cs`

## Dépendances hôte

Le redémarrage utilise l'interface `ISelfDevLifecycle`, implémentée par
l'hôte Desktop (`DesktopAppLifecycle`). Sans hôte, l'auto-fix fonctionne quand même.

## Retour arrière conversationnel (etour)

Chaque tour de conversation (chat **et** voix) est committé dans git avec la
liste des fichiers modifiés (%LOCALAPPDATA%\JarvisAI\turn-history.json,
plafond 200 tours). Jarvis peut ainsi revenir en arrière :

- **Chat** : boutons « Revenir » / « Modifier » au survol de chaque message
  utilisateur → annule ce tour et tout ce qui a suivi (fichiers inclus),
  tronque la conversation et préremplit la saisie pour modifier la demande.
- **Voix / chat** : outil etour — actions nnule (nombre ou id de tour),
  historique, estaure (avec sauf=chemin1,chemin2 pour conserver des
  fichiers précis : *« remets comme avant mais garde le truc sur les lumières »*).
- **Sécurité** : restauration depuis l'état git d'avant le tour ; les fichiers
  créés pendant la période annulée sont supprimés ; l'auto-fix IA garde ses
  propres garde-fous inchangés.

Réglages additionnels dans selfdev.json : CheckIntegrations (défaut true) —
après chaque vérification verte, la boucle sonde Home Assistant, Hue, Twilio
et Alexa et consigne une leçon si une intégration est injoignable.
