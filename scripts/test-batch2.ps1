# test-batch2.ps1 — Tests CU (Computer Use)
$ErrorActionPreference = "Stop"
$OllamaUrl = "http://localhost:11434"

$systemPrompt = @"
Tu es Jarvis, assistant PC Windows. Reponds en francais.
STYLE -- direct, bref, sans preambule. Fais avant de dire.
REGLES OUTILS :
- TOUTE demande d'ouvrir/lancer une application = OBLIGATOIREMENT computer_action.
- computer_action = OUTIL PRINCIPAL. UN SEUL APPEL suffit.
  Format: computer_action instruction="description de l'action"
- Decomposer les demandes complexes en actions simples.
  "Ouvre le bloc-notes en plein ecran" = 1) ouvre, 2) maximize.
- Toujours privilegier le controle clavier/souris en foreground.
- Apres chaque outil, attends le resultat AVANT de continuer.
- Si un outil reussit, TA TACHE EST TERMINEE : reponds.
"@

$tools = @(
    @{ type="function"; function=@{ name="computer_action"; description="Controle clavier/souris."; parameters=@{ type="object"; properties=@{ instruction=@{ type="string" } }; required=@("instruction") } } },
    @{ type="function"; function=@{ name="browser"; description="Navigation web."; parameters=@{ type="object"; properties=@{ action=@{ type="string" }; url=@{ type="string" } }; required=@("action") } } },
    @{ type="function"; function=@{ name="file_system"; description="Fichiers."; parameters=@{ type="object"; properties=@{ action=@{ type="string" }; path=@{ type="string" }; content=@{ type="string" } }; required=@("action","path") } } }
)

$prompts = @(
    @{ id="CU-001"; p="Ouvre le bloc-notes et tape 'Bonjour Jarvis'" },
    @{ id="CU-002"; p="Ouvre Paint et dessine un cercle rouge" },
    @{ id="CU-003"; p="Ouvre la calculatrice et calcule 123 * 456" },
    @{ id="CU-004"; p="Appuie sur Windows + D pour voir le bureau" },
    @{ id="CU-005"; p="Prends une capture d'ecran" },
    @{ id="CU-006"; p="Ouvre le bloc-notes, ecris 'test', puis sauvegarde" },
    @{ id="CU-007"; p="Clique sur le menu Demarrer" },
    @{ id="CU-008"; p="Appuie sur Ctrl+S" },
    @{ id="CU-009"; p="Deplace la fenetre du bloc-notes" },
    @{ id="CU-010"; p="Redimensionne la fenetre du bloc-notes" },
    @{ id="CU-011"; p="Ouvre le bloc-notes en plein ecran" },
    @{ id="CU-012"; p="Retablis le bloc-notes" },
    @{ id="CU-013"; p="Appuie sur Echap" },
    @{ id="CU-014"; p="Selectionne tout le texte avec Ctrl+A" },
    @{ id="CU-015"; p="Copie le texte avec Ctrl+C" }
)

$total = $prompts.Count; $pass = 0; $fail = 0
Write-Host "BATCH 2: COMPUTER USE ($total tests)"; Write-Host "=========================="

foreach ($item in $prompts) {
    $idx = [array]::IndexOf($prompts, $item) + 1
    Write-Host "[$idx/$total] $($item.id): $($item.p)" -NoNewline

    $body = @{ model="qwen3:8b"; messages=@(@{role="system";content=$systemPrompt},@{role="user";content=$item.p}); tools=$tools; stream=$false; options=@{temperature=0.1;num_predict=1500} } | ConvertTo-Json -Depth 10

    try {
        $r = Invoke-RestMethod -Uri "$OllamaUrl/api/chat" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 90
        $tool = ""; $args = ""; $text = ""
        if ($r.message.tool_calls) { $tool = $r.message.tool_calls[0].function.name; $args = $r.message.tool_calls[0].function.arguments | ConvertTo-Json -Depth 3 }
        if ($r.message.content) { $text = $r.message.content }

        $ok = $true; $reason = ""
        if ($item.p -match "ouvre|lance" -and $tool -ne "computer_action") { $ok = $false; $reason="no computer_action" }
        if ($args -match "/think") { $ok = $false; $reason="leaked /think" }
        if (-not $tool -and -not $text) { $ok = $false; $reason="empty response" }

        if ($ok) { Write-Host " PASS ($tool)" -ForegroundColor Green; $pass++ }
        else { Write-Host " FAIL ($reason)" -ForegroundColor Red; if ($tool) { Write-Host "    TOOL: $tool -> $args" }; if ($text) { Write-Host "    TEXT: $($text.Substring(0,[Math]::Min(100,$text.Length)))" }; $fail++ }
    } catch { Write-Host " ERROR" -ForegroundColor Red; $fail++ }
}
Write-Host "`nRESULTS: $pass PASS / $fail FAIL / $total TOTAL ($( ($pass*100.0/$total).ToString('F1') )%)"
