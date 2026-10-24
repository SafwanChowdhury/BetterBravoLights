using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace BravoLights.Installation
{
    public class CorruptExeXmlException : Exception
    {
        public CorruptExeXmlException(string exeXmlPath, Exception innerException)
            : base("Existing exe.xml file is corrupt", innerException)
        {
            ExeXmlFilename = exeXmlPath;
            try
            {
                OriginalContent = File.ReadAllText(exeXmlPath);
                RepairedContent = ExeXmlFixer.TryFix(OriginalContent);
            }
            catch
            {
            }
        }

        public string ExeXmlFilename { get; private set; }

        public string OriginalContent { get; private set; }
        public string RepairedContent { get; private set; }
    }

    /// <summary>
    /// The installation state of Better Bravo Lights for one simulator.
    /// </summary>
    public sealed class SimInstallationStatus
    {
        public SimInstallation Sim { get; init; }
        public bool AutoStartEnabled { get; init; }
        public string AutoStartPath { get; init; }
        public string InstalledWasmVersion { get; init; }
        public string IncludedWasmVersion { get; init; }
        public string Problem { get; init; }

        public bool WasmUpToDate => InstalledWasmVersion != null && InstalledWasmVersion == IncludedWasmVersion;

        /// <summary>
        /// Whether auto-start points at this copy of Better Bravo Lights.
        /// </summary>
        public bool AutoStartIsThisCopy => AutoStartPath != null &&
            string.Equals(Path.GetFullPath(AutoStartPath), Path.GetFullPath(FlightSimulatorPaths.BetterBravoLightsExePath), StringComparison.OrdinalIgnoreCase);
    }

    static class Installer
    {
        private static readonly string AFCBridgeAddonName = "AFCBridge";
        private static readonly string BravoLightsAddonName = "BravoLights";

        static Installer()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        #region Whole-machine operations (used by the /install and /uninstall command line options)

        /// <summary>
        /// Installs auto-start and the WASM module into every detected simulator.
        /// </summary>
        public static string Install()
        {
            var sims = SimInstallation.Detect();
            if (sims.Count == 0)
            {
                throw new Exception("Could not find an installation of Microsoft Flight Simulator 2020 or 2024.");
            }

            var message = new StringBuilder();
            var afcBridgeDisabled = false;
            foreach (var sim in sims)
            {
                afcBridgeDisabled |= InstallAutoStart(sim);
            }

            if (afcBridgeDisabled)
            {
                message.AppendLine("Better Bravo Lights will now automatically start up with MSFS instead of the AFCBridge.");
            }
            else
            {
                message.AppendLine("Better Bravo Lights will now automatically start up with MSFS.");
            }

            message.AppendLine();
            foreach (var sim in sims)
            {
                message.AppendLine($"• {sim.DisplayName}");
            }

            message.AppendLine();
            message.AppendLine($"Better Bravo Lights will run from {FlightSimulatorPaths.BetterBravoLightsPath}");

            foreach (var communityModulePath in InstallWasmModules(sims))
            {
                message.AppendLine();
                message.AppendLine($"WASM module installed to {communityModulePath}");
            }

            if (OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                message.AppendLine();
                message.AppendLine($"(By the way, you don't need to run this installer as Administrator!)");
            }

            return message.ToString();
        }

        public static string Uninstall()
        {
            var sims = SimInstallation.Detect();
            var message = new StringBuilder();
            var anything = false;

            foreach (var sim in sims)
            {
                if (UninstallAutoStart(sim, out var afcBridgeRestored))
                {
                    anything = true;
                    message.AppendLine(afcBridgeRestored
                        ? $"Better Bravo Lights will no longer start up with {sim.DisplayName}. AFCBridge will now be used instead."
                        : $"Better Bravo Lights will no longer start up with {sim.DisplayName}.");
                }

                if (GetInstalledWasmModuleVersion(sim) != null)
                {
                    UninstallWasmModule(sim);
                    anything = true;
                    message.AppendLine($"WASM module uninstalled from {sim.InstalledWasmModulePath}");
                }
            }

            return anything ? message.ToString() : "Better Bravo Lights was not installed.";
        }

        /// <summary>
        /// Installs the WASM module into each distinct Community folder (several simulators can share one).
        /// </summary>
        public static IList<string> InstallWasmModules(IEnumerable<SimInstallation> sims)
        {
            var installed = new List<string>();
            foreach (var sim in sims)
            {
                var target = sim.InstalledWasmModulePath;
                if (target == null || installed.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                InstallWasmModule(sim);
                installed.Add(target);
            }
            return installed;
        }

        #endregion

        #region Per-simulator operations

        public static SimInstallationStatus GetStatus(SimInstallation sim)
        {
            string problem = null;
            var enabled = false;
            string path = null;

            try
            {
                var xdoc = LoadExeXml(sim);
                var lightsEl = FindAddon(xdoc, BravoLightsAddonName);
                if (lightsEl != null)
                {
                    enabled = !IsTrue(lightsEl.Element("Disabled")?.Value);
                    path = lightsEl.Element("Path")?.Value;
                    if (enabled && path != null && !File.Exists(path))
                    {
                        problem = $"Auto-start points at {path}, which no longer exists.";
                    }
                }
            }
            catch (CorruptExeXmlException)
            {
                problem = $"{Path.GetFileName(sim.ExeXmlPath)} is corrupt.";
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }

            string included = null;
            try
            {
                included = IncludedWasmModuleVersion;
            }
            catch
            {
            }

            return new SimInstallationStatus
            {
                Sim = sim,
                AutoStartEnabled = enabled,
                AutoStartPath = path,
                InstalledWasmVersion = GetInstalledWasmModuleVersion(sim),
                IncludedWasmVersion = included,
                Problem = problem
            };
        }

        /// <summary>
        /// Adds (or updates) the Better Bravo Lights entry in the simulator's exe.xml.
        /// </summary>
        /// <returns>True if the AFCBridge was disabled.</returns>
        public static bool InstallAutoStart(SimInstallation sim)
        {
            var xdoc = LoadExeXml(sim);

            var afcBridgeEl = FindAddon(xdoc, AFCBridgeAddonName);
            if (afcBridgeEl != null)
            {
                afcBridgeEl.SetElementValue("Disabled", "True");
            }

            var lightsEl = FindAddon(xdoc, BravoLightsAddonName);
            if (lightsEl == null)
            {
                lightsEl = new XElement("Launch.Addon", new XElement("Name", BravoLightsAddonName));
                xdoc.Root.Add(lightsEl);
            }
            lightsEl.SetElementValue("Disabled", "False");
            lightsEl.SetElementValue("ManualLoad", "False");
            lightsEl.SetElementValue("Path", FlightSimulatorPaths.BetterBravoLightsExePath);
            lightsEl.SetElementValue("CommandLine", "/startedbysimulator");
            lightsEl.SetElementValue("NewConsole", "False");

            SaveExeXml(sim, xdoc);

            return afcBridgeEl != null;
        }

        /// <summary>
        /// Removes the Better Bravo Lights entry from the simulator's exe.xml.
        /// </summary>
        /// <returns>True if there was an entry to remove.</returns>
        public static bool UninstallAutoStart(SimInstallation sim, out bool afcBridgeRestored)
        {
            afcBridgeRestored = false;
            if (!File.Exists(sim.ExeXmlPath))
            {
                return false;
            }

            var xdoc = LoadExeXml(sim);

            var lightsEl = FindAddon(xdoc, BravoLightsAddonName);
            if (lightsEl == null)
            {
                return false;
            }
            lightsEl.Remove();

            var afcBridgeEl = FindAddon(xdoc, AFCBridgeAddonName);
            if (afcBridgeEl != null)
            {
                afcBridgeEl.SetElementValue("Disabled", "False");
                afcBridgeRestored = true;
            }

            SaveExeXml(sim, xdoc);
            return true;
        }

        public static void InstallWasmModule(SimInstallation sim)
        {
            var target = sim.InstalledWasmModulePath ?? throw new Exception($"Cannot locate the Community folder for {sim.DisplayName}.");
            if (!Directory.Exists(FlightSimulatorPaths.IncludedWasmModulePath))
            {
                throw new Exception("The WASM module is missing from this copy of Better Bravo Lights.");
            }
            UninstallWasmModule(sim);
            FileUtils.CopyDirectory(FlightSimulatorPaths.IncludedWasmModulePath, target);
        }

        public static void UninstallWasmModule(SimInstallation sim)
        {
            var target = sim.InstalledWasmModulePath;
            if (target != null)
            {
                FileUtils.RemoveDirectoryRecursively(target);
            }
        }

        public static string GetInstalledWasmModuleVersion(SimInstallation sim)
        {
            var path = sim.InstalledWasmModulePath;
            return path == null ? null : GetWasmModuleVersion(path);
        }

        #endregion

        private static bool IsTrue(string value) => string.Equals(value?.Trim(), "True", StringComparison.OrdinalIgnoreCase);

        private static XDocument LoadExeXml(SimInstallation sim)
        {
            var path = sim.ExeXmlPath;
            try
            {
                return XDocument.Load(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                // The exe.xml file does not exist. Make one.
                var doc = new XDocument(
                    new XDeclaration("1.0", "windows-1252", null),
                    new XElement("SimBase.Document",
                        new XAttribute("Type", sim.Version == SimVersion.MSFS2024 ? "Launch" : "SimConnect"),
                        new XAttribute("version", "1,0"),
                        new XElement("Descr", "Auto launch external applications on MSFS start"),
                        new XElement("Filename", Path.GetFileName(path)),
                        new XElement("Disabled", "False")
                        )
                    );
                return doc;
            }
            catch (XmlException ex)
            {
                throw new CorruptExeXmlException(path, ex);
            }
        }

        private static void SaveExeXml(SimInstallation sim, XDocument xdoc)
        {
            var path = sim.ExeXmlPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // Keep a backup of the previous version, in case anything goes wrong
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bbl-backup", true);
            }

            xdoc.Save(path);
        }

        private static XElement FindAddon(XDocument document, string name)
        {
            var root = document.Element("SimBase.Document");
            if (root == null)
            {
                return null;
            }

            foreach (var addonEl in root.Elements("Launch.Addon"))
            {
                var addonNameElement = addonEl.Element("Name");
                if (addonNameElement != null && addonNameElement.Value == name)
                {
                    return addonEl;
                }
            }
            return null;
        }

        public static string GetWasmModuleVersion(string modulePath)
        {
            var manifestPath = Path.Join(modulePath, "manifest.json");

            if (!File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                var text = File.ReadAllText(manifestPath);
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.GetProperty("package_version").GetString();
            }
            catch
            {
                return "unknown";
            }
        }

        public static string IncludedWasmModuleVersion
        {
            get
            {
                var version = GetWasmModuleVersion(FlightSimulatorPaths.IncludedWasmModulePath);
                if (version == null)
                {
                    throw new Exception("Missing included WASM module");
                }
                return version;
            }
        }

        /// <summary>
        /// Gets a value indicating whether any simulator is set to start BBL automatically.
        /// </summary>
        public static bool IsSetToRunOnStartup => SimInstallation.Detect().Any(s => GetStatus(s).AutoStartEnabled);

        /// <summary>
        /// Gets a value indicating whether there's a problem with the installation configuration; this should
        /// be used when starting up the app normally, to check if something has been broken in the installation.
        /// </summary>
        public static string InstallationProblem
        {
            get
            {
                foreach (var sim in SimInstallation.Detect())
                {
                    try
                    {
                        var xdoc = LoadExeXml(sim);
                        var lightsEl = FindAddon(xdoc, BravoLightsAddonName);
                        if (lightsEl == null || IsTrue(lightsEl.Element("Disabled")?.Value))
                        {
                            // Not installed, or installed but disabled. Don't bother checking the file path
                            continue;
                        }
                        var installedBblPath = lightsEl.Element("Path")?.Value;
                        if (installedBblPath != null && !File.Exists(installedBblPath))
                        {
                            return $"BetterBravoLights was installed into {sim.DisplayName} at the path\n{installedBblPath}\nbut that file no longer exists.\n\nAs a result, BetterBravoLights will not currently run automatically when MSFS starts.\n\nTo fix this, open Settings in this copy of Better Bravo Lights and install it again.";
                        }
                    }
                    catch (CorruptExeXmlException)
                    {
                        // Right now, the exe.xml file is corrupted. If we haven't been installed, that's okay.
                        if (IsBBLMentionedInExeXml(sim))
                        {
                            return $"After BetterBravoLights was installed, some other installation has broken the file that controls the programs that {sim.DisplayName} launches when it starts.\n\nAs a result, BetterBravoLights (and some other programs) will not currently run automatically when MSFS starts.\n\nRun the BetterBravoLights installer and it will either automatically fix the problem or help you get it fixed.";
                        }
                    }
                }

                return null;
            }
        }

        private static bool IsBBLMentionedInExeXml(SimInstallation sim)
        {
            try
            {
                return File.ReadAllText(sim.ExeXmlPath).Contains("BetterBravoLights.exe");
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
