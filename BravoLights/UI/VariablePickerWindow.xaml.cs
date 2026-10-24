using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BravoLights.Installation;

namespace BravoLights.UI
{
    public partial class VariablePickerWindow : Window
    {
        public sealed class Entry
        {
            public bool IsLVar { get; init; }
            public string Name { get; init; }
            public string Units { get; init; }
            public string Description { get; init; }
            public string Identifier => IsLVar ? $"L:{Name}" : $"A:{Name}, {Units}";
        }

        private readonly List<Entry> lvars;
        private readonly List<Entry> simVars;

        public VariablePickerWindow(AppCore core, IEnumerable<string> aircraftPatterns)
        {
            InitializeComponent();
            SourceInitialized += (s, e) => WindowHelpers.UseDarkTitleBar(this);

            var lvarNames = core.GetKnownLVarNames(aircraftPatterns);
            lvars = lvarNames.Select(n => new Entry { IsLVar = true, Name = n, Units = "", Description = null }).ToList();

            simVars = new List<Entry>();
            try
            {
                var csv = new CsvFile();
                csv.Load(FlightSimulatorPaths.KnownSimVarsPath);
                simVars = csv.Rows.Where(r => r.Length >= 4)
                    .Select(r => new Entry { IsLVar = false, Name = r[1], Units = r[2], Description = r[3] })
                    .ToList();
            }
            catch
            {
            }

            SourceText.Text = core.IsSimConnected && core.LVarCount > 0
                ? $"{lvars.Count} L: variables from the running simulator, and {simVars.Count} known A: simulation variables."
                : lvars.Count > 0
                    ? $"The simulator isn't running, so these {lvars.Count} L: variables were remembered from the last time this aircraft was flown."
                    : "The simulator isn't running and no L: variables have been seen for this profile's aircraft yet. Fly the aircraft once (or use the Variable scanner) to fill this list.";

            if (lvars.Count == 0)
            {
                SimVarsTab.IsChecked = true;
            }

            Loaded += (s, e) =>
            {
                Refresh();
                SearchBox.Focus();
            };
        }

        /// <summary>
        /// The text to insert, e.g. "L:FOO == 1".
        /// </summary>
        public string Result { get; private set; }

        private void Refresh()
        {
            if (ResultsList == null || lvars == null || simVars == null)
            {
                return;
            }
            var source = LVarsTab.IsChecked == true ? lvars : simVars;
            var terms = (SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var filtered = source.Where(e => terms.All(t =>
                e.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (e.Description?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)));
            ResultsList.ItemsSource = filtered.ToList();
            if (ResultsList.Items.Count > 0)
            {
                ResultsList.SelectedIndex = 0;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

        private void Kind_Checked(object sender, RoutedEventArgs e) => Refresh();

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Let the arrow keys move through the results while typing
            if (e.Key == Key.Down && ResultsList.SelectedIndex < ResultsList.Items.Count - 1)
            {
                ResultsList.SelectedIndex++;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && ResultsList.SelectedIndex > 0)
            {
                ResultsList.SelectedIndex--;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                e.Handled = true;
            }
        }

        private void Insert_Click(object sender, RoutedEventArgs e) => Accept();

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

        private void Accept()
        {
            if (ResultsList.SelectedItem is not Entry entry)
            {
                return;
            }
            var suffix = (ComparisonBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            Result = entry.Identifier + suffix;
            DialogResult = true;
        }
    }
}
