using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using BravoLights.Common;
using BravoLights.Profiles;
using Microsoft.Win32;

namespace BravoLights.UI.ViewModels
{
    /// <summary>
    /// A profile in the library list.
    /// </summary>
    public sealed class ProfileListItem : ViewModelBase
    {
        public ProfileListItem(Profile profile)
        {
            Profile = profile;
        }

        public Profile Profile { get; private set; }

        public string Id => Profile.Id;
        public string Name => Profile.Name;
        public bool IsDefault => Profile.IsDefault;
        public bool IsDisabled => !Profile.Enabled;

        public string Summary
        {
            get
            {
                if (Profile.IsDefault)
                {
                    return "Fallback for all aircraft";
                }
                if (Profile.Aircraft.Count == 0)
                {
                    return "No aircraft patterns";
                }
                var first = string.Join(", ", Profile.Aircraft.Take(2));
                return Profile.Aircraft.Count > 2 ? $"{first} +{Profile.Aircraft.Count - 2}" : first;
            }
        }

        private bool isActive;
        public bool IsActive
        {
            get => isActive;
            set => SetProperty(ref isActive, value);
        }
    }

    public sealed class ProfilesViewModel : ViewModelBase
    {
        private const string ProfileFileFilter = "Better Bravo Lights profiles (*.bblprofile;*.bblpack;*.json)|*.bblprofile;*.bblpack;*.json|Legacy Better Bravo Lights config (*.ini)|*.ini|All files (*.*)|*.*";

        private readonly AppCore core;
        private readonly ShellViewModel shell;

        public ProfilesViewModel(AppCore core, ShellViewModel shell)
        {
            this.core = core;
            this.shell = shell;

            ItemsView = CollectionViewSource.GetDefaultView(Items);
            ItemsView.Filter = FilterItem;

            NewBlankCommand = new RelayCommand(NewBlank);
            NewFromTemplateCommand = new RelayCommand(NewFromTemplate);
            NewForAircraftCommand = new RelayCommand(() => CreateProfileForCurrentAircraft(core.Aircraft.DisplayName), () => !core.Aircraft.IsEmpty);
            DuplicateCommand = new RelayCommand(Duplicate, () => Editor != null && !Editor.IsNew);
            DeleteCommand = new RelayCommand(Delete, () => Editor != null && !Editor.IsDefault);
            ImportCommand = new RelayCommand(Import);
            ExportCommand = new RelayCommand(Export, () => Editor != null);
            ExportAllCommand = new RelayCommand(ExportAll);

            core.PropertyChanged += Core_PropertyChanged;
            Refresh();
        }

        public ObservableCollection<ProfileListItem> Items { get; } = new();
        public ICollectionView ItemsView { get; }

        public ICommand NewBlankCommand { get; }
        public ICommand NewFromTemplateCommand { get; }
        public ICommand NewForAircraftCommand { get; }
        public ICommand DuplicateCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand ExportAllCommand { get; }

        public string ProfilesFolder => core.Store.Directory;

        private void Core_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppCore.Profiles))
            {
                Refresh();
            }
            else if (e.PropertyName == nameof(AppCore.ActiveMatch))
            {
                UpdateActive();
            }
        }

        private string filterText = "";
        public string FilterText
        {
            get => filterText;
            set
            {
                SetProperty(ref filterText, value);
                ItemsView.Refresh();
            }
        }

        private bool FilterItem(object o)
        {
            if (string.IsNullOrWhiteSpace(filterText))
            {
                return true;
            }
            var item = (ProfileListItem)o;
            return item.Name.Contains(filterText, StringComparison.CurrentCultureIgnoreCase) ||
                item.Profile.Aircraft.Any(a => a.Contains(filterText, StringComparison.CurrentCultureIgnoreCase));
        }

        private void Refresh()
        {
            var selectedId = selectedItem?.Id ?? Editor?.Id;

            Items.Clear();
            foreach (var p in core.Profiles)
            {
                Items.Add(new ProfileListItem(p));
            }
            UpdateActive();

            // Keep the same profile selected
            suppressSelectionHandling = true;
            try
            {
                SelectedItem = Items.FirstOrDefault(i => i.Id == selectedId);
            }
            finally
            {
                suppressSelectionHandling = false;
            }

            // If the open profile changed on disk and the user hasn't edited it, show the new version
            if (Editor != null && !Editor.IsNew && !Editor.IsDirty)
            {
                var latest = core.Store.Find(Editor.Id);
                if (latest != null)
                {
                    Editor.Reload(latest);
                }
                else
                {
                    SetEditor(null);
                }
            }

            if (Editor == null)
            {
                if (SelectedItem != null)
                {
                    SetEditor(new ProfileEditorViewModel(core, SelectedItem.Profile));
                }
                else if (Items.Count > 0)
                {
                    SelectedItem = Items.FirstOrDefault(i => i.IsActive) ?? Items[0];
                }
            }
        }

        private void UpdateActive()
        {
            var activeId = core.ActiveProfile?.Id;
            foreach (var item in Items)
            {
                item.IsActive = item.Id == activeId;
            }
        }

        private bool suppressSelectionHandling;

        private ProfileListItem selectedItem;
        public ProfileListItem SelectedItem
        {
            get => selectedItem;
            set
            {
                if (selectedItem == value)
                {
                    return;
                }

                if (!suppressSelectionHandling && value != null && Editor != null && value.Id != Editor.Id && !ConfirmLeaveEditor())
                {
                    // Re-raise to put the list selection back
                    Application.Current.Dispatcher.BeginInvoke(() => RaisePropertyChanged(nameof(SelectedItem)));
                    return;
                }

                selectedItem = value;
                RaisePropertyChanged(nameof(SelectedItem));

                if (!suppressSelectionHandling && value != null && value.Id != Editor?.Id)
                {
                    SetEditor(new ProfileEditorViewModel(core, value.Profile));
                }
            }
        }

        private ProfileEditorViewModel editor;
        public ProfileEditorViewModel Editor
        {
            get => editor;
            private set => SetProperty(ref editor, value);
        }

        private void SetEditor(ProfileEditorViewModel newEditor)
        {
            editor?.Detach();
            Editor = newEditor;
        }

        /// <summary>
        /// If the editor has unsaved changes, asks the user what to do. Returns false if they cancel.
        /// </summary>
        public bool ConfirmLeaveEditor()
        {
            if (Editor == null || !Editor.IsDirty)
            {
                return true;
            }

            var result = MessageBox.Show(
                $"Save changes to '{Editor.Name}'?",
                "Better Bravo Lights", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            switch (result)
            {
                case MessageBoxResult.Yes:
                    Editor.Save();
                    return true;
                case MessageBoxResult.No:
                    if (Editor.IsNew)
                    {
                        SetEditor(null);
                    }
                    else
                    {
                        Editor.Reload(core.Store.Find(Editor.Id) ?? Editor.BuildProfile());
                    }
                    return true;
                default:
                    return false;
            }
        }

        public void SelectProfile(string profileId, string lightName = null)
        {
            var item = Items.FirstOrDefault(i => i.Id == profileId);
            if (item == null)
            {
                return;
            }
            FilterText = "";
            SelectedItem = item;
            if (Editor?.Id == profileId)
            {
                Editor.RequestFocusLight(lightName);
            }
        }

        private void OpenNew(Profile profile)
        {
            if (!ConfirmLeaveEditor())
            {
                return;
            }
            suppressSelectionHandling = true;
            try
            {
                SelectedItem = null;
            }
            finally
            {
                suppressSelectionHandling = false;
            }
            var newEditor = new ProfileEditorViewModel(core, profile, isNew: true);
            newEditor.Saved += (s, e) => SelectProfileQuietly(newEditor.Id);
            SetEditor(newEditor);
        }

        private void SelectProfileQuietly(string id)
        {
            suppressSelectionHandling = true;
            try
            {
                SelectedItem = Items.FirstOrDefault(i => i.Id == id);
            }
            finally
            {
                suppressSelectionHandling = false;
            }
        }

        private void NewBlank()
        {
            OpenNew(new Profile { Name = "New profile" });
        }

        public void CreateProfileForCurrentAircraft(string name)
        {
            var profile = new Profile { Name = string.IsNullOrWhiteSpace(name) ? "New profile" : name };
            var pattern = AppCore.SuggestedPattern(core.Aircraft);
            if (pattern != null)
            {
                profile.Aircraft.Add(pattern);
            }
            OpenNew(profile);
        }

        private void NewFromTemplate()
        {
            var window = new TemplatePickerWindow(core) { Owner = Application.Current.MainWindow };
            if (window.ShowDialog() != true || window.SelectedTemplates.Count == 0)
            {
                return;
            }

            if (window.SelectedTemplates.Count == 1)
            {
                // Open a single template as a new, unsaved profile so the user can tweak it first
                var template = window.SelectedTemplates[0];
                var copy = ProfileStore.Duplicate(template, template.Name);
                copy.TemplateId = template.Id;
                if (copy.IsDefault)
                {
                    copy.IsDefault = false;
                    copy.Name = template.Name + " (copy)";
                }
                OpenNew(copy);
                return;
            }

            var added = new List<Profile>();
            foreach (var template in window.SelectedTemplates)
            {
                var copy = core.Store.Find(template.Id) == null && !template.IsDefault
                    ? template.Clone()
                    : ProfileStore.Duplicate(template);
                copy.TemplateId = template.Id;
                copy.FilePath = null;
                copy.IsDefault = false;
                added.Add(core.Store.Save(copy));
                core.Settings.KnownTemplateIds.Add(template.Id);
            }
            core.Settings.Save();
            MessageBox.Show($"Added {added.Count} profiles from templates.", "Better Bravo Lights");
        }

        private void Duplicate()
        {
            OpenNew(ProfileStore.Duplicate(Editor.BuildProfile()));
        }

        private void Delete()
        {
            if (Editor == null || Editor.IsDefault)
            {
                return;
            }
            if (Editor.IsNew)
            {
                SetEditor(null);
                return;
            }
            if (MessageBox.Show($"Delete the profile '{Editor.Name}'?\n\nThis can't be undone (but you can export it first).",
                "Better Bravo Lights", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }
            var id = Editor.Id;
            SetEditor(null);
            selectedItem = null;
            core.Store.Delete(id);
        }

        private void Import()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import profiles",
                Filter = ProfileFileFilter,
                Multiselect = true
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var incoming = new List<Profile>();
            foreach (var file in dialog.FileNames)
            {
                try
                {
                    incoming.AddRange(ProfileStore.ReadImportFile(file));
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Couldn't import {Path.GetFileName(file)}:\n\n{ex.Message}", "Better Bravo Lights", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            ImportProfiles(incoming);
        }

        public void ImportProfiles(IList<Profile> incoming)
        {
            if (incoming.Count == 0)
            {
                MessageBox.Show("No profiles were found to import.", "Better Bravo Lights");
                return;
            }

            var conflicts = incoming.Where(p => core.Store.Find(p.Id) != null).ToList();
            var mode = ImportConflictMode.KeepBoth;
            if (conflicts.Count > 0)
            {
                var names = string.Join("\n", conflicts.Take(8).Select(c => "• " + (core.Store.Find(c.Id)?.Name ?? c.Name)));
                var result = MessageBox.Show(
                    $"{conflicts.Count} of the imported profiles already exist in your library:\n\n{names}{(conflicts.Count > 8 ? "\n…" : "")}\n\n" +
                    "Yes: replace the existing profiles\nNo: keep both (imports are added as copies)\nCancel: don't import",
                    "Better Bravo Lights", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.Cancel)
                {
                    return;
                }
                mode = result == MessageBoxResult.Yes ? ImportConflictMode.Replace : ImportConflictMode.KeepBoth;
            }

            if (!ConfirmLeaveEditor())
            {
                return;
            }

            var imported = core.Store.Import(incoming, mode);
            if (imported.Count > 0)
            {
                SelectProfile(imported[0].Id);
            }
            MessageBox.Show($"Imported {imported.Count} profile{(imported.Count == 1 ? "" : "s")}.", "Better Bravo Lights");
        }

        private void Export()
        {
            var profile = Editor.BuildProfile();
            var dialog = new SaveFileDialog
            {
                Title = "Export profile",
                FileName = ProfileStore.MakeFileName(profile),
                Filter = "Better Bravo Lights profile (*.bblprofile)|*.bblprofile",
                DefaultExt = ProfileStore.ProfileExtension
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            try
            {
                ProfileStore.ExportProfile(profile, dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Better Bravo Lights", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportAll()
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export all profiles",
                FileName = $"BetterBravoLights profiles {DateTime.Now:yyyy-MM-dd}{ProfileStore.PackExtension}",
                Filter = "Better Bravo Lights profile pack (*.bblpack)|*.bblpack",
                DefaultExt = ProfileStore.PackExtension
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            try
            {
                ProfileStore.ExportPack(core.Profiles, dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Better Bravo Lights", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
