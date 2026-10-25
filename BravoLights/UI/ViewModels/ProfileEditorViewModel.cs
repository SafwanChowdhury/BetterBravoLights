using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Input;
using BravoLights.Common;
using BravoLights.Profiles;

namespace BravoLights.UI.ViewModels
{
    /// <summary>
    /// One aircraft match pattern in the editor.
    /// </summary>
    public sealed class PatternItem : ViewModelBase
    {
        private readonly ProfileEditorViewModel owner;

        public PatternItem(ProfileEditorViewModel owner, string pattern)
        {
            this.owner = owner;
            this.pattern = pattern;
            RemoveCommand = new RelayCommand(() => owner.RemovePattern(this));
        }

        private string pattern;
        public string Pattern
        {
            get => pattern;
            set
            {
                SetProperty(ref pattern, value);
                owner.OnPatternsChanged();
            }
        }

        private bool matchesCurrent;
        public bool MatchesCurrentAircraft
        {
            get => matchesCurrent;
            set => SetProperty(ref matchesCurrent, value);
        }

        public ICommand RemoveCommand { get; }
    }

    /// <summary>
    /// The editor row for one light.
    /// </summary>
    public sealed class LightEditorRow : ViewModelBase
    {
        private readonly ProfileEditorViewModel owner;

        public LightEditorRow(ProfileEditorViewModel owner, LightInfo info, LightDefinition def)
        {
            this.owner = owner;
            Info = info;
            expression = def?.Expression ?? "";
            invert = def?.Invert ?? false;
            note = def?.Note ?? "";
            Validate();
        }

        public LightInfo Info { get; }
        public string Name => Info.Name;
        public string Label => Info.Label;
        public string GroupName => Info.GroupName;
        public LightColour Colour => Info.Colour;

        private string expression;
        public string Expression
        {
            get => expression;
            set
            {
                if (SetPropertyAndCheck(ref expression, value ?? "", nameof(Expression)))
                {
                    Validate();
                    RaisePropertyChanged(nameof(IsEmpty));
                    RaisePropertyChanged(nameof(StatusText));
                    owner.OnContentChanged();
                }
            }
        }

        private bool invert;
        public bool Invert
        {
            get => invert;
            set
            {
                if (SetPropertyAndCheck(ref invert, value, nameof(Invert)))
                {
                    owner.OnContentChanged();
                }
            }
        }

        private string note;
        public string Note
        {
            get => note;
            set
            {
                if (SetPropertyAndCheck(ref note, value ?? "", nameof(Note)))
                {
                    owner.OnContentChanged();
                }
            }
        }

        private bool showNote;
        public bool ShowNote
        {
            get => showNote || !string.IsNullOrWhiteSpace(note);
            set => SetProperty(ref showNote, value);
        }

        public bool IsEmpty => string.IsNullOrWhiteSpace(expression);

        private string error;
        public string Error
        {
            get => error;
            private set => SetProperty(ref error, value);
        }

        private string inheritedExpression;
        /// <summary>
        /// What this light will do if left empty: the Default profile's expression, or "Off".
        /// </summary>
        public string Placeholder
        {
            get => inheritedExpression;
            private set => SetProperty(ref inheritedExpression, value);
        }

        public string StatusText => IsEmpty ? (owner.InheritsDefault ? "inherits" : "off") : null;

        private bool isLit;
        public bool IsLit
        {
            get => isLit;
            set => SetProperty(ref isLit, value);
        }

        public void RefreshPlaceholder(Profile defaultProfile, bool inherits)
        {
            var inherited = inherits ? defaultProfile?.GetLight(Name) : null;
            Placeholder = inherited != null
                ? $"Inherited from default: {LightExpressionConfig.NormaliseExpressionText(inherited.Expression)}{(inherited.Invert ? "  (inverted)" : "")}"
                : "Not set – light stays off";
            RaisePropertyChanged(nameof(StatusText));
        }

        private void Validate()
        {
            Error = LightExpressionConfig.Validate(expression);
        }

        public LightDefinition ToDefinition()
        {
            if (string.IsNullOrWhiteSpace(expression) && string.IsNullOrWhiteSpace(note))
            {
                return null;
            }
            return new LightDefinition
            {
                Expression = string.IsNullOrWhiteSpace(expression) ? null : expression.Trim(),
                Invert = invert,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
            };
        }

        private bool SetPropertyAndCheck<T>(ref T field, T value, string name)
        {
            if (Equals(field, value))
            {
                return false;
            }
            field = value;
            RaisePropertyChanged(name);
            return true;
        }
    }

    /// <summary>
    /// Edits a working copy of one profile.
    /// </summary>
    public sealed class ProfileEditorViewModel : ViewModelBase
    {
        private readonly AppCore core;
        private Profile original;
        private string originalJson;
        private bool loading;

        public ProfileEditorViewModel(AppCore core, Profile profile, bool isNew = false)
        {
            this.core = core;
            IsNew = isNew;

            SaveCommand = new RelayCommand(() => Save(), () => IsDirty || IsNew);
            RevertCommand = new RelayCommand(Revert, () => IsDirty && !IsNew);
            AddPatternCommand = new RelayCommand(AddPattern, () => !string.IsNullOrWhiteSpace(NewPattern));
            AddCurrentAircraftCommand = new RelayCommand(AddCurrentAircraft, () => !core.Aircraft.IsEmpty && !IsDefault);
            DetectFromSimCommand = new RelayCommand(DetectFromSim, () => !IsDefault);
            UseNowCommand = new RelayCommand(() => core.ManualProfileId = original.Id, () => !IsNew);

            Lights = new ObservableCollection<LightEditorRow>();
            LightsView = CollectionViewSource.GetDefaultView(Lights);
            LightsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LightEditorRow.GroupName)));

            Load(profile);

            core.Lights.LightChanged += Lights_LightChanged;
            core.PropertyChanged += Core_PropertyChanged;
        }

        public void Detach()
        {
            core.Lights.LightChanged -= Lights_LightChanged;
            core.PropertyChanged -= Core_PropertyChanged;
        }

        private void Core_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AppCore.Aircraft):
                    UpdatePatternMatches();
                    RaisePropertyChanged(nameof(CurrentAircraftFolder));
                    break;
                case nameof(AppCore.ActiveMatch):
                    RaisePropertyChanged(nameof(IsActive));
                    RefreshLitStates();
                    break;
                case nameof(AppCore.Profiles):
                    RefreshPlaceholders();
                    break;
            }
        }

        private void Lights_LightChanged(object sender, string lightName)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var row = Lights.FirstOrDefault(r => r.Name == lightName);
                if (row != null)
                {
                    row.IsLit = IsActive && core.Lights.IsLit(lightName);
                }
            });
        }

        private void RefreshLitStates()
        {
            foreach (var row in Lights)
            {
                row.IsLit = IsActive && core.Lights.IsLit(row.Name);
            }
        }

        public string Id => original.Id;
        public bool IsNew { get; private set; }
        public bool IsDefault => original.IsDefault;

        /// <summary>
        /// Whether this profile is the one currently driving the Bravo (so the editor can show live light states).
        /// </summary>
        public bool IsActive => !IsNew && core.ActiveProfile?.Id == original.Id;

        public ICommand SaveCommand { get; }
        public ICommand RevertCommand { get; }
        public ICommand AddPatternCommand { get; }
        public ICommand AddCurrentAircraftCommand { get; }
        public ICommand DetectFromSimCommand { get; }
        public ICommand UseNowCommand { get; }

        private void Load(Profile profile)
        {
            loading = true;
            try
            {
                original = profile.Clone();
                original.Normalise();
                originalJson = Serialize(original);

                Name = original.Name;
                Author = original.Author ?? "";
                Description = original.Description ?? "";
                Enabled = original.Enabled;
                InheritsDefault = original.InheritDefault;
                MasterEnable = original.MasterEnable ?? "";

                Patterns.Clear();
                foreach (var p in original.Aircraft)
                {
                    Patterns.Add(new PatternItem(this, p));
                }

                Lights.Clear();
                foreach (var info in LightCatalog.All)
                {
                    original.Lights.TryGetValue(info.Name, out var def);
                    Lights.Add(new LightEditorRow(this, info, def));
                }
            }
            finally
            {
                loading = false;
            }

            RefreshPlaceholders();
            UpdatePatternMatches();
            RefreshLitStates();
            RaisePropertyChanged(nameof(IsDefault));
            RaisePropertyChanged(nameof(Id));
            RaisePropertyChanged(nameof(IsActive));
            UpdateDirty();
        }

        /// <summary>
        /// Reloads the editor from a newer saved copy of the profile (e.g. edited outside the app).
        /// </summary>
        public void Reload(Profile profile)
        {
            Load(profile);
        }

        #region Details

        private string name;
        public string Name
        {
            get => name;
            set { SetProperty(ref name, value); OnContentChanged(); }
        }

        private string author;
        public string Author
        {
            get => author;
            set { SetProperty(ref author, value); OnContentChanged(); }
        }

        private string description;
        public string Description
        {
            get => description;
            set { SetProperty(ref description, value); OnContentChanged(); }
        }

        private bool enabled;
        public bool Enabled
        {
            get => enabled;
            set { SetProperty(ref enabled, value); OnContentChanged(); }
        }

        private bool inheritsDefault;
        public bool InheritsDefault
        {
            get => inheritsDefault;
            set
            {
                SetProperty(ref inheritsDefault, value);
                RefreshPlaceholders();
                OnContentChanged();
            }
        }

        private string masterEnable;
        public string MasterEnable
        {
            get => masterEnable;
            set
            {
                SetProperty(ref masterEnable, value);
                MasterEnableError = LightExpressionConfig.Validate(value);
                OnContentChanged();
            }
        }

        private string masterEnableError;
        public string MasterEnableError
        {
            get => masterEnableError;
            private set => SetProperty(ref masterEnableError, value);
        }

        private string masterEnablePlaceholder;
        public string MasterEnablePlaceholder
        {
            get => masterEnablePlaceholder;
            private set => SetProperty(ref masterEnablePlaceholder, value);
        }

        #endregion

        #region Aircraft patterns

        public ObservableCollection<PatternItem> Patterns { get; } = new();

        private string newPattern = "";
        public string NewPattern
        {
            get => newPattern;
            set => SetProperty(ref newPattern, value);
        }

        public string CurrentAircraftFolder => AppCore.SuggestedPattern(core.Aircraft);

        private string matchStatus;
        public string MatchStatus
        {
            get => matchStatus;
            private set => SetProperty(ref matchStatus, value);
        }

        private bool matchesCurrentAircraft;
        public bool MatchesCurrentAircraft
        {
            get => matchesCurrentAircraft;
            private set => SetProperty(ref matchesCurrentAircraft, value);
        }

        private void AddPattern()
        {
            var p = NewPattern.Trim();
            if (p.Length == 0)
            {
                return;
            }
            if (!Patterns.Any(x => string.Equals(x.Pattern, p, StringComparison.OrdinalIgnoreCase)))
            {
                Patterns.Add(new PatternItem(this, p));
                OnPatternsChanged();
            }
            NewPattern = "";
        }

        private void AddCurrentAircraft()
        {
            var p = CurrentAircraftFolder;
            if (p != null && !Patterns.Any(x => string.Equals(x.Pattern, p, StringComparison.OrdinalIgnoreCase)))
            {
                Patterns.Add(new PatternItem(this, p));
                OnPatternsChanged();
            }
        }

        /// <summary>
        /// Shows what the simulator reports about the loaded aircraft and adds the patterns the user picks.
        /// </summary>
        private void DetectFromSim()
        {
            var window = new DetectAircraftWindow(core, Patterns.Select(p => p.Pattern), creatingProfile: false)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            if (window.ShowDialog() == true)
            {
                AddPatterns(window.SelectedPatterns);
            }
        }

        public void AddPatterns(IEnumerable<string> patterns)
        {
            var added = false;
            foreach (var p in patterns.Select(p => p?.Trim()).Where(p => !string.IsNullOrEmpty(p)))
            {
                if (!Patterns.Any(x => string.Equals(x.Pattern, p, StringComparison.OrdinalIgnoreCase)))
                {
                    Patterns.Add(new PatternItem(this, p));
                    added = true;
                }
            }
            if (added)
            {
                OnPatternsChanged();
            }
        }

        public void RemovePattern(PatternItem item)
        {
            Patterns.Remove(item);
            OnPatternsChanged();
        }

        internal void OnPatternsChanged()
        {
            UpdatePatternMatches();
            OnContentChanged();
        }

        private void UpdatePatternMatches()
        {
            var aircraft = core.Aircraft;
            foreach (var p in Patterns)
            {
                p.MatchesCurrentAircraft = ProfileMatcher.MatchesAircraft(p.Pattern, aircraft);
            }

            MatchesCurrentAircraft = Patterns.Any(p => p.MatchesCurrentAircraft);
            if (IsDefault)
            {
                MatchStatus = "The default profile is used for any aircraft that no other profile matches.";
            }
            else if (aircraft.IsEmpty)
            {
                MatchStatus = Patterns.Count == 0 ? "No aircraft patterns: this profile will only be used if chosen manually." : null;
            }
            else
            {
                MatchStatus = MatchesCurrentAircraft
                    ? $"Matches the loaded aircraft ({aircraft.Folder})."
                    : $"Doesn't match the loaded aircraft ({aircraft.Folder}).";
            }
        }

        #endregion

        #region Lights

        public ObservableCollection<LightEditorRow> Lights { get; }
        public ICollectionView LightsView { get; }

        private void RefreshPlaceholders()
        {
            var defaultProfile = core.Store.DefaultProfile;
            var inherits = !IsDefault && InheritsDefault;
            foreach (var row in Lights)
            {
                row.RefreshPlaceholder(defaultProfile, inherits);
            }

            var inheritedMaster = inherits ? defaultProfile?.MasterEnable : null;
            MasterEnablePlaceholder = string.IsNullOrWhiteSpace(inheritedMaster)
                ? "Always enabled (e.g. A:CIRCUIT GENERAL PANEL ON, bool == 1)"
                : $"Inherited from default: {inheritedMaster}";
        }

        /// <summary>
        /// Adds a condition to a light, OR-ing it with any existing expression.
        /// </summary>
        public void AppendCondition(string lightName, string condition)
        {
            var row = Lights.FirstOrDefault(r => r.Name == lightName);
            if (row == null)
            {
                return;
            }
            var existing = row.Expression?.Trim();
            row.Expression = string.IsNullOrEmpty(existing) || existing == "OFF"
                ? condition
                : $"{existing} OR {condition}";
            FocusLightRequested?.Invoke(this, lightName);
        }

        /// <summary>
        /// Asks the view to scroll to and focus a light's expression box.
        /// </summary>
        public event EventHandler<string> FocusLightRequested;

        public void RequestFocusLight(string lightName)
        {
            if (lightName != null)
            {
                FocusLightRequested?.Invoke(this, lightName);
            }
        }

        #endregion

        #region Dirty tracking and saving

        private bool isDirty;
        public bool IsDirty
        {
            get => isDirty;
            private set => SetProperty(ref isDirty, value);
        }

        public int ErrorCount => Lights.Count(l => l.Error != null) + (MasterEnableError != null ? 1 : 0);

        internal void OnContentChanged()
        {
            if (loading)
            {
                return;
            }
            UpdateDirty();
        }

        private void UpdateDirty()
        {
            IsDirty = IsNew || Serialize(BuildProfile()) != originalJson;
            RaisePropertyChanged(nameof(ErrorCount));
        }

        public Profile BuildProfile()
        {
            var p = original.Clone();
            p.Name = string.IsNullOrWhiteSpace(Name) ? "Unnamed profile" : Name.Trim();
            p.Author = Author;
            p.Description = Description;
            p.Enabled = Enabled;
            p.InheritDefault = !IsDefault && InheritsDefault;
            p.MasterEnable = MasterEnable;
            p.Aircraft = IsDefault ? new() : Patterns.Select(x => x.Pattern).ToList();
            p.Lights = Lights
                .Select(r => (r.Name, Def: r.ToDefinition()))
                .Where(x => x.Def != null)
                .ToDictionary(x => x.Name, x => x.Def);
            p.Normalise();
            return p;
        }

        private static string Serialize(Profile p)
        {
            var copy = p.Clone();
            copy.FilePath = null;
            copy.Normalise();
            // Compare lights independently of the order they were written in
            copy.Lights = copy.Lights.OrderBy(kvp => kvp.Key, StringComparer.Ordinal).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            return JsonSerializer.Serialize(copy, ProfileStore.JsonOptions);
        }

        public bool Save()
        {
            var profile = BuildProfile();
            var saved = core.Store.Save(profile);
            IsNew = false;
            Load(saved);
            Saved?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public event EventHandler Saved;

        private void Revert()
        {
            var latest = core.Store.Find(original.Id) ?? original;
            Load(latest);
        }

        #endregion
    }
}
