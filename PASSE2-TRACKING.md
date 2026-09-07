# PASSE 2 — Suivi complet

Chaque item est retiré du fichier une fois implémenté, buildé et commité.
Fin de passe = fichier vide puis supprimé.

## A. REASONING / AGENT
- [x] A1. AgentOrchestrator.CancelAll : race condition `_globalCancel` → Interlocked.Exchange
- [x] A2. AIServiceAdapter : supprimer `"et"` de PlanTriggerKeywords (déclenche planning à tort)
- [x] A3. AIServiceAdapter : timeout mur (wall-clock) sur la boucle agent (15 rounds sans limite)
- [x] A4. AIServiceAdapter : extraire la logique d'exécution d'outils dupliquée (native + texte) → 1 méthode
- [x] A5. MultiAgentOrchestrator : isolation d'erreur des sous-agents (Task.WhenAll remplacé par gestion individuelle)
- [x] A6. AgentOrchestrator : Sémaphore conçu pour ne pas deadlocker avec MultiAgentOrchestrator (4 sous-agents > 3 slots)

## B. COMPUTER USE
- [x] B1. ComputerUseService : utiliser `_observeLock` déclaré mais jamais utilisé (protège _cache)
- [x] B2. ComputerUseService : écriture capture atomique (fichier partiel si crash)
- [x] B3. WindowsComputerController : `new Random()` → `Random.Shared`
- [x] B4. WindowsComputerController : retry clipboard sur contention (OpenClipboard qui échoue)
- [x] B5. ComputerUseService : attente intelligente optionnelle après action (vitesse adaptative)

## C. LATENCE
- [x] C1. ModelRouter : regex en cache pour mots courts (30+ regex compilées par requête → une seule)
- [x] C2. OllamaProvider : IsAvailable retourne valeur périmée pendant refresh → forcer wait sur 1er check
- [x] C3. ToolBase : timeout par outil (override Timeout) au lieu de 60s fixe
- [x] C4. ToolExecutor : double timeout (executor + tool) et code mort ligne 128-129
- [ ] C5. AgentMetrics : câbler LLM/computer-use/tool latencies + compteurs retries/erreurs

## D. CONTEXT / SESSION / MEMORY
- [x] D1. AgentSession : List<> non thread-safe (Steps/Decisions/Errors) → thread-safe
- [x] D2. AgentSession.BuildContextSummary : null-safe sur step.Result (IndexOutOfRange potentiel)
- [x] D3. SessionManager._activeSessionId : pas atomique → Interlocked
- [x] D4. MemoryService.GetAsync : LastAccessedAt/AccessCount modifiés mais JAMAIS persistés (champs morts)
- [x] D5. MemorySemanticScorer : 200 embeddings en parallèle sans throttling
- [x] D6. MemorySemanticScorer : _embeddingCache sans éviction (croissance illimitée)
- [x] D7. ContextBuilder : ActivePlugins toujours vide (dead code)
- [x] D8. ContextBuilder : Render tronque à 12000 chars → adapter au contexte modèle

## E. ROUTING / OUTILS / OBSERVABILITÉ
- [x] E1. ModelRouter : LastRoute écrit sans sync → volatile/Interlocked
- [x] E2. ModelRouter : fallback chain (modèle raisonnement ko → fast) — existant au niveau provider (OllamaProvider fallback loop)
- [x] E3. ToolResult : métadonnées (temps d'exécution, tool, durée)
- [x] E4. ModelCapabilities : _noToolSupportMarkers volatile mais contenu muté → lock

## F. QUALITÉ / AUTRES
- [ ] F1. VoiceConversationOrchestrator : `_history` List non thread-safe + SaveHistory sync dans async
- [ ] F2. VoiceEffectsService : sample rate incohérents (44100 vs 16000 hardcodés)
- [ ] F3. StressTestTool : `_allocatedMemory` non thread-safe + hériter de ToolBase
- [ ] F4. PlaywrightWebBrowser : `_closeLock`/dispose Playwright global (CloseAsync tue tous les Playwright)
- [ ] F5. AIServiceAdapter : decomposition du God Class (au minimum extract des handlers)