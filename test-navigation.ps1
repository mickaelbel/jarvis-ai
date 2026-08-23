# Script de test - Navigation web autonome
# Usage: .\test-navigation.ps1

Write-Host "=== TESTS NAVIGATION WEB AUTONOME ===" -ForegroundColor Cyan
Write-Host ""

$tests = @(
    @{
        Name = "1. Navigation simple (va sur google)"
        Input = "va sur google.com"
        Expected = "Playwright s'ouvre sur Google"
    },
    @{
        Name = "2. Recherche sur site (cherche clavier sur amazon)"
        Input = "cherche clavier mecanique sur amazon"
        Expected = "Amazon s'ouvre avec les résultats de recherche"
    },
    @{
        Name = "3. Ouvrir un site + action (ouvre youtube et cherche lasalle)"
        Input = "ouvre youtube et cherche lasalle"
        Expected = "YouTube s'ouvre et la recherche se lance"
    },
    @{
        Name = "4. Navigation + interaction (va sur amazon.fr et cherche un produit)"
        Input = "va sur amazon.fr et cherche keychron k8"
        Expected = "Amazon s'ouvre, cherche keychron k8, affiche les résultats"
    }
)

Write-Host "Tests disponibles :" -ForegroundColor Yellow
foreach ($t in $tests) {
    Write-Host "  - $($t.Name)" -ForegroundColor Green
    Write-Host "    Input: $($t.Input)" -ForegroundColor Gray
    Write-Host "    Expected: $($t.Expected)" -ForegroundColor Gray
    Write-Host ""
}

Write-Host "Pour tester, lance Jarvis et envoie les commandes ci-dessus." -ForegroundColor Cyan
Write-Host "Vérifie que :" -ForegroundColor Yellow
Write-Host "  1. La fenêtre Playwright s'ouvre (pas Chrome)" -ForegroundColor White
Write-Host "  2. L'agent navigue sur le bon site" -ForegroundColor White
Write-Host "  3. L'agent utilise la barre de recherche du site" -ForegroundColor White
Write-Host "  4. L'agent ne boucle pas sur web_search" -ForegroundColor White
Write-Host "  5. L'agent dit 2026 (pas 2024)" -ForegroundColor White
