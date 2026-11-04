using BravoLights.UI;
using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace BravoLights.Tests
{
    public class GitHubVersionTests
    {

        [Fact]
        public void DetectsLatestVersionFromAnOutOfOrderAssetsList()
        {
            var codeBaseUrl = new Uri(Assembly.GetExecutingAssembly().Location);
            var codeBasePath = Uri.UnescapeDataString(codeBaseUrl.AbsolutePath);
            var dirPath = Path.GetDirectoryName(codeBasePath);
            var filename = Path.Combine(dirPath, "GitHubReleasesSample.json");
            var text = File.ReadAllText(filename);

            var latestVersion = ProgramInfo.ExtractLatestVersionFromGitHubReleasesJson(text);
            Assert.Equal("0.6.0", latestVersion);
        }

        [Theory]
        [InlineData("1.0.1", "1.0.0", true)]
        [InlineData("1.10.0", "1.9.0", true)]
        [InlineData("1.0.0", "1.0.0", false)]
        [InlineData("0.8.2", "1.0.0", false)]
        [InlineData("not a version", "1.0.0", false)]
        [InlineData(null, "1.0.0", false)]
        public void OnlyOffersNewerVersions(string latest, string current, bool expected)
        {
            Assert.Equal(expected, ProgramInfo.IsNewerVersion(latest, current));
        }
    }
}
