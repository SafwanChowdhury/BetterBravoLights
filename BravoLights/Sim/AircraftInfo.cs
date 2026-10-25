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
    /// We expose the top-level folder (<see cref="Folder"/>, e.g. <c>FNX_32X</c>), the full relative path
    /// (<see cref="Path"/>), the aircraft TITLE, and its ATC model/type (e.g. <c>B738</c>, <c>Boeing</c>), so that
    /// profiles can match on whichever is most useful.
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

        // ATC strings are often localisation keys, e.g. "TT:ATCCOM.AC_MODEL_B738.0.text" (MSFS 2020) or
        // "ATCCOM.AC_MODEL B738.0.text" (MSFS 2024, without the TT: prefix)
        private static readonly Regex AtcKeyRegex = new(
            @"^(TT:)?ATCCOM\.(AC_MODEL|ATC_NAME|ATC_TYPE)[ _]?(?<value>.+?)\.\d+\.text$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public AircraftInfo(string folder, string path, string title, string atcModel = "", string atcType = "")
        {
            Folder = folder ?? "";
            Path = path ?? "";
            Title = title ?? "";
            AtcModel = CleanAtcString(atcModel);
            AtcType = CleanAtcString(atcType);
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

        /// <summary>
        /// Gets the ATC MODEL simulation variable, usually the ICAO type designator, e.g. <c>B738</c> or <c>A320</c>.
        /// </summary>
        public string AtcModel { get; }

        /// <summary>
        /// Gets the ATC TYPE simulation variable, usually the manufacturer, e.g. <c>Boeing</c>.
        /// </summary>
        public string AtcType { get; }

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

        /// <summary>
        /// Turns an ATC localisation key such as <c>TT:ATCCOM.AC_MODEL_B738.0.text</c> into <c>B738</c>.
        /// </summary>
        public static string CleanAtcString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }
            var trimmed = value.Trim();
            var match = AtcKeyRegex.Match(trimmed);
            return match.Success ? match.Groups["value"].Value.Trim() : trimmed;
        }

        /// <summary>
        /// Returns a copy with the simulation-variable details (title, ATC model, ATC type) replaced. Null keeps the current value.
        /// </summary>
        public AircraftInfo WithDetails(string title = null, string atcModel = null, string atcType = null)
        {
            return new AircraftInfo(Folder, Path, title ?? Title, atcModel ?? AtcModel, atcType ?? AtcType);
        }

        public AircraftInfo WithTitle(string title) => WithDetails(title: title);

        public bool Equals(AircraftInfo other)
        {
            return other != null &&
                string.Equals(Folder, other.Folder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Title, other.Title, StringComparison.Ordinal) &&
                string.Equals(AtcModel, other.AtcModel, StringComparison.Ordinal) &&
                string.Equals(AtcType, other.AtcType, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as AircraftInfo);

        public override int GetHashCode() => HashCode.Combine(Folder.ToUpperInvariant(), Path.ToUpperInvariant(), Title, AtcModel, AtcType);

        public override string ToString() => $"{Folder} | {Path} | {Title} | {AtcModel} | {AtcType}";
    }
}
