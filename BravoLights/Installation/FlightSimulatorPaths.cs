using System;
using System.IO;
using System.Windows.Forms;

namespace BravoLights.Installation
{
    static class FlightSimulatorPaths
    {
        public const string WasmModuleName = "better-bravo-lights-lvar-module";

        internal static string UnitTestRoot = null;

        public static string LocalAppData =>
            UnitTestRoot == null ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.Join(UnitTestRoot, "LOCALAPPDATA");

        public static string AppData =>
            UnitTestRoot == null ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Path.Join(UnitTestRoot, "APPDATA");

        /// <summary>
        /// Gets the path that the BBL .exe is running from.
        /// </summary>
        public static string BetterBravoLightsPath
        {
            get
            {
                if (UnitTestRoot == null)
                {
                    return Application.StartupPath;
                }

                // Unit tests run alongside the main program output
                return AppContext.BaseDirectory;
            }
        }

        public static string BetterBravoLightsExePath => Path.Join(BetterBravoLightsPath, "BetterBravoLights.exe");

        /// <summary>
        /// Gets the location of the WASM module package shipped with the program.
        /// </summary>
        public static string IncludedWasmModulePath => Path.Join(BetterBravoLightsPath, "Packages", WasmModuleName);

        /// <summary>
        /// Gets the location of the read-only aircraft profile templates shipped with the program.
        /// </summary>
        public static string TemplatesPath => Path.Join(BetterBravoLightsPath, "Templates");

        public static string KnownSimVarsPath => Path.Join(BetterBravoLightsPath, "KnownSimVars.csv");

        /// <summary>
        /// Gets the per-user data directory: %APPDATA%\BetterBravoLights.
        /// </summary>
        public static string UserDataPath => Path.Join(AppData, "BetterBravoLights");

        /// <summary>
        /// Gets the directory holding the user's aircraft profiles.
        /// </summary>
        public static string ProfilesPath => Path.Join(UserDataPath, "Profiles");

        /// <summary>
        /// Gets a directory for caching data such as the LVars seen for each aircraft.
        /// </summary>
        public static string CachePath => Path.Join(UserDataPath, "Cache");

        /// <summary>
        /// Gets the location of the old single-file user configuration (pre-profiles), which lived next to the Program directory.
        /// </summary>
        public static string LegacyUserConfigIniPath
        {
            get
            {
                var parent = new DirectoryInfo(BetterBravoLightsPath).Parent;
                return parent == null ? null : Path.Combine(parent.FullName, "Config.ini");
            }
        }
    }
}
