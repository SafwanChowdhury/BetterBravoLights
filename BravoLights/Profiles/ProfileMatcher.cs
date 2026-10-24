using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BravoLights.Sim;

namespace BravoLights.Profiles
{
    /// <summary>
    /// The result of choosing a profile for an aircraft.
    /// </summary>
    public sealed class ProfileMatch
    {
        public ProfileMatch(Profile profile, string pattern, string matchedOn, bool isManual)
        {
            Profile = profile;
            Pattern = pattern;
            MatchedOn = matchedOn;
            IsManual = isManual;
        }

        public Profile Profile { get; }

        /// <summary>
        /// The pattern that matched, or null if the default profile was used or the profile was chosen manually.
        /// </summary>
        public string Pattern { get; }

        /// <summary>
        /// Which aspect of the aircraft matched: "folder", "path" or "title".
        /// </summary>
        public string MatchedOn { get; }

        public bool IsManual { get; }

        public string Explanation
        {
            get
            {
                if (Profile == null)
                {
                    return "No profile";
                }
                if (IsManual)
                {
                    return "Selected manually";
                }
                if (Pattern == null)
                {
                    return "No aircraft-specific profile; using the default";
                }
                return $"Matched aircraft {MatchedOn} with pattern '{Pattern}'";
            }
        }
    }

    public static class ProfileMatcher
    {
        /// <summary>
        /// Tests whether a single wildcard pattern matches a value (case-insensitive).
        /// </summary>
        public static bool IsMatch(string pattern, string value)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(value))
            {
                return false;
            }

            if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
            {
                return string.Equals(pattern.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            var regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value.Trim(), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Computes how specific a match is. Exact (wildcard-free) patterns beat wildcard patterns, and longer
        /// literal text beats shorter, so that e.g. 'PMDG 737-800' beats 'PMDG 737-*' which beats 'PMDG*'.
        /// </summary>
        private static int Specificity(string pattern)
        {
            var literalChars = pattern.Count(c => c != '*' && c != '?');
            var exact = pattern.IndexOfAny(new[] { '*', '?' }) < 0;
            return (exact ? 10000 : 0) + literalChars;
        }

        /// <summary>
        /// Tests a profile against an aircraft, returning the best pattern and what it matched on.
        /// </summary>
        public static bool TryMatch(Profile profile, AircraftInfo aircraft, out string pattern, out string matchedOn, out int score)
        {
            pattern = null;
            matchedOn = null;
            score = -1;

            if (profile?.Aircraft == null || aircraft == null || aircraft.IsEmpty)
            {
                return false;
            }

            foreach (var candidate in profile.Aircraft)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string on = null;
                if (IsMatch(candidate, aircraft.Folder))
                {
                    on = "folder";
                }
                else if (IsMatch(candidate, aircraft.Path))
                {
                    on = "path";
                }
                else if (IsMatch(candidate, aircraft.Title))
                {
                    on = "title";
                }

                if (on != null)
                {
                    var s = Specificity(candidate);
                    if (s > score)
                    {
                        score = s;
                        pattern = candidate;
                        matchedOn = on;
                    }
                }
            }

            return pattern != null;
        }

        /// <summary>
        /// Chooses the profile to use for an aircraft.
        /// </summary>
        public static ProfileMatch Resolve(IEnumerable<Profile> profiles, AircraftInfo aircraft, string manualProfileId = null)
        {
            var all = profiles.ToList();
            var defaultProfile = all.FirstOrDefault(p => p.IsDefault);

            if (!string.IsNullOrEmpty(manualProfileId))
            {
                var manual = all.FirstOrDefault(p => p.Id == manualProfileId);
                if (manual != null)
                {
                    return new ProfileMatch(manual, null, null, true);
                }
            }

            Profile best = null;
            string bestPattern = null, bestOn = null;
            var bestScore = -1;

            foreach (var profile in all.Where(p => p.Enabled && !p.IsDefault).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (TryMatch(profile, aircraft, out var pattern, out var on, out var score) && score > bestScore)
                {
                    best = profile;
                    bestPattern = pattern;
                    bestOn = on;
                    bestScore = score;
                }
            }

            if (best != null)
            {
                return new ProfileMatch(best, bestPattern, bestOn, false);
            }

            return new ProfileMatch(defaultProfile, null, null, false);
        }
    }
}
