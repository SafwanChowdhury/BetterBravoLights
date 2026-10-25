using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BravoLights.Common;
using BravoLights.Profiles;
using BravoLights.Sim;

namespace BravoLights.UI
{
    /// <summary>
    /// Shows what the simulator reports about the loaded aircraft and lets the user choose patterns from it.
    /// </summary>
    public partial class DetectAircraftWindow : Window
    {
        public sealed class SuggestionItem : ViewModelBase
        {
            public PatternSuggestion Suggestion { get; init; }
            public string Pattern => Suggestion.Pattern;
            public string Kind => Suggestion.Kind;
            public string Description => Suggestion.Description;
            public bool IsRecommended => Suggestion.IsRecommended;
            public bool IsBroad => Suggestion.IsBroad;
            public bool AlreadyAdded { get; init; }
            public bool CanSelect => !AlreadyAdded;

            private bool isChecked;
            public bool IsChecked
            {
                get => isChecked;
                set
                {
                    SetProperty(ref isChecked, value);
                    CheckedChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            public event EventHandler CheckedChanged;
        }

        private readonly AppCore core;
        private readonly ISet<string> existingPatterns;
        private readonly bool creatingProfile;
        private readonly HashSet<string> checkedPatterns = new(StringComparer.OrdinalIgnoreCase);
        private bool userHasChosen;
        private List<SuggestionItem> items = new();

        /// <param name="existingPatterns">Patterns the profile already has; they're shown but can't be added again.</param>
        /// <param name="creatingProfile">True when choosing patterns for a brand new profile.</param>
        public DetectAircraftWindow(AppCore core, IEnumerable<string> existingPatterns, bool creatingProfile)
        {
            this.core = core;
            this.existingPatterns = new HashSet<string>(existingPatterns ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            this.creatingProfile = creatingProfile;

            InitializeComponent();
            SourceInitialized += (s, e) => ThemeManager.StyleTitleBar(this);

            if (creatingProfile)
            {
                Title = "New profile from the simulator";
                IntroText.Text = "Choose how the new profile should recognise this aircraft. This updates live if you change aircraft in the sim.";
            }

            core.PropertyChanged += Core_PropertyChanged;
            Closed += (s, e) => core.PropertyChanged -= Core_PropertyChanged;

            Refresh();
        }

        /// <summary>
        /// The patterns the user chose.
        /// </summary>
        public IList<string> SelectedPatterns { get; private set; } = new List<string>();

        /// <summary>
        /// The aircraft as it was when the user confirmed, e.g. to name a new profile.
        /// </summary>
        public AircraftInfo Aircraft { get; private set; } = AircraftInfo.None;

        private void Core_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AppCore.Aircraft) or nameof(AppCore.ActiveMatch) or nameof(AppCore.SimState))
            {
                Dispatcher.BeginInvoke(Refresh);
            }
        }

        private void Refresh()
        {
            var aircraft = core.Aircraft;
            Aircraft = aircraft;
            var hasAircraft = !aircraft.IsEmpty;

            NoAircraftPanel.Visibility = hasAircraft ? Visibility.Collapsed : Visibility.Visible;
            NoAircraftText.Text = core.IsSimConnected
                ? "The simulator is connected but no aircraft is loaded yet. Load a flight and this window will fill in."
                : "Start MSFS 2020 or 2024 and load a flight. This window will fill in as soon as the aircraft loads.";
            IdentifiersPanel.Visibility = hasAircraft ? Visibility.Visible : Visibility.Collapsed;
            SuggestionsHeading.Visibility = hasAircraft ? Visibility.Visible : Visibility.Collapsed;

            BuildIdentifiers(aircraft);

            var match = core.ActiveMatch;
            CurrentProfileText.Text = !hasAircraft || match?.Profile == null
                ? ""
                : match.Pattern != null
                    ? $"Currently uses the profile '{match.Profile.Name}' (pattern '{match.Pattern}')."
                    : match.IsManual
                        ? $"Currently uses '{match.Profile.Name}', chosen manually."
                        : $"No profile matches this aircraft yet, so the default profile is used.";

            // Rebuild suggestions, keeping the user's choices where the same pattern is still offered
            foreach (var old in items)
            {
                old.CheckedChanged -= Item_CheckedChanged;
            }

            items = AircraftPatternSuggestions.For(aircraft).Select(s => new SuggestionItem
            {
                Suggestion = s,
                AlreadyAdded = existingPatterns.Contains(s.Pattern)
            }).ToList();

            foreach (var item in items)
            {
                if (item.AlreadyAdded)
                {
                    continue;
                }
                var shouldCheck = userHasChosen ? checkedPatterns.Contains(item.Pattern) : item.IsRecommended && !items.Any(i => i.AlreadyAdded);
                if (shouldCheck)
                {
                    item.IsChecked = true;
                }
                item.CheckedChanged += Item_CheckedChanged;
            }

            if (!userHasChosen)
            {
                checkedPatterns.Clear();
                checkedPatterns.UnionWith(items.Where(i => i.IsChecked).Select(i => i.Pattern));
            }

            SuggestionsList.ItemsSource = items;
            UpdateCustomMatch();
            UpdateSelection();
        }

        private void BuildIdentifiers(AircraftInfo aircraft)
        {
            IdentifiersGrid.Children.Clear();
            IdentifiersGrid.RowDefinitions.Clear();

            var rows = new (string Label, string Value)[]
            {
                ("Folder", aircraft.Folder),
                ("Path", aircraft.Path),
                ("Title", aircraft.Title),
                ("ICAO model", aircraft.AtcModel),
                ("Manufacturer", aircraft.AtcType),
            };

            for (var i = 0; i < rows.Length; i++)
            {
                IdentifiersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock { Text = rows[i].Label, Style = (Style)FindResource("Faint"), Margin = new Thickness(0, 4, 12, 4), VerticalAlignment = VerticalAlignment.Top };
                Grid.SetRow(label, i);
                IdentifiersGrid.Children.Add(label);

                var hasValue = !string.IsNullOrEmpty(rows[i].Value);
                var value = new TextBox
                {
                    Text = hasValue ? rows[i].Value : "not reported",
                    Style = (Style)FindResource("ReadOnlyText"),
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
                    FontSize = 12.5,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                if (!hasValue)
                {
                    value.SetResourceReference(ForegroundProperty, "TextFaintBrush");
                }
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                IdentifiersGrid.Children.Add(value);
            }
        }

        private void Item_CheckedChanged(object sender, EventArgs e)
        {
            var item = (SuggestionItem)sender;
            userHasChosen = true;
            if (item.IsChecked)
            {
                checkedPatterns.Add(item.Pattern);
            }
            else
            {
                checkedPatterns.Remove(item.Pattern);
            }
            UpdateSelection();
        }

        private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is SuggestionItem item && item.CanSelect)
            {
                item.IsChecked = !item.IsChecked;
            }
        }

        private void CustomBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCustomMatch();
            UpdateSelection();
        }

        private string CustomPattern => CustomBox?.Text?.Trim() ?? "";

        private void UpdateCustomMatch()
        {
            if (CustomMatchText == null)
            {
                return;
            }

            var pattern = CustomPattern;
            if (pattern.Length == 0 || core.Aircraft.IsEmpty)
            {
                CustomMatchText.Text = "";
                return;
            }

            var matches = ProfileMatcher.MatchesAircraft(pattern, core.Aircraft);
            CustomMatchText.Text = matches ? "✓ matches this aircraft" : "doesn't match this aircraft";
            CustomMatchText.SetResourceReference(ForegroundProperty, matches ? "SuccessBrush" : "TextFaintBrush");
        }

        private List<string> ChosenPatterns()
        {
            var chosen = items.Where(i => i.IsChecked && !i.AlreadyAdded).Select(i => i.Pattern).ToList();
            var custom = CustomPattern;
            if (custom.Length > 0 && !existingPatterns.Contains(custom) && !chosen.Contains(custom, StringComparer.OrdinalIgnoreCase))
            {
                chosen.Add(custom);
            }
            return chosen;
        }

        private void UpdateSelection()
        {
            if (OkButton == null)
            {
                return;
            }

            var count = ChosenPatterns().Count;
            SelectionText.Text = count == 0 ? "Nothing selected" : $"{count} pattern{(count == 1 ? "" : "s")} selected";
            OkButton.Content = creatingProfile ? "Create profile" : count <= 1 ? "Add pattern" : $"Add {count} patterns";
            OkButton.IsEnabled = count > 0 || (creatingProfile && !core.Aircraft.IsEmpty);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            SelectedPatterns = ChosenPatterns();
            Aircraft = core.Aircraft;
            DialogResult = true;
        }
    }
}
