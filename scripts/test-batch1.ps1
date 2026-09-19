# test-batch1.ps1 — Tests 1-35 (APP + FILE)
$ErrorActionPreference = "Stop"
$OllamaUrl = "http://localhost:11434"
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logDir = "$env:USERPROFILE\Desktop\JarvisQA_Intercept"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = "$logDir\batch1_$timestamp.txt"

$systemPrompt = @"
Tu es Jarvis, assistant PC Windows. Reponds en francais. Date : $(Get-Date -Format "dddd d MMMM yyyy, HH:mm").
STYLE -- direct, bref, sans preambule. Fais avant de dire.
REGLES OUTILS :
- TOUTE demande d'ouvrir/lancer une application = OBLIGATOIREMENT computer_action.
- Un seul outil quand suffit. Max 2 tentatives par outil.
-computer_action = OUTIL PRINCIPAL pour applications PC. UN SEUL APPEL suffit.
  Format: computer_action instruction="description de l'action"
  NE JAMAIS dire "via le menu Demarrer".
- Decomposer les demandes complexes en actions simples.
  "Ouvre le bloc-notes en plein ecran" = 1) ouvre, 2) maximize.
-browser = pour web uniquement.
file_system : TOUJOURS utiliser des chemins Windows reels (C:\Users\...). JAMAIS /think.
Ne JAMAIS repeter le contenu des thinking tags dans les arguments d'outils.
- Apres chaque outil, attends le resultat AVANT de continuer.
- Si un outil reussit, TA TACHE EST TERMINEE : reponds.
"@

$tools = @(
    @{ type="function"; function=@{ name="computer_action"; description="Controle clavier/souris et applications PC."; parameters=@{ type="object"; properties=@{ instruction=@{ type="string"; description="Description de l'action" } }; required=@("instruction") } } },
    @{ type="function"; function=@{ name="file_system"; description="Lecture/ecriture fichiers."; parameters=@{ type="object"; properties=@{ action=@{ type="string" }; path=@{ type="string" }; content=@{ type="string" } }; required=@("action","path") } } },
    @{ type="function"; function=@{ name="browser"; description="Navigation web."; parameters=@{ type="object"; properties=@{ action=@{ type="string" }; url=@{ type="string" } }; required=@("action") } } },
    @{ type="function"; function=@{ name="terminal"; description="Commandes shell."; parameters=@{ type="object"; properties=@{ command=@{ type="string" } }; required=@("command") } } },
    @{ type="function"; function=@{ name="calculator"; description="Calculs."; parameters=@{ type="object"; properties=@{ expression=@{ type="string" } }; required=@("expression") } } }
)

$prompts = @(
    @{ id="APP-001"; p="Ouvre le bloc-notes" },
    @{ id="APP-002"; p="Lance la calculatrice" },
    @{ id="APP-003"; p="Ouvre le bloc-notes et la calculatrice" },
    @{ id="APP-004"; p="Ferme le bloc-notes" },
    @{ id="APP-005"; p="Ouvre l'explorateur de fichiers" },
    @{ id="APP-006"; p="Ouvre Paint" },
    @{ id="APP-007"; p="Lance Notepad" },
    @{ id="APP-008"; p="Ouvre le bloc-notes s'il te plait" },
    @{ id="APP-009"; p="Tu peux ouvrir le calculateur ?" },
    @{ id="APP-010"; p="J'ai besoin du bloc-notes" },
    @{ id="APP-011"; p="Ouvre-moi Notepad" },
    @{ id="APP-012"; p="Lance l'application de dessin" },
    @{ id="APP-013"; p="Ouvre le gestionnaire de fichiers" },
    @{ id="APP-014"; p="Lance Word" },
    @{ id="APP-015"; p="Ouvre le terminal" },
    @{ id="APP-016"; p="Ouvre Chrome" },
    @{ id="APP-017"; p="Lance le navigateur web" },
    @{ id="APP-018"; p="Ouvre le bloc-notes et passe dessus" },
    @{ id="FILE-001"; p="Cree un fichier texte qui dit 'Bonjour'" },
    @{ id="FILE-002"; p="Cree un dossier nomme 'test_jarvis' sur le Bureau" },
    @{ id="FILE-003"; p="Cree un fichier sur le Bureau appele 'note.txt' avec le contenu 'Hello'" },
    @{ id="FILE-004"; p="Ecris 'Hello World' dans un fichier" },
    @{ id="FILE-005"; p="Cree un fichier CSV avec des donnees de test" },
    @{ id="FILE-006"; p="Cree le fichier C:\temp\jarvis_test.txt" },
    @{ id="FILE-007"; p="Ecris les nombres de 1 a 10 dans un fichier" },
    @{ id="FILE-008"; p="Cree un fichier JSON de test" },
    @{ id="FILE-009"; p="Cree un fichier XML de configuration vide" },
    @{ id="FILE-010"; p="Cree un fichier Python avec un print Hello" },
    @{ id="FILE-011"; p="Cree un fichier bash avec un shebang" },
    @{ id="FILE-012"; p="Cree un fichier de config YAML" },
    @{ id="FILE-013"; p="Cree un fichier markdown avec des titres" },
    @{ id="FILE-015"; p="Cree un fichier log de test avec des timestamps" }
)

$total = $prompts.Count
$pass = 0; $fail = 0

Write-Host "BATCH 1: APP + FILE ($total tests)"
Write-Host "=============================="

foreach ($item in $prompts) {
    $idx = [array]::IndexOf($prompts, $item) + 1
    Write-Host "[$idx/$total] $($item.id): $($item.p)" -NoNewline

    $body = @{ model="qwen3:8b"; messages=@(@{role="system";content=$systemPrompt},@{role="user";content=$item.p}); tools=$tools; stream=$false; options=@{temperature=0.1;num_predict=1000} } | ConvertTo-Json -Depth 10

    try {
        $r = Invoke-RestMethod -Uri "$OllamaUrl/api/chat" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 90
        $tool = ""; $args = ""; $text = ""
        if ($r.message.tool_calls) {
            $tool = $r.message.tool_calls[0].function.name
            $args = $r.message.tool_calls[0].function.arguments | ConvertTo-Json -Depth 3
        }
        if ($r.message.content) { $text = $r.message.content }

        $ok = $true
        $reason = ""

        # Check: app launch must use computer_action
        if ($item.p -match "ouvre|lance|ouvrir|lancer" -and $item.id -match "APP") {
            if ($tool -ne "computer_action") { $ok = $false; $reason="no computer_action" }
        }
        # Check: file operations must use file_system
        if ($item.id -match "FILE" -and $item.p -match "cree|ecri|fichier") {
            if ($tool -ne "file_system") { $ok = $false; $reason="no file_system" }
        }
        # Check: no /think in args
        if ($args -match "/think|\\\\think") { $ok = $false; $reason="leaked /think in args" }
        # Check: no empty response
        if (-not $tool -and -not $text) { $ok = $false; $reason="empty response" }
        # Check: no Start menu mention
        if ($text -match "menu demarrer|via le menu") { $ok = $false; $reason="mentions Start menu" }

        if ($ok) {
            Write-Host " PASS ($tool)" -ForegroundColor Green
            $pass++
        } else {
            Write-Host " FAIL ($reason)" -ForegroundColor Red
            if ($tool) { Write-Host "    TOOL: $tool -> $args" }
            if ($text) { Write-Host "    TEXT: $($text.Substring(0, [Math]::Min(100, $text.Length)))" }
            $fail++
        }

        "[$($item.id)] tool=$tool args=$args text=$text" | Out-File $logFile -Append -Encoding utf8
    } catch {
        Write-Host " ERROR: $_" -ForegroundColor Red
        $fail++
    }
}

Write-Host ""
Write-Host "RESULTS: $pass PASS / $fail FAIL / $total TOTAL"
Write-Host "RATE: $(($pass * 100.0 / $total).ToString('F1'))%"
