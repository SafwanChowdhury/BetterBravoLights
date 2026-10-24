using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BravoLights.Installation
{
    public enum SimVersion
    {
        MSFS2020,
        MSFS2024
    }

    /// <summary>
    /// One installed copy of Microsoft Flight Simulator (2020 or 2024, Microsoft Store or Steam).
    /// </summary>
    public sealed class SimInstallation
    {
        private static readonly Regex InstalledPackagesPathRegex = new("^\\s*InstalledPackagesPath\\s+\"(.*)\"", RegexOptions.IgnoreCase);

        private SimInstallation(SimVersion version, string distribution, string configFolder)
        {
            Version = version;
            Distribution = distribution;
            ConfigFolder = configFolder;
        }

        public SimVersion Version { get; }

        /// <summary>"Microsoft Store" or "Steam".</summary>
        public string Distribution { get; }

        /// <summary>
        /// The folder holding UserCfg.opt and exe.xml.
        /// </summary>
        public string ConfigFolder { get; }

        public string VersionName => Version == SimVersion.MSFS2024 ? "MSFS 2024" : "MSFS 2020";

        public string DisplayName => $"{VersionName} ({Distribution})";

        /// <summary>
        /// The name of the simulator's process.
        /// </summary>
        public string ProcessName => ProcessNameFor(Version);

        public static string ProcessNameFor(SimVersion version) => version == SimVersion.MSFS2024 ? "FlightSimulator2024" : "FlightSimulator";

        public static readonly string[] AllProcessNames = { "FlightSimulator", "FlightSimulator2024" };

        public string UserCfgOptPath => Path.Join(ConfigFolder, "UserCfg.opt");

        /// <summary>
        /// The exe.xml file which controls programs launched with the simulator. It may not exist yet.
        /// </summary>
        public string ExeXmlPath
        {
            get
            {
                // MSFS 2024 (Store) names the file EXE.xml. Windows doesn't care about case, but keep the existing name if there is one.
                if (Directory.Exists(ConfigFolder))
                {
                    var existing = Directory.EnumerateFiles(ConfigFolder, "*.xml")
                        .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "exe.xml", StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        return existing;
                    }
                }
                return Path.Join(ConfigFolder, Version == SimVersion.MSFS2024 ? "EXE.xml" : "exe.xml");
            }
        }

        /// <summary>
        /// Whether this simulator appears to be installed.
        /// </summary>
        public bool IsPresent => File.Exists(UserCfgOptPath);

        /// <summary>
        /// The location of the Official and Community package directories, or null if it can't be determined.
        /// </summary>
        public string PackagesPath
        {
            get
            {
                try
                {
                    foreach (var line in File.ReadLines(UserCfgOptPath))
                    {
                        var match = InstalledPackagesPathRegex.Match(line);
                        if (match.Success)
                        {
                            return match.Groups[1].Value;
                        }
                    }
                }
                catch
                {
                }
                return null;
            }
        }

        public string CommunityPath
        {
            get
            {
                var packages = PackagesPath;
                return packages == null ? null : Path.Join(packages, "Community");
            }
        }

        public string InstalledWasmModulePath
        {
            get
            {
                var community = CommunityPath;
                return community == null ? null : Path.Join(community, FlightSimulatorPaths.WasmModuleName);
            }
        }

        /// <summary>
        /// Gets all the places a simulator could be installed, whether or not one is.
        /// </summary>
        public static IReadOnlyList<SimInstallation> AllCandidates()
        {
            var local = FlightSimulatorPaths.LocalAppData;
            var roaming = FlightSimulatorPaths.AppData;

            return new[]
            {
                new SimInstallation(SimVersion.MSFS2024, "Microsoft Store", Path.Join(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache")),
                new SimInstallation(SimVersion.MSFS2024, "Steam", Path.Join(roaming, "Microsoft Flight Simulator 2024")),
                new SimInstallation(SimVersion.MSFS2020, "Microsoft Store", Path.Join(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache")),
                new SimInstallation(SimVersion.MSFS2020, "Steam", Path.Join(roaming, "Microsoft Flight Simulator")),
            };
        }

        /// <summary>
        /// Gets the simulators that are installed on this machine, newest first.
        /// </summary>
        public static IReadOnlyList<SimInstallation> Detect()
        {
            return AllCandidates().Where(s => s.IsPresent).ToList();
        }

        public override string ToString() => DisplayName;
    }
}
