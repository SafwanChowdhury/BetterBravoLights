using System.Collections.Generic;
using System.Linq;
using BravoLights.Ast;
using BravoLights.Common;
using BravoLights.Profiles;

namespace BravoLights
{
    public enum LightSource
    {
        /// <summary>The light is defined by the active profile itself.</summary>
        Profile,
        /// <summary>The light is inherited from the Default profile.</summary>
        Default,
        /// <summary>No profile defines the light, so it's off.</summary>
        None
    }

    /// <summary>
    /// The resolved configuration for a single light, after profile inheritance.
    /// </summary>
    public sealed class ResolvedLight
    {
        public string LightName { get; init; }
        public string ExpressionText { get; init; }
        public bool Invert { get; init; }
        public string Note { get; init; }
        public LightSource Source { get; init; }
        public LightExpression LightExpression { get; init; }
    }

    internal static class LightExpressionConfig
    {
        /// <summary>
        /// Normalises expression text entered by users (or read from files) so the parser is happy with it.
        /// Multi-line expressions and legacy INI continuation characters are joined with spaces.
        /// </summary>
        public static string NormaliseExpressionText(string text)
        {
            if (text == null)
            {
                return null;
            }

            var lines = text.Replace("\r", "").Split('\n')
                .Select(l => l.Trim())
                .Select(l => l.EndsWith("\\") ? l[..^1].Trim() : l)
                .Where(l => l.Length > 0);
            return string.Join(" ", lines);
        }

        public static string ResolveMasterEnable(Profile profile, Profile defaultProfile)
        {
            if (profile != null && !string.IsNullOrWhiteSpace(profile.MasterEnable))
            {
                return profile.MasterEnable;
            }
            if (defaultProfile != null && (profile == null || profile.InheritDefault || profile == defaultProfile) &&
                !string.IsNullOrWhiteSpace(defaultProfile.MasterEnable))
            {
                return defaultProfile.MasterEnable;
            }
            return "ON";
        }

        /// <summary>
        /// Computes the light expressions for a profile, falling back to the default profile where appropriate.
        /// </summary>
        /// <remarks>
        /// Returns an entry for every known light.
        /// </remarks>
        public static IReadOnlyList<ResolvedLight> Resolve(Profile profile, Profile defaultProfile)
        {
            var masterEnable = NormaliseExpressionText(ResolveMasterEnable(profile, defaultProfile));

            return LightNames.AllNames.Select(lightName =>
            {
                LightDefinition def = profile?.GetLight(lightName);
                var source = LightSource.Profile;

                if (def == null && defaultProfile != null && profile != defaultProfile && (profile == null || profile.InheritDefault))
                {
                    def = defaultProfile.GetLight(lightName);
                    source = LightSource.Default;
                }

                if (def == null)
                {
                    source = LightSource.None;
                }

                var expressionText = NormaliseExpressionText(def?.Expression) ?? "OFF";
                var invert = def?.Invert ?? false;

                var fullText = invert ? $"NOT({expressionText})" : expressionText;
                fullText = $"({masterEnable}) AND ({fullText})";

                var expression = MSFSExpressionParser.Parse(fullText).Optimize();

                return new ResolvedLight
                {
                    LightName = lightName,
                    ExpressionText = expressionText,
                    Invert = invert,
                    Note = def?.Note,
                    Source = source,
                    LightExpression = new LightExpression(lightName, expression, true)
                };
            }).ToList();
        }

        /// <summary>
        /// Validates a single expression as typed by a user. Returns null if it's valid, or an error message.
        /// </summary>
        public static string Validate(string expressionText, bool requireBoolean = true)
        {
            var text = NormaliseExpressionText(expressionText);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var node = MSFSExpressionParser.Parse(text);
            if (node.ErrorText != null)
            {
                return node.ErrorText;
            }
            if (requireBoolean && node.ValueType != Common.Ast.NodeDataType.Boolean)
            {
                return "A light needs a true/false expression (e.g. 'L:VAR == 1'), not a number.";
            }
            return null;
        }
    }
}
