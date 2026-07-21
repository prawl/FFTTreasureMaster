using System;
using System.IO;
using System.Reflection;
using System.Threading;
using FFTTreasureMaster.Configuration;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace FFTTreasureMaster;

/// <summary>
/// Reloaded-II entry point. Runs in-process inside FFT_enhanced.exe. Reloaded instantiates
/// this type (the constructor starts the engine) and then calls StartEx -- the real IModV2
/// entry, which hands over the loader instance. The loader is needed only by the optional
/// Treasure Hunter grant; the engine itself never touches it.
/// </summary>
public class Mod : IMod
{
    private Engine? _engine;
    private bool _started;
    private bool _grantEnabled;

    public Mod() => StartEngine();

    /// <summary>IModV1 entry. Some hosts call this instead of StartEx.</summary>
    public void Start(IModLoaderV1 modLoader) => HookInnateGrant(modLoader);

    /// <summary>IModV2 entry -- what current Reloaded actually calls after the ctor.</summary>
    public void StartEx(IModLoaderV1 modLoader, IModConfigV1 modConfig) => HookInnateGrant(modLoader);

    /// <summary>
    /// Arms the "All units gain Treasure Hunter" grant: when the toggle is on, subscribe to
    /// the after-all-mods-loaded moment and run the grant on a background thread from there
    /// (the modloader finds the job table with an async signature scan, so the grant polls
    /// readiness instead of assuming order). Toggle off: returns immediately -- no
    /// subscription, no controller traffic, no log lines. Guarded so no failure here can
    /// disturb the running engine.
    /// </summary>
    private void HookInnateGrant(IModLoaderV1? modLoader)
    {
        if (modLoader == null || !_grantEnabled) return;
        try
        {
            ModLogger.Event(LogVerb.Config,
                "Treasure Hunter grant armed: waiting for all mods to finish loading. " +
                "(Tech: loader captured in StartEx; grant runs after OnModLoaderInitialized.)");
            modLoader.OnModLoaderInitialized += () => StartGrantThread(modLoader);
        }
        catch (Exception ex)
        {
            try { ModLogger.Warn(LogVerb.Config, $"The Treasure Hunter grant could not be armed: {ex.Message}"); } catch { }
        }
    }

    private static void StartGrantThread(IModLoaderV1 modLoader)
    {
        try
        {
            var t = new Thread(() => RunGrant(modLoader)) { IsBackground = true, Name = "TreasureMaster.Grant" };
            t.Start();
        }
        catch (Exception ex)
        {
            try { ModLogger.Warn(LogVerb.Config, $"The Treasure Hunter grant thread could not start: {ex.Message}"); } catch { }
        }
    }

    /// <summary>Background: acquire the modloader's job-table controller and run the grant.
    /// A null controller while the modloader is active gets its own warn line -- that is the
    /// tripwire for an interfaces version mismatch, distinct from "not installed".</summary>
    private static void RunGrant(IModLoaderV1 modLoader)
    {
        try
        {
            var table = FftivcJobTable.TryCreate(modLoader);
            if (table == null && ModLoaderActive(modLoader))
                ModLogger.Warn(LogVerb.Config,
                    "The FFT Ivalice Chronicles Mod Loader is installed, but its job-table " +
                    "controller could not be acquired, so the Treasure Hunter grant is off " +
                    "this session. (Tech: GetController returned null while " +
                    "fftivc.utility.modloader is active; likely an interfaces version mismatch.)");
            new TreasureHunterGrant(enabled: true, table, Tuning.TreasureHunterGrantJobIds,
                                    msg => ModLogger.Event(LogVerb.Config, msg),
                                    Thread.Sleep).Run();
        }
        catch (Exception ex)
        {
            try { ModLogger.Warn(LogVerb.Config, $"The Treasure Hunter grant failed; the game runs normally without it: {ex.Message}"); } catch { }
        }
    }

    private static bool ModLoaderActive(IModLoaderV1 modLoader)
    {
        try
        {
            foreach (var mod in modLoader.GetActiveMods())
                if (mod.Generic?.ModId == "fftivc.utility.modloader") return true;
        }
        catch { }
        return false;
    }

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
            bool enabled        = Tuning.TreasureEnabled;                 // documented default
            bool claimDetection = Tuning.ClaimDetectionEnabled;           // documented default
            _grantEnabled       = Tuning.AllUnitsTreasureHunterEnabled;   // documented default (false)
            try
            {
                var configPath = ResolveConfigPath(modDir);
                var cfg        = Configurable<Config>.FromFile(configPath, "FFT Treasure Master Configuration");
                enabled        = cfg.Enabled;
                claimDetection = cfg.HideClaimedTiles;
                _grantEnabled  = cfg.AllUnitsTreasureHunter;
                ModLogger.EventWithTrace(LogVerb.Config,
                    $"Configuration loaded: Enabled={enabled} HideClaimedTiles={claimDetection} AllUnitsTreasureHunter={_grantEnabled}.",
                    $"config source {configPath}");
            }
            catch (Exception cfgEx)
            {
                ModLogger.Warn(LogVerb.Config,
                    $"The configuration could not be read; using defaults Enabled={enabled} HideClaimedTiles={claimDetection} AllUnitsTreasureHunter={_grantEnabled}: {cfgEx.Message}");
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
