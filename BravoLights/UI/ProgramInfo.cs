using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace BravoLights.UI
{
    public class ProgramInfo
    {
        /// <summary>
        /// The GitHub repository whose releases are checked for new versions.
        /// </summary>
        private const string GitHubRepository = "SafwanChowdhury/BetterBravoLights";

        public static string ProductNameAndVersion
        {
            get
            {
                return $"Better Bravo Lights {VersionString}";
            }
        }

        public static string VersionString
        {
            get {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        private static async Task<string> FetchLatestVersionStringAsync()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BetterBravoLights", VersionString));

            // We're not going to bother paging so we'll assume that the latest version is somewhere in the first page.
            var response = await client.GetStringAsync($"https://api.github.com/repos/{GitHubRepository}/releases");
            return ExtractLatestVersionFromGitHubReleasesJson(response);
        }

        internal static string ExtractLatestVersionFromGitHubReleasesJson(string json)
        {
            var doc = JsonDocument.Parse(json);

            Version latestVersion = null;
            foreach (var releaseEntry in doc.RootElement.EnumerateArray())
            {
                try
                {
                    // v0.6.0
                    var releaseName = releaseEntry.GetProperty("name").GetString();
                    var versionString = releaseName[1..];
                    var version = new Version(versionString);
                    if (latestVersion == null || version.CompareTo(latestVersion) > 0)
                    {
                        latestVersion = version;
                    }
                }
                catch
                {
                }
            }

            return latestVersion.ToString();
        }

        private static Task<string> cachedLatestVersionFetch;

        public static Task<string> GetLatestVersionStringAsync()
        {
            if (cachedLatestVersionFetch == null)
            {
                cachedLatestVersionFetch = FetchLatestVersionStringAsync();
            }

            return cachedLatestVersionFetch;
        }

        public static async Task<bool> IsNewVersionAvailableAsync()
        {
            try
            {
                var latestVersion = await GetLatestVersionStringAsync();
                return IsNewerVersion(latestVersion, VersionString);
            } catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tests whether a released version is newer than the running one. An older release is never offered as an update.
        /// </summary>
        internal static bool IsNewerVersion(string latest, string current)
        {
            return Version.TryParse(latest, out var latestVersion) &&
                Version.TryParse(current, out var currentVersion) &&
                latestVersion.CompareTo(currentVersion) > 0;
        }
    }
}
