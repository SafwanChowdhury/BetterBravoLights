using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BravoLights.Common;

namespace BravoLights.Profiles
{
    /// <summary>
    /// Converts the old single-file INI configuration (Config.ini / Config.BuiltIn.ini) into profiles.
    /// </summary>
    /// <remarks>
    /// The INI format has a [Default] section plus [Aircraft.NAME, Aircraft.NAME2] sections. Sections can be repeated
    /// and overlap, so we first compute the merged settings for each aircraft name, then group aircraft that ended
    /// up with identical settings into a single profile.
    /// </remarks>
    public static class LegacyIniImporter
    {
        private const string AircraftPrefix = "Aircraft.";
        private const string DefaultSectionName = "Default";

        private static readonly Regex SectionRegex = new(@"^\[(.*)\]$");
        private static readonly Regex KeyValueRegex = new(@"^(.*?)\s*=\s*(.*)$");

        private sealed class Setting
        {
            public string Value;
            public string Note;
        }

        private sealed class Section
        {
            public string Title;
            public List<string> Names = new();
            public int Order;
        }

        public static IList<Profile> ImportFile(string path)
        {
            return Import(File.ReadAllLines(path), Path.GetFileNameWithoutExtension(path));
        }

        public static IList<Profile> Import(string[] lines, string sourceName = null)
        {
            // Settings keyed by section name, then by key
            var settings = new Dictionary<string, Dictionary<string, Setting>>(StringComparer.OrdinalIgnoreCase);
            var sections = new List<Section>();

            var currentSectionNames = new List<string>();
            var pendingComments = new List<string>();
            var continuation = "";

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (line.StartsWith(";") || line.StartsWith("#"))
                {
                    pendingComments.Add(line);
                    continue;
                }

                if (line.EndsWith("\\"))
                {
                    continuation = (continuation + " " + line[..^1].Trim()).Trim();
                    continue;
                }

                line = (continuation + " " + line).Trim();
                continuation = "";

                if (line.Length == 0)
                {
                    pendingComments.Clear();
                    continue;
                }

                var sectionMatch = SectionRegex.Match(line);
                if (sectionMatch.Success)
                {
                    var section = new Section
                    {
                        Title = ExtractTitle(pendingComments),
                        Names = sectionMatch.Groups[1].Value.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList(),
                        Order = sections.Count
                    };
                    sections.Add(section);
                    currentSectionNames = section.Names;
                    pendingComments.Clear();
                    continue;
                }

                var kvMatch = KeyValueRegex.Match(line);
                if (kvMatch.Success)
                {
                    var key = kvMatch.Groups[1].Value.Trim();
                    var value = kvMatch.Groups[2].Value.Trim();
                    var note = ExtractNote(pendingComments);
                    pendingComments.Clear();

                    foreach (var sectionName in currentSectionNames)
                    {
                        if (!settings.TryGetValue(sectionName, out var dict))
                        {
                            dict = new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase);
                            settings[sectionName] = dict;
                        }
                        dict[key] = new Setting { Value = value, Note = note };
                    }
                }
            }

            var profiles = new List<Profile>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            settings.TryGetValue(DefaultSectionName, out var defaultSettings);
            if (defaultSettings != null)
            {
                var defaultProfile = BuildProfile(defaultSettings, null);
                defaultProfile.Id = Profile.DefaultProfileId;
                defaultProfile.Name = "Default (all aircraft)";
                defaultProfile.Description = "Used for any aircraft without its own profile, and for lights that an aircraft profile doesn't define.";
                defaultProfile.IsDefault = true;
                defaultProfile.InheritDefault = false;
                usedIds.Add(defaultProfile.Id);
                profiles.Add(defaultProfile);
            }

            // Group aircraft names with identical merged settings
            var aircraftNames = settings.Keys
                .Where(k => k.StartsWith(AircraftPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var groups = aircraftNames
                .GroupBy(name => Signature(settings[name]))
                .Select(g => g.ToList())
                // Keep file order, so that generated profiles appear in a sensible order
                .OrderBy(g => g.Min(name => FirstSectionIndex(sections, name)))
                .ToList();

            foreach (var group in groups)
            {
                var profile = BuildProfile(settings[group[0]], defaultSettings);

                var patterns = group
                    .Select(name => NormalisePattern(name[AircraftPrefix.Length..]))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                profile.Aircraft = patterns;
                profile.Name = ChooseTitle(sections, group) ?? patterns[0];
                profile.Id = MakeUniqueId(Slugify(profile.Name), usedIds);
                if (sourceName != null)
                {
                    profile.Description = $"Imported from {sourceName}.";
                }

                profiles.Add(profile);
            }

            // Different groups can end up with the same title (e.g. variants sharing a header); disambiguate them.
            foreach (var dupes in profiles.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                foreach (var p in dupes)
                {
                    p.Name = $"{p.Name} ({string.Join(", ", p.Aircraft.Take(2))}{(p.Aircraft.Count > 2 ? ", ..." : "")})";
                }
            }

            return profiles;
        }

        private static Profile BuildProfile(Dictionary<string, Setting> settings, Dictionary<string, Setting> defaultSettings)
        {
            var profile = new Profile { InheritDefault = true };
            var invert = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (key, setting) in settings)
            {
                if (key.Equals("MasterEnable", StringComparison.OrdinalIgnoreCase))
                {
                    profile.MasterEnable = setting.Value;
                    continue;
                }

                if (key.Equals("Invert", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var name in setting.Value.Split(',', ' ').Select(n => n.Trim()).Where(n => n.Length > 0))
                    {
                        invert.Add(name);
                    }
                    continue;
                }

                var lightName = LightNames.AllNames.FirstOrDefault(n => n.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (lightName == null)
                {
                    // Unknown key; ignore it, as the old code did.
                    continue;
                }

                profile.Lights[lightName] = new LightDefinition { Expression = setting.Value, Note = setting.Note };
            }

            foreach (var lightName in invert)
            {
                var canonical = LightNames.AllNames.FirstOrDefault(n => n.Equals(lightName, StringComparison.OrdinalIgnoreCase));
                if (canonical == null)
                {
                    continue;
                }

                if (!profile.Lights.TryGetValue(canonical, out var def))
                {
                    // The old format applied 'Invert' to whatever expression the light inherited, so bring that expression in.
                    Setting inherited = null;
                    defaultSettings?.TryGetValue(canonical, out inherited);
                    def = new LightDefinition { Expression = inherited?.Value ?? "OFF", Note = inherited?.Note };
                    profile.Lights[canonical] = def;
                }
                def.Invert = true;
            }

            return profile;
        }

        /// <summary>
        /// MSFS 2024 aircraft paths look like 'FNX_32X\presets\fnx\FNX_320_CFM\config'. Profiles match on the
        /// top-level folder, so we reduce those paths to just 'FNX_32X'.
        /// </summary>
        public static string NormalisePattern(string aircraftName)
        {
            var name = aircraftName.Trim();
            var slash = name.IndexOf('\\');
            return slash > 0 ? name[..slash] : name;
        }

        private static string Signature(Dictionary<string, Setting> settings)
        {
            var sb = new StringBuilder();
            foreach (var kvp in settings.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(kvp.Key.ToUpperInvariant()).Append('=').Append(kvp.Value.Value).Append('\n');
            }
            return sb.ToString();
        }

        private static int FirstSectionIndex(List<Section> sections, string name)
        {
            var section = sections.FirstOrDefault(s => s.Names.Contains(name, StringComparer.OrdinalIgnoreCase));
            return section?.Order ?? int.MaxValue;
        }

        private static string ChooseTitle(List<Section> sections, List<string> group)
        {
            // Prefer a titled section that contains exactly this group; otherwise the smallest titled section mentioning any of it.
            var groupSet = new HashSet<string>(group, StringComparer.OrdinalIgnoreCase);
            var candidates = sections
                .Where(s => s.Title != null && s.Names.Any(groupSet.Contains))
                .OrderBy(s => s.Names.All(groupSet.Contains) && s.Names.Count == groupSet.Count ? 0 : 1)
                .ThenBy(s => s.Names.Count)
                .ThenBy(s => s.Order)
                .ToList();
            return candidates.FirstOrDefault()?.Title;
        }

        private static string ExtractTitle(List<string> comments)
        {
            // ';; Title' lines introduce an aircraft section. ';;;' lines are group headings, which we ignore.
            var titleLine = comments.FirstOrDefault(c => c.StartsWith(";;") && !c.StartsWith(";;;"));
            return titleLine == null ? null : titleLine.TrimStart(';').Trim();
        }

        private static string ExtractNote(List<string> comments)
        {
            var noteLines = comments
                .Where(c => !c.StartsWith(";;"))
                .Select(c => c.TrimStart(';', '#').Trim())
                .Where(c => c.Length > 0)
                .ToList();
            return noteLines.Count == 0 ? null : string.Join(" ", noteLines);
        }

        public static string Slugify(string text)
        {
            var slug = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return slug.Length == 0 ? "profile" : slug;
        }

        public static string MakeUniqueId(string baseId, ISet<string> used)
        {
            var id = baseId;
            var n = 2;
            while (used.Contains(id))
            {
                id = $"{baseId}-{n++}";
            }
            used.Add(id);
            return id;
        }
    }
}
