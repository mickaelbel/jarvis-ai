# test-batch3.ps1 — Multi-Step tests
$ErrorActionPreference = "Stop"
$OllamaUrl = "http://localhost:11434"

$systemPrompt = @"
Tu es Jarvis, assistant PC Windows. Reponds en francais.
STYLE -- direct, bref, sans preambule. Fais avant de dire.
REGLES OUTILS :
- TOUTE demande d'ouvrir/lancer une application = OBLIGATOIREMENT computer_action.
- computer_action = OUTIL PRINCIPAL. UN SEUL APPEL suffit.
  Format: computer_action instruction="description de l'action"
- Decomposer les demandes complexes en actions simples et sequentielles.
- Toujours privilegier le controle clavier/souris en foreground.
- Apres chaque outil, attends le resultat AVANT de continuer.
- Si un outil reussit, TA TACHE EST TERMINEE : reponds.
- file_system : TOUJOURS utiliser des chemins Windows reels (C:\Users\...). JAMAIS /think.
"@

$tools = @(
    @{ type="function"; function=@{ name="computer_action"; description="Controle clavier/souris."; parameters=@{ type="object"; properties=@{ instruction=@{ type="string" } }; required=@("instruction") } } },
    @{ type="function"; function=@{ name="file_system"; description="Fichiers."; parameters=@{ type="object"; properties=@{ action=@{ type="string" }; path=@{ type="string" }; content=@{ type="string" } }; required=@("action","path") } } },
    @{ type="function"; function=@{ name="browser"; description="Navigation web."; parameters=@{ type="object"; properties=@{ action=@{ type="string" }; url=@{ type="string" } }; required=@("action") } } },
    @{ type="function"; function=@{ name="calculator"; description="Calculs."; parameters=@{ type="object"; properties=@{ expression=@{ type="string" } }; required=@("expression") } } }
)

$prompts = @(
    @{ id="MS-001"; p="Ouvre le bloc-notes, ecris 'Test Jarvis', puis sauvegarde le fichier" },
    @{ id="MS-002"; p="Ouvre la calculatrice, calcule 2+2, et dis-moi le resultat" },
    @{ id="MS-003"; p="Cree un dossier 'projet' sur le Bureau, puis cree un fichier 'README.md' dedans" },
    @{ id="MS-004"; p="Ouvre Paint, dessine un carre, puis passe en couleur bleue" },
    @{ id="MS-005"; p="Ouvre le bloc-notes, ecris 'Bonjour', sauvegarde, ferme, puis rouvre le fichier" },
    @{ id="MS-006"; p="Cree 3 fichiers texte sur le Bureau avec les noms 'a.txt', 'b.txt', 'c.txt'" },
    @{ id="MS-007"; p="Ouvre le bloc-notes et l'explorateur en meme temps" },
    @{ id="MS-008"; p="Ecris 'ligne 1', 'ligne 2', 'ligne 3' dans le bloc-notes" },
    @{ id="MS-009"; p="Cree un fichier, ecris du texte, puis verifie qu'il existe" },
    @{ id="MS-010"; p="Ouvre Paint, dessine un cercle, puis sauvegarde l'image" },
    @{ id="MS-011"; p="Cree un dossier, cree 5 fichiers dedans, puis liste le contenu" },
    @{ id="MS-012"; p="Ouvre le bloc-notes, tape un paragraphe de 3 lignes" },
    @{ id="MS-013"; p="Cree un fichier de test et verifie sa taille" },
    @{ id="MS-014"; p="Ouvre la calculatrice 3 fois de suite" },
    @{ id="MS-015"; p="Cree un fichier, lis-le, modifie-le, relis-le" }
)

$total = $prompts.Count; $pass = 0; $fail = 0
Write-Host "BATCH 3: MULTI-STEP ($total tests)"; Write-Host "=========================="

foreach ($item in $prompts) {
    $idx = [array]::IndexOf($prompts, $item) + 1
    Write-Host "[$idx/$total] $($item.id): $($item.p)" -NoNewline

    $body = @{ model="qwen3:8b"; messages=@(@{role="system";content=$systemPrompt},@{role="user";content=$item.p}); tools=$tools; stream=$false; options=@{temperature=0.1;num_predict=1500} } | ConvertTo-Json -Depth 10

    try {
        $r = Invoke-RestMethod -Uri "$OllamaUrl/api/chat" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 120
        $tool = ""; $args = ""; $text = ""
        if ($r.message.tool_calls) { $tool = $r.message.tool_calls[0].function.name; $args = $r.message.tool_calls[0].function.arguments | ConvertTo-Json -Depth 3 }
        if ($r.message.content) { $text = $r.message.content }

        $ok = $true; $reason = ""
        if ($args -match "/think") { $ok = $false; $reason="leaked /think" }
        if (-not $tool -and -not $text) { $ok = $false; $reason="empty response" }
        if ($text -match "menu demarrer|via le menu") { $ok = $false; $reason="mentions Start menu" }

        if ($ok) { Write-Host " PASS ($tool)" -ForegroundColor Green; $pass++ }
        else { Write-Host " FAIL ($reason)" -ForegroundColor Red; if ($tool) { Write-Host "    TOOL: $tool -> $args" }; if ($text -and $text.Length -gt 0) { Write-Host "    TEXT: $($text.Substring(0,[Math]::Min(100,$text.Length)))" }; $fail++ }
    } catch { Write-Host " ERROR" -ForegroundColor Red; $fail++ }
}
Write-Host "`nRESULTS: $pass PASS / $fail FAIL / $total TOTAL ($( ($pass*100.0/$total).ToString('F1') )%)"
