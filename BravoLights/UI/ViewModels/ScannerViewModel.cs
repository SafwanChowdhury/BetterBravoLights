using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using BravoLights.Ast;
using BravoLights.Common;
using BravoLights.Connections;
using BravoLights.Installation;
using BravoLights.Profiles;

namespace BravoLights.UI.ViewModels
{
    public enum ScanKind
    {
        LVar,
        SimVar
    }

    /// <summary>
    /// A variable in the scanner, with its live value and change statistics.
    /// </summary>
    public sealed class ScanItem : ViewModelBase
    {
        private int refCount;

        public ScanItem(ScanKind kind, string name, string units, string group, string description, IVariable variable)
        {
            Kind = kind;
            Name = name;
            Units = units;
            Group = group;
            Description = description;
            Variable = variable;
        }

        public ScanKind Kind { get; }
        public string Name { get; }
        public string Units { get; }
        public string Group { get; }
        public string Description { get; }
        public IVariable Variable { get; }

        public string Identifier => Kind == ScanKind.LVar ? $"L:{Name}" : $"A:{Name}, {Units}";

        /// <summary>
        /// Whether the name can be used in an expression (the expression language is picky about LVar characters).
        /// </summary>
        public bool IsUsableInExpressions => Kind == ScanKind.SimVar || Name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == ' ' || c == ':' || c == '.');

        private object value;
        public object Value
        {
            get => value;
            private set
            {
                this.value = value;
                RaisePropertyChanged(nameof(Value));
                RaisePropertyChanged(nameof(ValueText));
                RaisePropertyChanged(nameof(HasValue));
            }
        }

        public bool HasValue => value is double;

        public double? NumericValue => value is double d ? d : null;

        public string ValueText => value switch
        {
            double d => FormatNumber(d),
            Exception ex => ex.Message,
            null => "",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };

        private double? baseline;
        public string BaselineText => baseline.HasValue ? FormatNumber(baseline.Value) : "";

        private int changeCount;
        public int ChangeCount
        {
            get => changeCount;
            private set => SetProperty(ref changeCount, value);
        }

        private DateTime? lastChanged;
        public DateTime? LastChanged
        {
            get => lastChanged;
            private set
            {
                SetProperty(ref lastChanged, value);
                RaisePropertyChanged(nameof(LastChangedText));
            }
        }

        public string LastChangedText => lastChanged?.ToLocalTime().ToString("HH:mm:ss") ?? "";

        private bool muted;
        public bool Muted
        {
            get => muted;
            set => SetProperty(ref muted, value);
        }

        /// <summary>
        /// True if this variable has changed since the snapshot (and isn't muted).
        /// </summary>
        public bool HasChanged => !muted && changeCount > 0;

        public bool IsWatched => refCount > 0;

        public void AddRef()
        {
            if (refCount++ == 0)
            {
                Variable.ValueChanged += Variable_ValueChanged;
            }
        }

        public void Release()
        {
            if (refCount == 0)
            {
                return;
            }
            if (--refCount == 0)
            {
                Variable.ValueChanged -= Variable_ValueChanged;
            }
        }

        public void ReleaseAll()
        {
            if (refCount > 0)
            {
                refCount = 1;
                Release();
            }
        }

        private void Variable_ValueChanged(object sender, ValueChangedEventArgs e)
        {
            var newValue = e.NewValue;
            if (Application.Current?.Dispatcher.CheckAccess() == false)
            {
                Application.Current.Dispatcher.BeginInvoke(() => Apply(newValue));
            }
            else
            {
                Apply(newValue);
            }
        }

        private void Apply(object newValue)
        {
            if (newValue is double d)
            {
                if (!baseline.HasValue)
                {
                    baseline = d;
                    RaisePropertyChanged(nameof(BaselineText));
                }
                else if (value is double old && old != d)
                {
                    ChangeCount++;
                    LastChanged = DateTime.UtcNow;
                    ChangedNoticed?.Invoke(this, EventArgs.Empty);
                }
            }
            Value = newValue;
        }

        /// <summary>
        /// Raised when the value changes, so the scanner knows to refresh its "changed" filter.
        /// </summary>
        public event EventHandler ChangedNoticed;

        /// <summary>
        /// Takes the current value as the new baseline and clears the change history.
        /// </summary>
        public void ResetBaseline()
        {
            baseline = value as double?;
            ChangeCount = 0;
            LastChanged = null;
            RaisePropertyChanged(nameof(BaselineText));
        }

        public string ConditionText
        {
            get
            {
                var v = NumericValue;
                var formatted = v.HasValue ? FormatNumber(v.Value) : "1";
                return $"{Identifier} == {formatted}";
            }
        }

        private static string FormatNumber(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
    }

    public sealed class ScannerViewModel : ViewModelBase
    {
        private readonly AppCore core;
        private readonly ShellViewModel shell;
        private readonly Dictionary<string, ScanItem> lvarItems = new(StringComparer.Ordinal);
        private readonly HashSet<ScanItem> visibleItems = new();
        private readonly DispatcherTimer refreshTimer;
        private bool simVarsLoaded;
        private bool filterRefreshPending;

        public ScannerViewModel(AppCore core, ShellViewModel shell)
        {
            this.core = core;
            this.shell = shell;

            ItemsView = CollectionViewSource.GetDefaultView(Items);
            ItemsView.Filter = FilterItem;

            RefreshListCommand = new RelayCommand(() => core.Sim.RequestFullLVarList(), () => core.IsSimConnected);
            SnapshotCommand = new RelayCommand(Snapshot);
            MuteNoisyCommand = new RelayCommand(MuteNoisy);
            UnmuteAllCommand = new RelayCommand(UnmuteAll);
            CopyNameCommand = new RelayCommand(p => CopyToClipboard((p as ScanItem)?.Identifier));
            CopyConditionCommand = new RelayCommand(p => CopyToClipboard((p as ScanItem)?.ConditionText));
            AddToLightCommand = new RelayCommand(AddToLight);

            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            refreshTimer.Tick += (s, e) =>
            {
                RaisePropertyChanged(nameof(PendingRequests));
                if (filterRefreshPending)
                {
                    filterRefreshPending = false;
                    RefreshFilter();
                }
            };

            core.LVars.OnLVarListChanged += (s, e) => Application.Current?.Dispatcher.BeginInvoke(RebuildLVars);
            core.PropertyChanged += Core_PropertyChanged;

            RefreshTargets();
            RebuildLVars();
        }

        public ObservableCollection<ScanItem> Items { get; } = new();
        public ICollectionView ItemsView { get; }

        public ICommand RefreshListCommand { get; }
        public ICommand SnapshotCommand { get; }
        public ICommand MuteNoisyCommand { get; }
        public ICommand UnmuteAllCommand { get; }
        public ICommand CopyNameCommand { get; }
        public ICommand CopyConditionCommand { get; }
        public ICommand AddToLightCommand { get; }

        public IReadOnlyList<LightInfo> AllLights => LightCatalog.All;

        private void Core_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AppCore.Profiles):
                    RefreshTargets();
                    break;
                case nameof(AppCore.ActiveMatch):
                    if (!targetChosenByUser)
                    {
                        RefreshTargets();
                    }
                    break;
                case nameof(AppCore.ModuleStatus):
                case nameof(AppCore.SimState):
                case nameof(AppCore.LVarCount):
                    RaisePropertyChanged(nameof(StatusText));
                    break;
            }
        }

        private bool isActive;
        /// <summary>
        /// Whether the scanner page is visible. Watching stops while it isn't, to avoid loading the simulator.
        /// </summary>
        public bool IsActive
        {
            get => isActive;
            set
            {
                if (isActive == value)
                {
                    return;
                }
                isActive = value;
                if (value)
                {
                    EnsureSimVarsLoaded();
                    refreshTimer.Start();
                    if (core.IsSimConnected && lvarItems.Count == 0)
                    {
                        core.Sim.RequestFullLVarList();
                    }
                }
                else
                {
                    refreshTimer.Stop();
                }
                ApplyWatchAll();
            }
        }

        /// <summary>
        /// Called when the scanner view goes away: stop watching the rows it was showing.
        /// </summary>
        public void ReleaseVisibleRows()
        {
            foreach (var item in visibleItems)
            {
                item.Release();
            }
            visibleItems.Clear();
        }

        #region Loading

        private void RebuildLVars()
        {
            var names = core.LVars.LVarList;
            var nameSet = new HashSet<string>(names, StringComparer.Ordinal);

            // Remove LVars that have gone (the simulator restarted)
            foreach (var gone in lvarItems.Keys.Where(n => !nameSet.Contains(n)).ToList())
            {
                var item = lvarItems[gone];
                item.ReleaseAll();
                visibleItems.Remove(item);
                Items.Remove(item);
                lvarItems.Remove(gone);
            }

            foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (lvarItems.ContainsKey(name))
                {
                    continue;
                }
                var item = new ScanItem(ScanKind.LVar, name, "", "LVar", "", new LvarExpression { LVarName = name });
                item.ChangedNoticed += Item_ChangedNoticed;
                lvarItems[name] = item;
                Items.Add(item);
            }

            ApplyWatchAll();
            RaisePropertyChanged(nameof(StatusText));
        }

        private void EnsureSimVarsLoaded()
        {
            if (simVarsLoaded)
            {
                return;
            }
            simVarsLoaded = true;

            try
            {
                var csv = new CsvFile();
                csv.Load(FlightSimulatorPaths.KnownSimVarsPath);
                foreach (var row in csv.Rows.Where(r => r.Length >= 4))
                {
                    var item = new ScanItem(ScanKind.SimVar, row[1], row[2], row[0], row[3], new SimVarExpression(row[1], row[2]));
                    item.ChangedNoticed += Item_ChangedNoticed;
                    Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Failed to load known simulation variables");
            }
        }

        private void Item_ChangedNoticed(object sender, EventArgs e)
        {
            if (onlyChanged)
            {
                filterRefreshPending = true;
            }
        }

        #endregion

        #region Filtering

        private ScanKind kind = ScanKind.LVar;
        public bool ShowLVars
        {
            get => kind == ScanKind.LVar;
            set { if (value) { kind = ScanKind.LVar; RaiseKind(); } }
        }
        public bool ShowSimVars
        {
            get => kind == ScanKind.SimVar;
            set { if (value) { kind = ScanKind.SimVar; RaiseKind(); } }
        }

        private void RaiseKind()
        {
            RaisePropertyChanged(nameof(ShowLVars));
            RaisePropertyChanged(nameof(ShowSimVars));
            RefreshFilter();
        }

        private string filterText = "";
        public string FilterText
        {
            get => filterText;
            set
            {
                SetProperty(ref filterText, value);
                RefreshFilter();
            }
        }

        private bool onlyChanged;
        public bool OnlyChanged
        {
            get => onlyChanged;
            set
            {
                SetProperty(ref onlyChanged, value);
                if (value && !watchAll && kind == ScanKind.LVar)
                {
                    // Changes can only be detected for variables we're watching
                    WatchAll = true;
                }
                RefreshFilter();
            }
        }

        private bool FilterItem(object o)
        {
            var item = (ScanItem)o;
            if (item.Kind != kind)
            {
                return false;
            }
            if (onlyChanged && !item.HasChanged)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(filterText))
            {
                return true;
            }

            // Space-separated terms must all match
            foreach (var term in filterText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                    !(item.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !(item.Group?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    return false;
                }
            }
            return true;
        }

        private void RefreshFilter()
        {
            ItemsView.Refresh();
            RaisePropertyChanged(nameof(VisibleCount));
            RaisePropertyChanged(nameof(ChangedCount));
        }

        public int VisibleCount => ItemsView.Cast<object>().Count();
        public int ChangedCount => lvarItems.Values.Count(i => i.HasChanged);

        #endregion

        #region Watching

        private bool watchAll;
        /// <summary>
        /// Subscribe to every LVar (not just the visible rows) so that changes can be detected anywhere.
        /// </summary>
        public bool WatchAll
        {
            get => watchAll;
            set
            {
                SetProperty(ref watchAll, value);
                if (!value && onlyChanged)
                {
                    onlyChanged = false;
                    RaisePropertyChanged(nameof(OnlyChanged));
                    RefreshFilter();
                }
                ApplyWatchAll();
            }
        }

        private bool watchingAll;

        private void ApplyWatchAll()
        {
            var shouldWatch = watchAll && isActive;

            if (shouldWatch)
            {
                foreach (var item in lvarItems.Values)
                {
                    if (!watchedByWatchAll.Contains(item))
                    {
                        item.AddRef();
                        watchedByWatchAll.Add(item);
                    }
                }
            }
            else if (watchingAll || watchedByWatchAll.Count > 0)
            {
                foreach (var item in watchedByWatchAll)
                {
                    item.Release();
                }
                watchedByWatchAll.Clear();
            }

            watchingAll = shouldWatch;
            RaisePropertyChanged(nameof(StatusText));
        }

        private readonly HashSet<ScanItem> watchedByWatchAll = new();

        /// <summary>
        /// Called by the view as rows scroll into and out of view: visible rows are always watched.
        /// </summary>
        public void SetRowVisible(ScanItem item, bool visible)
        {
            if (item == null)
            {
                return;
            }
            if (visible)
            {
                if (visibleItems.Add(item))
                {
                    item.AddRef();
                }
            }
            else if (visibleItems.Remove(item))
            {
                item.Release();
            }
        }

        public int PendingRequests => core.Sim.PendingRequestCount;

        public string StatusText
        {
            get
            {
                if (!core.IsSimConnected)
                {
                    return "Simulator not running. Start MSFS and load a flight to list its LVars.";
                }
                switch (core.ModuleStatus)
                {
                    case LVarModuleStatus.NotResponding:
                        return "The WASM module isn't responding, so LVars can't be listed. Install it from Settings and restart the simulator.";
                    case LVarModuleStatus.Unknown when lvarItems.Count == 0:
                        return "Asking the simulator for its LVars…";
                }
                return $"{lvarItems.Count} LVars in the simulator" + (watchingAll ? " · watching all" : "");
            }
        }

        private void Snapshot()
        {
            foreach (var item in Items)
            {
                item.ResetBaseline();
            }
            RefreshFilter();
        }

        private void MuteNoisy()
        {
            // Anything that changed without the user touching anything is noise (timers, animations, ...).
            foreach (var item in Items.Where(i => i.ChangeCount > 0))
            {
                item.Muted = true;
            }
            Snapshot();
        }

        private void UnmuteAll()
        {
            foreach (var item in Items)
            {
                item.Muted = false;
            }
            RefreshFilter();
        }

        #endregion

        #region Using variables

        public ObservableCollection<ProfileChoice> Targets { get; } = new();

        private bool targetChosenByUser;
        private ProfileChoice selectedTarget;
        public ProfileChoice SelectedTarget
        {
            get => selectedTarget;
            set
            {
                if (value == null)
                {
                    return;
                }
                if (!refreshingTargets)
                {
                    targetChosenByUser = true;
                }
                SetProperty(ref selectedTarget, value);
            }
        }

        private bool refreshingTargets;

        private void RefreshTargets()
        {
            refreshingTargets = true;
            try
            {
                var previousId = targetChosenByUser ? selectedTarget?.ProfileId : core.ActiveProfile?.Id;
                Targets.Clear();
                foreach (var p in core.Profiles)
                {
                    Targets.Add(new ProfileChoice { ProfileId = p.Id, Name = p.Name });
                }
                SelectedTarget = Targets.FirstOrDefault(t => t.ProfileId == previousId) ?? Targets.FirstOrDefault();
            }
            finally
            {
                refreshingTargets = false;
            }
        }

        private void AddToLight(object parameter)
        {
            if (parameter is not object[] args || args.Length < 2 || args[0] is not ScanItem item || args[1] is not LightInfo light)
            {
                return;
            }
            if (selectedTarget == null)
            {
                return;
            }
            if (!item.IsUsableInExpressions)
            {
                MessageBox.Show($"'{item.Name}' contains characters that can't be used in a light expression.", "Better Bravo Lights");
                return;
            }
            shell.AppendConditionToLight(selectedTarget.ProfileId, light.Name, item.ConditionText);
        }

        private static void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                // The clipboard is occasionally locked by another app
            }
        }

        #endregion
    }
}
