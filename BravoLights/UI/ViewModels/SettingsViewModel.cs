using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using BravoLights.Common;
using BravoLights.Installation;
using BravoLights.Profiles;
using Microsoft.Win32;

namespace BravoLights.UI.ViewModels
{
    /// <summary>
    /// One detected simulator on the Settings page.
    /// </summary>
    public sealed class SimRowViewModel : ViewModelBase
    {
        private readonly SettingsViewModel owner;

        public SimRowViewModel(SettingsViewModel owner, SimInstallation sim)
        {
            this.owner = owner;
            Sim = sim;
            InstallCommand = new RelayCommand(() => owner.RunInstallAction(() => { Installer.InstallAutoStart(sim); Installer.InstallWasmModule(sim); },
                $"Better Bravo Lights is now installed for {sim.DisplayName}.\n\nIt will start automatically with the simulator. If the simulator is running, restart it so that it loads the WASM module."));
            UninstallCommand = new RelayCommand(() => owner.RunInstallAction(() => { Installer.UninstallAutoStart(sim, out _); Installer.UninstallWasmModule(sim); },
                $"Better Bravo Lights has been removed from {sim.DisplayName}."));
            InstallWasmCommand = new RelayCommand(() => owner.RunInstallAction(() => Installer.InstallWasmModule(sim),
                $"The WASM module is now installed in\n{sim.InstalledWasmModulePath}\n\nRestart the simulator if it's running."));
            OpenCommunityCommand = new RelayCommand(() => SettingsViewModel.OpenFolder(sim.CommunityPath), () => sim.CommunityPath != null && Directory.Exists(sim.CommunityPath));
            OpenConfigCommand = new RelayCommand(() => SettingsViewModel.OpenFolder(sim.ConfigFolder));
            Refresh();
        }

        public SimInstallation Sim { get; }
        public string Name => Sim.DisplayName;
        public string ConfigFolder => Sim.ConfigFolder;
        public string CommunityPath => Sim.CommunityPath ?? "(unknown – UserCfg.opt has no InstalledPackagesPath)";

        public ICommand InstallCommand { get; }
        public ICommand UninstallCommand { get; }
        public ICommand InstallWasmCommand { get; }
        public ICommand OpenCommunityCommand { get; }
        public ICommand OpenConfigCommand { get; }

        private SimInstallationStatus status;

        public string AutoStartText
        {
            get
            {
                if (status == null) return "";
                if (!status.AutoStartEnabled) return "Not set to start with the simulator";
                return status.AutoStartIsThisCopy ? "Starts with the simulator" : $"Starts a different copy: {status.AutoStartPath}";
            }
        }

        public StatusLevel AutoStartLevel => status == null ? StatusLevel.Neutral
            : !status.AutoStartEnabled ? StatusLevel.Neutral
            : status.AutoStartIsThisCopy ? StatusLevel.Good : StatusLevel.Warning;

        public string WasmText
        {
            get
            {
                if (status == null) return "";
                if (status.InstalledWasmVersion == null) return "WASM module not installed (L: variables won't work)";
                return status.WasmUpToDate
                    ? $"WASM module {status.InstalledWasmVersion} installed"
                    : $"WASM module {status.InstalledWasmVersion} installed; {status.IncludedWasmVersion} available";
            }
        }

        public StatusLevel WasmLevel => status == null ? StatusLevel.Neutral
            : status.InstalledWasmVersion == null ? StatusLevel.Bad
            : status.WasmUpToDate ? StatusLevel.Good : StatusLevel.Warning;

        public string Problem => status?.Problem;

        public bool IsFullyInstalled => status != null && status.AutoStartEnabled && status.AutoStartIsThisCopy && status.WasmUpToDate;

        private string sharedNote;
        public string SharedNote
        {
            get => sharedNote;
            set => SetProperty(ref sharedNote, value);
        }

        public void Refresh()
        {
            status = Installer.GetStatus(Sim);
            RaisePropertyChanged(nameof(AutoStartText));
            RaisePropertyChanged(nameof(AutoStartLevel));
            RaisePropertyChanged(nameof(WasmText));
            RaisePropertyChanged(nameof(WasmLevel));
            RaisePropertyChanged(nameof(Problem));
            RaisePropertyChanged(nameof(IsFullyInstalled));
            RaisePropertyChanged(nameof(CommunityPath));
        }
    }

    public sealed class SettingsViewModel : ViewModelBase
    {
        private readonly AppCore core;

        public SettingsViewModel(AppCore core)
        {
            this.core = core;

            InstallAllCommand = new RelayCommand(() => RunInstallAction(() => Installer.Install(), null, showResultOfInstall: true), () => Sims.Count > 0);
            OpenProfilesFolderCommand = new RelayCommand(() => OpenFolder(core.Store.Directory));
            OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(FlightSimulatorPaths.BetterBravoLightsPath));
            ImportLegacyCommand = new RelayCommand(ImportLegacy);
            RestoreTemplatesCommand = new RelayCommand(RestoreTemplates);
            RefreshCommand = new RelayCommand(Refresh);
            OpenWebsiteCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://github.com/RoystonS/BetterBravoLights/wiki") { UseShellExecute = true }));

            Refresh();
            CheckForUpdate();
        }

        public ObservableCollection<SimRowViewModel> Sims { get; } = new();

        public bool NoSimsFound => Sims.Count == 0;

        public ICommand InstallAllCommand { get; }
        public ICommand OpenProfilesFolderCommand { get; }
        public ICommand OpenLogsFolderCommand { get; }
        public ICommand ImportLegacyCommand { get; }
        public ICommand RestoreTemplatesCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand OpenWebsiteCommand { get; }

        public string ProfilesFolder => core.Store.Directory;
        public string ProgramFolder => FlightSimulatorPaths.BetterBravoLightsPath;
        public string Version => ProgramInfo.ProductNameAndVersion;

        private string updateText;
        public string UpdateText
        {
            get => updateText;
            private set => SetProperty(ref updateText, value);
        }

        private async void CheckForUpdate()
        {
            if (!core.Settings.CheckForUpdates)
            {
                return;
            }
            if (await ProgramInfo.IsNewVersionAvailableAsync())
            {
                UpdateText = $"Version {await ProgramInfo.GetLatestVersionStringAsync()} is available upstream.";
            }
        }

        public void Refresh()
        {
            Sims.Clear();
            foreach (var sim in SimInstallation.Detect())
            {
                Sims.Add(new SimRowViewModel(this, sim));
            }

            // Note simulators that share a Community folder (e.g. a UserCfg.opt copied to make an older tool work)
            foreach (var group in Sims.Where(s => s.Sim.CommunityPath != null).GroupBy(s => s.Sim.CommunityPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                foreach (var row in group)
                {
                    row.SharedNote = "Shares its Community folder with " + string.Join(", ", group.Where(r => r != row).Select(r => r.Name));
                }
            }

            RaisePropertyChanged(nameof(NoSimsFound));
        }

        #region General preferences

        public bool ExitWithSimulator
        {
            get => core.Settings.ExitWithSimulator;
            set { core.Settings.ExitWithSimulator = value; core.Settings.Save(); Notify(); }
        }

        public bool ShowWindowOnManualStart
        {
            get => core.Settings.ShowWindowOnManualStart;
            set { core.Settings.ShowWindowOnManualStart = value; core.Settings.Save(); Notify(); }
        }

        public bool AlwaysOnTop
        {
            get => core.Settings.AlwaysOnTop;
            set
            {
                core.Settings.AlwaysOnTop = value;
                core.Settings.Save();
                Notify();
                if (Application.Current.MainWindow != null)
                {
                    Application.Current.MainWindow.Topmost = value;
                }
            }
        }

        public bool CheckForUpdates
        {
            get => core.Settings.CheckForUpdates;
            set { core.Settings.CheckForUpdates = value; core.Settings.Save(); Notify(); }
        }

        public AppTheme Theme
        {
            get => core.Settings.Theme;
            set
            {
                core.Settings.Theme = value;
                core.Settings.Save();
                ThemeManager.Apply(value);
                Notify(nameof(ThemeSystem));
                Notify(nameof(ThemeLight));
                Notify(nameof(ThemeDark));
            }
        }

        // Two-way bindable flags for the theme segmented control
        public bool ThemeSystem { get => Theme == AppTheme.System; set { if (value) Theme = AppTheme.System; } }
        public bool ThemeLight { get => Theme == AppTheme.Light; set { if (value) Theme = AppTheme.Light; } }
        public bool ThemeDark { get => Theme == AppTheme.Dark; set { if (value) Theme = AppTheme.Dark; } }

        private void Notify([System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            base.RaisePropertyChanged(name);
        }

        #endregion

        #region Installation

        /// <summary>
        /// Runs an installation action, coping with corrupt exe.xml files.
        /// </summary>
        public void RunInstallAction(Action action, string successMessage, bool showResultOfInstall = false, bool isRetry = false)
        {
            try
            {
                if (showResultOfInstall)
                {
                    var message = Installer.Install();
                    MessageBox.Show(message, "Better Bravo Lights", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    action();
                    if (successMessage != null)
                    {
                        MessageBox.Show(successMessage, "Better Bravo Lights", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (CorruptExeXmlException ex)
            {
                if (ex.RepairedContent != null && !isRetry)
                {
                    var answer = MessageBox.Show($"The simulator's exe.xml file is corrupt, but it looks like it can be repaired automatically.\n\n{ex.ExeXmlFilename}\n\nRepair it now? (A backup copy is kept.)",
                        "Better Bravo Lights", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer == MessageBoxResult.Yes)
                    {
                        File.Copy(ex.ExeXmlFilename, ex.ExeXmlFilename + ".corrupt-backup", true);
                        File.WriteAllText(ex.ExeXmlFilename, ex.RepairedContent);
                        RunInstallAction(action, successMessage, showResultOfInstall, isRetry: true);
                        return;
                    }
                }
                else
                {
                    new CorruptExeXmlErrorWindow { Exception = ex, Owner = Application.Current.MainWindow }.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"That didn't work: {ex.Message}", "Better Bravo Lights", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                foreach (var row in Sims)
                {
                    row.Refresh();
                }
            }
        }

        #endregion

        #region Data

        private void ImportLegacy()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import an old Better Bravo Lights Config.ini",
                Filter = "Better Bravo Lights config (*.ini)|*.ini|All files (*.*)|*.*",
            };
            var legacy = FlightSimulatorPaths.LegacyUserConfigIniPath;
            if (legacy != null && File.Exists(legacy))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(legacy);
                dialog.FileName = Path.GetFileName(legacy);
            }
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var profiles = LegacyIniImporter.ImportFile(dialog.FileName);
                var shell = (Application.Current.MainWindow?.DataContext as ShellViewModel);
                if (shell != null)
                {
                    shell.CurrentPage = Page.Profiles;
                    shell.Profiles.ImportProfiles(profiles);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't import that file: {ex.Message}", "Better Bravo Lights", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RestoreTemplates()
        {
            var added = core.Store.SeedFromTemplates(core.Templates, core.Settings.KnownTemplateIds, force: true);
            core.Settings.Save();
            MessageBox.Show(added == 0
                ? "Your library already contains every built-in template."
                : $"Added {added} built-in profile{(added == 1 ? "" : "s")} to your library.", "Better Bravo Lights");
        }

        public static void OpenFolder(string path)
        {
            try
            {
                if (path != null && Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch
            {
            }
        }

        #endregion
    }
}
