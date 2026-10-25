using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BravoLights.Common;
using BravoLights.Profiles;

namespace BravoLights.UI
{
    public partial class TemplatePickerWindow : Window
    {
        public sealed class TemplateItem : ViewModelBase
        {
            public Profile Template { get; init; }
            public string Name => Template.Name;
            public bool InLibrary { get; init; }

            public string Summary => Template.IsDefault
                ? "Generic defaults for all aircraft"
                : string.Join(", ", Template.Aircraft.Take(4)) + (Template.Aircraft.Count > 4 ? ", …" : "");

            private bool isChecked;
            public bool IsChecked
            {
                get => isChecked;
                set => SetProperty(ref isChecked, value);
            }
        }

        private readonly List<TemplateItem> items;

        public TemplatePickerWindow(AppCore core)
        {
            InitializeComponent();
            SourceInitialized += (s, e) => ThemeManager.StyleTitleBar(this);

            var library = core.Store.Profiles;
            items = core.Templates.Templates.Select(t => new TemplateItem
            {
                Template = t,
                InLibrary = library.Any(p => p.Id == t.Id || p.TemplateId == t.Id)
            }).ToList();

            Refresh();
            UpdateCount();
            Loaded += (s, e) => SearchBox.Focus();
        }

        public IList<Profile> SelectedTemplates { get; private set; } = new List<Profile>();

        private void Refresh()
        {
            var terms = (SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            TemplateList.ItemsSource = items.Where(i => terms.All(t =>
                i.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                i.Template.Aircraft.Any(a => a.Contains(t, StringComparison.OrdinalIgnoreCase)))).ToList();
        }

        private void UpdateCount()
        {
            var count = items.Count(i => i.IsChecked);
            CountText.Text = count == 0 ? $"{items.Count} templates" : $"{count} selected";
            OkButton.Content = count > 1 ? $"Add {count} profiles" : "Use template";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

        private void CheckBox_Click(object sender, RoutedEventArgs e) => UpdateCount();

        private void TemplateList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (TemplateList.SelectedItem is TemplateItem item)
            {
                SelectedTemplates = new List<Profile> { item.Template };
                DialogResult = true;
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var chosen = items.Where(i => i.IsChecked).Select(i => i.Template).ToList();
            if (chosen.Count == 0 && TemplateList.SelectedItem is TemplateItem selected)
            {
                chosen.Add(selected.Template);
            }
            if (chosen.Count == 0)
            {
                return;
            }
            SelectedTemplates = chosen;
            DialogResult = true;
        }
    }
}
