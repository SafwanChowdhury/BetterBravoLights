using System;
using System.Collections.Generic;
using System.Linq;
using BravoLights.Sim;

namespace BravoLights.Profiles
{
    /// <summary>
    /// A pattern that could be used to match the loaded aircraft, with an explanation of what it covers.
    /// </summary>
    public sealed class PatternSuggestion
    {
        public string Pattern { get; init; }

        /// <summary>Short label for what the pattern is based on, e.g. "Aircraft folder".</summary>
        public string Kind { get; init; }

        /// <summary>What the pattern will (and won't) match.</summary>
        public string Description { get; init; }

        /// <summary>Whether this is the pattern we'd normally recommend.</summary>
        public bool IsRecommended { get; init; }

        /// <summary>Whether the pattern is broad enough to catch other developers' aircraft too.</summary>
        public bool IsBroad { get; init; }
    }

    /// <summary>
    /// Suggests aircraft patterns from what the simulator reports about the loaded aircraft.
    /// </summary>
    public static class AircraftPatternSuggestions
    {
        private static readonly char[] FamilySeparators = { '-', ' ', '_' };

        public static IReadOnlyList<PatternSuggestion> For(AircraftInfo aircraft)
        {
            var suggestions = new List<PatternSuggestion>();
            if (aircraft == null || aircraft.IsEmpty)
            {
                return suggestions;
            }

            void Add(PatternSuggestion s)
            {
                if (!string.IsNullOrWhiteSpace(s.Pattern) && !suggestions.Any(x => string.Equals(x.Pattern, s.Pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    suggestions.Add(s);
                }
            }

            if (aircraft.Folder.Length > 0)
            {
                Add(new PatternSuggestion
                {
                    Pattern = aircraft.Folder,
                    Kind = "Aircraft folder",
                    Description = "Every livery and variant installed in this aircraft's folder. Usually the best choice.",
                    IsRecommended = true
                });

                var family = FamilyPattern(aircraft.Folder);
                if (family != null)
                {
                    Add(new PatternSuggestion
                    {
                        Pattern = family,
                        Kind = "Aircraft family",
                        Description = $"Every aircraft folder starting with '{family.TrimEnd('*')}', e.g. other variants from the same developer."
                    });
                }
            }

            if (aircraft.Path.Length > 0 && !string.Equals(aircraft.Path, aircraft.Folder, StringComparison.OrdinalIgnoreCase))
            {
                Add(new PatternSuggestion
                {
                    Pattern = aircraft.Path,
                    Kind = "Exact variant",
                    Description = "Only this exact preset/variant of the aircraft (MSFS 2024 reports one per livery or engine option)."
                });
            }

            if (aircraft.Title.Length > 0)
            {
                Add(new PatternSuggestion
                {
                    Pattern = aircraft.Title,
                    Kind = "Livery title",
                    Description = "Only aircraft with exactly this title, which usually means just this livery."
                });
            }

            if (aircraft.AtcModel.Length > 0)
            {
                Add(new PatternSuggestion
                {
                    Pattern = aircraft.AtcModel,
                    Kind = "ICAO model",
                    Description = $"Any aircraft that reports the type '{aircraft.AtcModel}', including other developers' versions. Their LVars will usually differ.",
                    IsBroad = true
                });
            }

            if (aircraft.AtcType.Length > 0)
            {
                Add(new PatternSuggestion
                {
                    Pattern = aircraft.AtcType,
                    Kind = "Manufacturer",
                    Description = $"Any aircraft from '{aircraft.AtcType}'. Very broad.",
                    IsBroad = true
                });
            }

            return suggestions;
        }

        /// <summary>
        /// Turns a folder name like 'PMDG 737-800' into a family wildcard like 'PMDG 737-*', by replacing the last
        /// separated part with '*'. Returns null if there's no sensible family.
        /// </summary>
        public static string FamilyPattern(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            var trimmed = folder.Trim();
            var index = trimmed.LastIndexOfAny(FamilySeparators);

            // Need a meaningful prefix and something after the separator to replace
            if (index < 3 || index >= trimmed.Length - 1)
            {
                return null;
            }

            return trimmed[..(index + 1)] + "*";
        }
    }
}
