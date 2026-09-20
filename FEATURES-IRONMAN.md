# Jarvis AI - Specifications Fonctionnalites Iron Man

> Chaque feature est **independante**, **toggle-on/off**, et **rollback-safe**.
> Aucune feature ne casse une autre. Tout est optionnel et desactivable.

---

## Regles Generales

1. **Independance totale** : chaque feature a son propre service, son toggle, et son etat d'erreur isole.
2. **Pas de breaking change** : si une feature crash, les autres continuent de fonctionner.
3. **Rollback** : chaque feature garde une copie de la version precedente avant modification. Un bouton "Annuler" restaure l'etat d'avant.
4. **Auto-amelioration** : un bouton "Ameliorer Jarvis" dans le chat + commande vocale "ameliorer Jarvis" permettent de lancer l'auto-amelioration. L'IA analyse les erreurs recentes, genere des corrections, propose les changements, et l'utilisateur accepte ou refuse. Historique complet des versions avec rollback.
5. **Reset par defaut** : un bouton "Reinitialiser les parametres" dans la page Parametres Avances remet tout aux valeurs par defaut.

---

## Feature #1 - Reconnaissance Faciale (Webcam)

### Objectif
Savoir **qui** est devant l'ecran. Pas de securite, juste de la personnalisation.

### Comportement
- Au demarrage, Jarvis capture un frame via la webcam
- Si un visage connu est detecte -> "Bonjour Mickael"
- Si un visage **inconnu** est detecte -> "Je ne te connais pas encore. Comment tu t'appelles ?"
- L'utilisateur dit son nom -> Jarvis l'enregistre avec son embedding facial
- Si plusieurs personnes sont visibles -> Jarvis peut dire : "Il y a 2 personnes. Tu es le seul que je connais."
- **Detection en arriere-plan** : quand quelqu'un appara derriere l'utilisateur -> "Il y a quelqu'un derriere toi avec des lunettes, c'est qui ?"

### Architecture
`
src/Application/Biometry/
  IFaceRecognitionService.cs          - interface
  FaceEnrollment.cs                   - modele (Name, FaceId, Embedding, CreatedAt)

src/Infrastructure/Biometry/
  FaceRecognitionService.cs           - impl FaceAiSharp + OpenCvSharp webcam
  FaceDatabase.cs                     - stockage JSON dans %LOCALAPPDATA%/JarvisAI/faces/

src/Infrastructure/Tools/
  FaceTool.cs                         - outil agent : recognize, enroll, list, forget
`

### Packages NuGet
- FaceAiSharp.Bundle (0.6.35) - detection + reconnaissance faciale ONNX local
- OpenCvSharp4.Windows (4.13.0) - capture webcam via VideoCapture

### Toggle
- AdvancedSettings.FaceRecognitionEnabled (defaut : false)
- AdvancedSettings.WebcamDeviceIndex (defaut : 0)

### UI
- Page Parametres : section "Biometry" avec toggle ON/OFF
- Bouton "Enregistrer un visage" -> capture 3 frames -> enregistre
- Liste des visages enregistres avec bouton Supprimer

---

## Feature #2 - IA Proactive (Anticipe les Besoins)

### Objectif
Jarvis observe tes habitudes et agit **avant** que tu demandes.

### Comportement
- Jarvis enregistre : apps ouvertes, heures, frequence, contexte
- Apres 1 semaine d'apprentissage, Jarvis commence a predire :
  - "D'habitude tu ouvres Spotify a cette heure-la, je le lance ?"
  - "Tu ouvres souvent VS Code apres Explorer, je le prepare ?"
  - "C'est l'heure de ta reunion, je prepare le meeting ?"
- L'utilisateur peut dire "oui" ou "non" -> Jarvis apprend de la feedback
- **Jamais intrusif** : Jarvis propose, ne force jamais

### Architecture
`
src/Application/Proactive/
  IProactiveService.cs               - interface
  UserPattern.cs                      - modele (Action, Frequency, TimeRange, LastSeen)
  ProactivePrediction.cs              - modele (SuggestedAction, Confidence, Reason)

src/Infrastructure/Proactive/
  ProactiveService.cs                 - implementation avec Ollama
  PatternStore.cs                     - stockage SQLite patterns
  ActivityMonitor.cs                  - observe fenetres actives (PollingTimer)
`

### Toggle
- AdvancedSettings.ProactiveEnabled (defaut : false)
- AdvancedSettings.ProactiveLearningDays (defaut : 7)
- AdvancedSettings.ProactiveConfidenceThreshold (defaut : 0.7)

---

## Feature #3 - UI 3D Holographique

### Objectif
Les reponses de Jarvis s'affichent dans un espace 3D style Iron Man.

### Comportement
- Un panneau 3D overlay affiche les reponses avec animation
- Texte flottant avec effet holographique (cyan glow)
- Animations d'entree/sortie
- Les donnees (meteo, news, etc.) s'affichent en 3D
- Click-through : l'utilisateur peut cliquer a travers

### Architecture
`
src/Infrastructure/Hologram/
  HologramRenderer.cs                 - Three.js via WebView2
  HologramBridge.cs                   - C# <-> JavaScript communication
  HologramOverlay.cs                  - fenetre overlay transparente

src/Web/JarvisAI.Web/wwwroot/hologram/
  index.html                          - scene Three.js
  hologram.js                         - logique 3D
  styles.css                          - styles holographiques
`

### Packages NuGet
- ClickableTransparentOverlay (11.1.0) - fenetre overlay ImGui + Vortice
- Three.js via CDN dans WebView2

### Toggle
- AdvancedSettings.HologramEnabled (defaut : false)

---

## Feature #4 - Clonage Vocal / TTS Personnalise

### Objectif
Jarvis parle avec **ta voix** ou une voix que tu choisis.

### Comportement
- L'utilisateur enregistre 3 secondes de sa voix
- Jarvis clone cette voix et l'utilise pour toutes les reponses TTS
- Alternatives : voix prededefinies (Ryan, Emily, etc.)
- **100% local** : pas de cloud, pas d'envoi de donnees
- Toggle entre voix clonee et voix par defaut

### Architecture
`
src/Application/Voice/
  IVoiceCloningService.cs             - interface
  VoiceCloneProfile.cs                - modele (Name, ReferenceAudioPath, Language)

src/Infrastructure/Voice/
  QwenVoiceCloningService.cs          - impl ElBruno.QwenTTS
  VoiceCloneStore.cs                  - stockage profils dans %LOCALAPPDATA%/JarvisAI/voice-clone/
`

### Packages NuGet
- ElBruno.QwenTTS (1.10.0) - TTS local ONNX
- ElBruno.QwenTTS.VoiceCloning (1.10.0) - clonage vocal

### Toggle
- AdvancedSettings.VoiceCloningEnabled (defaut : false)
- AdvancedSettings.ClonedVoiceProfile (defaut : "")

### UI
- Page Parametres : section "Voice Clone"
- Bouton "Enregistrer ma voix" -> capture 3 sec -> enregistre
- Bouton "Ecouter un apercu" -> genere un sample avec la voix clonee
- Selecteur de voix predefinie

### Note
Le download du modele (~5.5 GB) se fait automatiquement au premier lancement. L'utilisateur est prevenu.

---

## Feature #5 - Overlay AR sur l'Ecran

### Objectif
Jarvis affiche des informations en temps reel au-dessus de TOUTES les applications.

### Comportement
- Une fenetre transparente, click-through, toujours au premier plan
- Jarvis y affiche :
  - Traduction en temps reel de texte selectionne
  - Meteo, alertes, notifications
  - Resultats de recherche
  - Statuts systeme (CPU, RAM, reseau)
- Le contenu se met a jour en temps reel
- L'utilisateur peut masquer/afficher avec un raccourci clavier

### Architecture
`
src/Application/Overlay/
  IOverlayService.cs                  - interface
  OverlayContent.cs                   - modele (Text, Position, Duration, Style)

src/Infrastructure/Overlay/
  JarvisOverlayService.cs             - impl ClickableTransparentOverlay
  OverlayRenderer.cs                  - rendu ImGui avec styles Iron Man
  OverlayHotkeyService.cs             - raccourci clavier global (Ctrl+Shift+J)
`

### Packages NuGet
- ClickableTransparentOverlay (11.1.0) - overlay transparent ImGui + Vortice
- ImGui.NET (dependance) - rendu UI

### Toggle
- AdvancedSettings.OverlayEnabled (defaut : false)
- AdvancedSettings.OverlayHotkey (defaut : "Ctrl+Shift+J")
- AdvancedSettings.OverlayOpacity (defaut : 0.85)

### UI
- Page Parametres : section "Overlay" avec toggle ON/OFF
- Selecteur de position (top-left, top-center, top-right, etc.)
- Slider d'opacite

---

## Feature #6 - Conscience Multi-Ecrans

### Objectif
Jarvis sait sur quel ecran tu regardes et envoie les infos au bon endroit.

### Comportement
- Detection de tous les ecrans connectes
- Calibration : l'utilisateur regarde chaque ecran -> Jarvis memorise la position du visage
- Ensuite, Jarvis envoie les notifications sur l'ecran ou tu regardes
- Si tu regardes l'ecran de gauche -> Jarvis affiche les infos sur cet ecran

### Architecture
`
src/Application/MultiMonitor/
  IMultiMonitorService.cs             - interface
  MonitorConfig.cs                    - modele (Id, Bounds, IsPrimary, FacePosition)

src/Infrastructure/MultiMonitor/
  MultiMonitorService.cs              - impl Screen.AllScreens + FaceAiSharp
  MonitorCalibrationService.cs        - calibration du regard par ecran
`

### Toggle
- AdvancedSettings.MultiMonitorEnabled (defaut : false)
- AdvancedSettings.MonitorCount (defaut : 1)

---

## Feature #7 - Detection d'Emotions (Voix + Visage)

### Objectif
Jarvis comprend ton etat emotionnel et adapte son comportement.

### Comportement
- Analyse le ton de ta voix en temps reel
- Si tu es frustré -> Jarvis simplifie, parle plus lentement
- Si tu es presse -> Jarvis va a l'essentiel
- Si tu es content -> Jarvis est plus decontracte, fait des blagues
- Si tu es confus -> Jarvis explique plus en detail
- **Aucune donnee n'est sauvegardee** -> vie privee respectee

### Architecture
`
src/Application/Emotion/
  IEmotionService.cs                  - interface
  EmotionProfile.cs                   - modele (Emotion, Confidence, Timestamp)

src/Infrastructure/Emotion/
  EmotionDetectionService.cs          - impl ONNX emotion model
  VoiceEmotionAnalyzer.cs             - analyse ton de voix via NAudio MFCC
  FaceEmotionAnalyzer.cs              - analyse expression via FaceAiSharp landmarks
`

### Packages NuGet
- FaceAiSharp.Bundle (0.6.35) - landmarks faciaux
- NAudio (2.2.1) - capture audio
- Microsoft.ML.OnnxRuntime - modele emotion local

### Toggle
- AdvancedSettings.EmotionDetectionEnabled (defaut : false)

---

## Feature #8 - Detection Menaces Reseau (Local)

### Objectif
Jarvis surveille ton reseau et t'alerte en cas de menace.

### Comportement
- Capture les paquets reseau en temps reel
- Detection d'anomalies : connexions suspectes, tentatives d'intrusion
- Alertes : "Attention, connexion suspecte vers 45.33.xx.xx"
- Rapport quotidien des activites reseau
- **100% local** : aucune donnee envoyee

### Architecture
`
src/Application/Security/
  INetworkSecurityService.cs          - interface
  NetworkThreat.cs                    - modele (Type, Source, Severity, Timestamp)

src/Infrastructure/Security/
  NetworkSecurityService.cs           - impl SharpPcap + PacketDotNet
  AnomalyDetector.cs                  - ML.NET anomaly detection
  ThreatRuleEngine.cs                 - regles de detection
`

### Packages NuGet
- SharpPcap - capture paquets
- PacketDotNet - parsing protocoles
- Microsoft.ML - anomaly detection (K-Means, SR-CNN)

### Toggle
- AdvancedSettings.NetworkSecurityEnabled (defaut : false)

---

## Feature #9 - Auto-Amelioration du Code

### Objectif
Jarvis s'auto-ameliore quand il rencontre des erreurs.

### Comportement
- Quand Jarvis fait une erreur (outil qui echoue, reponse incorrecte) :
  1. Il analyse l'erreur
  2. Il genere une correction
  3. Il propose le changement a l'utilisateur
  4. L'utilisateur accepte ou refuse
  5. Si accepte, le code est modifie et teste
  6. L'historique complet est garde pour rollback

### Bouton "Ameliorer Jarvis" dans le chat
- Affiche les erreurs recentes
- Propose des corrections
- L'utilisateur clique "Appliquer" ou "Annuler"

### Commande vocale
- "Ameliore Jarvis" -> lance l'auto-amelioration
- "Annule la derniere amelioration" -> rollback

### Architecture
`
src/Application/SelfImprovement/
  ISelfImprovementService.cs          - interface
  ImprovementProposal.cs              - modele (Description, CodeChange, Status, Timestamp)
  ImprovementHistory.cs               - historique des changements

src/Infrastructure/SelfImprovement/
  SelfImprovementService.cs           - impl avec Ollama + Roslyn
  CodeAnalyzer.cs                     - analyse des erreurs
  CodeModifier.cs                     - modification du code
  ImprovementStore.cs                 - historique JSON dans %LOCALAPPDATA%/JarvisAI/improvements/
`

### Toggle
- AdvancedSettings.SelfImprovementEnabled (defaut : false)
- AdvancedSettings.AutoApproveImprovements (defaut : false)

### UI
- Bouton "Ameliorer Jarvis" dans le chat
- Page Parametres : historique des ameliorations avec bouton "Annuler" par ligne

---

## Feature #10 - Eye Tracking (Suivi du Regard)

### Objectif
Jarvis sait exactement ou tu regardes sur l'ecran.

### Comportement
- Webcam -> MediaPipe Face Mesh (478 landmarks + iris) -> position du regard
- Si tu regardes un bouton -> Jarvis le highlight
- Si tu regardes du code -> Jarvis explique cette partie
- Si tu regardes une image -> Jarvis la decrit
- Calibration une fois : "Regarde le coin superieur gauche" -> calibre

### Architecture
`
src/Application/EyeTracking/
  IGazeTrackingService.cs             - interface
  GazePoint.cs                        - modele (X, Y, Confidence, Timestamp)
  GazeCalibration.cs                  - calibration par ecran

src/Infrastructure/EyeTracking/
  GazeTrackingService.cs              - impl MediaPipe Face Mesh + ONNX
  GazeToScreenMapper.cs               - mappe les coordonnees du regard sur l'ecran
  GazeHistory.cs                      - historique temporel + Kalman filter
`

### Packages NuGet
- OpenCvSharp4.Windows (4.13.0) - capture webcam
- Microsoft.ML.OnnxRuntime - modele gaze estimation
- Accord.NET (optionnel) - filtre de Kalman

### Toggle
- AdvancedSettings.GazeTrackingEnabled (defaut : false)
- AdvancedSettings.GazeCalibrated (defaut : false)

---

## Ordre d'Implementation

| # | Feature | Impact | Effort | Faisable | Ordre |
|---|---------|--------|--------|----------|-------|
| 1 | Reconnaissance Faciale | enorme | moyen | Oui | 1 |
| 4 | Clonage Vocal | enorme | faible | Oui | 2 |
| 5 | Overlay AR | enorme | moyen | Oui | 3 |
| 3 | UI 3D Holographique | tres haut | moyen | Oui | 4 |
| 6 | Multi-Ecrans | tres haut | moyen | Oui | 5 |
| 9 | Auto-Amelioration | moyen | tres haut | Recherche | 6 |
| 2 | IA Proactive | tres haut | tres haut | Plus tard | 7 |
| 7 | Detection Emotions | haut | haut | Plus tard | 8 |
| 8 | Menaces Reseau | haut | moyen-haut | Plus tard | 9 |
| 10 | Eye Tracking | haut | haut | Plus tard | 10 |

---

## Notes Techniques

### Gestion des erreurs
Chaque feature doit :
- Attraper toutes les exceptions et les logger
- Ne jamais crasher l'application principale
- Retourner un etat "Unavailable" si le service n'est pas pret
- Fournir un message d'erreur clair dans les logs

### Performance
- Chaque feature tourne dans son propre Task/Thread
- Pas de blocage du thread principal
- Cache des resultats quand c'est possible
- Lazy loading des modeles lourds

### Vie privee
- Aucune donnee n'est envoyee vers l'extérieur
- Tout est stocke localement dans %LOCALAPPDATA%/JarvisAI/
- L'utilisateur peut supprimer n'importe quelle donnee a tout moment
- Les modeles ONNX tournent 100% local
