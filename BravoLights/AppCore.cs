using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using BravoLights.Common;
using BravoLights.Connections;
using BravoLights.Installation;
using BravoLights.Profiles;
using BravoLights.Sim;
using NLog;

namespace BravoLights
{
    /// <summary>
    /// The heart of the application: it watches the simulator and the profile library, chooses the right profile
    /// for the loaded aircraft and drives the Bravo's lights from it.
    /// </summary>
    /// <remarks>
    /// All public state is updated on the UI thread, so the UI can bind to it directly.
    /// </remarks>
    public sealed class AppCore : ViewModelBase, IDisposable
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly Dispatcher dispatcher;

        public AppCore(Dispatcher dispatcher, bool useHardware = true)
        {
            this.dispatcher = dispatcher;

            Settings = AppSettings.Load();
            Templates = new TemplateLibrary(FlightSimulatorPaths.TemplatesPath);
            Store = new ProfileStore(FlightSimulatorPaths.ProfilesPath);
            Lights = new MainViewModel();
            // Without hardware (e.g. when rendering screenshots) we must not touch a Bravo another copy may be driving
            Usb = useHardware ? new UsbLogic(Lights) : new NoBravo();
            GlobalLights = new GlobalLightController(Usb);
        }

        public AppSettings Settings { get; }
        public TemplateLibrary Templates { get; }
        public ProfileStore Store { get; }
        public MainViewModel Lights { get; }
        public IUsbLogic Usb { get; }
        public GlobalLightController GlobalLights { get; }

        internal SimConnectConnection Sim => SimConnectConnection.Connection;
        public LVarManager LVars => LVarManager.Connection;

        public void Initialize()
        {
            Templates.Load();
            Store.Load();

            var added = Store.SeedFromTemplates(Templates, Settings.KnownTemplateIds);
            if (added > 0)
            {
                logger.Info("Added {0} profile templates to the library", added);
            }
            Store.EnsureDefaultProfile();
            Settings.Save();

            Store.ProfilesChanged += (s, e) => OnUi(() =>
            {
                RaisePropertyChanged(nameof(Profiles));
                Reapply();
            });
            Store.StartWatching();

            Sim.OnAircraftLoaded += (s, e) => OnUi(() => Aircraft = e.Aircraft);
            Sim.OnSimStateChanged += (s, e) => OnUi(() => HandleSimStateChanged(e.SimState));
            Sim.OnInMainMenuChanged += (s, e) => OnUi(() =>
            {
                GlobalLights.SimulatorInMainMenu = Sim.InMainMenu;
                RaisePropertyChanged(nameof(InMainMenu));
            });
            Sim.OnSimulatorInfoChanged += (s, e) => OnUi(() => RaisePropertyChanged(nameof(SimulatorName)));
            LVars.OnModuleStatusChanged += (s, e) => OnUi(() => RaisePropertyChanged(nameof(ModuleStatus)));
            LVars.OnLVarListChanged += (s, e) => OnUi(HandleLVarListChanged);
            Usb.PropertyChanged += (s, e) => OnUi(() => RaisePropertyChanged(nameof(BravoPresent)));

            GlobalLights.SimulatorInMainMenu = Sim.InMainMenu;

            Reapply();
        }

        /// <summary>
        /// Raised when the simulator exits, so the app can decide whether to exit too.
        /// </summary>
        public event EventHandler SimulatorExited;

        private void OnUi(Action action)
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action);
            }
        }

        #region Simulator state

        private SimState simState = SimState.SimExited;
        public SimState SimState
        {
            get => simState;
            private set
            {
                SetProperty(ref simState, value);
                RaisePropertyChanged(nameof(IsSimConnected));
            }
        }

        public bool IsSimConnected => Sim.IsConnected;
        public string SimulatorName => Sim.SimulatorName;
        public bool InMainMenu => Sim.InMainMenu;
        public bool BravoPresent => Usb.BravoPresent;
        public LVarModuleStatus ModuleStatus => LVars.ModuleStatus;
        public int LVarCount => LVars.LVarList.Count;

        private void HandleSimStateChanged(SimState state)
        {
            logger.Debug("SimState {0}", state);
            SimState = state;
            GlobalLights.SimulatorConnected = state == SimState.SimRunning;
            RaisePropertyChanged(nameof(SimulatorName));

            if (state == SimState.SimExited)
            {
                SimulatorExited?.Invoke(this, EventArgs.Empty);
            }
        }

        private void HandleLVarListChanged()
        {
            RaisePropertyChanged(nameof(LVarCount));
            CacheLVarsForAircraft();
        }

        #endregion

        #region Aircraft and profile selection

        private AircraftInfo aircraft = AircraftInfo.None;
        public AircraftInfo Aircraft
        {
            get => aircraft;
            private set
            {
                if (value.Equals(aircraft))
                {
                    return;
                }
                logger.Info("Aircraft loaded: {0}", value);
                aircraft = value;
                RaisePropertyChanged(nameof(Aircraft));
                Reapply();
            }
        }

        /// <summary>
        /// Pretends an aircraft is loaded. Only used when rendering screenshots without a simulator.
        /// </summary>
        internal void SetPreviewAircraft(AircraftInfo preview) => Aircraft = preview;

        public IReadOnlyList<Profile> Profiles => Store.Profiles;

        private ProfileMatch activeMatch;
        public ProfileMatch ActiveMatch
        {
            get => activeMatch;
            private set
            {
                activeMatch = value;
                RaisePropertyChanged(nameof(ActiveMatch));
                RaisePropertyChanged(nameof(ActiveProfile));
            }
        }

        public Profile ActiveProfile => activeMatch?.Profile;

        private IReadOnlyList<ResolvedLight> resolvedLights = Array.Empty<ResolvedLight>();
        public IReadOnlyList<ResolvedLight> ResolvedLights
        {
            get => resolvedLights;
            private set => SetProperty(ref resolvedLights, value);
        }

        public ResolvedLight GetResolvedLight(string lightName) => resolvedLights.FirstOrDefault(l => l.LightName == lightName);

        public string ManualProfileId
        {
            get => Settings.ManualProfileId;
            set
            {
                if (Settings.ManualProfileId == value)
                {
                    return;
                }
                Settings.ManualProfileId = value;
                Settings.Save();
                RaisePropertyChanged(nameof(ManualProfileId));
                Reapply();
            }
        }

        /// <summary>
        /// Re-chooses the profile and rebuilds the light expressions. Called whenever anything relevant changes.
        /// </summary>
        public void Reapply()
        {
            var profiles = Store.Profiles;

            // A manual selection that no longer exists reverts to automatic selection
            if (Settings.ManualProfileId != null && profiles.All(p => p.Id != Settings.ManualProfileId))
            {
                Settings.ManualProfileId = null;
                Settings.Save();
                RaisePropertyChanged(nameof(ManualProfileId));
            }

            var match = ProfileMatcher.Resolve(profiles, aircraft, Settings.ManualProfileId);
            logger.Info("Using profile '{0}' for aircraft '{1}': {2}", match.Profile?.Name, aircraft, match.Explanation);

            // Turn off the lights whilst we reconfigure everything
            GlobalLights.ReadingConfiguration = true;
            try
            {
                var resolved = LightExpressionConfig.Resolve(match.Profile, Store.DefaultProfile);
                Lights.RegisterLights(resolved.Select(r => r.LightExpression));
                ActiveMatch = match;
                ResolvedLights = resolved;
            }
            finally
            {
                GlobalLights.ReadingConfiguration = false;
            }
        }

        /// <summary>
        /// Adds the current aircraft's folder to a profile's match patterns, so that it's used for this aircraft from now on.
        /// </summary>
        public Profile AssignCurrentAircraft(Profile profile)
        {
            var pattern = SuggestedPattern(aircraft);
            if (pattern == null || profile.IsDefault)
            {
                return profile;
            }

            var copy = profile.Clone();
            if (!copy.Aircraft.Any(p => ProfileMatcher.MatchesAircraft(p, aircraft)))
            {
                copy.Aircraft.Add(pattern);
            }
            copy.Enabled = true;

            // Other profiles that claim exactly this folder would compete; leave them alone, but the most specific wins.
            var saved = Store.Save(copy);
            ManualProfileId = null;
            return saved;
        }

        /// <summary>
        /// The pattern we'd suggest for matching an aircraft: its folder, which is stable across liveries and presets.
        /// </summary>
        public static string SuggestedPattern(AircraftInfo aircraft)
        {
            if (aircraft == null || aircraft.IsEmpty)
            {
                return null;
            }
            return aircraft.Folder.Length > 0 ? aircraft.Folder : aircraft.Title;
        }

        #endregion

        #region Light test

        private bool lightTestRunning;
        public bool LightTestRunning
        {
            get => lightTestRunning;
            private set => SetProperty(ref lightTestRunning, value);
        }

        public async void RunLightTest()
        {
            if (LightTestRunning)
            {
                return;
            }
            LightTestRunning = true;
            Lights.LightTest = true;
            GlobalLights.LightTest = true;
            await Task.Delay(TimeSpan.FromSeconds(3));
            Lights.LightTest = false;
            GlobalLights.LightTest = false;
            LightTestRunning = false;
        }

        #endregion

        #region LVar cache

        // The simulator only tells us about LVars while it's running. We remember the LVars seen for each aircraft
        // so that the profile editor can still offer them when the simulator isn't running.

        private static string CacheFileFor(string folder)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(folder.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return Path.Combine(FlightSimulatorPaths.CachePath, $"lvars-{safe}.txt");
        }

        private void CacheLVarsForAircraft()
        {
            var folder = aircraft.Folder;
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(FlightSimulatorPaths.CachePath);
                File.WriteAllLines(CacheFileFor(folder), LVars.LVarList.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Failed to cache LVars");
            }
        }

        /// <summary>
        /// Gets LVar names for offering in pickers: live ones if connected, otherwise cached ones for the given aircraft folders.
        /// </summary>
        public IList<string> GetKnownLVarNames(IEnumerable<string> aircraftFolders)
        {
            var names = new HashSet<string>(LVars.LVarList, StringComparer.OrdinalIgnoreCase);
            if (names.Count > 0)
            {
                return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }

            foreach (var folder in aircraftFolders.Where(f => !string.IsNullOrWhiteSpace(f) && f.IndexOfAny(new[] { '*', '?' }) < 0))
            {
                try
                {
                    var file = CacheFileFor(folder);
                    if (File.Exists(file))
                    {
                        names.UnionWith(File.ReadAllLines(file).Where(l => l.Length > 0));
                    }
                }
                catch
                {
                }
            }
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        #endregion

        public void Dispose()
        {
            Store.Dispose();
        }
    }
}

namespace BravoLights
{
    /// <summary>
    /// Stands in for the Bravo when the app must not drive real hardware.
    /// </summary>
    internal sealed class NoBravo : BravoLights.Common.ViewModelBase, BravoLights.Common.IUsbLogic
    {
        public bool LightsEnabled { get; set; }
        public bool BravoPresent => false;
    }
}
