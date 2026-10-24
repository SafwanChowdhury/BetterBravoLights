using System.Linq;
using BravoLights.Profiles;
using BravoLights.Sim;
using Xunit;

namespace BravoLights.Tests
{
    /// <summary>
    /// Tests that the old INI configuration format is converted into profiles with the same meaning.
    /// </summary>
    public class ConfigTests
    {
        private sealed class ImportedConfig
        {
            private readonly System.Collections.Generic.IList<Profile> profiles;

            public ImportedConfig(string file)
            {
                profiles = LegacyIniImporter.Import(file.Split('\r', '\n'));
            }

            /// <summary>
            /// Gets the expression for a light as the old configuration system would have done:
            /// the aircraft's own setting, falling back to the [Default] section.
            /// </summary>
            public string GetConfig(string aircraft, string key)
            {
                var match = ProfileMatcher.Resolve(profiles, new AircraftInfo(aircraft, aircraft, ""));
                var defaultProfile = profiles.FirstOrDefault(p => p.IsDefault);
                var profile = match.Profile;

                if (key == "MasterEnable")
                {
                    return profile?.MasterEnable ?? defaultProfile?.MasterEnable;
                }

                return profile?.GetLight(key)?.Expression ?? defaultProfile?.GetLight(key)?.Expression;
            }
        }

        private static ImportedConfig CreateConfig(string file) => new(file);

        [Fact]
        public void IgnoresComments()
        {
            var config = CreateConfig(@"
;This is a comment
[Aircraft.Some_Aircraft]
;This is also a comment
REV = A:AUTOPILOT BACKCOURSE HOLD, bool == 1
;Another comment
");
            Assert.Equal("A:AUTOPILOT BACKCOURSE HOLD, bool == 1", config.GetConfig("Some_Aircraft", "REV"));
        }

        [Fact]
        public void ReturnsSpecificConfigurationsForMatchingAircraft()
        {
            var config = CreateConfig(@"
[Aircraft.Aircraft1]
REV = A:AUTOPILOT BACKCOURSE HOLD, bool == 1

[Aircraft.Aircraft2]
REV = A:AUTOPILOT BACKCOURSE HOLD, bool == 2
");
            Assert.Equal("A:AUTOPILOT BACKCOURSE HOLD, bool == 1", config.GetConfig("Aircraft1", "REV"));
            Assert.Equal("A:AUTOPILOT BACKCOURSE HOLD, bool == 2", config.GetConfig("Aircraft2", "REV"));
        }

        [Fact]
        public void MergesMultipleSectionsForASingleAircraft()
        {
            var config = CreateConfig(@"
[Aircraft.Aircraft1]
LowFuelPressure = 1 < 2
[Aircraft.Aircraft2]
LowFuelPressure = 2 < 3
[Aircraft.Aircraft1]
HDG = 3 > 4
");
            Assert.Equal("1 < 2", config.GetConfig("Aircraft1", "LowFuelPressure"));
            Assert.Equal("2 < 3", config.GetConfig("Aircraft2", "LowFuelPressure"));
            Assert.Equal("3 > 4", config.GetConfig("Aircraft1", "HDG"));
        }

        [Fact]
        public void AllowsAircraftToOverrideOrFallbackToDefault()
        {
            var config = CreateConfig(@"
[Default]
LowVolts = A:ELECTRICAL MAIN BUS VOLTAGE:1, volts <= 28
Vacuum = A:PARTIAL PANEL VACUUM, Enum == 1

[Aircraft.Aircraft1]
LowVolts = A:ELECTRICAL MAIN BUS VOLTAGE:3, volts <= 26
");
            Assert.Equal("A:ELECTRICAL MAIN BUS VOLTAGE:3, volts <= 26", config.GetConfig("Aircraft1", "LowVolts"));
            Assert.Equal("A:ELECTRICAL MAIN BUS VOLTAGE:1, volts <= 28", config.GetConfig("Aircraft2", "LowVolts"));
            Assert.Equal("A:PARTIAL PANEL VACUUM, Enum == 1", config.GetConfig("Aircraft1", "Vacuum"));
        }

        [Fact]
        public void AllowsCommaSeparatedAircraftAndSpecific()
        {
            var config = CreateConfig(@"
[Default]
LowVolts = A:ELECTRICAL MAIN BUS VOLTAGE:1, volts <= 28
Vacuum = A:PARTIAL PANEL VACUUM, Enum == 1
LowFuelPressure = OFF

[Aircraft.Aircraft1, Aircraft.Aircraft2]
LowVolts = A:ELECTRICAL MAIN BUS VOLTAGE:3, volts <= 26

[Aircraft.Aircraft1]
LowFuelPressure = A:GENERAL ENG FUEL PRESSURE:1, psf < 65
");
            Assert.Equal("A:ELECTRICAL MAIN BUS VOLTAGE:3, volts <= 26", config.GetConfig("Aircraft1", "LowVolts"));
            Assert.Equal("A:ELECTRICAL MAIN BUS VOLTAGE:3, volts <= 26", config.GetConfig("Aircraft2", "LowVolts"));
            Assert.Equal("A:GENERAL ENG FUEL PRESSURE:1, psf < 65", config.GetConfig("Aircraft1", "LowFuelPressure"));
            Assert.Equal("OFF", config.GetConfig("Aircraft2", "LowFuelPressure"));
        }

        [Fact]
        public void ReturnsNullForMissingConfiguration()
        {
            var config = CreateConfig(@"
[Default]
LowFuelPressure = OFF
");
            Assert.Equal("OFF", config.GetConfig("Aircraft1", "LowFuelPressure"));
            Assert.Null(config.GetConfig("Aircraft1", "EngineFire"));
        }

        [Fact]
        public void SupportsBackslashLineContinuationCharacters()
        {
            // We'll insert some extra spaces in to check that continuations work even if there's trailing whitespace,
            // and that whitespace is all trimmed out (and re-inserted) correctly.
            var extraSpaces = "   ";

            var config = CreateConfig($@"
[Default]
LowFuelPressure = {extraSpaces}\{extraSpaces}
       L:I_OH_FUEL_CENTER_1_L == 1 \{extraSpaces}
    OR L:I_OH_FUEL_CENTER_1_U == 1 {extraSpaces}\
    {extraSpaces} OR L:I_OH_FUEL_CENTER_2_L == 1 \
    OR L:I_OH_FUEL_CENTER_2_U == 1{extraSpaces}");

            Assert.Equal("L:I_OH_FUEL_CENTER_1_L == 1 OR L:I_OH_FUEL_CENTER_1_U == 1 OR L:I_OH_FUEL_CENTER_2_L == 1 OR L:I_OH_FUEL_CENTER_2_U == 1", config.GetConfig("Aircraft", "LowFuelPressure"));
        }

        [Fact]
        public void TreatsHashLinesAsComments()
        {
            var config = CreateConfig(@"
[Aircraft.A]
# AVAIL is illuminated
APU = L:I_OH_ELEC_APU_START_U == 1
");
            Assert.Equal("L:I_OH_ELEC_APU_START_U == 1", config.GetConfig("A", "APU"));
        }

        [Fact]
        public void ReducesMsfs2024PresetPathsToTheAircraftFolder()
        {
            var profiles = LegacyIniImporter.Import(@"
;; Fenix A32X
[Aircraft.FNX_32X\presets\fnx\FNX_320_CFM\config, Aircraft.FNX_32X\presets\fnx\FNX_321_IAE\config]
APR = L:I_FCU_APPR == 1
".Split('\n'));

            var fenix = Assert.Single(profiles);
            Assert.Equal("Fenix A32X", fenix.Name);
            Assert.Equal(new[] { "FNX_32X" }, fenix.Aircraft);
        }

        [Fact]
        public void UsesHeaderCommentsAsProfileNamesAndKeyCommentsAsNotes()
        {
            var profiles = LegacyIniImporter.Import(@"
;;; A group heading which is ignored

;; PMDG 737-800
[Aircraft.PMDG 737-800]
; Thanks to someone for this
REV = L:ngx_MCP_VNav > 0
".Split('\n'));

            var pmdg = Assert.Single(profiles);
            Assert.Equal("PMDG 737-800", pmdg.Name);
            Assert.Equal("Thanks to someone for this", pmdg.Lights["REV"].Note);
        }

        [Fact]
        public void ConvertsInvertIntoPerLightInvertFlags()
        {
            var profiles = LegacyIniImporter.Import(@"
[Default]
Door = L:DOOR == 1

[Aircraft.A]
ParkingBrake = L:BRAKE == 1
Invert = ParkingBrake, Door
".Split('\n'));

            var a = profiles.Single(p => !p.IsDefault);
            Assert.True(a.Lights["ParkingBrake"].Invert);
            // Door wasn't defined for the aircraft, so the inherited expression is brought in so it can be inverted
            Assert.True(a.Lights["Door"].Invert);
            Assert.Equal("L:DOOR == 1", a.Lights["Door"].Expression);
        }

        [Fact]
        public void ImportsMasterEnable()
        {
            var config = CreateConfig(@"
[Default]
MasterEnable = A:CIRCUIT GENERAL PANEL ON, bool == 1

[Aircraft.A]
MasterEnable = L:BATT == 1
");
            Assert.Equal("L:BATT == 1", config.GetConfig("A", "MasterEnable"));
            Assert.Equal("A:CIRCUIT GENERAL PANEL ON, bool == 1", config.GetConfig("B", "MasterEnable"));
        }
    }
}
