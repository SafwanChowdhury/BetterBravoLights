using BravoLights.Common;
using NLog;

namespace BravoLights
{
    /// <summary>
    /// Decides whether the Bravo should be showing lights at all.
    /// </summary>
    public class GlobalLightController : ViewModelBase
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly IUsbLogic usbLogic;

        public GlobalLightController(IUsbLogic usbLogic)
        {
            this.usbLogic = usbLogic;
        }

        private bool simulatorInMainMenu = false;
        public bool SimulatorInMainMenu { get => simulatorInMainMenu; set { simulatorInMainMenu = value; Check(); } }

        private bool readingConfiguration = false;
        public bool ReadingConfiguration { get => readingConfiguration; set { readingConfiguration = value; Check(); } }

        private bool applicationExiting;
        public bool ApplicationExiting { get => applicationExiting; set { applicationExiting = value; Check(); } }

        private bool simulatorConnected;
        public bool SimulatorConnected { get => simulatorConnected; set { simulatorConnected = value; Check(); } }

        private bool lightTest;
        /// <summary>
        /// Forces the lights on (with every light lit) so that the user can test their Bravo.
        /// </summary>
        public bool LightTest { get => lightTest; set { lightTest = value; Check(); } }

        private bool lightsEnabled;
        public bool LightsEnabled
        {
            get => lightsEnabled;
            private set => SetProperty(ref lightsEnabled, value);
        }

        /// <summary>
        /// A short human-readable explanation of why the lights are on or off.
        /// </summary>
        public string Reason
        {
            get
            {
                if (ApplicationExiting) return "Exiting";
                if (LightTest) return "Light test";
                if (!SimulatorConnected) return "Waiting for a flight";
                if (SimulatorInMainMenu) return "In main menu";
                if (ReadingConfiguration) return "Applying profile";
                return "Active";
            }
        }

        private void Check()
        {
            logger.Debug("SimulatorInMainMenu={0}, ReadingConfiguration={1}, ApplicationExiting={2}, SimulatorConnected={3}, LightTest={4}",
                SimulatorInMainMenu, ReadingConfiguration, ApplicationExiting, SimulatorConnected, LightTest);

            var lightsShouldBeOn = !ApplicationExiting &&
                (LightTest || (!SimulatorInMainMenu && !ReadingConfiguration && SimulatorConnected));
            usbLogic.LightsEnabled = lightsShouldBeOn;
            LightsEnabled = lightsShouldBeOn;
            RaisePropertyChanged(nameof(Reason));
        }
    }
}
