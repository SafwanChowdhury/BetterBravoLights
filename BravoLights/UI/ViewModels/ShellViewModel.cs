using BravoLights.Common;

namespace BravoLights.UI.ViewModels
{
    public enum Page
    {
        Dashboard,
        Profiles,
        Scanner,
        Settings
    }

    /// <summary>
    /// The main window: navigation between the pages.
    /// </summary>
    public sealed class ShellViewModel : ViewModelBase
    {
        public ShellViewModel(AppCore core)
        {
            Core = core;
            Dashboard = new DashboardViewModel(core, this);
            Profiles = new ProfilesViewModel(core, this);
            Scanner = new ScannerViewModel(core, this);
            Settings = new SettingsViewModel(core);
            CurrentContent = Dashboard;

            ToggleThemeCommand = new RelayCommand(() => Settings.Theme = ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark);
            ThemeManager.ThemeChanged += (s, e) => RaisePropertyChanged(nameof(ThemeIcon));
        }

        public System.Windows.Input.ICommand ToggleThemeCommand { get; }

        /// <summary>
        /// Sun in dark mode (switch to light), moon in light mode (switch to dark).
        /// </summary>
        public string ThemeIcon => ThemeManager.IsDark ? "" : "";

        public AppCore Core { get; }
        public DashboardViewModel Dashboard { get; }
        public ProfilesViewModel Profiles { get; }
        public ScannerViewModel Scanner { get; }
        public SettingsViewModel Settings { get; }

        public string Title => ProgramInfo.ProductNameAndVersion;
        public string Version => ProgramInfo.VersionString;

        private Page currentPage = Page.Dashboard;
        public Page CurrentPage
        {
            get => currentPage;
            set
            {
                if (currentPage == value)
                {
                    return;
                }
                if (currentPage == Page.Profiles && !Profiles.ConfirmLeaveEditor())
                {
                    // The user cancelled; stay on the profiles page. Re-raise so the nav buttons reflect that.
                    RaisePropertyChanged(nameof(CurrentPage));
                    RaiseNavFlags();
                    return;
                }

                currentPage = value;
                CurrentContent = value switch
                {
                    Page.Profiles => Profiles,
                    Page.Scanner => Scanner,
                    Page.Settings => Settings,
                    _ => Dashboard
                };
                Scanner.IsActive = value == Page.Scanner;
                if (value == Page.Settings)
                {
                    Settings.Refresh();
                }
                RaisePropertyChanged(nameof(CurrentPage));
                RaiseNavFlags();
            }
        }

        private void RaiseNavFlags()
        {
            RaisePropertyChanged(nameof(IsDashboard));
            RaisePropertyChanged(nameof(IsProfiles));
            RaisePropertyChanged(nameof(IsScanner));
            RaisePropertyChanged(nameof(IsSettings));
        }

        // Two-way bindable flags for the navigation radio buttons
        public bool IsDashboard { get => currentPage == Page.Dashboard; set { if (value) CurrentPage = Page.Dashboard; } }
        public bool IsProfiles { get => currentPage == Page.Profiles; set { if (value) CurrentPage = Page.Profiles; } }
        public bool IsScanner { get => currentPage == Page.Scanner; set { if (value) CurrentPage = Page.Scanner; } }
        public bool IsSettings { get => currentPage == Page.Settings; set { if (value) CurrentPage = Page.Settings; } }

        private object currentContent;
        public object CurrentContent
        {
            get => currentContent;
            private set => SetProperty(ref currentContent, value);
        }

        /// <summary>
        /// Opens a profile in the editor, optionally focusing one light.
        /// </summary>
        public void ShowProfile(string profileId, string lightName = null)
        {
            CurrentPage = Page.Profiles;
            if (currentPage == Page.Profiles)
            {
                Profiles.SelectProfile(profileId, lightName);
            }
        }

        /// <summary>
        /// Opens a profile in the editor and adds a condition to one of its lights (unsaved, so the user can review it).
        /// </summary>
        public void AppendConditionToLight(string profileId, string lightName, string condition)
        {
            ShowProfile(profileId, lightName);
            if (currentPage == Page.Profiles)
            {
                Profiles.Editor?.AppendCondition(lightName, condition);
            }
        }
    }
}
