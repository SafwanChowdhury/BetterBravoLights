using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using BravoLights.Connections;
using BravoLights.Installation;
using BravoLights.Profiles;
using BravoLights.UI;
using BravoLights.UI.ViewModels;
using NLog;
using Forms = System.Windows.Forms;

namespace BravoLights
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private const int WM_USER_SIMCONNECT = 0x0402;

        private AppCore core;
        private ShellViewModel shell;
        private MainWindow mainWindow;
        private HwndSource messageWindow;
        private Forms.NotifyIcon notifyIcon;
        private Mutex singleInstanceMutex;
        private bool startedBySimulator;
        private bool shownTrayHint;
        private string renderRoot;

        private static bool TryInstallOrUninstall(string cmd)
        {
            try
            {
                switch (cmd)
                {
                    case "/install":
                        MessageBox.Show(Installer.Install(), "Better Bravo Lights");
                        return true;
                    case "/uninstall":
                        MessageBox.Show(Installer.Uninstall(), "Better Bravo Lights");
                        return true;
                }
            }
            catch (CorruptExeXmlException ex)
            {
                // The exe.xml file is broken. Maybe we can fix it?
                if (ex.RepairedContent != null)
                {
                    // Yes! Fix the exe.xml file, keeping a copy of the broken one
                    File.Copy(ex.ExeXmlFilename, ex.ExeXmlFilename + ".corrupt-backup", true);
                    File.WriteAllText(ex.ExeXmlFilename, ex.RepairedContent);
                    // Go try the install/uninstall again
                    return TryInstallOrUninstall(cmd);
                }

                new CorruptExeXmlErrorWindow { Exception = ex }.ShowDialog();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Operation failed: {ex.GetType().FullName}: {ex.Message}", "Better Bravo Lights", MessageBoxButton.OK, MessageBoxImage.Error);
                return true;
            }
            return false;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            logger.Info("Starting {0}", ProgramInfo.ProductNameAndVersion);
            logger.Debug("Arguments {0}", string.Join(" ", e.Args));

            DispatcherUnhandledException += (s, args) =>
            {
                logger.Error(args.Exception, "Unhandled exception");
                MessageBox.Show($"Something went wrong: {args.Exception.Message}\n\nDetails are in the log file.", "Better Bravo Lights", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            // Developer tool: convert an old INI configuration into profile files.
            // BetterBravoLights.exe /convert-ini Config.BuiltIn.ini OutputDirectory
            if (e.Args.Length == 3 && e.Args[0] == "/convert-ini")
            {
                ConvertIni(e.Args[1], e.Args[2]);
                Shutdown(0);
                return;
            }

            // Developer tool: render each page to a PNG, e.g. to check layout changes.
            // BetterBravoLights.exe /render-pages OutputDirectory
            // This runs fully isolated so it can't disturb a copy that's in use: a throwaway data folder, no single-instance
            // lock, no Bravo, no SimConnect and no tray icon.
            if (e.Args.Length == 2 && e.Args[0] == "/render-pages")
            {
                renderRoot = Path.Combine(Path.GetTempPath(), "BetterBravoLights-render-" + Guid.NewGuid().ToString("N"));
                FlightSimulatorPaths.UnitTestRoot = renderRoot;
                SimConnectConnection.Disabled = true;
                core = new AppCore(Dispatcher, useHardware: false);
                core.Initialize();
                ThemeManager.Apply(AppTheme.Dark);
                shell = new ShellViewModel(core);
                mainWindow = new MainWindow(shell);
                MainWindow = mainWindow;
                RenderPagesAsync(e.Args[1]);
                return;
            }

            if (e.Args.Length == 1)
            {
                var cmd = e.Args[0];
                if (cmd == "/startedbysimulator")
                {
                    startedBySimulator = true;
                    logger.Info("Started by simulator");
                }
                else if (TryInstallOrUninstall(cmd))
                {
                    Shutdown(0);
                    return;
                }
            }
            else
            {
                // We're being run manually. Are we in a situation where the user has previously installed BBL but now it isn't working
                // and they're running it manually? Let's check.
                var problem = Installer.InstallationProblem;
                if (problem != null)
                {
                    logger.Warn("Installation problem detected on manual startup: {0}", problem);
                    MessageBox.Show($"Better Bravo Lights has detected a problem with its installation:\n\n{problem}", "Better Bravo Lights", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            singleInstanceMutex = new Mutex(true, "BetterBravoLights.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                logger.Info("Existing copy already running");
                if (!startedBySimulator)
                {
                    MessageBox.Show("Better Bravo Lights is already running. Look for its icon in the notification area.", "Better Bravo Lights");
                }
                Shutdown(0);
                return;
            }

            // The window that receives SimConnect's messages must exist before anything can connect to the simulator
            CreateSimConnectMessageWindow();

            core = new AppCore(Dispatcher);
            core.Initialize();
            ThemeManager.Apply(core.Settings.Theme);
            core.SimulatorExited += Core_SimulatorExited;

            shell = new ShellViewModel(core);
            mainWindow = new MainWindow(shell);
            mainWindow.HiddenToTray += MainWindow_HiddenToTray;
            MainWindow = mainWindow;

            CreateNotifyIcon();
            core.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName is nameof(AppCore.SimState) or nameof(AppCore.BravoPresent) or nameof(AppCore.ActiveMatch))
                {
                    UpdateTrayIconText();
                }
            };

            OfferLegacyImport();
            OfferWasmInstallIfMissing();

            if (!startedBySimulator && core.Settings.ShowWindowOnManualStart)
            {
                mainWindow.ShowAndActivate();
            }

            // Strictly speaking we only really need to connect once we have variable-based light expressions registered,
            // but in practice we want to know if the sim has exited, even if we never use it.
            SimConnectConnection.Connection.Start();
        }

        /// <summary>
        /// SimConnect posts window messages to tell us when data is waiting. Use a hidden message-only window for that.
        /// </summary>
        private void CreateSimConnectMessageWindow()
        {
            var parameters = new HwndSourceParameters("BetterBravoLightsSimConnect")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = new IntPtr(-3) // HWND_MESSAGE
            };
            messageWindow = new HwndSource(parameters);
            messageWindow.AddHook(WndProc);
            SimConnectConnection.HWnd = messageWindow.Handle;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_USER_SIMCONNECT)
            {
                handled = true;
                SimConnectConnection.Connection.ReceiveMessage();
            }

            return IntPtr.Zero;
        }

        private void CreateNotifyIcon()
        {
            var menu = new Forms.ContextMenuStrip();
            var productLabel = new Forms.ToolStripLabel { Text = ProgramInfo.ProductNameAndVersion, Font = new Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
            menu.Items.Add(productLabel);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Dashboard", BravoLights.Properties.Resources.DebuggerImage, (s, e) => ShowPage(Page.Dashboard));
            menu.Items.Add("Aircraft profiles", null, (s, e) => ShowPage(Page.Profiles));
            menu.Items.Add("Variable scanner", BravoLights.Properties.Resources.TableImage, (s, e) => ShowPage(Page.Scanner));
            menu.Items.Add("Settings", null, (s, e) => ShowPage(Page.Settings));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Light test", null, (s, e) => core.RunLightTest());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(BravoLights.Properties.Resources.TrayIconMenuExit, BravoLights.Properties.Resources.ExitImage, (s, e) =>
            {
                logger.Debug("User requested exit");
                ExitApplication();
            });

            notifyIcon = new Forms.NotifyIcon
            {
                Icon = BravoLights.Properties.Resources.TrayIcon,
                Visible = true,
                ContextMenuStrip = menu,
            };
            notifyIcon.DoubleClick += (s, e) => ShowPage(null);
            UpdateTrayIconText();

            CheckForNewVersionAsync(productLabel);
        }

        private async void CheckForNewVersionAsync(Forms.ToolStripLabel productLabel)
        {
            if (!core.Settings.CheckForUpdates)
            {
                return;
            }
            if (await ProgramInfo.IsNewVersionAvailableAsync())
            {
                productLabel.Text += $" (new version {await ProgramInfo.GetLatestVersionStringAsync()} available)";
            }
        }

        private void ShowPage(Page? page)
        {
            Dispatcher.Invoke(() =>
            {
                if (page.HasValue)
                {
                    shell.CurrentPage = page.Value;
                }
                mainWindow.ShowAndActivate();
            });
        }

        private void MainWindow_HiddenToTray(object sender, EventArgs e)
        {
            if (!shownTrayHint)
            {
                shownTrayHint = true;
                notifyIcon.ShowBalloonTip(3000, "Better Bravo Lights", "Still running in the notification area. Right-click the icon to exit.", Forms.ToolTipIcon.Info);
            }
        }

        private void UpdateTrayIconText()
        {
            if (notifyIcon == null)
            {
                return;
            }

            string status;
            if (!core.BravoPresent)
            {
                status = "No Bravo throttle found";
            }
            else if (core.SimState == SimState.SimRunning)
            {
                status = core.ActiveProfile != null ? $"Profile: {core.ActiveProfile.Name}" : "Connected";
            }
            else
            {
                status = "Waiting for the simulator";
            }

            // NotifyIcon text is limited to 127 characters
            var text = $"Better Bravo Lights\n{status}";
            notifyIcon.Text = text.Length > 127 ? text[..127] : text;
        }

        private void Core_SimulatorExited(object sender, EventArgs e)
        {
            logger.Info("Simulator exited");
            if (core.Settings.ExitWithSimulator)
            {
                // MSFS has exited, so we will too.
                ExitApplication();
            }
        }

        /// <summary>
        /// The first time the new version runs, offer to bring across the user's old Config.ini customisations.
        /// </summary>
        private void OfferLegacyImport()
        {
            if (core.Settings.LegacyConfigImportOffered)
            {
                return;
            }
            core.Settings.LegacyConfigImportOffered = true;
            core.Settings.Save();

            var legacyPath = FlightSimulatorPaths.LegacyUserConfigIniPath;
            if (legacyPath == null || !File.Exists(legacyPath))
            {
                return;
            }

            try
            {
                var profiles = LegacyIniImporter.ImportFile(legacyPath);
                if (profiles.Count == 0)
                {
                    return;
                }

                var answer = MessageBox.Show(
                    $"Better Bravo Lights now uses a separate profile for each aircraft.\n\nYour old configuration file\n{legacyPath}\ncontains {profiles.Count} custom section(s). Import them as profiles?",
                    "Better Bravo Lights", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    foreach (var p in profiles)
                    {
                        if (p.IsDefault)
                        {
                            // The user's old [Default] overrides become a normal profile they can merge by hand
                            p.IsDefault = false;
                            p.Id = Guid.NewGuid().ToString("N");
                            p.Name = "Imported defaults (from Config.ini)";
                            p.Enabled = false;
                        }
                    }
                    core.Store.Import(profiles, ImportConflictMode.KeepBoth);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to import legacy config");
            }
        }

        private void OfferWasmInstallIfMissing()
        {
            if (startedBySimulator)
            {
                return;
            }

            try
            {
                var sims = SimInstallation.Detect();
                var missing = sims.Where(s => s.InstalledWasmModulePath != null && Installer.GetInstalledWasmModuleVersion(s) != Installer.IncludedWasmModuleVersion).ToList();
                if (missing.Count == 0)
                {
                    return;
                }

                var names = string.Join("\n", missing.Select(s => "• " + s.DisplayName));
                var answer = MessageBox.Show(
                    $"Lights that use L: variables need the Better Bravo Lights WASM module in the simulator's Community folder. It's missing or out of date for:\n\n{names}\n\nInstall it now?",
                    "Better Bravo Lights", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    Installer.InstallWasmModules(missing);
                    MessageBox.Show("The WASM module is now installed. If the simulator was already running it will need to be restarted.", "Better Bravo Lights");
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to check/install WASM module");
            }
        }

        private async void RenderPagesAsync(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            mainWindow.Left = -20000;
            mainWindow.Top = -20000;
            mainWindow.ShowInTaskbar = false;
            mainWindow.Show();

            // An example aircraft, as MSFS 2024 reports the PMDG 737-800, so pages that show aircraft details have something to show
            var previewAircraft = new Sim.AircraftInfo("PMDG 737-800", @"PMDG 737-800\presets\pmdg\PMDG 737-BBJ2 BW\config", "737-800 BB2 BW", "B738", "Boeing");

            var shots = new (string Name, Action Setup)[]
            {
                ("Dashboard", () => shell.CurrentPage = Page.Dashboard),
                ("DashboardAircraft", () => { core.SetPreviewAircraft(previewAircraft); shell.CurrentPage = Page.Dashboard; }),
                ("Profiles", () => { core.SetPreviewAircraft(Sim.AircraftInfo.None); shell.CurrentPage = Page.Profiles; }),
                ("ProfileEditor", () => shell.ShowProfile(core.Profiles.FirstOrDefault(p => !p.IsDefault && p.MasterEnable != null)?.Id)),
                ("Scanner", () => shell.CurrentPage = Page.Scanner),
                ("ScannerSimVars", () => shell.Scanner.ShowSimVars = true),
                ("Settings", () => shell.CurrentPage = Page.Settings),
            };

            var originalTheme = core.Settings.Theme;
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme);
                foreach (var (name, setup) in shots)
                {
                    setup();
                    await System.Threading.Tasks.Task.Delay(1200);
                    mainWindow.UpdateLayout();
                    SaveRender((FrameworkElement)mainWindow.Content, Path.Combine(outputDirectory, $"{name}-{theme}.png"));
                }

                // The aircraft detector dialog
                core.SetPreviewAircraft(previewAircraft);
                var detector = new DetectAircraftWindow(core, new[] { "PMDG 737-800" }, creatingProfile: false)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false
                };
                detector.Show();
                await System.Threading.Tasks.Task.Delay(1000);
                detector.UpdateLayout();
                SaveRender((FrameworkElement)detector.Content, Path.Combine(outputDirectory, $"DetectAircraft-{theme}.png"));
                detector.Close();
                core.SetPreviewAircraft(Sim.AircraftInfo.None);
            }

            ThemeManager.Apply(originalTheme);
            ExitApplication();
        }

        private void SaveRender(FrameworkElement content, string path)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                dc.DrawRectangle((System.Windows.Media.Brush)FindResource("BgBrush"), null, bounds);
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(content), null, bounds);
            }
            bitmap.Render(visual);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        private static void ConvertIni(string iniPath, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            foreach (var profile in LegacyIniImporter.ImportFile(iniPath))
            {
                profile.Description = profile.IsDefault ? profile.Description : null;
                ProfileStore.ExportProfile(profile, Path.Combine(outputDirectory, profile.Id + ProfileStore.ProfileExtension));
            }
        }

        private void ExitApplication()
        {
            logger.Info("Application exiting");
            core.GlobalLights.ApplicationExiting = true;
            if (mainWindow != null)
            {
                mainWindow.AllowClose = true;
                mainWindow.Close();
            }
            notifyIcon?.Dispose();
            notifyIcon = null;
            core.Dispose();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            logger.Debug("OnExit");
            notifyIcon?.Dispose();
            messageWindow?.Dispose();
            singleInstanceMutex?.Dispose();
            if (renderRoot != null)
            {
                try
                {
                    Directory.Delete(renderRoot, true);
                }
                catch
                {
                }
            }
            base.OnExit(e);
        }
    }
}
