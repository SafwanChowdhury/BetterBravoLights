using System;
using System.Collections.Generic;
using System.Linq;
using BravoLights.Common;

namespace BravoLights
{
    /// <summary>
    /// Tracks which lights are currently lit, and feeds that to the Bravo via <see cref="ILightsState"/>.
    /// </summary>
    public class MainViewModel : ViewModelBase, ILightsState
    {
        private readonly object sync = new();
        private readonly HashSet<string> litLights = new();

        private Dictionary<string, LightExpression> lightExpressions = new();

        public IReadOnlyDictionary<string, LightExpression> LightExpressions
        {
            get { return lightExpressions; }
        }

        /// <summary>
        /// Raised when a light turns on or off. The argument is the light name.
        /// </summary>
        public event EventHandler<string> LightChanged;

        public void RegisterLights(IEnumerable<LightExpression> lights)
        {
            var newExpressions = new Dictionary<string, LightExpression>();

            // Unsubscribe from the old expressions first, so that variables shared by old and new expressions
            // aren't unsubscribed from the simulator only to be immediately resubscribed.
            var old = lightExpressions;

            foreach (var light in lights)
            {
                newExpressions[light.LightName] = light;
            }

            // Make the new expressions current before subscribing, as subscribing delivers the current value immediately
            lightExpressions = newExpressions;

            foreach (var light in newExpressions.Values)
            {
                light.ValueChanged += ExpressionValueChanged;
            }

            foreach (var expression in old.Values)
            {
                if (expression != null)
                {
                    expression.ValueChanged -= ExpressionValueChanged;
                }
            }

            // Lights which no longer have an expression must go out
            foreach (var name in old.Keys.Except(newExpressions.Keys).ToList())
            {
                SetLit(name, false);
            }

            RaisePropertyChanged(nameof(LightExpressions));
        }

        private void ExpressionValueChanged(object sender, ValueChangedEventArgs e)
        {
            var lightExpression = (LightExpression)sender;

            // Ignore late notifications from expressions we've since replaced
            if (!lightExpressions.TryGetValue(lightExpression.LightName, out var current) || current != lightExpression)
            {
                return;
            }

            var lit = e.NewValue is bool b && b;
            SetLit(lightExpression.LightName, lit);
        }

        private void SetLit(string lightName, bool lit)
        {
            bool changed;
            lock (sync)
            {
                changed = lit ? litLights.Add(lightName) : litLights.Remove(lightName);
            }

            if (changed)
            {
                RaisePropertyChanged(lightName);
                LightChanged?.Invoke(this, lightName);
            }
        }

        private bool lightTest;

        /// <summary>
        /// When true, every light is reported as lit, so the user can check their Bravo.
        /// </summary>
        public bool LightTest
        {
            get { return lightTest; }
            set
            {
                if (lightTest == value)
                {
                    return;
                }
                lightTest = value;
                RaisePropertyChanged(nameof(LightTest));
                foreach (var name in LightNames.AllNames)
                {
                    LightChanged?.Invoke(this, name);
                }
            }
        }

        public IEnumerable<string> LitLights
        {
            get
            {
                if (lightTest)
                {
                    return LightNames.AllNames.ToList();
                }
                lock (sync)
                {
                    return litLights.ToList();
                }
            }
        }

        public bool IsLit(string lightName)
        {
            if (lightTest)
            {
                return true;
            }
            lock (sync)
            {
                return litLights.Contains(lightName);
            }
        }
    }
}
