# dry-run-tools.ps1 — Test with actual Ollama tool definitions
$ErrorActionPreference = "Stop"
$OllamaUrl = "http://localhost:11434"

$systemPrompt = @"
Tu es Jarvis, assistant PC Windows. Reponds en francais.
STYLE -- direct, bref, sans preambule. Fais avant de dire.
Tu as acces aux outils suivants: computer_action, browser, file_system, terminal, calculator, clipboard, memory.
TOUTE demande d'ouvrir/lancer une application = OBLIGATOIREMENT computer_action.
"@

$tools = @(
    @{
        type = "function"
        function = @{
            name = "computer_action"
            description = "Controle clavier/souris et applications PC. UN SEUL APPEL suffit."
            parameters = @{
                type = "object"
                properties = @{
                    instruction = @{
                        type = "string"
                        description = "Description de l'action a effectuer"
                    }
                }
                required = @("instruction")
            }
        }
    },
    @{
        type = "function"
        function = @{
            name = "browser"
            description = "Navigation web uniquement"
            parameters = @{
                type = "object"
                properties = @{
                    url = @{ type = "string"; description = "URL a ouvrir" }
                }
                required = @("url")
            }
        }
    }
)

$prompts = @(
    "Ouvre le bloc-notes",
    "Lance la calculatrice",
    "Ouvre le calculateur",
    "Ouvre le bloc-notes en plein ecran",
    "Ouvre Paint et dessine un cercle rouge",
    "Appuie sur Ctrl+S",
    "Ferme le bloc-notes",
    "Ouvre Chrome et va sur google.com"
)

Write-Host "DRY-RUN WITH REAL TOOL DEFINITIONS"
Write-Host "==================================="

foreach ($prompt in $prompts) {
    Write-Host ""
    Write-Host "PROMPT: $prompt"

    $body = @{
        model = "qwen3:8b"
        messages = @(
            @{ role = "system"; content = $systemPrompt }
            @{ role = "user"; content = $prompt }
        )
        tools = $tools
        stream = $false
        options = @{
            temperature = 0.1
            num_predict = 500
        }
    } | ConvertTo-Json -Depth 10

    try {
        $response = Invoke-RestMethod -Uri "$OllamaUrl/api/chat" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 60

        if ($response.message.tool_calls) {
            foreach ($tc in $response.message.tool_calls) {
                $fn = $tc.function.name
                $args = $tc.function.arguments | ConvertTo-Json -Depth 3
                Write-Host "  TOOL: $fn -> $args"
            }
        } else {
            $content = $response.message.content
            Write-Host "  TEXT: $content"
            if ($content -match "computer_action") {
                Write-Host "  [OK] Mentions computer_action in text"
            } else {
                Write-Host "  [FAIL] No tool call and no computer_action"
            }
        }
    } catch {
        Write-Host "  ERROR: $_"
    }

    Write-Host "---"
}
