using System;
using System.Collections.Generic;

namespace JarvisAI.Application.Voice;

/// <summary>
/// Analyse continue de l'ambiance sonore captée par le micro, même quand on ne
/// s'adresse pas à Jarvis. Conserve une fenêtre glissante de quelques minutes
/// (niveau sonore + détection de parole via le VAD du moteur) et produit un
/// résumé injecté dans le prompt système des échanges vocaux, pour que Jarvis
/// ait conscience du contexte environnant.
/// </summary>
public sealed class AmbientContextService
{
    private readonly object _lock = new();
    private readonly Queue<AmbientSample> _samples = new();
    private double _lastLevel;
    private int _speech;
    private int _silence;
    private int _activity;
    private DateTime _lastSpeech;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public void Feed(double level, bool speech, DateTime now)
    {
        lock (_lock)
        {
            _lastLevel = level;
            var cls = level < 0.01 ? "silence" : (speech ? "parole" : "ambiance");
            if (cls == "parole") _lastSpeech = now;
            _samples.Enqueue(new AmbientSample(now, level, cls));
            switch (cls)
            {
                case "parole": _speech++; break;
                case "silence": _silence++; break;
                default: _activity++; break;
            }

            while (_samples.Count > 0 && now - _samples.Peek().Time > Window)
            {
                var old = _samples.Dequeue();
                switch (old.Class)
                {
                    case "parole": _speech--; break;
                    case "silence": _silence--; break;
                    default: _activity--; break;
                }
            }
        }
    }

    public string GetContextSummary()
    {
        lock (_lock)
        {
            var total = _samples.Count;
            if (total == 0) return string.Empty;

            var speechPct = (double)_speech / total;
            var silencePct = (double)_silence / total;
            var level = _lastLevel switch
            {
                < 0.015 => "silencieuse",
                < 0.05 => "calme",
                < 0.15 => "modérée",
                _ => "bruyante"
            };

            var parts = new List<string> { $"ambiance sonore {level}" };
            if (speechPct > 0.10) parts.Add($"présence de voix humaines ({speechPct:P0} du temps)");
            if (silencePct < 0.35) parts.Add("activité sonore soutenue");

            return $"Il est {DateTime.Now:HH\\hmm}. Sur les 10 dernières minutes, {string.Join(", ", parts)}.";
        }
    }

    public bool HasData
    {
        get { lock (_lock) return _samples.Count > 0; }
    }

    /// <summary>
    /// Vrai si une parole humaine a été détectée récemment (fenêtre glissante).
    /// Sert à ne pas couper la parole à l'utilisateur lors d'annonces proactives.
    /// </summary>
    public bool HasRecentSpeech(TimeSpan window)
    {
        lock (_lock) return _lastSpeech != default && DateTime.Now - _lastSpeech <= window;
    }

    public double LastLevel
    {
        get { lock (_lock) return _lastLevel; }
    }

    private sealed record AmbientSample(DateTime Time, double Level, string Class);
}
