using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BravoLights.Common;

namespace BravoLights.UI
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> execute;
        private readonly Func<object, bool> canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute == null ? null : _ => canExecute())
        {
        }

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            this.execute = execute;
            this.canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => canExecute == null || canExecute(parameter);

        public void Execute(object parameter) => execute(parameter);
    }

    public enum LightGroup
    {
        Autopilot,
        Gear,
        Warning,
        Caution
    }

    public enum LightColour
    {
        Red,
        Amber,
        Green
    }

    /// <summary>
    /// Display information about each of the Bravo's lights.
    /// </summary>
    public sealed class LightInfo
    {
        public string Name { get; init; }
        public string Label { get; init; }
        public string ShortLabel { get; init; }
        public LightGroup Group { get; init; }
        public LightColour Colour { get; init; }

        public string GroupName => Group switch
        {
            LightGroup.Autopilot => "Autopilot buttons",
            LightGroup.Gear => "Landing gear",
            LightGroup.Warning => "Annunciators – warnings (red)",
            LightGroup.Caution => "Annunciators – cautions (amber)",
            _ => Group.ToString()
        };
    }

    public static class LightCatalog
    {
        public static readonly IReadOnlyList<LightInfo> All = new List<LightInfo>
        {
            new() { Name = LightNames.HDG, Label = "HDG", ShortLabel = "HDG", Group = LightGroup.Autopilot, Colour = LightColour.Green },
            new() { Name = LightNames.NAV, Label = "NAV", ShortLabel = "NAV", Group = LightGroup.Autopilot, Colour = LightColour.Green },
            new() { Name = LightNames.APR, Label = "APR", ShortLabel = "APR", Group = LightGroup.Autopilot, Colour = LightColour.Green },
            new() { Name = LightNames.REV, Label = "REV", ShortLabel = "REV", Group = LightGroup.Autopilot, Colour = LightColour.Green },
            new() { Name = LightNames.ALT, Label = "ALT", ShortLabel = "ALT", Group = LightGroup.Autopilot, Colour = LightColour.Green },
            new() { Name = LightNames.VS, Label = "VS", ShortLabel = "VS", Group = LightGroup.Autopilot, Colour = LightColour.Green },
            new() { Name = LightNames.IAS, Label = "IAS", ShortLabel = "IAS", Group = LightGroup.Autopilot, Colour = LightColour.Green },
            new() { Name = LightNames.AUTOPILOT, Label = "AUTOPILOT", ShortLabel = "AP", Group = LightGroup.Autopilot, Colour = LightColour.Green },

            new() { Name = LightNames.GearLGreen, Label = "Gear L green", ShortLabel = "L", Group = LightGroup.Gear, Colour = LightColour.Green },
            new() { Name = LightNames.GearCGreen, Label = "Gear nose green", ShortLabel = "N", Group = LightGroup.Gear, Colour = LightColour.Green },
            new() { Name = LightNames.GearRGreen, Label = "Gear R green", ShortLabel = "R", Group = LightGroup.Gear, Colour = LightColour.Green },
            new() { Name = LightNames.GearLRed, Label = "Gear L red", ShortLabel = "L", Group = LightGroup.Gear, Colour = LightColour.Red },
            new() { Name = LightNames.GearCRed, Label = "Gear nose red", ShortLabel = "N", Group = LightGroup.Gear, Colour = LightColour.Red },
            new() { Name = LightNames.GearRRed, Label = "Gear R red", ShortLabel = "R", Group = LightGroup.Gear, Colour = LightColour.Red },

            new() { Name = LightNames.MasterWarning, Label = "MASTER WARNING", ShortLabel = "MASTER WARNING", Group = LightGroup.Warning, Colour = LightColour.Red },
            new() { Name = LightNames.EngineFire, Label = "ENGINE FIRE", ShortLabel = "ENGINE FIRE", Group = LightGroup.Warning, Colour = LightColour.Red },
            new() { Name = LightNames.LowOilPressure, Label = "LOW OIL PRESSURE", ShortLabel = "LOW OIL PRESSURE", Group = LightGroup.Warning, Colour = LightColour.Red },
            new() { Name = LightNames.LowFuelPressure, Label = "LOW FUEL PRESSURE", ShortLabel = "LOW FUEL PRESSURE", Group = LightGroup.Warning, Colour = LightColour.Red },
            new() { Name = LightNames.AntiIce, Label = "ANTI ICE", ShortLabel = "ANTI ICE", Group = LightGroup.Warning, Colour = LightColour.Red },
            new() { Name = LightNames.StarterEngaged, Label = "STARTER ENGAGED", ShortLabel = "STARTER ENGAGED", Group = LightGroup.Warning, Colour = LightColour.Red },
            new() { Name = LightNames.APU, Label = "APU", ShortLabel = "APU", Group = LightGroup.Warning, Colour = LightColour.Red },

            new() { Name = LightNames.MasterCaution, Label = "MASTER CAUTION", ShortLabel = "MASTER CAUTION", Group = LightGroup.Caution, Colour = LightColour.Amber },
            new() { Name = LightNames.Vacuum, Label = "VACUUM", ShortLabel = "VACUUM", Group = LightGroup.Caution, Colour = LightColour.Amber },
            new() { Name = LightNames.LowHydPressure, Label = "LOW HYD PRESSURE", ShortLabel = "LOW HYD PRESSURE", Group = LightGroup.Caution, Colour = LightColour.Amber },
            new() { Name = LightNames.AuxFuelPump, Label = "AUX FUEL PUMP", ShortLabel = "AUX FUEL PUMP", Group = LightGroup.Caution, Colour = LightColour.Amber },
            new() { Name = LightNames.ParkingBrake, Label = "PARKING BRAKE", ShortLabel = "PARKING BRAKE", Group = LightGroup.Caution, Colour = LightColour.Amber },
            new() { Name = LightNames.LowVolts, Label = "LOW VOLTS", ShortLabel = "LOW VOLTS", Group = LightGroup.Caution, Colour = LightColour.Amber },
            new() { Name = LightNames.Door, Label = "DOOR", ShortLabel = "DOOR", Group = LightGroup.Caution, Colour = LightColour.Amber },
        };

        public static LightInfo Get(string name) => All.First(l => l.Name == name);
    }

    /// <summary>
    /// Converts a light colour plus lit state to a brush. Bind with a MultiBinding of (Colour, IsLit).
    /// </summary>
    public sealed class LightBrushConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not LightColour colour || values[1] is not bool lit || !lit)
            {
                return Application.Current.FindResource("LightOffBrush");
            }
            return Application.Current.FindResource(colour switch
            {
                LightColour.Red => "LitRedBrush",
                LightColour.Amber => "LitAmberBrush",
                _ => "LitGreenBrush"
            });
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var b = value is bool v && v;
            if (Invert)
            {
                b = !b;
            }
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hasValue = value != null && !(value is string s && s.Length == 0);
            if (Invert)
            {
                hasValue = !hasValue;
            }
            return hasValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class BoolToBrushConverter : IValueConverter
    {
        public Brush TrueBrush { get; set; }
        public Brush FalseBrush { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && b ? TrueBrush : FalseBrush;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>
    /// Visible when a count is zero (or, with Invert, when it's non-zero).
    /// </summary>
    public sealed class ZeroToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var zero = value is not int i || i == 0;
            if (Invert)
            {
                zero = !zero;
            }
            return zero ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    }

    /// <summary>
    /// Maps a <see cref="StatusLevel"/> to a brush.
    /// </summary>
    public sealed class StatusBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = value is StatusLevel level ? level switch
            {
                StatusLevel.Good => "SuccessBrush",
                StatusLevel.Warning => "WarningBrush",
                StatusLevel.Bad => "DangerBrush",
                _ => "TextFaintBrush"
            } : "TextFaintBrush";
            return Application.Current.FindResource(key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public enum StatusLevel
    {
        Neutral,
        Good,
        Warning,
        Bad
    }

    public static class WindowHelpers
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>
        /// Asks Windows to draw a dark title bar, to match the app's theme.
        /// </summary>
        public static void UseDarkTitleBar(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                var on = 1;
                // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 on Windows 10 20H1+ and Windows 11
                DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
            }
            catch
            {
                // Not supported on this version of Windows; never mind.
            }
        }

        public static T FindAncestor<T>(DependencyObject child) where T : DependencyObject
        {
            var current = child;
            while (current != null && current is not T)
            {
                current = VisualTreeHelper.GetParent(current);
            }
            return current as T;
        }

        public static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                yield break;
            }
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t)
                {
                    yield return t;
                }
                foreach (var d in FindDescendants<T>(child))
                {
                    yield return d;
                }
            }
        }
    }
}
