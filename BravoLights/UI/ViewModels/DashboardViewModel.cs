using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using BravoLights.Common;
using BravoLights.Connections;
using BravoLights.Profiles;

namespace BravoLights.UI.ViewModels
{
    /// <summary>
    /// One light on the on-screen Bravo.
    /// </summary>
    public sealed class LightTileViewModel : ViewModelBase
    {
        public LightTileViewModel(LightInfo info)
        {
            Info = info;
        }

        public LightInfo Info { get; }
        public string Name => Info.Name;
        public string Label => Info.ShortLabel;
        public LightColour Colour => Info.Colour;

        private bool isLit;
        public bool IsLit
        {
            get => isLit;
            set => SetProperty(ref isLit, value);
        }

        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set => SetProperty(ref isSelected, value);
        }

        private LightSource source;
        public LightSource Source
        {
            get => source;
            set
            {
                SetProperty(ref source, value);
                RaisePropertyChanged(nameof(IsUnconfigured));
            }
        }

        private bool hasError;
        public bool HasError
        {
            get => hasError;
            set => SetProperty(ref hasError, value);
        }

        public bool IsUnconfigured => Source == LightSource.None;

        private string toolTip;
        public string ToolTip
        {
            get => toolTip;
            set => SetProperty(ref toolTip, value);
        }
    }

    /// <summary>
    /// An entry in the "profile to use" drop-down: either automatic selection or a specific profile.
    /// </summary>
    public sealed class ProfileChoice
    {
        public string ProfileId { get; init; }
        public string Name { get; init; }
        public override string ToString() => Name;
    }

    public sealed class DashboardViewModel : ViewModelBase
    {
        private readonly AppCore core;
        private readonly ShellViewModel shell;
        private readonly Dictionary<string, LightTileViewModel> tiles;

        public DashboardViewModel(AppCore core, ShellViewModel shell)
        {
            this.core = core;
            this.shell = shell;

            tiles = LightCatalog.All.ToDictionary(l => l.Name, l => new LightTileViewModel(l));

            AutopilotTiles = LightCatalog.All.Where(l => l.Group == LightGroup.Autopilot).Select(l => tiles[l.Name]).ToList();
            WarningTiles = LightCatalog.All.Where(l => l.Group == LightGroup.Warning).Select(l => tiles[l.Name]).ToList();
            CautionTiles = LightCatalog.All.Where(l => l.Group == LightGroup.Caution).Select(l => tiles[l.Name]).ToList();
            GearGreenTiles = new[] { LightNames.GearLGreen, LightNames.GearCGreen, LightNames.GearRGreen }.Select(n => tiles[n]).ToList();
            GearRedTiles = new[] { LightNames.GearLRed, LightNames.GearCRed, LightNames.GearRRed }.Select(n => tiles[n]).ToList();

            SelectLightCommand = new RelayCommand(p => SelectLight(p as LightTileViewModel));
            EditProfileCommand = new RelayCommand(() => shell.ShowProfile(core.ActiveProfile?.Id), () => core.ActiveProfile != null);
            EditLightCommand = new RelayCommand(() => shell.ShowProfile(core.ActiveProfile?.Id, SelectedLight?.Name), () => core.ActiveProfile != null && SelectedLight != null);
            CreateProfileForAircraftCommand = new RelayCommand(CreateProfileForAircraft, () => !core.Aircraft.IsEmpty);
            AssignAircraftCommand = new RelayCommand(p => AssignAircraft(p as Profile), p => !core.Aircraft.IsEmpty && p is Profile);
            LightTestCommand = new RelayCommand(core.RunLightTest, () => !core.LightTestRunning);
            OpenScannerCommand = new RelayCommand(() => shell.CurrentPage = Page.Scanner);

            core.PropertyChanged += Core_PropertyChanged;
            core.Lights.LightChanged += (s, name) => Application.Current?.Dispatcher.BeginInvoke(() => UpdateLit(name));
            core.GlobalLights.PropertyChanged += (s, e) => Application.Current?.Dispatcher.BeginInvoke(() => RaiseStatus());

            RefreshProfileChoices();
            RefreshTiles();
            RaiseStatus();
            SelectLight(tiles[LightNames.MasterWarning]);
        }

        public IReadOnlyList<LightTileViewModel> AutopilotTiles { get; }
        public IReadOnlyList<LightTileViewModel> WarningTiles { get; }
        public IReadOnlyList<LightTileViewModel> CautionTiles { get; }
        public IReadOnlyList<LightTileViewModel> GearGreenTiles { get; }
        public IReadOnlyList<LightTileViewModel> GearRedTiles { get; }

        public ICommand SelectLightCommand { get; }
        public ICommand EditProfileCommand { get; }
        public ICommand EditLightCommand { get; }
        public ICommand CreateProfileForAircraftCommand { get; }
        public ICommand AssignAircraftCommand { get; }
        public ICommand LightTestCommand { get; }
        public ICommand OpenScannerCommand { get; }

        private void Core_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AppCore.ResolvedLights):
                    RefreshTiles();
                    RefreshSelectedDetail();
                    break;
                case nameof(AppCore.Profiles):
                    RefreshProfileChoices();
                    break;
                case nameof(AppCore.ActiveMatch):
                case nameof(AppCore.ManualProfileId):
                    RaisePropertyChanged(nameof(ActiveProfileName));
                    RaisePropertyChanged(nameof(MatchExplanation));
                    RaisePropertyChanged(nameof(IsUsingDefaultProfile));
                    RaisePropertyChanged(nameof(SelectedChoice));
                    break;
                case nameof(AppCore.Aircraft):
                    RaisePropertyChanged(nameof(AircraftTitle));
                    RaisePropertyChanged(nameof(AircraftFolder));
                    RaisePropertyChanged(nameof(AircraftPath));
                    RaisePropertyChanged(nameof(AircraftModel));
                    RaisePropertyChanged(nameof(HasAircraft));
                    break;
            }
            RaiseStatus();
        }

        #region Status cards

        private void RaiseStatus()
        {
            RaisePropertyChanged(nameof(SimStatusText));
            RaisePropertyChanged(nameof(SimStatusDetail));
            RaisePropertyChanged(nameof(SimStatusLevel));
            RaisePropertyChanged(nameof(BravoStatusText));
            RaisePropertyChanged(nameof(BravoStatusLevel));
            RaisePropertyChanged(nameof(ModuleStatusText));
            RaisePropertyChanged(nameof(ModuleStatusDetail));
            RaisePropertyChanged(nameof(ModuleStatusLevel));
            RaisePropertyChanged(nameof(LightsStatusText));
            RaisePropertyChanged(nameof(LightsStatusLevel));
        }

        public string SimStatusText => core.SimState switch
        {
            SimState.SimRunning => "In flight",
            SimState.SimStopped when core.IsSimConnected => core.InMainMenu ? "Main menu" : "Connected (paused)",
            _ => "Not running"
        };

        public string SimStatusDetail => core.IsSimConnected ? core.SimulatorName ?? "Connected" : "Waiting for MSFS 2020 / 2024…";

        public StatusLevel SimStatusLevel => core.SimState == SimState.SimRunning ? StatusLevel.Good
            : core.IsSimConnected ? StatusLevel.Warning : StatusLevel.Neutral;

        public string BravoStatusText => core.BravoPresent ? "Connected" : "Not found";
        public StatusLevel BravoStatusLevel => core.BravoPresent ? StatusLevel.Good : StatusLevel.Bad;

        public string ModuleStatusText => core.ModuleStatus switch
        {
            LVarModuleStatus.Responding => $"{core.LVarCount} LVars",
            LVarModuleStatus.NotResponding => "Not responding",
            _ => core.IsSimConnected ? "Waiting…" : "—"
        };

        public string ModuleStatusDetail => core.ModuleStatus == LVarModuleStatus.NotResponding
            ? "Install the WASM module from Settings, then restart the sim"
            : "WASM module (needed for L: variables)";

        public StatusLevel ModuleStatusLevel => core.ModuleStatus switch
        {
            LVarModuleStatus.Responding => StatusLevel.Good,
            LVarModuleStatus.NotResponding => StatusLevel.Bad,
            _ => StatusLevel.Neutral
        };

        public string LightsStatusText => core.GlobalLights.Reason;
        public StatusLevel LightsStatusLevel => core.GlobalLights.LightsEnabled ? StatusLevel.Good : StatusLevel.Neutral;

        #endregion

        #region Aircraft and profile

        public bool HasAircraft => !core.Aircraft.IsEmpty;
        public string AircraftTitle => core.Aircraft.IsEmpty ? "No aircraft loaded" : core.Aircraft.DisplayName;
        public string AircraftFolder => core.Aircraft.Folder;
        public string AircraftPath => core.Aircraft.Path;

        /// <summary>
        /// The ICAO model and manufacturer, e.g. "B738 · Boeing", or null if the simulator didn't report them.
        /// </summary>
        public string AircraftModel
        {
            get
            {
                var parts = new[] { core.Aircraft.AtcModel, core.Aircraft.AtcType }.Where(p => !string.IsNullOrEmpty(p)).ToList();
                return parts.Count == 0 ? null : string.Join(" · ", parts);
            }
        }

        public string ActiveProfileName => core.ActiveProfile?.Name ?? "(none)";
        public string MatchExplanation => core.ActiveMatch?.Explanation;
        public bool IsUsingDefaultProfile => core.ActiveProfile?.IsDefault == true;

        public IReadOnlyList<Profile> AssignableProfiles => core.Profiles.Where(p => !p.IsDefault).ToList();

        public ObservableCollection<ProfileChoice> ProfileChoices { get; } = new();

        private void RefreshProfileChoices()
        {
            ProfileChoices.Clear();
            ProfileChoices.Add(new ProfileChoice { ProfileId = null, Name = "Automatic (match aircraft)" });
            foreach (var p in core.Profiles)
            {
                ProfileChoices.Add(new ProfileChoice { ProfileId = p.Id, Name = p.Name });
            }
            RaisePropertyChanged(nameof(SelectedChoice));
            RaisePropertyChanged(nameof(AssignableProfiles));
        }

        public ProfileChoice SelectedChoice
        {
            get => ProfileChoices.FirstOrDefault(c => c.ProfileId == core.ManualProfileId) ?? ProfileChoices.FirstOrDefault();
            set
            {
                if (value != null)
                {
                    core.ManualProfileId = value.ProfileId;
                }
            }
        }

        private void CreateProfileForAircraft()
        {
            shell.CurrentPage = Page.Profiles;
            if (shell.CurrentPage == Page.Profiles)
            {
                shell.Profiles.CreateProfileFromSim();
            }
        }

        private void AssignAircraft(Profile profile)
        {
            if (profile == null)
            {
                return;
            }
            core.AssignCurrentAircraft(profile);
        }

        #endregion

        #region Lights

        private void UpdateLit(string lightName)
        {
            if (tiles.TryGetValue(lightName, out var tile))
            {
                tile.IsLit = core.Lights.IsLit(lightName);
            }
        }

        private void RefreshTiles()
        {
            foreach (var tile in tiles.Values)
            {
                var resolved = core.GetResolvedLight(tile.Name);
                tile.IsLit = core.Lights.IsLit(tile.Name);
                tile.Source = resolved?.Source ?? LightSource.None;
                tile.HasError = resolved?.LightExpression.Expression.ErrorText != null;
                tile.ToolTip = $"{tile.Info.Label}\n{resolved?.ExpressionText ?? "OFF"}";
            }
        }

        private LightTileViewModel selectedLight;
        public LightTileViewModel SelectedLight
        {
            get => selectedLight;
            private set => SetProperty(ref selectedLight, value);
        }

        private readonly ExpressionAndVariablesViewModel monitor = new();
        public ExpressionAndVariablesViewModel Monitor => monitor;

        private void SelectLight(LightTileViewModel tile)
        {
            if (tile == null)
            {
                return;
            }
            if (selectedLight != null)
            {
                selectedLight.IsSelected = false;
            }
            SelectedLight = tile;
            tile.IsSelected = true;
            RefreshSelectedDetail();
        }

        private void RefreshSelectedDetail()
        {
            var resolved = selectedLight == null ? null : core.GetResolvedLight(selectedLight.Name);
            monitor.Monitor(resolved?.LightExpression);

            SelectedExpression = resolved?.ExpressionText;
            SelectedNote = resolved?.Note;
            SelectedInvert = resolved?.Invert == true;
            SelectedError = resolved?.LightExpression.Expression.ErrorText;
            SelectedSource = resolved?.Source switch
            {
                LightSource.Profile => $"Defined in '{core.ActiveProfile?.Name}'",
                LightSource.Default => $"Inherited from '{core.Store.DefaultProfile?.Name}'",
                _ => "Not configured – always off"
            };
            MasterEnableText = LightExpressionConfig.ResolveMasterEnable(core.ActiveProfile, core.Store.DefaultProfile);
        }

        private string selectedExpression;
        public string SelectedExpression { get => selectedExpression; private set => SetProperty(ref selectedExpression, value); }

        private string selectedNote;
        public string SelectedNote { get => selectedNote; private set => SetProperty(ref selectedNote, value); }

        private bool selectedInvert;
        public bool SelectedInvert { get => selectedInvert; private set => SetProperty(ref selectedInvert, value); }

        private string selectedError;
        public string SelectedError { get => selectedError; private set => SetProperty(ref selectedError, value); }

        private string selectedSource;
        public string SelectedSource { get => selectedSource; private set => SetProperty(ref selectedSource, value); }

        private string masterEnableText;
        public string MasterEnableText { get => masterEnableText; private set => SetProperty(ref masterEnableText, value); }

        #endregion
    }
}
