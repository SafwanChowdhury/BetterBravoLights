using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BravoLights.UI.Views
{
    public partial class DashboardView : UserControl
    {
        public DashboardView()
        {
            InitializeComponent();
        }

        private void AssignButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            if (button.ContextMenu != null)
            {
                button.ContextMenu.DataContext = DataContext;
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = PlacementMode.Bottom;
                button.ContextMenu.IsOpen = true;
            }
        }
    }
}
