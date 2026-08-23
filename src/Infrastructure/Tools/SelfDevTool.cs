using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Dev;

namespace JarvisAI.Infrastructure.Tools;

// Outil d'auto-développement (docs/selfdev.md) : Jarvis peut construire, tester,
// committer, publier et réparer son propre code. C'est le socle de son
// autonomie technique : « autodev test » puis « autodev fix » = auto-amélioration.
public sealed class SelfDevTool : ITool
{
    private readonly SelfDevEngine _engine;
    private readonly SelfDevStore _store;

    public SelfDevTool(SelfDevEngine engine, SelfDevStore store)
    {
        _engine = engine;
        _store = store;
    }

    public string Name => "autodev";
    public string Description =>
        "Auto-développement de Jarvis : « status », « build », « test [filtre] », « fix » (auto-réparation IA avec rollback), " +
        "« checkpoint <msg> », « annuler » (rollback), « publier », « installateur », « redemarrer », " +
        "« logs web|desktop [n] », « boucle on|off|status|config [int Minutes] [autopub on|off] [autofix on|off] ».";
    public string Category => "dev";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium; // écrit du code mais toujours derrière build+tests+rollback

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "status | build | test | fix | checkpoint | annuler | publier | installateur | redemarrer | logs | boucle", typeof(string), required: true),
        new("filtre", "Filtre dotnet test (--filter), p. ex. FullyQualifiedName~WakeWord", typeof(string), required: false),
        new("message", "Message du checkpoint git", typeof(string), required: false),
        new("source", "Journal à lire : web | desktop (défaut web)", typeof(string), required: false),
        new("lignes", "Nombre de lignes de journal (défaut 40, max 400)", typeof(string), required: false),
        new("valeur", "Pour boucle config : intervalle minutes / on / off", typeof(string), required: false),
        new("autopub", "Publication auto si vert : on | off", typeof(string), required: false),
        new("autofix", "Auto-fix IA : on | off", typeof(string), required: false)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("filtre", out var filtre);
        parameters.TryGetValue("message", out var message);
        parameters.TryGetValue("source", out var source);
        parameters.TryGetValue("lignes", out var lignesStr);
        parameters.TryGetValue("valeur", out var valeur);
        parameters.TryGetValue("autopub", out var autopub);
        parameters.TryGetValue("autofix", out var autofix);

        switch (action?.ToLowerInvariant().Trim())
        {
            case "status":
            {
                var repo = _engine.FindRepo();
                if (repo is null)
                    return ToolResult.Succeeded("Dépôt introuvable : JarvisAI.sln absent depuis l'exécutable.");
                var gitStatus = await _engine.StatusAsync();
                var s = _store.Get();
                return ToolResult.Succeeded(
                    $"Dépôt : {repo}\nGit : {gitStatus}\n" +
                    $"Boucle maintenance : {(s.AutoCheckEnabled ? $"ON toutes les {s.IntervalMinutes} min" : "OFF")}, " +
                    $"auto-fix IA : {(s.AutoFixWithAi ? "on" : "off")} ({s.MaxAutoFixPerDay}/jour max), " +
                    $"publication auto si vert : {(s.AutoPublishOnGreen ? "on" : "off")}.");
            }

            case "build":
            {
                var r = await _engine.BuildAsync();
                return ToolResult.Succeeded(r.Success
                    ? "Build OK ✓"
                    : "Build en échec ✗\n" + string.Join("\n", r.Errors.Take(8)));
            }

            case "test":
            {
                var r = await _engine.TestAsync(filtre);
                if (r.Success)
                    return ToolResult.Succeeded($"Tests verts ✓ — {r.Passed} réussis.");
                var detail = string.Join("\n", r.Failures.Take(6).Select(f =>
                    $"✗ {f.Name}" + (string.IsNullOrWhiteSpace(f.Message) ? "" : $"\n  {Trunc(f.Message, 250)}")));
                return ToolResult.Succeeded($"Tests ROUGES ✗ — {r.Failed} échec(s) sur {r.Passed + r.Failed}.\n{detail}");
            }

            case "fix":
                return ToolResult.Succeeded(await _engine.TryAutoFixAsync());

            case "checkpoint":
                var (ok, info) = await _engine.CheckpointAsync(string.IsNullOrWhiteSpace(message) ? "checkpoint manuel" : message!);
                return ToolResult.Succeeded(ok ? $"Checkpoint : {info}." : $"Échec : {info}");

            case "annuler":
                var (rok, rinfo) = await _engine.RollbackAsync();
                return ToolResult.Succeeded(rok ? rinfo : "Rollback impossible : " + rinfo);

            case "publier":
                var (pok, pinfo) = await _engine.PublishAsync();
                return ToolResult.Succeeded(pok ? "Publication terminée : dist\\JarvisAI mis à jour." : "Publication échouée :\n" + Trunc(pinfo, 500));

            case "installateur":
                var (iok, iinfo) = await _engine.BuildInstallerAsync();
                return ToolResult.Succeeded(iok ? "Installateur généré dans dist\\." : "Échec installateur :\n" + Trunc(iinfo, 500));

            case "redemarrer":
                var (resOk, resInfo) = _engine.RestartApp();
                return ToolResult.Succeeded(resOk ? "Redémarrage lancé — je serai de retour dans quelques secondes." : resInfo);

            case "logs":
            {
                var which = string.IsNullOrWhiteSpace(source) ? "web" : source!;
                var n = int.TryParse(lignesStr, out var nn) ? nn : 40;
                return ToolResult.Succeeded(Trunc(_engine.TailLog(which, n), 3500));
            }

            case "boucle":
            {
                var s = _store.Get();
                var v = valeur?.ToLowerInvariant();
                if (v == "on") s.AutoCheckEnabled = true;
                else if (v == "off") s.AutoCheckEnabled = false;
                else if (!string.IsNullOrWhiteSpace(v) && int.TryParse(v, out var mins))
                    s.IntervalMinutes = Math.Clamp(mins, 30, 1440);
                else if (!string.IsNullOrWhiteSpace(v))
                    return ToolResult.Failed("Valeur inconnue : utilise on, off ou un intervalle en minutes.");
                if (autopub is not null) s.AutoPublishOnGreen = autopub.Equals("on", StringComparison.OrdinalIgnoreCase);
                if (autofix is not null) s.AutoFixWithAi = autofix.Equals("on", StringComparison.OrdinalIgnoreCase);
                _store.Save(s);
                return ToolResult.Succeeded($"Boucle mise à jour : {(s.AutoCheckEnabled ? $"ON ({s.IntervalMinutes} min)" : "OFF")}, autofix={OnOff(s.AutoFixWithAi)}, autopub={OnOff(s.AutoPublishOnGreen)}.");
            }

            default:
                return ToolResult.Failed("Action inconnue. Actions : status, build, test, fix, checkpoint, annuler, publier, installateur, redemarrer, logs, boucle.");
        }
    }

    private static string OnOff(bool b) => b ? "on" : "off";

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + " …";
}