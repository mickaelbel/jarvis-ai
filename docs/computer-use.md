# Computer Use — Contrôle autonome de Windows

Jarvis peut observer l'écran, détecter les éléments d'interface, décider de la
prochaine action, l'exécuter (souris / clavier / fenêtres), vérifier le résultat
et recommencer jusqu'à accomplir la tâche. C'est le système « Computer Use ».

## Architecture

```
                          ┌──────────────────────────────┐
  LLM ──tool call──▶ ToolExecutor ──security──▶ ITool     │
                          │  computer_use / ui_elements / computer
                          ▼                              │
                   IComputerUseService  (orchestration)  │
                    │            │            │          │
                    ▼            ▼            ▼          │
              IComputerController   IUiElementDetector   │
                    │                 (IOcrService)      │
                    │                    │               │
              WindowsComputer   OcrUiElementDetector     │
              Controller (Win32)      + OCR Tesseract    │
                          │                              │
                          ▼                              │
                     IObservationProvider (boucle autonome)◀
                          └──────────────────────────────┘
```

### Couches

| Couche | Rôle |
|---|---|
| `IComputerController` (Application) | Abstrait l'écran, la souris, le clavier, les fenêtres, le presse-papiers. |
| `WindowsComputerController` (Infrastructure) | Implémentation Win32 (`SendInput`, `SetCursorPos`, `EnumWindows`, `MoveWindow`, `GetWindowRect`, clipboard). |
| `IUiElementDetector` (Application) | Convertit une capture en éléments interactifs cliquables. |
| `OcrUiElementDetector` (Infrastructure) | Détection heuristique : regroupe les mots OCR en lignes, classe chaque ligne (bouton, lien, dropdown, case à cocher, libellé + champ). |
| `IComputerUseService` (Application) | Orchestration haut niveau : observer, trouver par libellé, cliquer intelligemment, taper. |
| `ComputerUseService` (Infrastructure) | Implémentation qui capture → OCR → détecte → agit. |
| `IOcrService` / `IVisionService` (Application) | OCR (Tesseract) et description d'image (modèle vision Ollama). |

## Boucle autonome (observe → décide → exécute → vérifie)

`AutonomousAgentLoop` (commande `/auto <but>` dans le chat) itère jusqu'à
`MaxIterations` :

1. **Observe** : `ScreenAndPageObservationProvider` capture l'écran, produit
   OCR, la liste des **éléments UI détectés**, la position du curseur, les
   **fenêtres** visibles et le texte de la page navigateur.
2. **Décide** : le LLM choisit l'action suivante (généralement un appel d'outil
   `computer_use`).
3. **Exécute** : l'outil est exécuté par `ToolExecutor` après passage sécurité.
4. **Vérifie** : un appel LLM strict (`{"verified", "reason", "correction"}`)
   valide que le but est atteint ou fournit une correction à appliquer.
5. **Répète** jusqu'à vérification ou épuisement des itérations.

L'observation enrichie fournie au LLM ressemble à :

```
SCREEN: 1920x1080, cursor at (960, 540)
SCREEN OCR:
Bonjour...
UI ELEMENTS (3):
  #1 button "OK" at (120, 210) [40x20, conf 0.70]
  #2 input "input_Nom" at (73, 96) [1847x28, conf 0.40]
WINDOWS (2):
  * Notepad (handle 131072) at (0,0) 800x600
BROWSER PAGE (https://...):
...
```

Le LLM peut ensuite appeler `click_element` avec le libellé `"OK"` — le clic
intelligent le résout vers les coordonnées du centre de l'élément.

## Outils du système

### `computer_use` — exécution d'actions par libellé (risque : HIGH)

Pilote réellement la souris et le clavier. Chaque appel passe par la
confirmation utilisateur (voir Sécurité).

| Action | Paramètres | Description |
|---|---|---|
| `observe` | — | Observation complète : taille d'écran, curseur, OCR, éléments UI (JSON), fenêtres, chemin image PNG. |
| `find_element` | `label` | Résout un libellé vers un élément détecté (id, type, coordonnées, centre, confiance). |
| `click_element` | `label`, `button` (`left`/`right`/`middle`) | Déplace la souris au centre de l'élément et clique. |
| `double_click_element` | `label`, `button` | Double-clic au centre de l'élément. |
| `type_into` | `label`, `text` | Clique dans l'élément puis tape le texte. |

**Exemple (le LLM veut cliquer sur « OK ») :**
```
computer_use  action=observe
computer_use  action=click_element  label=OK
computer_use  action=type_into      label=input_Email  text=bonjour@test.fr
```

### `ui_elements` — inspection seule (risque : LOW)

Lecture seule, aucun clic ni saisie. Jamais bloquée par confirmation.

| Action | Paramètres | Description |
|---|---|---|
| `detect` | — | Liste tous les éléments détectés avec `id, type, label, x, y, width, height, centerX, centerY, confidence`. |
| `find` | `label` | Cherche un élément par libellé (flou, insensible à la casse et aux accents). Retourne `found: true/false`. |

### `computer` — contrôle direct (risque : HIGH)

Actions souris / clavier / fenêtres à coordonnées brutes (outil existant, étendu).

| Action | Paramètres | Description |
|---|---|---|
| `capture_screen` | — | Capture l'écran, sauvegarde un PNG, retourne métadonnées + base64. |
| `move_mouse` | `x`, `y` | Déplace le curseur. |
| `click` / `double_click` | `x`, `y`, `button` | (Double-)clic à des coordonnées. |
| `scroll` | `deltaY` | Molette (positif = haut). |
| `type_text` | `text` | Saisit du texte. |
| `press_key` | `keys` | Combinaison type `ctrl+c`, `enter`. |
| `list_windows` | — | Fenêtres visibles avec `handle, title, bounds`. |
| `focus_window` | `handle` | Met une fenêtre au premier plan (restaure si réduite). |
| `get_foreground_window` | — | Handle de la fenêtre active. |
| `minimize_window` / `maximize_window` / `restore_window` | `handle` | Réduit / agrandit / restaure. |
| `close_window` | `handle` | Envoie `WM_CLOSE` (demande de fermeture). |
| `move_window` | `handle`, `x`, `y` | Déplace une fenêtre. |
| `resize_window` | `handle`, `width`, `height` | Redimensionne une fenêtre. |
| `get_window_rect` | `handle` | Bords actuels `(x, y) w×h`. |
| `get_clipboard` / `set_clipboard` | `clipboard_text` | Lire / écrire le presse-papiers. |

### `vision` — voir et lire (risque : LOW, existant)

| Action | Description |
|---|---|
| `screen_ocr` / `screen_describe` | OCR ou description IA de l'écran actuel. |
| `image_ocr` / `image_describe` | OCR ou description IA d'un fichier image. |

## Détection d'éléments d'interface (heuristiques)

`OcrUiElementDetector` transforme les mots OCR en éléments :

1. **Regroupement en lignes** : les mots dont les centres verticaux sont proches
   (tolérance = hauteur moyenne × 0.5) sont réunis.
2. **Classification** par heuristiques, dans l'ordre :
   - marqueur `☑`/`[x]`/… → `checkbox`
   - `▾`/`▼`/`...` → `dropdown`
   - `http(s)://`/`www.`/domaine `foo.bar` → `link`
   - libellé terminé par `:` → `text` (libellé de formulaire)
   - texte court, commençant par une majuscule ou tout en majuscules → `button`
   - sinon → `text`
3. **Champ inféré** : un libellé `Nom:` génère aussi un élément `input` à droite
   du libellé (`input_Nom`), car un champ vide n'a pas de texte à lire par OCR.
4. **Confiance** : score 0.0–1.0 selon la force des signaux.
5. **Coordonnées** : `centerX/centerY` = centre du rectangle englobant, borné à l'écran.

## Clic intelligent (matching flou)

`UiElementMatcher.BestMatch(elements, query)` normalise les libellés
(minuscules, accents supprimés) puis classe :

- égalité exacte → 1.0
- libellé court contenu dans la requête (ex. « send » dans « send message ») → 0.9−0.05·len
- libellé contenant la requête → 0.85−0.02·écart de longueur
- préfixe commun → 0.7
- sinon → aucun match

Aucun seuil de correspondance par caractères : une requête sans lien réel
retourne « introuvable » plutôt qu'un faux clic.

## Sécurité

| Outil | Risque | Confirmation |
|---|---|---|
| `ui_elements` | Low | auto (lecture seule) |
| `vision` | Low | auto (lecture seule) |
| `computer` | High | requise si `RequireConfirmationForHighRisk=true` |
| `computer_use` | High | requise si `RequireConfirmationForHighRisk=true` |

La confirmation passe par `ISecurityManager` → `IUserConfirmationService`
(console ou web/SignalR). Les autres garde-fous existants s'appliquent :
liste blanche (`WhitelistedTools`), limite de débit (`MaxActionsPerMinute`),
mode développeur (`AllowDisableConfirmation`). Un refus utilisateur bloque
l'action avant tout mouvement de souris.

## Tests

Unitaires + intégration dans `Tests/` :

- `UiElementDetectorTests` — regroupement, classification, champs inférés, bornage.
- `UiElementMatcherTests` — correspondance exacte, partielle, accents, non-match.
- `ComputerUseServiceTests` — observe / find / click / double-click / type.
- `UiElementToolTests` et `ComputerUseToolTests` — contrats JSON et niveaux de risque.
- `ComputerToolTests` — actions fenêtres (minimize/maximize/restore/close/move/resize).
- `ObservationProviderTests` — observation enrichie (éléments + fenêtres + curseur).
- `ComputerUseIntegrationTests` — pipeline complet `ToolExecutor` + sécurité +
  `computer_use`/`ui_elements` + `ComputerUseService` + détecteur : confirmation
  requise/refusée/acceptée, boucle multi-étapes observe→clic→saisie, liste blanche.

```powershell
dotnet build JarvisAI.sln
dotnet test Tests/JarvisAI.Tests.csproj
```
