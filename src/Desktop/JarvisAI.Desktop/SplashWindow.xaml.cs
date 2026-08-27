using System.Windows;
using System.Windows.Threading;

namespace JarvisAI.Desktop;

public partial class SplashWindow : Window
{
    private readonly DispatcherTimer _statusTimer;
    private readonly List<(double Pct, string Text)> _steps = new()
    {
        (5, "Préparation de l'environnement..."),
        (20, "Initialisation du moteur vocal..."),
        (38, "Chargement des modèles IA..."),
        (55, "Connexion à Ollama..."),
        (70, "Démarrage du serveur web..."),
        (85, "Chargement de l'interface..."),
        (92, "Configuration des services..."),
    };
    private int _stepIndex;
    private double _currentPercent;

    public SplashWindow()
    {
        InitializeComponent();
        _currentPercent = 1;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _statusTimer.Tick += (_, _) => Poke();
        _statusTimer.Start();
    }

    /// <summary>
    /// Accélère le texte de chargement : quand un vrai progrès est signalé par
    /// l'application on avance la barre proportionnellement, sinon on anime
    /// indirectement pendant l'attente pour ne pas laisser de temps mort avec
    /// une impression de blocage.
    /// </summary>
    public void SetProgress(double percent, string? text = null)
    {
        // Ne permet jamais à la barre de reculer.
        if (percent > _currentPercent) _currentPercent = Math.Min(100, percent);

        if (text is not null) StatusText.Text = text;

        // Place la barre proportionnellement au vrai avancement du démarrage.
        ProgressBar.Width = 220 * (_currentPercent / 100);
        PercentText.Text = (int)_currentPercent + "%";

        // Met à jour le pas courant pour que le texte tournant reste cohérent.
        for (int i = 0; i < _steps.Count; i++)
        {
            if (_currentPercent >= _steps[i].Pct) _stepIndex = i;
        }
        StateHasChangedIfNeeded();
    }

    /// <summary>
    /// Animation d'attente « vivante » : fait défiler des messages préparés et
    /// anime un léger reflet (shimmer) pour donner une impression de progression
    /// même quand une étape longue (ex. démarrage d'Ollama) est en cours.
    /// </summary>
    private void Poke()
    {
        // Le shimmer avance en boucle sur toute la largeur.
        ShimmerTranslate.X = (ShimmerTranslate.X + 6) % 260;

        if (ShimmerTranslate.X <= 0)
        {
            // Une boucle complète de shimmer = on fait tourner le texte vers le pas suivant.
            if (_stepIndex < _steps.Count - 1) _stepIndex++;
            StatusText.Text = _steps[_stepIndex].Text;
        }
    }

    public double CurrentPercent => _currentPercent;

    private void StateHasChangedIfNeeded() { }

    public void Finish()
    {
        _statusTimer.Stop();
        ProgressBar.Width = 220;
        PercentText.Text = "100%";
        Close();
    }
}
