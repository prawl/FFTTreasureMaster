using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using FFTTreasureMaster.Configuration;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace FFTTreasureMaster;

/// <summary>
/// Reloaded-II entry point. Runs in-process inside FFT_enhanced.exe. Reloaded instantiates
/// this type (the constructor starts the engine) and then calls StartEx, the real IModV2
/// entry, which hands over the loader instance. The loader is needed only by the optional
/// loader-backed features; the engine itself never touches it.
/// </summary>
public class Mod : IMod
{
    private Engine? _engine;
    private bool _started;
    private bool _grantEnabled;
    private bool _rareOnlyEnabled;
    private LoaderGrantHost? _loaderHost;

    public Mod() => StartEngine();

    /// <summary>IModV1 entry. Some hosts call this instead of StartEx.</summary>
    public void Start(IModLoaderV1 modLoader) => ArmLoaderJobs(modLoader);

    /// <summary>IModV2 entry, what current Reloaded actually calls after the ctor.</summary>
    public void StartEx(IModLoaderV1 modLoader, IModConfigV1 modConfig) => ArmLoaderJobs(modLoader);

    /// <summary>
    /// Hands the jobs for the switched-on loader features to the host. All switches off, or no
    /// loader handle, arms nothing and logs nothing. Both entry points land here and the host
    /// arms only once.
    /// </summary>
    private void ArmLoaderJobs(IModLoaderV1? loader)
    {
        if (loader == null || (!_rareOnlyEnabled && !_grantEnabled)) return;

        var events = new ReloadedLoaderEvents(loader);
        var jobs = new List<(string Name, Action Job)>();
        if (_rareOnlyEnabled)
            jobs.Add(("Rare treasure only", () => LoaderJobs.RunRareTreasure(
                () => FftivcTrapTable.TryCreate(loader), events.IsLoaderActive, Info, Warn, Thread.Sleep)));
        if (_grantEnabled)
            jobs.Add(("All units gain Treasure Hunter", () => LoaderJobs.RunTreasureHunter(
                () => FftivcJobTable.TryCreate(loader), events.IsLoaderActive, Info, Warn, Thread.Sleep)));

        _loaderHost ??= new LoaderGrantHost(jobs, Info, Warn, work =>
            new Thread(() => work()) { IsBackground = true, Name = "TreasureMaster.LoaderJobs" }.Start());
        _loaderHost.Arm(events);
    }

    private static void Info(string msg) => ModLogger.Event(LogVerb.Config, msg);
    private static void Warn(string msg) => ModLogger.Warn(LogVerb.Config, msg);

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
            // TM-8: the Denuvo-free build loads at a random (ASLR) image base. Resolve it once here,
            // before the Engine and its threads exist, so every pinned address rebases onto it.
            if (ImageBase.TryResolveLive(out long liveBase) && ImageBase.Set(liveBase))
                ModLogger.Event(LogVerb.Startup, $"Game image base 0x{ImageBase.Base:X} (preferred 0x{ImageBase.Preferred:X}, delta 0x{ImageBase.Delta:X}); every pinned address is rebased onto it.");
            else
                ModLogger.Warn(LogVerb.Startup, $"Could not read the game image base; using the preferred base 0x{ImageBase.Preferred:X}. If the game loaded elsewhere the treasure marks will never arm.");
            ModLogger.Event(LogVerb.Startup, "Treasure Master is starting inside fft_enhanced.exe.");

            // Load mod config fail-soft: any read failure falls back to the Tuning default (true).
            // The Reloaded launcher writes the user's edits to <Reloaded>/User/Mods/<ModId>/Config.json,
            // NOT to the deployed mod folder -- so read the user file when it exists, falling back to
            // modDir/Config.json (the shipped default) before the user has opened the config UI.
            bool enabled        = Tuning.TreasureEnabled;                 // documented default
            bool claimDetection = Tuning.ClaimDetectionEnabled;           // documented default
            _grantEnabled       = Tuning.AllUnitsTreasureHunterEnabled;   // documented default (false)
            _rareOnlyEnabled    = Tuning.RareTreasureOnlyEnabled;         // documented default (false)
            try
            {
                var configPath = ResolveConfigPath(modDir);
                var cfg        = Configurable<Config>.FromFile(configPath, "FFT Treasure Master Configuration");
                enabled        = cfg.Enabled;
                claimDetection = cfg.HideClaimedTiles;
                _grantEnabled  = cfg.AllUnitsTreasureHunter;
                _rareOnlyEnabled = cfg.RareTreasureOnly;
                ModLogger.EventWithTrace(LogVerb.Config,
                    $"Configuration loaded: Enabled={enabled} HideClaimedTiles={claimDetection} AllUnitsTreasureHunter={_grantEnabled} RareTreasureOnly={_rareOnlyEnabled}.",
                    $"config source {configPath}");
            }
            catch (Exception cfgEx)
            {
                ModLogger.Warn(LogVerb.Config,
                    $"The configuration could not be read; using defaults Enabled={enabled} HideClaimedTiles={claimDetection} AllUnitsTreasureHunter={_grantEnabled} RareTreasureOnly={_rareOnlyEnabled}: {cfgEx.Message}");
            }

            _engine = new Engine(modDir, enabled, claimDetection);
            _engine.Start();
        }
        catch (Exception ex)
        {
            try { ModLogger.Error(LogVerb.Startup, "Startup failed; Treasure Master will not run.", ex); } catch { }
        }
    }

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
                var userConfig = Path.Combine(reloadedRoot, "User", "Mods", Tuning.OwnerModId, "Config.json");
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
