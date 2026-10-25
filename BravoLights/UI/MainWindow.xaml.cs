using System;
using System.ComponentModel;
using System.Windows;
using BravoLights.UI.ViewModels;

namespace BravoLights.UI
{
    public partial class MainWindow : Window
    {
        private readonly AppCore core;

        public MainWindow(ShellViewModel shell)
        {
            core = shell.Core;
            DataContext = shell;
            InitializeComponent();

            Width = Math.Max(MinWidth, core.Settings.WindowWidth);
            Height = Math.Max(MinHeight, core.Settings.WindowHeight);
            Topmost = core.Settings.AlwaysOnTop;

            SourceInitialized += (s, e) => ThemeManager.StyleTitleBar(this);
        }

        public ShellViewModel Shell => (ShellViewModel)DataContext;

        /// <summary>
        /// Set when the application is really exiting, so that closing isn't turned into hiding.
        /// </summary>
        public bool AllowClose { get; set; }

        public event EventHandler HiddenToTray;

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (WindowState == WindowState.Normal)
            {
                core.Settings.WindowWidth = Width;
                core.Settings.WindowHeight = Height;
                core.Settings.Save();
            }

            if (AllowClose)
            {
                return;
            }

            if (!Shell.Profiles.ConfirmLeaveEditor())
            {
                e.Cancel = true;
                return;
            }

            // Closing the window just hides it; Better Bravo Lights keeps running in the notification area.
            e.Cancel = true;
            Shell.Scanner.IsActive = false;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }

        public void ShowAndActivate()
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Shell.Scanner.IsActive = Shell.CurrentPage == Page.Scanner;
            Activate();
        }
    }
}
