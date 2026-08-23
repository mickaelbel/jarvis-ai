# Jarvis AI — Rapport final Phase 4 « Agent autonome »

## Validation

| Étape | Résultat |
|---|---|
| `dotnet clean` | OK |
| `dotnet build -c Release` (solution complète) | **0 erreur / 0 avertissement** |
| `dotnet test -c Release` | **684 / 684 verts** (0 échec, 0 ignoré, ~4 s) |

- Tests avant Phase 4 : 522 → **+162 nouveaux tests** → 684.
- Builds intermédiaires validés en continu : Application + Web 0/0 après P4.1–P4.11, puis après l'ajout des tests P4.12.

## Fichiers créés

### Application (`src/Application`)
- `Agents/AgentRequest.cs` — requête d'exécution (Goal, CommandText, Source, CorrelationId, Mode, Metadata, MaxIterations, Timeout, AllowParallelTools).
- `Agents/OrchestrationResult.cs` — résultat d'un run (Status, Success, FinalResponse, Reason, RunId, Iterations…).
- `Agents/IAgentOrchestrator.cs` / `AgentOrchestrator.cs` — **point d'entrée unique** de l'agent : BuildContext → routage modèle → sélection de stratégie → planification (avec retry + plan de repli) → boucle ReAct → mémoire automatique → historique. Gère timeout (CTS lié + `CancelAfter`, 5 min par défaut), annulation utilisateur, événement `RunUpdated`.
- `Agents/ReasoningLoop.cs` (+ `IReasoningLoop`, `ReasoningLoopOptions`, `LoopTurn`, `ReasoningLoopResult`) — boucle **ReAct** Thought→Action→Observation→Final ; température 0.2 ; détection de boucle par signature `name|key=value&…` (`LoopDetectionMaxRepeats=3`) ; `MaxIterations`/`MaxConsecutiveErrors`/`MaxToolCallsPerTurn` ; retries LLM via `IRetryPolicy` ; résultats d'outils → mémoire auto (`MemoryType.ToolResult`) ; `onStep` pour le journal.
- `Agents/RetryPolicy.cs` (+ `RetryPolicyOptions`, `RetryAttempt`, `IRetryPolicy`) — backoff exponentiel plafonné, mots-clés retryables (ollama/connection/timeout/503/…), options RetryOnTimeout/HttpErrors/Crash, propagation des annulations.
- `Agents/AutomaticMemoryService.cs` (+ `AutomaticMemoryOptions`, `IAutomaticMemoryService`) — importance (préférences +4, important +3, longueur +1, ':' +1, cap 10), catégorisation (UserPreference/ToolResult/Fact), tiers (>=7 LongTerm), TTL, dédoublonnage par clé SHA-256 (`agent.…`), troncature.
- `Agents/RunHistory.cs` (+ `RunRecord`, `RunStep`, `RunStepKind`, `RunStatus`, `IRunHistory`) — thread-safe, `RunRecord` avec verrou et listes copiées, métriques (itérations, retries, outils, tokens, durée), étapes de plan.
- `Context/ContextBuilder.cs` (+ `ContextBundle`, `ContextSection`, `IContextBuilder`) — sections SYSTEM / OLLAMA / ACTIVE PLUGINS / AVAILABLE TOOLS / MEMORY / CONVERSATION, tolérance aux erreurs plugins/mémoire, rendu plafonné (12000).
- `Planning/Strategies/` — `IPlanningStrategy`, `PlanningStrategyKind`, `PlanningStrategyBase`, `PlanningStrategySelector`, `SimplePlanningStrategy`, `ComplexPlanningStrategy`, `ResearchPlanningStrategy`, `ComputerUsePlanningStrategy`, `AutomationPlanningStrategy`. Sélection : mots-clés (ComputerUse > Automation > Research), longueur > 60 → Complex, repli Simple.
- `Planning/IPlanner.cs` — étendu : `CreatePlanWithInstructionsAsync(goal, instructions, context, ct)` ; `CreatePlanWithContextAsync` délègue.
- `Tools/ToolSelectionService.cs` (+ `ToolSelectionResult`, `IToolSelectionService`) — scoring nom/description/catégorie, AlwaysInclude (system_info, date_time, memory), filtrage high-risk sans intention d'exécution, limite 12, aucun outil si le goal n'a pas de token significatif (hors AlwaysInclude).
- `Tools/ParallelToolExecutor.cs` (+ `PendingToolCall`, `ToolExecutionOutcome`, `IParallelToolExecutor`) — exécution parallèle des outils sûrs (liste intégrée + risque Low enregistré), séquentiel sinon, capture des échecs/exceptions/annulations, retry par outil.
- `AI/ToolDefinitionBuilder.cs` — nouveau overload `Build(IEnumerable<ITool>)`.
- `DependencyInjection.cs` — enregistrements DI P4.

### Infrastructure (`src/Infrastructure`)
- `Planning/Planner.cs` — prompt de planification avec liste d'outils, bloc `Strategy instructions:`, parse JSON, plan de repli (1 étape) sur erreur/JSON invalide, contexte mémoire.

### Web (`src/Web/JarvisAI.Web`)
- `Components/Pages/Agent.razor` — dashboard temps réel : état SignalR, objectif + Lancer, carte du run actif (statut, modèle, itérations, retries, outils, durée, plugins, plan, journal d'étapes, réponse finale/erreur), historique cliquable, timer 1 s, modèle forcé `Powerful`.
- `Hubs/AgentHub.cs` — hub SignalR `/hubs/agent` (groupes `run:{runId}`, `GetRuns`).
- `Services/AgentRunBroadcaster.cs` — abonné à `IAgentOrchestrator.RunUpdated` → `RunUpdated` (groupe run) + `RunListChanged` (tous).
- `Services/AgentRunDto.cs` (+ `AgentRunStepDto`, `AgentPlanStepDto`) — DTO temps réel incluant PlanSteps/`PlanStepStatus`.
- `wwwroot/js/agent.js` — IIFE `jarvisAgent` (connexion avec `withAutomaticReconnect`, DotNetObjectReference, handlers Connected/RunUpdated/RunListChanged, `loadRuns()`).
- `Components/Pages/Chat.razor` — P4.11 : l'exécution autonome passe par `IAgentOrchestrator.ExecuteAsync(new AgentRequest { … AllowParallelTools = true })`.
- `Components/App.razor` (script agent.js), `Components/_Imports.razor` (usings), `Components/Layout/NavMenu.razor` (lien Agent), `WebAppFactory.cs` (hub mappé, broadcaster démarré).

### Tests (`Tests/`) — 11 fichiers, 162 tests
| Fichier | Tests |
|---|---|
| `RunHistoryTests.cs` | 16 |
| `RetryPolicyTests.cs` | 15 |
| `AutomaticMemoryTests.cs` | 21 |
| `ToolSelectionTests.cs` | 16 |
| `PlanningStrategyTests.cs` | 19 |
| `ParallelToolExecutorTests.cs` | 16 |
| `ContextBuilderTests.cs` | 16 |
| `ReasoningLoopTests.cs` | 15 |
| `AgentOrchestratorTests.cs` | 16 |
| `AgentDashboardTests.cs` | 12 |
| `Phase4TestDoubles.cs` | — (doubles : TestTool, FakeToolExecutor, FakePluginManager, FakeMemoryService, HangingProvider) |

Tests notables : orchestrateur happy path / modèles / repli planification / timeout (provider bloquant) / annulation / observation mémoire / ordre de l'historique ; boucle ReAct détection de boucle, erreurs consécutives, troncature multi-outils, continuation sur réponse vide ; retry exhaustion et respect des options ; sélection d'outils (high-risk, AlwaysInclude, limite, casse, ponctuation) ; exécution parallèle plus rapide que séquentielle.

## Architecture

```
Chat.razor / Agent.razor (Web + SignalR)
   │
   ▼
IAgentOrchestrator (point d'entrée unique)
   ├─ IContextBuilder ──────────────► contexte (sections, plugins, outils, mémoire)
   ├─ IModelRouter ─────────────────► modèle fast/powerful
   ├─ IPlanningStrategySelector ────► Simple/Complex/Research/ComputerUse/Automation
   ├─ IPlanner (+ retry, repli)
   ├─ IReasoningLoop ── ReAct ──► IToolSelectionService ─► IParallelToolExecutor ─► IToolExecutor
   │      └─ IAutomaticMemoryService (résultats d'outils)
   ├─ IRetryPolicy (planning + LLM + outils)
   ├─ IRunHistory / RunRecord (thread-safe)
   └─ event RunUpdated ──► AgentRunBroadcaster ──► AgentHub (groupes run) ──► Agent.razor
```

## Dette / limites connues

- Les étapes/plan du run sont des snapshots ; pas de reprise d'un run interrompu.
- Le plan n'est pas exécuté strictement étape par étape : la boucle ReAct l'utilise comme directive (`FOLLOW THIS PLAN`), pas comme une machine à états.
- Timeout : l'annulation peut arriver pendant la planification ; la distinction timeout/annulation repose sur le token d'origine.
- `ParallelToolExecutor` : la liste des outils « sûrs » est codée en dur (extensible par enregistrement à risque Low).
- La sélection d'outils ne gère pas les pluriels/racines lexicales (comparaison par token exact/substring).
- Le tableau de bord reflète l'état du processus ; l'historique des runs est en mémoire (perdu au redémarrage).
- Pas de persistance SQLite des runs ni de quotas de ressources (tokens, débit).

## Propositions Phase 5

1. **Persistance** : stockage des runs (SQLite/EF) + gestionnaire d'historique avec filtres.
2. **Exécution du plan strict** : moteur d'étapes (InProgress→Completed/Failed, reprise, rollback) branché sur la boucle ReAct.
3. **Files d'attente & concurrence** : exécution de plusieurs agents en parallèle avec quotas (tokens/minute, worker limité).
4. **Sécurité** : intégration d'`ISecurityManager` aux décisions d'exécution d'outils high-risk (approbation interactive via hub SignalR).
5. **Profilage LLM** : suivi des tokens coût/réponse par run, bascule auto vers le modèle rapide en cas de dérive.
6. **Amélioration mémoire** : recherche sémantique (embeddings), consolidation/oublie, export/import.
7. **Tableau de bord enrichi** : visualisation du graphe d'étapes, streaming des pensées (Thought/Final), arrêt manuel d'un run (annulation).
