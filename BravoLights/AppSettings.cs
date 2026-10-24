using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BravoLights.Installation;
using NLog;

namespace BravoLights
{
    /// <summary>
    /// User preferences, stored as JSON in the user's application data folder.
    /// </summary>
    public sealed class AppSettings
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// If set, this profile is always used instead of matching the loaded aircraft.
        /// </summary>
        public string ManualProfileId { get; set; }

        /// <summary>
        /// Exit Better Bravo Lights when the simulator exits.
        /// </summary>
        public bool ExitWithSimulator { get; set; } = true;

        /// <summary>
        /// Show the main window when Better Bravo Lights is started manually (rather than by the simulator).
        /// </summary>
        public bool ShowWindowOnManualStart { get; set; } = true;

        /// <summary>
        /// Keep the main window above other windows.
        /// </summary>
        public bool AlwaysOnTop { get; set; }

        /// <summary>
        /// Whether to check GitHub for new releases.
        /// </summary>
        public bool CheckForUpdates { get; set; } = true;

        /// <summary>
        /// Templates that have been added to the user's library at some point. Templates the user deletes stay
        /// in this list so that they don't reappear.
        /// </summary>
        public HashSet<string> KnownTemplateIds { get; set; } = new();

        /// <summary>
        /// True once the old Config.ini has been offered for import.
        /// </summary>
        public bool LegacyConfigImportOffered { get; set; }

        public double WindowWidth { get; set; } = 1240;
        public double WindowHeight { get; set; } = 840;

        public static string FilePath => Path.Combine(FlightSimulatorPaths.UserDataPath, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (settings != null)
                    {
                        settings.KnownTemplateIds ??= new();
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to read settings; using defaults");
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to save settings");
            }
        }
    }
}
