using System;
using System.Text.RegularExpressions;

namespace BravoLights.Sim
{
    /// <summary>
    /// Identifies the aircraft currently loaded in the simulator.
    /// </summary>
    /// <remarks>
    /// MSFS 2020 reports aircraft as <c>SimObjects\Airplanes\Asobo_C172SP_Classic\aircraft.cfg</c>.
    /// MSFS 2024 reports a preset path such as
    /// <c>SimObjects\Airplanes\FNX_32X\presets\fnx\FNX_320_CFM\config\aircraft.cfg</c>.
    /// We expose both the top-level folder (<see cref="Folder"/>, e.g. <c>FNX_32X</c>) and the
    /// full relative path (<see cref="Path"/>), plus the aircraft TITLE, so that profiles can match on whichever is most useful.
    /// </remarks>
    public sealed class AircraftInfo : IEquatable<AircraftInfo>
    {
        public static readonly AircraftInfo None = new("", "", "");

        private static readonly Regex SimObjectPathRegex = new(
            @"SimObjects\\[^\\]+\\(?<path>.+)\\[^\\]+\.cfg$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Fallback for paths without the .cfg file name on the end
        private static readonly Regex SimObjectDirRegex = new(
            @"SimObjects\\[^\\]+\\(?<path>.+?)\\?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public AircraftInfo(string folder, string path, string title)
        {
            Folder = folder ?? "";
            Path = path ?? "";
            Title = title ?? "";
        }

        /// <summary>
        /// Gets the top-level aircraft folder, e.g. <c>FNX_32X</c> or <c>PMDG 737-800</c>.
        /// </summary>
        public string Folder { get; }

        /// <summary>
        /// Gets the aircraft path below the SimObjects category directory, without the trailing .cfg file,
        /// e.g. <c>PMDG 737-800\presets\pmdg\PMDG 737-800 BW HD\config</c>.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Gets the TITLE simulation variable for the aircraft (typically the livery/variant title).
        /// </summary>
        public string Title { get; }

        public bool IsEmpty => Folder.Length == 0 && Path.Length == 0 && Title.Length == 0;

        /// <summary>
        /// The name to show the user for this aircraft.
        /// </summary>
        public string DisplayName => Title.Length > 0 ? Title : Folder.Length > 0 ? Folder : "(no aircraft)";

        public static AircraftInfo FromSimPath(string aircraftCfgPath, string title = "")
        {
            if (string.IsNullOrWhiteSpace(aircraftCfgPath))
            {
                return new AircraftInfo("", "", title);
            }

            var normalised = aircraftCfgPath.Replace('/', '\\').Trim();

            var match = SimObjectPathRegex.Match(normalised);
            if (!match.Success)
            {
                match = SimObjectDirRegex.Match(normalised);
            }

            string path;
            if (match.Success)
            {
                path = match.Groups["path"].Value;
            }
            else
            {
                // Not a path we recognise; use the containing directory (or the whole thing).
                var dir = System.IO.Path.GetDirectoryName(normalised);
                path = string.IsNullOrEmpty(dir) ? normalised : dir;
            }

            path = path.Trim('\\');
            var slash = path.IndexOf('\\');
            var folder = slash >= 0 ? path[..slash] : path;

            return new AircraftInfo(folder, path, title);
        }

        public AircraftInfo WithTitle(string title)
        {
            return new AircraftInfo(Folder, Path, title);
        }

        public bool Equals(AircraftInfo other)
        {
            return other != null &&
                string.Equals(Folder, other.Folder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Title, other.Title, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as AircraftInfo);

        public override int GetHashCode() => HashCode.Combine(Folder.ToUpperInvariant(), Path.ToUpperInvariant(), Title);

        public override string ToString() => $"{Folder} | {Path} | {Title}";
    }
}
