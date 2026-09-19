# dry-run-analyze.ps1
$ErrorActionPreference = "Stop"
$OllamaUrl = "http://localhost:11434"

$systemPrompt = @"
Tu es Jarvis, assistant PC Windows. Reponds en francais.

STYLE -- direct, bref, sans preambule. Fais avant de dire.

REGLES OUTILS :
- Question simple = pas d'outil, reponds directement.
- Un seul outil quand suffit.
-computer_action = OUTIL PRINCIPAL pour applications PC. UN SEUL APPEL suffit.
  Format: computer_action instruction="description de l'action"
  Exemples: "ouvre le bloc-notes", "clique sur le bouton Enregistrer"
- Decomposer les demandes complexes en actions simples et sequentielles.
  "Ouvre le bloc-notes en plein ecran" = 1) ouvre le bloc-notes, 2) mets-le en plein ecran.
  NE JAMAIS chercher toute la phrase dans le menu Demarrer.
- Toujours privilegier le controle clavier/souris en foreground.
-browser = pour web uniquement. Jamais browser pour du local.
- Apres chaque outil, attends le resultat AVANT de continuer.
- Si un outil reussit, TA TACHE EST TERMINEE : reponds.

OUTILS :
- computer_action : Controle clavier/souris.
- browser : Navigation web.
- file_system : Lecture/ecriture fichiers.
- terminal : Execution commandes.
- calculator : Calculs.
"@

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

Write-Host "DRY-RUN ANALYZER"
Write-Host "================"

foreach ($prompt in $prompts) {
    Write-Host ""
    Write-Host "PROMPT: $prompt"

    $body = @{
        model = "qwen3:8b"
        messages = @(
            @{ role = "system"; content = $systemPrompt },
            @{ role = "user"; content = $prompt }
        )
        stream = $false
        options = @{
            temperature = 0.1
            num_predict = 500
        }
    } | ConvertTo-Json -Depth 5

    try {
        $response = Invoke-RestMethod -Uri "$OllamaUrl/api/chat" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 60
        $content = $response.message.content

        Write-Host "RESPONSE:"
        Write-Host "  $content"

        if ($content -match "computer_action") {
            Write-Host "  [OK] Uses computer_action"
        } else {
            Write-Host "  [FAIL] Does NOT use computer_action"
        }

        if ($content -match "bloc-notes en plein ecran|calculateur.*menu|cherch.*demarrer") {
            Write-Host "  [FAIL] Searches Start menu for compound name"
        }

        if ($content -match "\\\\n") {
            Write-Host "  [FAIL] Uses literal backslash-n"
        }

    } catch {
        Write-Host "  ERROR: $_"
    }

    Write-Host "---"
}
