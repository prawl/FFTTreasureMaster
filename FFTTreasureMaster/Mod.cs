using System;
using System.IO;
using System.Reflection;
using FFTTreasureMaster.Configuration;
using Reloaded.Mod.Interfaces;

namespace FFTTreasureMaster;

/// <summary>
/// Reloaded-II entry point. Runs in-process inside FFT_enhanced.exe. Reloaded instantiates
/// this type and the constructor fires, so we start the engine there (Start is also wired,
/// guarded, in case a host prefers it).
/// </summary>
public class Mod : IMod
{
    private Engine? _engine;
    private bool _started;

    public Mod() => StartEngine();

    public void Start(IModLoader? modLoader) => StartEngine();

    private void StartEngine()
    {
        if (_started) return;
        _started = true;
        try
        {
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                            ?? Environment.CurrentDirectory;
            ModLogger.Init(modDir);
            Flight.Init(modDir);
            ModLogger.Event(LogVerb.Startup, "Treasure Master is starting inside fft_enhanced.exe.");

            // Load mod config fail-soft: any read failure falls back to the Tuning default (true).
            // The Reloaded launcher writes the user's edits to <Reloaded>/User/Mods/<ModId>/Config.json,
            // NOT to the deployed mod folder -- so read the user file when it exists, falling back to
            // modDir/Config.json (the shipped default) before the user has opened the config UI.
            bool enabled        = Tuning.TreasureEnabled;        // documented default
            bool claimDetection = Tuning.ClaimDetectionEnabled;  // documented default (false)
            try
            {
                var configPath = ResolveConfigPath(modDir);
                var cfg        = Configurable<Config>.FromFile(configPath, "FFT Treasure Master Configuration");
                enabled        = cfg.Enabled;
                claimDetection = cfg.HideClaimedTiles;
                ModLogger.EventWithTrace(LogVerb.Config,
                    $"Configuration loaded: Enabled={enabled} HideClaimedTiles={claimDetection}.",
                    $"config source {configPath}");
            }
            catch (Exception cfgEx)
            {
                ModLogger.Warn(LogVerb.Config,
                    $"The configuration could not be read; using defaults Enabled={enabled} HideClaimedTiles={claimDetection}: {cfgEx.Message}");
            }

            _engine = new Engine(modDir, enabled, claimDetection);
            _engine.Start();
        }
        catch (Exception ex)
        {
            try { ModLogger.Error(LogVerb.Startup, "Startup failed; Treasure Master will not run.", ex); } catch { }
        }
    }

    /// <summary>The mod namespace -- the folder name under both Mods/ and User/Mods/.</summary>
    private const string ModId = "prawl.fft.treasuremaster";

    /// <summary>
    /// The config the DLL should read. The Reloaded launcher saves user edits to
    /// &lt;Reloaded&gt;/User/Mods/&lt;ModId&gt;/Config.json (modDir is Mods/&lt;ModId&gt;, two
    /// levels under the Reloaded root). Prefer that file when it exists; otherwise fall back to
    /// the shipped default in modDir. Any path error returns the modDir path (FromFile is fail-soft).
    /// </summary>
    private static string ResolveConfigPath(string modDir)
    {
        try
        {
            var reloadedRoot = Directory.GetParent(modDir)?.Parent?.FullName;
            if (reloadedRoot != null)
            {
                var userConfig = Path.Combine(reloadedRoot, "User", "Mods", ModId, "Config.json");
                if (File.Exists(userConfig)) return userConfig;
            }
        }
        catch { /* fall through to the modDir default */ }
        return Path.Combine(modDir, "Config.json");
    }

    public void Suspend() { }
    public void Resume() { }
    public void Unload() => _engine?.Stop();
    public bool CanUnload() => false;
    public bool CanSuspend() => false;
    public Action Disposing { get; } = () => { };
}
