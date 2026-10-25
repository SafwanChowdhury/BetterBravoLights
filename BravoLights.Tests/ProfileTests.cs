using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BravoLights.Common;
using BravoLights.Installation;
using BravoLights.Profiles;
using BravoLights.Sim;
using Xunit;

namespace BravoLights.Tests
{
    public class AircraftInfoTests
    {
        [Fact]
        public void ParsesMsfs2020Paths()
        {
            var info = AircraftInfo.FromSimPath(@"SimObjects\Airplanes\Asobo_C172SP_Classic\aircraft.CFG");
            Assert.Equal("Asobo_C172SP_Classic", info.Folder);
            Assert.Equal("Asobo_C172SP_Classic", info.Path);
        }

        [Fact]
        public void ParsesMsfs2024PresetPaths()
        {
            var info = AircraftInfo.FromSimPath(@"SimObjects\Airplanes\FNX_32X\presets\fnx\FNX_320_CFM\config\aircraft.cfg");
            Assert.Equal("FNX_32X", info.Folder);
            Assert.Equal(@"FNX_32X\presets\fnx\FNX_320_CFM\config", info.Path);
        }

        [Fact]
        public void ParsesAbsolutePaths()
        {
            var info = AircraftInfo.FromSimPath(@"E:\Games\MSFS24\Packages\Community\pmdg-aircraft-738\SimObjects\Airplanes\PMDG 737-800\presets\pmdg\PMDG 737-800 BW HD\config\aircraft.cfg");
            Assert.Equal("PMDG 737-800", info.Folder);
            Assert.Equal(@"PMDG 737-800\presets\pmdg\PMDG 737-800 BW HD\config", info.Path);
        }

        [Fact]
        public void ParsesRotorcraftPaths()
        {
            var info = AircraftInfo.FromSimPath(@"SimObjects\Rotorcraft\r44\aircraft.cfg");
            Assert.Equal("r44", info.Folder);
        }

        [Fact]
        public void CopesWithEmptyPaths()
        {
            Assert.True(AircraftInfo.FromSimPath("").IsEmpty);
            Assert.True(AircraftInfo.FromSimPath(null).IsEmpty);
        }

        [Theory]
        [InlineData("TT:ATCCOM.AC_MODEL_B738.0.text", "B738")]
        [InlineData("TT:ATCCOM.AC_MODEL A320.0.text", "A320")]
        [InlineData("TT:ATCCOM.ATC_NAME BOEING.0.text", "BOEING")]
        // MSFS 2024 drops the TT: prefix (as reported live for the PMDG 737-800)
        [InlineData("ATCCOM.AC_MODEL B738.0.text", "B738")]
        [InlineData("ATCCOM.ATC_NAME BOEING.0.text", "BOEING")]
        [InlineData("B738", "B738")]
        [InlineData("  Boeing ", "Boeing")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void CleansAtcStrings(string raw, string expected)
        {
            Assert.Equal(expected, AircraftInfo.CleanAtcString(raw));
        }

        [Fact]
        public void KeepsDetailsWhenTheyArrive()
        {
            var info = AircraftInfo.FromSimPath(@"SimObjects\Airplanes\PMDG 737-800\aircraft.cfg")
                .WithDetails(title: "737-800 BB2 BW")
                .WithDetails(atcModel: "TT:ATCCOM.AC_MODEL_B738.0.text")
                .WithDetails(atcType: "Boeing");
            Assert.Equal("PMDG 737-800", info.Folder);
            Assert.Equal("737-800 BB2 BW", info.Title);
            Assert.Equal("B738", info.AtcModel);
            Assert.Equal("Boeing", info.AtcType);
        }
    }

    public class PatternSuggestionTests
    {
        private static readonly AircraftInfo Pmdg = new(
            "PMDG 737-800", @"PMDG 737-800\presets\pmdg\PMDG 737-BBJ2 BW\config", "737-800 BB2 BW", "B738", "Boeing");

        [Theory]
        [InlineData("PMDG 737-800", "PMDG 737-*")]
        [InlineData("FNX_32X", "FNX_*")]
        [InlineData("Asobo_C172SP_Classic", "Asobo_C172SP_*")]
        [InlineData("r44", null)]
        [InlineData("Ab-c", null)]
        [InlineData("Trailing-", null)]
        public void SuggestsFamilyPatterns(string folder, string expected)
        {
            Assert.Equal(expected, AircraftPatternSuggestions.FamilyPattern(folder));
        }

        [Fact]
        public void SuggestsPatternsFromBroadToNarrow()
        {
            var suggestions = AircraftPatternSuggestions.For(Pmdg);
            Assert.Equal(
                new[] { "PMDG 737-800", "PMDG 737-*", @"PMDG 737-800\presets\pmdg\PMDG 737-BBJ2 BW\config", "737-800 BB2 BW", "B738", "Boeing" },
                suggestions.Select(s => s.Pattern));
            Assert.True(suggestions[0].IsRecommended);
            Assert.True(suggestions.Single(s => s.Pattern == "B738").IsBroad);

            // Every suggestion must actually match the aircraft it came from
            Assert.All(suggestions, s => Assert.True(ProfileMatcher.MatchesAircraft(s.Pattern, Pmdg), s.Pattern));
        }

        [Fact]
        public void SuggestsNothingWithoutAnAircraft()
        {
            Assert.Empty(AircraftPatternSuggestions.For(AircraftInfo.None));
        }

        [Fact]
        public void SkipsDuplicateSuggestions()
        {
            // MSFS 2020: path equals folder, so there's no separate 'exact variant' suggestion
            var suggestions = AircraftPatternSuggestions.For(AircraftInfo.FromSimPath(@"SimObjects\Airplanes\Asobo_C172SP_Classic\aircraft.cfg"));
            Assert.Equal(new[] { "Asobo_C172SP_Classic", "Asobo_C172SP_*" }, suggestions.Select(s => s.Pattern));
        }
    }

    public class ProfileMatcherTests
    {
        private static Profile P(string name, params string[] patterns) => new() { Id = name, Name = name, Aircraft = patterns.ToList() };

        private static readonly Profile Default = new() { Id = "default", Name = "Default", IsDefault = true };

        [Theory]
        [InlineData("PMDG 737-*", "PMDG 737-800", true)]
        [InlineData("pmdg 737-*", "PMDG 737-800", true)]
        [InlineData("PMDG 737-?00", "PMDG 737-800", true)]
        [InlineData("PMDG 737-*", "PMDG 777-300ER", false)]
        [InlineData("FNX_32X", "FNX_32X", true)]
        [InlineData("FNX_32X", "FNX_32X_v2", false)]
        [InlineData("*", "anything", true)]
        public void MatchesWildcards(string pattern, string value, bool expected)
        {
            Assert.Equal(expected, ProfileMatcher.IsMatch(pattern, value));
        }

        [Fact]
        public void MatchesOnFolderPathOrTitle()
        {
            var aircraft = new AircraftInfo("FNX_32X", @"FNX_32X\presets\fnx\FNX_320_CFM\config", "Fenix A320 CFM - British Airways");
            Assert.Equal("folder", ProfileMatcher.Resolve(new[] { Default, P("a", "FNX_32X") }, aircraft).MatchedOn);
            Assert.Equal("path", ProfileMatcher.Resolve(new[] { Default, P("b", "*FNX_320_CFM*") }, aircraft).MatchedOn);
            Assert.Equal("title", ProfileMatcher.Resolve(new[] { Default, P("c", "*British Airways") }, aircraft).MatchedOn);
        }

        [Fact]
        public void MatchesOnIcaoModelAndManufacturer()
        {
            var aircraft = new AircraftInfo("Some 737", "Some 737", "Livery", "B738", "Boeing");
            Assert.Equal("ICAO model", ProfileMatcher.Resolve(new[] { Default, P("a", "B738") }, aircraft).MatchedOn);
            Assert.Equal("manufacturer", ProfileMatcher.Resolve(new[] { Default, P("b", "Boeing") }, aircraft).MatchedOn);
        }

        [Fact]
        public void PrefersMoreSpecificPatterns()
        {
            var aircraft = new AircraftInfo("PMDG 737-800", "PMDG 737-800", "");
            var profiles = new[] { Default, P("generic", "PMDG*"), P("family", "PMDG 737-*"), P("exact", "PMDG 737-800") };
            Assert.Equal("exact", ProfileMatcher.Resolve(profiles, aircraft).Profile.Id);

            profiles = new[] { Default, P("generic", "PMDG*"), P("family", "PMDG 737-*") };
            Assert.Equal("family", ProfileMatcher.Resolve(profiles, aircraft).Profile.Id);
        }

        [Fact]
        public void FallsBackToDefault()
        {
            var match = ProfileMatcher.Resolve(new[] { Default, P("a", "Other") }, new AircraftInfo("Mine", "Mine", ""));
            Assert.True(match.Profile.IsDefault);
            Assert.Null(match.Pattern);
        }

        [Fact]
        public void IgnoresDisabledProfiles()
        {
            var disabled = P("a", "Mine");
            disabled.Enabled = false;
            Assert.True(ProfileMatcher.Resolve(new[] { Default, disabled }, new AircraftInfo("Mine", "Mine", "")).Profile.IsDefault);
        }

        [Fact]
        public void ManualSelectionWins()
        {
            var match = ProfileMatcher.Resolve(new[] { Default, P("a", "Mine"), P("b", "Other") }, new AircraftInfo("Mine", "Mine", ""), "b");
            Assert.Equal("b", match.Profile.Id);
            Assert.True(match.IsManual);
        }
    }

    public class LightResolutionTests
    {
        [Fact]
        public void InheritsUndefinedLightsFromDefault()
        {
            var def = new Profile { IsDefault = true, MasterEnable = "L:BATT == 1" };
            def.Lights["HDG"] = new LightDefinition { Expression = "L:DEF_HDG == 1" };
            def.Lights["NAV"] = new LightDefinition { Expression = "L:DEF_NAV == 1" };

            var profile = new Profile { InheritDefault = true };
            profile.Lights["HDG"] = new LightDefinition { Expression = "L:MY_HDG == 1" };

            var resolved = LightExpressionConfig.Resolve(profile, def).ToDictionary(r => r.LightName);
            Assert.Equal(LightSource.Profile, resolved["HDG"].Source);
            Assert.Equal("L:MY_HDG == 1", resolved["HDG"].ExpressionText);
            Assert.Equal(LightSource.Default, resolved["NAV"].Source);
            Assert.Equal(LightSource.None, resolved["APR"].Source);
            Assert.Equal("OFF", resolved["APR"].ExpressionText);
            Assert.Equal("L:BATT == 1", LightExpressionConfig.ResolveMasterEnable(profile, def));
        }

        [Fact]
        public void DoesNotInheritWhenDisabled()
        {
            var def = new Profile { IsDefault = true, MasterEnable = "L:BATT == 1" };
            def.Lights["NAV"] = new LightDefinition { Expression = "L:DEF_NAV == 1" };

            var profile = new Profile { InheritDefault = false };
            var resolved = LightExpressionConfig.Resolve(profile, def).ToDictionary(r => r.LightName);
            Assert.Equal(LightSource.None, resolved["NAV"].Source);
            Assert.Equal("ON", LightExpressionConfig.ResolveMasterEnable(profile, def));
        }

        [Fact]
        public void ResolvesEveryLight()
        {
            var resolved = LightExpressionConfig.Resolve(new Profile(), null);
            Assert.Equal(LightNames.AllNames.Count(), resolved.Count);
        }

        [Theory]
        [InlineData("L:A == 1", null)]
        [InlineData("L:A == 1 OR\nL:B == 2", null)]
        [InlineData("L:A == 1 OR \\\n L:B == 2", null)]
        [InlineData("", null)]
        [InlineData("L:A", "A light needs a true/false expression (e.g. 'L:VAR == 1'), not a number.")]
        public void ValidatesExpressions(string text, string expectedError)
        {
            Assert.Equal(expectedError, LightExpressionConfig.Validate(text));
        }

        [Fact]
        public void ReportsSyntaxErrors()
        {
            Assert.NotNull(LightExpressionConfig.Validate("L:A == == 1"));
            Assert.NotNull(LightExpressionConfig.Validate("A:NO UNITS == 1"));
        }
    }

    public class ProfileStoreTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "bbl-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        private static Profile Sample(string name = "My aircraft")
        {
            var p = new Profile { Name = name, Aircraft = new List<string> { "MY_AIRCRAFT" } };
            p.Lights["HDG"] = new LightDefinition { Expression = "L:HDG == 1", Note = "a note" };
            p.Lights["Door"] = new LightDefinition { Expression = "L:DOOR > 0", Invert = true };
            return p;
        }

        [Fact]
        public void SavesAndLoadsProfiles()
        {
            var store = new ProfileStore(dir);
            store.Load();
            var saved = store.Save(Sample());

            var reloaded = new ProfileStore(dir);
            reloaded.Load();
            var p = Assert.Single(reloaded.Profiles);
            Assert.Equal(saved.Id, p.Id);
            Assert.Equal("L:HDG == 1", p.Lights["HDG"].Expression);
            Assert.Equal("a note", p.Lights["HDG"].Note);
            Assert.True(p.Lights["Door"].Invert);
            Assert.EndsWith("My aircraft.bblprofile", p.FilePath);
        }

        [Fact]
        public void RenamesFileWhenProfileIsRenamed()
        {
            var store = new ProfileStore(dir);
            store.Load();
            var saved = store.Save(Sample("Before"));
            saved.Name = "After";
            store.Save(saved);

            Assert.False(File.Exists(Path.Combine(dir, "Before.bblprofile")));
            Assert.True(File.Exists(Path.Combine(dir, "After.bblprofile")));
            Assert.Single(store.Profiles);
        }

        [Fact]
        public void DropsEmptyLightsWhenSaving()
        {
            var store = new ProfileStore(dir);
            store.Load();
            var p = Sample();
            p.Lights["NAV"] = new LightDefinition { Expression = "  " };
            var saved = store.Save(p);
            Assert.False(saved.Lights.ContainsKey("NAV"));
        }

        [Fact]
        public void ExportsAndImportsSingleProfilesAndPacks()
        {
            Directory.CreateDirectory(dir);
            var single = Path.Combine(dir, "one.bblprofile");
            var pack = Path.Combine(dir, "all.bblpack");

            ProfileStore.ExportProfile(Sample("One"), single);
            ProfileStore.ExportPack(new[] { Sample("One"), Sample("Two") }, pack);

            Assert.Equal("One", Assert.Single(ProfileStore.ReadImportFile(single)).Name);
            Assert.Equal(new[] { "One", "Two" }, ProfileStore.ReadImportFile(pack).Select(p => p.Name));
        }

        [Fact]
        public void ImportsLegacyIniFiles()
        {
            Directory.CreateDirectory(dir);
            var ini = Path.Combine(dir, "Config.ini");
            File.WriteAllText(ini, "[Aircraft.Foo]\nHDG = L:FOO == 1\n");
            var p = Assert.Single(ProfileStore.ReadImportFile(ini));
            Assert.Equal(new[] { "Foo" }, p.Aircraft);
        }

        [Fact]
        public void KeepsBothOnImportConflictIfAsked()
        {
            var store = new ProfileStore(dir);
            store.Load();
            var saved = store.Save(Sample());

            store.Import(new[] { saved.Clone() }, ImportConflictMode.KeepBoth);
            Assert.Equal(2, store.Profiles.Count);

            store.Import(new[] { saved.Clone() }, ImportConflictMode.Replace);
            Assert.Equal(2, store.Profiles.Count);
        }

        [Fact]
        public void RejectsProfilesFromNewerVersions()
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "future.bblprofile");
            File.WriteAllText(file, "{ \"format\": \"BetterBravoLights.Profile\", \"version\": 99, \"name\": \"Future\" }");
            Assert.Throws<InvalidDataException>(() => ProfileStore.ReadImportFile(file));
        }

        [Fact]
        public void SeedsTemplatesOnlyOnce()
        {
            var templatesDir = Path.Combine(dir, "templates");
            Directory.CreateDirectory(templatesDir);
            var t = Sample("Template");
            t.Id = "my-template";
            ProfileStore.ExportProfile(t, Path.Combine(templatesDir, "t.bblprofile"));
            var templates = new TemplateLibrary(templatesDir);
            templates.Load();

            var store = new ProfileStore(Path.Combine(dir, "profiles"));
            store.Load();
            var known = new HashSet<string>();
            Assert.Equal(1, store.SeedFromTemplates(templates, known));

            // The user deletes it; it shouldn't come back
            store.Delete("my-template");
            Assert.Equal(0, store.SeedFromTemplates(templates, known));

            // ... unless they ask for it
            Assert.Equal(1, store.SeedFromTemplates(templates, known, force: true));
        }
    }

    public class TemplateTests
    {
        private static IReadOnlyList<Profile> LoadTemplates()
        {
            var library = new TemplateLibrary(Path.Combine(AppContext.BaseDirectory, "Templates"));
            library.Load();
            return library.Templates;
        }

        [Fact]
        public void ShipsTemplates()
        {
            var templates = LoadTemplates();
            Assert.True(templates.Count > 20);
            Assert.Single(templates, t => t.IsDefault);
            Assert.Equal(templates.Count, templates.Select(t => t.Id).Distinct().Count());
        }

        [Fact]
        public void EveryTemplateExpressionIsValid()
        {
            var problems = new List<string>();
            foreach (var template in LoadTemplates())
            {
                foreach (var (light, def) in template.Lights)
                {
                    Assert.Contains(light, LightNames.AllNames);
                    var error = LightExpressionConfig.Validate(def.Expression);
                    if (error != null)
                    {
                        problems.Add($"{template.Name} / {light}: {error}");
                    }
                }
                if (template.MasterEnable != null && LightExpressionConfig.Validate(template.MasterEnable) is string masterError)
                {
                    problems.Add($"{template.Name} / MasterEnable: {masterError}");
                }
            }
            Assert.Empty(problems);
        }

        [Theory]
        [InlineData(@"SimObjects\Airplanes\FNX_32X\presets\fnx\FNX_320_CFM\config\aircraft.cfg", "fenix-a32x")]
        [InlineData(@"SimObjects\Airplanes\PMDG 737-800\presets\pmdg\PMDG 737-800 BW HD\config\aircraft.cfg", "pmdg-737")]
        [InlineData(@"SimObjects\Airplanes\PMDG 737-700\aircraft.cfg", "pmdg-737")]
        [InlineData(@"SimObjects\Airplanes\PMDG 777-300ER\presets\pmdg\PMDG 777-300ER\config\aircraft.cfg", "pmdg-777")]
        [InlineData(@"SimObjects\Airplanes\Asobo_C172SP_Classic\aircraft.cfg", "asobo-textron-aviation-cessna-172-skyhawk-g1000")]
        [InlineData(@"SimObjects\Airplanes\Some_Unknown_Aircraft\aircraft.cfg", "default")]
        public void TemplatesMatchTheRightAircraft(string path, string expectedId)
        {
            var match = ProfileMatcher.Resolve(LoadTemplates(), AircraftInfo.FromSimPath(path));
            Assert.Equal(expectedId, match.Profile.Id);
        }
    }
}
