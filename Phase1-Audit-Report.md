# JARVIS AI — PHASE 1 AUDIT REPORT
# Date: 2026-09-19

## ARCHITECTURE OVERVIEW

The project is a .NET 8 Blazor WebView2 desktop app with Clean Architecture:
- Domain → Application → Infrastructure → Core/Web/Desktop
- 9 projects, 2295 passing tests, 128 test files
- 80+ tool implementations
- Multi-provider LLM (Ollama local + cloud OpenAI-compatible)

## MESSAGE FLOW (verified)

```
User Input → Chat.razor → IAIService (AIServiceAdapter)
  → Trivial fast-path? → hardcoded response
  → Pre-processing heuristics (Chrome, web nav, site search)
  → Model routing (keyword classifier)
  → Tool definitions (pruned to ~12)
  → Optional plan generation
  → AGENT LOOP (max 15 rounds, 300s timeout):
    → OllamaProvider.StreamChatAsync() → HTTP POST localhost:11434
    → Buffer tokens (NOT streamed during tool rounds)
    → Tool calls detected? → ExecuteToolCallCoreAsync → ToolExecutor
    → Tool result → conversation.AddToolResult → next round
    → No tool calls? → yield final response
  → Anti-loop / refusal / dedup / self-verification checks
```

## CRITICAL BUGS FOUND

### BUG-001: Memory Consolidation Duplicate Key Error
- **File:** MemoryMaintenanceService.cs:141
- **Root cause:** ConsolidateLongTermAsync calls SaveMemoryAsync with the SAME key as the existing entry. LiteDB Upsert should handle this, but MemoryService.SaveMemoryAsync generates a NEW key (timestamped) instead of upserting the existing one.
- **Impact:** Spammed warnings in logs, memory not properly consolidated
- **Fix:** Use the original entry key directly, or use _store.UpsertAsync instead of _memory.SaveMemoryAsync

### BUG-002: Self-Verification Disabled by Default
- **File:** AIOptions.cs:18
- **Root cause:** SelfVerificationEnabled = false
- **Impact:** After tool execution, the agent never verifies if its final answer actually satisfies the user's request
- **Fix:** Enable by default, or make it automatic for multi-tool tasks

### BUG-003: Tool Results Not Streamed to User During Agent Loop
- **File:** AIServiceAdapter.cs:650-658
- **Root cause:** During tool rounds, ALL prose tokens are buffered and only yielded on the FINAL round (no tool calls). User sees nothing until the very end.
- **Impact:** Long tasks appear frozen. User doesn't know what's happening.
- **Fix:** Yield tool execution status updates as they happen

### BUG-004: Vision Not Integrated in Agent Loop
- **File:** AIServiceAdapter.cs (entire StreamChatAsync)
- **Root cause:** The agent loop sends text + OCR to the LLM but NEVER sends screenshots/images. The model cannot see the screen visually.
- **Impact:** Computer Use relies entirely on OCR text, missing visual elements (icons, colors, layout)
- **Fix:** After computer_use observe, attach the screenshot as a multimodal message

### BUG-005: System Prompt Too Minimal for Complex Tasks
- **File:** AgentSystemPrompt.cs
- **Root cause:** Only 47 lines, generic instructions, no structured output format for tool calls
- **Impact:** Model often refuses to use tools, hallucinates tool names, or gives prose instead of actions
- **Fix:** Enhance with structured instructions per tool category

### BUG-006: Model Routing Always Uses Powerful Mode
- **File:** Chat.razor (StreamResponseAsync)
- **Root cause:** Always passes ModelSelectionMode.Powerful
- **Impact:** Simple questions (greetings, time, weather) use the heavy reasoning model, increasing latency
- **Fix:** Use Auto mode, let the classifier decide

### BUG-007: ComputerActionTool Action Parsing is Fragile
- **File:** ComputerActionTool.cs:344-455
- **Root cause:** Simple string.Contains() matching, no NLP. "supprime le cube dans blender" might not parse correctly.
- **Impact:** Complex instructions fail silently
- **Fix:** Improve parsing with regex patterns and context awareness

### BUG-008: Anti-Loop Recovery Message Too Aggressive
- **File:** AIServiceAdapter.cs:595-604
- **Root cause:** Tells model to use browser when it might need computer_use
- **Impact:** Model switches to wrong tool after loop detection
- **Fix:** Make recovery message context-aware

### BUG-009: No Observation-After-Action Verification
- **File:** ComputerActionTool.cs ( ExecuteActionAsync)
- **Root cause:** After executing an action (click, type), no screenshot is taken to verify the action succeeded
- **Impact:** Agent assumes actions succeeded without verifying
- **Fix:** Add ObserveAsync call after each significant action

### BUG-010: Conversation Condensation Can Lose Tool Context
- **File:** ConversationCondenser.cs
- **Root cause:** Summarization may omit tool call details, breaking multi-step tasks
- **Impact:** After condensation, model forgets what tools it already called
- **Fix:** Preserve tool call history in summaries

## EXISTING TEST HARNESS

The LiveValidation project already has a comprehensive test harness (765 lines) with 23 checks:
- Controller, OCR, screen capture, mouse, windows, focus, tools, click/type, clipboard, double-click, right-click, alt-tab, OCR read, UI elements, search-then-click, type-into-element, scroll, window management, file system, multi-step notepad roundtrip, vision

This is a SOLID foundation. It needs:
1. Integration with the full agent loop (not just tools)
2. Prompt → response → verification pipeline tests
3. Automated pass/fail reporting
4. CI integration

## PRIORITY FIX ORDER

1. BUG-003 (streaming during tool rounds) - immediate UX improvement
2. BUG-009 (observation after action) - critical for reliability
3. BUG-004 (vision in agent loop) - critical for computer use
4. BUG-001 (memory consolidation) - stops log spam
5. BUG-006 (model routing) - performance improvement
6. BUG-005 (system prompt) - improves tool usage
7. BUG-002 (self-verification) - improves answer quality
8. BUG-007 (action parsing) - improves computer action
9. BUG-008 (anti-loop recovery) - improves recovery
10. BUG-010 (condensation) - long conversation reliability
