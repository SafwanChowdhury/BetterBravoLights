using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BravoLights.UI.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace BravoLights.UI.Views
{
    public partial class ScannerView : UserControl
    {
        public ScannerView()
        {
            InitializeComponent();

            // Rows aren't individually unloaded when the whole page goes away, so release them all
            Unloaded += (s, e) => ViewModel?.ReleaseVisibleRows();
        }

        private ScannerViewModel ViewModel => DataContext as ScannerViewModel;

        /// <summary>
        /// Called when a row becomes visible on screen: watch its variable.
        /// </summary>
        private void Grid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            ViewModel?.SetRowVisible(e.Row.DataContext as ScanItem, true);
        }

        /// <summary>
        /// Called when a row is no longer visible.
        /// </summary>
        private void Grid_UnloadingRow(object sender, DataGridRowEventArgs e)
        {
            ViewModel?.SetRowVisible(e.Row.DataContext as ScanItem, false);
        }

        private void CopyName_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CopyNameCommand.Execute((sender as FrameworkElement)?.DataContext);
        }

        private void CopyCondition_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CopyConditionCommand.Execute((sender as FrameworkElement)?.DataContext);
        }

        private void AddToLight_Click(object sender, RoutedEventArgs e)
        {
            // The click bubbles up from the light's sub-item; the parent menu item's DataContext is the variable row.
            var item = (sender as FrameworkElement)?.DataContext as ScanItem;
            var light = (e.OriginalSource as FrameworkElement)?.DataContext as LightInfo;
            if (item != null && light != null)
            {
                ViewModel?.AddToLightCommand.Execute(new object[] { item, light });
            }
        }
    }

    /// <summary>
    /// Flashes a value's background when it changes, so changing variables are easy to spot.
    /// </summary>
    public class HighlightValueChangeBehavior : Behavior<TextBlock>
    {
        // We don't highlight on _every_ value change, as that would cause us to highlight
        // rows as you scroll around (because the UI reuses rows that scroll off the top, bringing
        // them back in at the bottom, plus even a new row being _given_ a value would be a 'new' value).
        private bool animateOnChanges = false;
        private ScanItem lastContext;

        private static readonly ColorAnimation HighlightAnimation = new()
        {
            AutoReverse = false,
            From = Color.FromArgb(0x99, 0xF2, 0xB3, 0x3D),
            To = Colors.Transparent,
            FillBehavior = FillBehavior.Stop,
            Duration = new Duration(TimeSpan.FromMilliseconds(3000))
        };

        protected override void OnAttached()
        {
            AssociatedObject.Background = new SolidColorBrush(Colors.Transparent);
            AssociatedObject.DataContextChanged += AssociatedObject_DataContextChanged;
            SubscribeToItem(AssociatedObject.DataContext as ScanItem);
        }

        protected override void OnDetaching()
        {
            AssociatedObject.DataContextChanged -= AssociatedObject_DataContextChanged;
            UnsubscribeFromItem();
        }

        private void AssociatedObject_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            UnsubscribeFromItem();
            AssociatedObject.Background.BeginAnimation(SolidColorBrush.ColorProperty, null);
            SubscribeToItem(e.NewValue as ScanItem);
        }

        private void SubscribeToItem(ScanItem item)
        {
            if (item == null)
            {
                return;
            }
            lastContext = item;
            animateOnChanges = false;
            item.PropertyChanged += Item_PropertyChanged;
        }

        private void UnsubscribeFromItem()
        {
            if (lastContext != null)
            {
                lastContext.PropertyChanged -= Item_PropertyChanged;
                lastContext = null;
            }
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ScanItem.Value))
            {
                return;
            }

            // When we first subscribe to an item we _might_ get a 'No value yet from simulator' error then a proper value,
            // and the first value we get shouldn't cause a highlight animation.
            if (!((ScanItem)sender).HasValue)
            {
                return;
            }

            if (!animateOnChanges)
            {
                animateOnChanges = true;
                return;
            }

            AssociatedObject.Dispatcher.BeginInvoke(() =>
                AssociatedObject.Background.BeginAnimation(SolidColorBrush.ColorProperty, HighlightAnimation));
        }
    }
}
