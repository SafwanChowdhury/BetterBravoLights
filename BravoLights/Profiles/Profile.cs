using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace BravoLights.Profiles
{
    /// <summary>
    /// The configuration for a single light within a profile.
    /// </summary>
    public sealed class LightDefinition
    {
        /// <summary>
        /// The expression that drives the light, e.g. <c>L:I_FCU_AP1 == 1</c>, or <c>ON</c>/<c>OFF</c>.
        /// A null or blank expression means "not set here" (fall back to the Default profile if inheriting).
        /// </summary>
        [JsonPropertyName("expression")]
        public string Expression { get; set; }

        /// <summary>
        /// If true, the light is lit when the expression is false.
        /// </summary>
        [JsonPropertyName("invert")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Invert { get; set; }

        /// <summary>
        /// Free-text notes, e.g. explaining where an LVar came from.
        /// </summary>
        [JsonPropertyName("note")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Note { get; set; }

        [JsonIgnore]
        public bool HasExpression => !string.IsNullOrWhiteSpace(Expression);

        public LightDefinition Clone() => new() { Expression = Expression, Invert = Invert, Note = Note };
    }

    /// <summary>
    /// An aircraft profile: a named set of light expressions plus the aircraft it applies to.
    /// </summary>
    public sealed class Profile
    {
        public const string FormatName = "BetterBravoLights.Profile";
        public const int CurrentVersion = 1;
        public const string DefaultProfileId = "default";

        [JsonPropertyName("format")]
        public string Format { get; set; } = FormatName;

        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        /// <summary>
        /// Stable identifier. Templates use readable slugs; user-created profiles use GUIDs.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("name")]
        public string Name { get; set; } = "New profile";

        [JsonPropertyName("author")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Author { get; set; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Description { get; set; }

        /// <summary>
        /// True for the single fallback profile used for any aircraft without a more specific profile.
        /// </summary>
        [JsonPropertyName("isDefault")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsDefault { get; set; }

        /// <summary>
        /// Disabled profiles are never matched automatically.
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Aircraft match patterns. Each is a case-insensitive wildcard pattern (<c>*</c> and <c>?</c>)
        /// that's tested against the aircraft folder, the aircraft path and the aircraft title.
        /// </summary>
        [JsonPropertyName("aircraft")]
        public List<string> Aircraft { get; set; } = new();

        /// <summary>
        /// If true, lights (and master enable) that aren't defined in this profile fall back to the Default profile.
        /// </summary>
        [JsonPropertyName("inheritDefault")]
        public bool InheritDefault { get; set; } = true;

        /// <summary>
        /// An expression which must be true for any light to be lit, e.g. battery master on. Null means inherit/always on.
        /// </summary>
        [JsonPropertyName("masterEnable")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string MasterEnable { get; set; }

        [JsonPropertyName("lights")]
        public Dictionary<string, LightDefinition> Lights { get; set; } = new();

        /// <summary>
        /// The file this profile was loaded from (not serialized).
        /// </summary>
        [JsonIgnore]
        public string FilePath { get; set; }

        /// <summary>
        /// The template this profile was created from, if any.
        /// </summary>
        [JsonPropertyName("templateId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string TemplateId { get; set; }

        public LightDefinition GetLight(string lightName)
        {
            return Lights != null && Lights.TryGetValue(lightName, out var def) && def != null && def.HasExpression ? def : null;
        }

        public Profile Clone()
        {
            return new Profile
            {
                Format = Format,
                Version = Version,
                Id = Id,
                Name = Name,
                Author = Author,
                Description = Description,
                IsDefault = IsDefault,
                Enabled = Enabled,
                Aircraft = new List<string>(Aircraft ?? new()),
                InheritDefault = InheritDefault,
                MasterEnable = MasterEnable,
                Lights = (Lights ?? new()).ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Clone()),
                FilePath = FilePath,
                TemplateId = TemplateId,
            };
        }

        /// <summary>
        /// Removes empty light definitions and blank patterns so that saved files stay tidy.
        /// </summary>
        public void Normalise()
        {
            Aircraft = (Aircraft ?? new())
                .Select(a => a?.Trim())
                .Where(a => !string.IsNullOrEmpty(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Lights = (Lights ?? new())
                .Where(kvp => kvp.Value != null && (kvp.Value.HasExpression || !string.IsNullOrWhiteSpace(kvp.Value.Note)))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            foreach (var def in Lights.Values)
            {
                def.Expression = def.Expression?.Trim();
                if (string.IsNullOrWhiteSpace(def.Note))
                {
                    def.Note = null;
                }
            }

            if (string.IsNullOrWhiteSpace(MasterEnable))
            {
                MasterEnable = null;
            }
            if (string.IsNullOrWhiteSpace(Author))
            {
                Author = null;
            }
            if (string.IsNullOrWhiteSpace(Description))
            {
                Description = null;
            }
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// A bundle of several profiles, used for exporting/importing many at once.
    /// </summary>
    public sealed class ProfilePack
    {
        public const string FormatName = "BetterBravoLights.ProfilePack";

        [JsonPropertyName("format")]
        public string Format { get; set; } = FormatName;

        [JsonPropertyName("version")]
        public int Version { get; set; } = Profile.CurrentVersion;

        [JsonPropertyName("profiles")]
        public List<Profile> Profiles { get; set; } = new();
    }
}
