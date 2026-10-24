using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BravoLights.UI.ViewModels;

namespace BravoLights.UI.Views
{
    public partial class ProfilesView : UserControl
    {
        private ProfilesViewModel viewModel;
        private ProfileEditorViewModel hookedEditor;

        public ProfilesView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Unloaded += (s, e) => HookEditor(null);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (viewModel != null)
            {
                viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }
            viewModel = DataContext as ProfilesViewModel;
            if (viewModel != null)
            {
                viewModel.PropertyChanged += ViewModel_PropertyChanged;
                HookEditor(viewModel.Editor);
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProfilesViewModel.Editor))
            {
                HookEditor(viewModel.Editor);
                Dispatcher.BeginInvoke(() => EditorScroll?.ScrollToTop(), DispatcherPriority.Loaded);
            }
        }

        private void HookEditor(ProfileEditorViewModel editor)
        {
            if (hookedEditor != null)
            {
                hookedEditor.FocusLightRequested -= Editor_FocusLightRequested;
            }
            hookedEditor = editor;
            if (hookedEditor != null)
            {
                hookedEditor.FocusLightRequested += Editor_FocusLightRequested;
            }
        }

        private void Editor_FocusLightRequested(object sender, string lightName)
        {
            // Wait for the editor's controls to be generated before looking for the light's text box
            Dispatcher.BeginInvoke(() =>
            {
                var box = WindowHelpers.FindDescendants<TextBox>(EditorScroll)
                    .FirstOrDefault(t => t.Name == "expr" && (t.Tag as string) == lightName);
                if (box == null)
                {
                    return;
                }
                box.BringIntoView(new Rect(0, -60, box.ActualWidth, box.ActualHeight + 120));
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }, DispatcherPriority.Loaded);
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            button.ContextMenu.DataContext = DataContext;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        private void NewPatternBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && viewModel?.Editor?.AddPatternCommand.CanExecute(null) == true)
            {
                viewModel.Editor.AddPatternCommand.Execute(null);
                e.Handled = true;
            }
        }

        private void PickVariable_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not TextBox target || viewModel?.Editor == null)
            {
                return;
            }

            var core = ((ShellViewModel)Application.Current.MainWindow.DataContext).Core;
            var aircraftPatterns = viewModel.Editor.Patterns.Select(p => p.Pattern).ToList();
            var picker = new VariablePickerWindow(core, aircraftPatterns) { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() != true || string.IsNullOrEmpty(picker.Result))
            {
                return;
            }

            InsertAtCaret(target, picker.Result);
        }

        /// <summary>
        /// Inserts text at the caret, adding spaces and an 'OR' so that the result is still a valid expression.
        /// </summary>
        private static void InsertAtCaret(TextBox box, string text)
        {
            var existing = box.Text ?? "";
            var caret = box.IsKeyboardFocusWithin || box.CaretIndex > 0 ? box.CaretIndex : existing.Length;
            if (box.SelectionLength > 0)
            {
                existing = existing.Remove(box.SelectionStart, box.SelectionLength);
                caret = box.SelectionStart;
            }

            var before = existing[..caret];
            var after = existing[caret..];

            // If we're appending to the end of a complete expression, join with OR
            var trimmedBefore = before.TrimEnd();
            var needsJoin = after.Trim().Length == 0 && trimmedBefore.Length > 0 &&
                !trimmedBefore.EndsWith("(") &&
                !trimmedBefore.EndsWith("AND", StringComparison.OrdinalIgnoreCase) &&
                !trimmedBefore.EndsWith("OR", StringComparison.OrdinalIgnoreCase) &&
                !trimmedBefore.EndsWith("NOT", StringComparison.OrdinalIgnoreCase) &&
                "=<>!&|".IndexOf(trimmedBefore[^1]) < 0;

            string insert;
            if (needsJoin)
            {
                insert = " OR " + text;
                before = trimmedBefore;
            }
            else
            {
                insert = (before.Length > 0 && !char.IsWhiteSpace(before[^1]) && before[^1] != '(' ? " " : "") + text;
            }

            box.Text = before + insert + after;
            box.CaretIndex = before.Length + insert.Length;
            box.Focus();

            // Make sure the binding sees the change immediately
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }
}
