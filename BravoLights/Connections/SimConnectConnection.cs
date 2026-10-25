using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BravoLights.Ast;
using BravoLights.Common;
using BravoLights.Installation;
using BravoLights.Sim;
using Microsoft.FlightSimulator.SimConnect;
using NLog;

namespace BravoLights.Connections
{
    enum DefineId : uint
    {
        WASMRequestResponse = 1,
        WASMLVars = 2,
        AircraftTitle = 3,
        AircraftAtcModel = 4,
        AircraftAtcType = 5,

        // The start of dynamically-allocated definitions for simulator variables
        DynamicStart = 100
    }

    enum RequestId : uint
    {
        SimState = 1,
        WASMResponse = 2,
        WASMLVars = 3,
        AircraftLoaded = 4,
        FlightLoaded = 5,
        AircraftTitle = 6,
        AircraftAtcModel = 7,
        AircraftAtcType = 8,

        // The start of dynamically-allocated request ids for simulator variables
        DynamicStart = DefineId.DynamicStart
    }

    enum WASMReaderState
    {
        Neutral,
        ReadingLVars
    }

    class SimConnectConnection : IConnection, IWASMChannel
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        // Names of the client data areas established by the WASM module
        private const string CDA_NAME_SIMVAR = "BetterBravoLights.LVars";
        private const string CDA_NAME_REQUEST = "BetterBravoLights.Request";
        private const string CDA_NAME_RESPONSE = "BetterBravoLights.Response";

        // Ids for the client data areas
        private enum ClientDataId
        {
            LVars = 0,
            Request = 1,
            Response = 2
        }

        private const string RESPONSE_LVAR_START = "!LVARS-START";
        private const string RESPONSE_LVAR_END = "!LVARS-END";

        // Size of the request and response CDAs
        private const int RequestResponseSize = 256;

        [StructLayout(LayoutKind.Sequential)]
        public struct RequestString
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = RequestResponseSize)]
            public byte[] data;

            public RequestString(string str)
            {
                var bytes = Encoding.ASCII.GetBytes(str);
                var ret = new byte[RequestResponseSize];
                Array.Copy(bytes, ret, Math.Min(bytes.Length, RequestResponseSize - 1));
                data = ret;
            }
        }

        public struct ResponseString
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RequestResponseSize)]
            public string Data;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct AircraftTitleStruct
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Title;
        }

        // This MUST match the value in the WASM
        private const int MaxDataValuesInPacket = 10;

        public struct LVarData : ILVarData
        {
            [MarshalAs(UnmanagedType.U2)]
            public short ValueCount;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxDataValuesInPacket, ArraySubType = UnmanagedType.U2)]
            public short[] Ids;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxDataValuesInPacket, ArraySubType = UnmanagedType.R8)]
            public double[] Values;

            short ILVarData.ValueCount => ValueCount;

            short[] ILVarData.Ids => Ids;

            double[] ILVarData.Values => Values;
        }

        private readonly uint LVarDataSize = (uint)Marshal.SizeOf<LVarData>();

        public static SimConnectConnection Connection = new();

        private Timer reconnectTimer = null;
        private Timer detectExitTimer = null;

        // MSFS does use SimConnect to tell us when it's exited, but some users have reported that
        // BBL sometimes doesn't exit. So as well as exiting when the sim tells us it's exited,
        // we'll also exit if we haven't heard anything from the sim for a while. We'll also
        // prod the sim every so often, to ensure that we _should_ hear something.
        private DateTime lastReceivedResponseFromSim = DateTime.MinValue;
        private static readonly TimeSpan ExitWhenNotHeardFromSimFor = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SimLivenessPollPeriod = ExitWhenNotHeardFromSimFor / 2;

        /// <summary>
        /// How long we wait for the WASM module to answer a request for the LVar list before deciding it's not there.
        /// </summary>
        private static readonly TimeSpan WasmResponseTimeout = TimeSpan.FromSeconds(10);

        private SimState simState = SimState.SimExited;

        private SimConnectConnection()
        {
            LVarManager.Connection.SetWASMChannel(this);
            requestQueueTimer = new Timer(_ => DrainRequestQueue(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Set once the app is ready for us to connect. Until then, nothing may connect: in particular, subscribing to
        /// variables while the app is starting up must not open a connection before <see cref="HWnd"/> exists, or
        /// SimConnect would have nowhere to post its "data waiting" messages and we'd never receive anything.
        /// </summary>
        private bool started;

        /// <summary>
        /// When the current connection was opened, and whether the simulator has answered it yet.
        /// </summary>
        private DateTime connectedAt;
        private bool receivedOpen;

        /// <summary>
        /// How long we wait for the simulator to answer a new connection before dropping it and trying again.
        /// </summary>
        private static readonly TimeSpan ReconnectIfNoAnswerAfter = TimeSpan.FromSeconds(45);

        public void Start()
        {
            logger.Debug("Start");

            started = true;
            ConnectNow();
        }

        private uint nextVariableId = (uint)DefineId.DynamicStart;

        private readonly Dictionary<uint, NameAndUnits> idToName = new();
        private readonly Dictionary<NameAndUnits, uint> nameToId = new(new NameAndUnitsComparer());
        private readonly Dictionary<NameAndUnits, EventHandler<ValueChangedEventArgs>> variableHandlers =
            new(new NameAndUnitsComparer());
        private readonly Dictionary<NameAndUnits, double> lastReportedValue = new(new NameAndUnitsComparer());

        private void SubscribeToSimConnect(NameAndUnits nameAndUnits)
        {
            nameToId.TryGetValue(nameAndUnits, out var id);

            // Do we already have an id for this variable?
            if (id == 0)
            {
                // No. Create one.
                id = ++nextVariableId;

                idToName[id] = nameAndUnits;
                nameToId[nameAndUnits] = id;
            }

            simconnect.AddToDataDefinition((DefineId)id, nameAndUnits.Name, nameAndUnits.Units, SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
            simconnect.RegisterDataDefineStruct<double>((DefineId)id);
            simconnect.RequestDataOnSimObject((RequestId)id, (DefineId)id, 0, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        }

        private void UnsubscribeFromSimConnect(SimVarExpression simvar)
        {
            var name = simvar.NameAndUnits;

            if (!nameToId.TryGetValue(name, out var id))
            {
                return;
            }

            logger.Debug("UnsubscribeFromSimConnect:{0} ({1})", name, id);

            // We're unsubscribing from this variable, so any value is going to be out of date, so remove the old value
            lastReportedValue.Remove(name);

            try
            {
                simconnect.RequestDataOnSimObject((RequestId)id, (DefineId)id, 0, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                simconnect.ClearDataDefinition((DefineId)id);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Failed to unsubscribe {0}", name);
            }
        }

        public void AddListener(IVariable variable, EventHandler<ValueChangedEventArgs> handler)
        {
            var simvar = (SimVarExpression)variable;
            var nau = simvar.NameAndUnits;

            logger.Trace("AddListener {0}. Existing: {1}", variable.Identifier, variableHandlers.Count);

            lock (this)
            {
                variableHandlers.TryGetValue(nau, out var existingHandlers);
                var newHandlers = (EventHandler<ValueChangedEventArgs>)Delegate.Combine(existingHandlers, handler);
                variableHandlers[nau] = newHandlers;

                if (existingHandlers == null)
                {
                    if (this.simconnect != null)
                    {
                        try
                        {
                            SubscribeToSimConnect(nau);
                        }
                        catch (Exception ex)
                        {
                            logger.Debug(ex, "Failed to subscribe to {0}", nau);
                        }
                    }
                    else if (reconnectTimer == null)
                    {
                        ConnectNow();
                    }
                }

                SendLastValue(nau, handler);
            }
        }

        private void SendLastValue(NameAndUnits variable, EventHandler<ValueChangedEventArgs> handler)
        {
            if (simState == SimState.SimRunning || (simState == SimState.SimStopped && simconnect != null))
            {
                if (lastReportedValue.TryGetValue(variable, out double lastValue))
                {
                    handler(this, new ValueChangedEventArgs { NewValue = lastValue });
                }
                else
                {
                    VariableHandlerUtils.SendNoValueError(this, handler);
                }
            }
            else
            {
                VariableHandlerUtils.SendNoConnectionError(this, handler);
            }
        }

        public void RemoveListener(IVariable variable, EventHandler<ValueChangedEventArgs> handler)
        {
            var simvar = (SimVarExpression)variable;
            var nau = simvar.NameAndUnits;

            logger.Trace("RemoveListener {0}", variable.Identifier);

            lock (this)
            {
                variableHandlers.TryGetValue(nau, out var existingHandlers);
                var newListeners = (EventHandler<ValueChangedEventArgs>)Delegate.Remove(existingHandlers, handler);

                if (newListeners == null)
                {
                    variableHandlers.Remove(nau);

                    if (this.simconnect != null)
                    {
                        UnsubscribeFromSimConnect(simvar);
                    }
                }
                else
                {
                    variableHandlers[nau] = newListeners;
                }
            }
        }

        private const int WM_USER_SIMCONNECT = 0x0402;

        private SimConnect simconnect;

        public bool IsConnected => simconnect != null;

        /// <summary>
        /// A description of the simulator we're connected to, e.g. "MSFS 2024".
        /// </summary>
        public string SimulatorName { get; private set; }

        public void ReceiveMessage()
        {
            if (simconnect != null)
            {
                try
                {
                    simconnect.ReceiveMessage();
                }
                catch (Exception)
                {
                    // Just squash. Sim's probably about to exit.
                }
            }
        }

        private void ReconnectTimerElapsed(object sender)
        {
            ConnectNow();
        }

        /// <summary>
        /// When set, never connect to the simulator (e.g. when rendering screenshots alongside a copy that's in use:
        /// connecting would reset the shared WASM module's subscriptions).
        /// </summary>
        public static bool Disabled { get; set; }

        private void ConnectNow()
        {
            if (Disabled || !started)
            {
                return;
            }

            if (HWnd == IntPtr.Zero)
            {
                logger.Warn("Not connecting to the simulator: there's no window to receive its messages");
                return;
            }

            logger.Debug("Attempting to connect to simulator");

            if (reconnectTimer != null)
            {
                reconnectTimer.Dispose();
                reconnectTimer = null;
            }

            if (simconnect != null)
            {
                logger.Debug("Already connected");
                return;
            }

            try
            {
                simconnect = new SimConnect("BravoLights", HWnd, WM_USER_SIMCONNECT, null, 0);
                logger.Info("SimConnect connected to simulator successfully");
                connectedAt = DateTime.UtcNow;
                lastReceivedResponseFromSim = connectedAt;
                receivedOpen = false;

                simconnect.OnRecvOpen += Simconnect_OnRecvOpen;
                simconnect.OnRecvSimobjectData += Simconnect_OnRecvSimobjectData;
                simconnect.OnRecvQuit += Simconnect_OnRecvQuit;
                simconnect.OnRecvException += Simconnect_OnRecvException;

                simconnect.OnRecvEvent += Simconnect_OnRecvEvent;
                simconnect.OnRecvEventFilename += Simconnect_OnRecvEventFilename;
                simconnect.SubscribeToSystemEvent(RequestId.SimState, "Sim");
                simconnect.SubscribeToSystemEvent(RequestId.AircraftLoaded, "AircraftLoaded");
                simconnect.SubscribeToSystemEvent(RequestId.FlightLoaded, "FlightLoaded");

                simconnect.OnRecvSystemState += Simconnect_OnRecvSystemState;

                ConfigureAircraftTitle();
                ConfigureWASMComms();
                lock (requestQueue)
                {
                    requestQueue.Clear();
                }
                SendLVarRequest("CLEAR");

                RequestAircraftAndFlightStatus();

                RegisterCurrentVariables();

                // We're connected, but the sim might be in the main menu, so it's not 'running' yet.
                if (simState == SimState.SimExited)
                {
                    RaiseSimStateChanged(SimState.SimStopped);
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Connection failure");
                simconnect = null;

                reconnectTimer = new(ReconnectTimerElapsed, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

                if (simState == SimState.SimRunning)
                {
                    RaiseSimStateChanged(SimState.SimStopped);
                }
            }
        }

        private void Simconnect_OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            lastReceivedResponseFromSim = DateTime.UtcNow;
            receivedOpen = true;

            // Prefer process detection: it's unambiguous. Fall back to the version SimConnect reports.
            string name;
            if (Process.GetProcessesByName(SimInstallation.ProcessNameFor(SimVersion.MSFS2024)).Length > 0)
            {
                name = "MSFS 2024";
            }
            else if (Process.GetProcessesByName(SimInstallation.ProcessNameFor(SimVersion.MSFS2020)).Length > 0)
            {
                name = "MSFS 2020";
            }
            else
            {
                name = data.dwApplicationVersionMajor >= 12 ? "MSFS 2024" : "MSFS 2020";
            }

            SimulatorName = $"{name} ({data.szApplicationName} {data.dwApplicationVersionMajor}.{data.dwApplicationVersionMinor}.{data.dwApplicationBuildMajor}.{data.dwApplicationBuildMinor})";
            logger.Info("Connected to {0}", SimulatorName);
            OnSimulatorInfoChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Simconnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            logger.Debug("SimConnect exception {0} (send id {1}, index {2})", (SIMCONNECT_EXCEPTION)data.dwException, data.dwSendID, data.dwIndex);
        }

        /// <summary>
        /// Asks for the string variables that identify the user's aircraft. Each has its own definition so that a
        /// variable one simulator doesn't recognise can't stop us receiving the others.
        /// </summary>
        private void ConfigureAircraftTitle()
        {
            RequestAircraftString(DefineId.AircraftTitle, RequestId.AircraftTitle, "TITLE");
            RequestAircraftString(DefineId.AircraftAtcModel, RequestId.AircraftAtcModel, "ATC MODEL");
            RequestAircraftString(DefineId.AircraftAtcType, RequestId.AircraftAtcType, "ATC TYPE");
        }

        private void RequestAircraftString(DefineId defineId, RequestId requestId, string simVarName)
        {
            try
            {
                simconnect.AddToDataDefinition(defineId, simVarName, null, SIMCONNECT_DATATYPE.STRING256, 0, SimConnect.SIMCONNECT_UNUSED);
                simconnect.RegisterDataDefineStruct<AircraftTitleStruct>(defineId);
                simconnect.RequestDataOnSimObject(requestId, defineId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Failed to request {0}", simVarName);
            }
        }

        private void ConfigureWASMComms()
        {
            simconnect.MapClientDataNameToID(CDA_NAME_SIMVAR, ClientDataId.LVars);
            simconnect.CreateClientData(ClientDataId.LVars, (uint)Marshal.SizeOf<LVarData>(), SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);

            simconnect.MapClientDataNameToID(CDA_NAME_REQUEST, ClientDataId.Request);
            simconnect.CreateClientData(ClientDataId.Request, RequestResponseSize, SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);

            simconnect.MapClientDataNameToID(CDA_NAME_RESPONSE, ClientDataId.Response);
            simconnect.CreateClientData(ClientDataId.Response, RequestResponseSize, SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);

            simconnect.AddToClientDataDefinition(DefineId.WASMRequestResponse, 0, RequestResponseSize, 0, 0);
            simconnect.RegisterStruct<SIMCONNECT_RECV_CLIENT_DATA, ResponseString>(DefineId.WASMRequestResponse);
            simconnect.RequestClientData(
                ClientDataId.Response,
                RequestId.WASMResponse,
                DefineId.WASMRequestResponse,
                SIMCONNECT_CLIENT_DATA_PERIOD.ON_SET,
                SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT,
                0,
                0,
                0
            );

            simconnect.AddToClientDataDefinition(DefineId.WASMLVars, 0, LVarDataSize, 0, 0);
            simconnect.RegisterStruct<SIMCONNECT_RECV_CLIENT_DATA, LVarData>(DefineId.WASMLVars);
            simconnect.RequestClientData(
                ClientDataId.LVars,
                RequestId.WASMLVars,
                DefineId.WASMLVars,
                SIMCONNECT_CLIENT_DATA_PERIOD.ON_SET,
                SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT,
                0,
                0,
                0
            );

            simconnect.OnRecvClientData += SimConnect_OnRecvClientData;
        }

        private void RequestAircraftAndFlightStatus()
        {
            simconnect.RequestSystemState(RequestId.AircraftLoaded, "AircraftLoaded");
            simconnect.RequestSystemState(RequestId.FlightLoaded, "FlightLoaded");
        }

        private void Simconnect_OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
        {
            lock (this)
            {
                logger.Debug("SimConnectOnRecvEvent {0}", data.uEventID);
                lastReceivedResponseFromSim = DateTime.UtcNow;

                switch ((RequestId)data.uEventID)
                {
                    case RequestId.SimState:
                        logger.Trace("SimState {0}", data.dwData);
                        RaiseSimStateChanged(data.dwData == 1 ? SimState.SimRunning : SimState.SimStopped);
                        break;
                }
            }
        }

        private void Simconnect_OnRecvEventFilename(SimConnect sender, SIMCONNECT_RECV_EVENT_FILENAME data)
        {
            lock (this)
            {
                lastReceivedResponseFromSim = DateTime.UtcNow;

                switch ((RequestId)data.uEventID)
                {
                    case RequestId.AircraftLoaded:
                        HandleAircraftChanged(data.szFileName);
                        break;
                    case RequestId.FlightLoaded:
                        HandleFlightLoaded(data.szFileName);
                        break;
                }
            }
        }

        private void Simconnect_OnRecvSystemState(SimConnect sender, SIMCONNECT_RECV_SYSTEM_STATE data)
        {
            lock (this)
            {
                logger.Debug("SimConnectOnRecvSystemState {0}", data.dwRequestID);
                lastReceivedResponseFromSim = DateTime.UtcNow;

                switch ((RequestId)data.dwRequestID)
                {
                    case RequestId.SimState:
                        RaiseSimStateChanged(data.dwInteger == 1 ? SimState.SimRunning : SimState.SimStopped);
                        break;
                    case RequestId.AircraftLoaded:
                        HandleAircraftChanged(data.szString);
                        break;
                    case RequestId.FlightLoaded:
                        HandleFlightLoaded(data.szString);
                        break;
                }
            }
        }

        private Timer periodicLVarTimer = null;

        private void PeriodicLVarTimerElapsed(object state)
        {
            logger.Debug("PeriodicLVarTimerElapsed");
            ScheduleLVarCheck();
        }

        private void RaiseSimStateChanged(SimState state)
        {
            lock (this)
            {
                if (simState == state)
                {
                    // State is unchanged
                    return;
                }

                logger.Debug("SimStateChange {0}", state);

                simState = state;

                if (periodicLVarTimer != null)
                {
                    periodicLVarTimer.Dispose();
                    periodicLVarTimer = null;
                }

                if (simState == SimState.SimRunning)
                {
                    periodicLVarTimer = new(PeriodicLVarTimerElapsed, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                    ScheduleLVarCheck();
                }

                if (simState != SimState.SimExited && detectExitTimer == null)
                {
                    // This is the first time we've connected to the sim. Start polling it to check it's still there.
                    detectExitTimer = new(ExitDetectionTimerElapsed, null, TimeSpan.Zero, SimLivenessPollPeriod);
                }

                if (simState == SimState.SimExited)
                {
                    // The sim has exited. We're likely to be exiting soon, but in
                    // case we don't, we should tidy up.

                    // Any previous values and subscriptions/definitions are now not valid.
                    idToName.Clear();
                    nameToId.Clear();
                    lastReportedValue.Clear();

                    lvarCheckTimer?.Dispose();
                    lvarCheckTimer = null;
                    periodicLVarTimer?.Dispose();
                    periodicLVarTimer = null;
                    detectExitTimer?.Dispose();
                    detectExitTimer = null;
                    wasmTimeoutTimer?.Dispose();
                    wasmTimeoutTimer = null;
                    hasEverCheckedForLVars = false;
                    lock (requestQueue)
                    {
                        requestQueue.Clear();
                    }

                    // SimConnect has gone away; presumably MSFS has exited.
                    // BetterBravoLights itself might not be exiting (if it's being run without install),
                    // but we need to clean up these registrations.
                    // (We don't Dispose it here: we may be inside one of its own callbacks.)
                    simconnect = null;
                    SimulatorName = null;
                    OnSimulatorInfoChanged?.Invoke(this, EventArgs.Empty);

                    LVarManager.Connection.ModuleStatus = LVarModuleStatus.Unknown;

                    // Update all existing subscribers
                    foreach (var kvp in variableHandlers)
                    {
                        SendLastValue(kvp.Key, kvp.Value);
                    }

                    CurrentAircraft = AircraftInfo.None;
                    OnAircraftLoaded?.Invoke(this, new AircraftEventArgs { Aircraft = AircraftInfo.None });

                    // Keep trying to connect in case the sim is started again (if we're not exiting)
                    reconnectTimer ??= new(ReconnectTimerElapsed, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
                }
                else if (simconnect != null)
                {
                    // Despite subscribing to AircraftLoaded and FlightLoaded events, we don't always
                    // receive them, so we explicitly ask for them when we get SimStart/SimStop events.
                    try
                    {
                        RequestAircraftAndFlightStatus();
                    }
                    catch (Exception ex)
                    {
                        logger.Debug(ex, "Failed to request aircraft status");
                    }
                }

                OnSimStateChanged?.Invoke(this, new SimStateEventArgs { SimState = state });
            }
        }

        public AircraftInfo CurrentAircraft { get; private set; } = AircraftInfo.None;

        private string lastAircraftPath;

        private void HandleAircraftChanged(string aircraftPath)
        {
            logger.Info("AircraftChanged {0}", aircraftPath);

            // MSFS 2024 sometimes reports a partial path from RequestSystemState after the event has reported the full one.
            // If the new one is just a suffix of what we had, keep what we had.
            if (lastAircraftPath != null && aircraftPath != null && aircraftPath.Length < lastAircraftPath.Length &&
                lastAircraftPath.EndsWith(aircraftPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            lastAircraftPath = aircraftPath;

            // Keep the details we already have from simulation variables until updated ones arrive
            var info = AircraftInfo.FromSimPath(aircraftPath).WithDetails(CurrentAircraft.Title, CurrentAircraft.AtcModel, CurrentAircraft.AtcType);
            SetAircraft(info);

            // Note: LVars are not registered by an aircraft until a little while _after_ it has loaded.
            // So the first time we get an AircraftChanged event, the lvars will not be present.
            // However, we request aircraft + flight information on each SimStart/SimStop, which should catch them.
            ScheduleLVarCheck();
        }

        private void SetAircraft(AircraftInfo info)
        {
            if (info.Equals(CurrentAircraft))
            {
                return;
            }

            logger.Info("Aircraft is now {0}", info);
            CurrentAircraft = info;
            OnAircraftLoaded?.Invoke(this, new AircraftEventArgs { Aircraft = info });
        }

        private void HandleFlightLoaded(string flightPath)
        {
            // Examples:
            // flights\other\MainMenu.FLT
            // C:\Users\royston\AppData\Local\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalState\MISSIONS\ACTIVITIES\ASOBO-BUSHTRIP-FINLAND_SAVE\ASOBO-BUSHTRIP-FINLAND_SAVE\ASOBO-BUSHTRIP-FINLAND_SAVE.FLT
            // missions\Asobo\Tutorials\VFRNavigation\LandmarkNavigation\03_Training_LandmarkNavigation.FLT

            logger.Info("FlightLoaded {0}", flightPath);

            InMainMenu = flightPath != null && flightPath.EndsWith("mainmenu.flt", StringComparison.InvariantCultureIgnoreCase);
            ScheduleLVarCheck();
        }

        /// <summary>
        /// Called when we receive some event that suggests that the simulator LVars may have changed, e.g. aircraft/flight change/simstart/simstop
        /// </summary>
        private void ScheduleLVarCheck()
        {
            logger.Debug("ScheduleLVarCheck");

            if (lvarCheckTimer == null)
            {
                lvarCheckTimer = new(LVarCheckTimerElapsed, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            }
        }

        private Timer lvarCheckTimer = null;

        private void LVarCheckTimerElapsed(object state)
        {
            logger.Debug("LVarCheckTimerElapsed");
            lvarCheckTimer?.Dispose();
            lvarCheckTimer = null;

            CheckForNewLVars();
        }

        private bool hasEverCheckedForLVars = false;

        /// <summary>
        /// Asks the WASM module to check for new LVars.
        /// </summary>
        private void CheckForNewLVars()
        {
            logger.Debug("CheckForNewLVars");

            if (simconnect == null)
            {
                return;
            }

            if (hasEverCheckedForLVars)
            {
                // Ask the WASM module to check for new lvars
                SendLVarRequest($"CHECKLVARS");
            }
            else
            {
                RequestFullLVarList();
            }
        }

        /// <summary>
        /// Asks the WASM module for the complete list of LVars, whether or not it has changed.
        /// </summary>
        public void RequestFullLVarList()
        {
            if (simconnect == null)
            {
                return;
            }

            hasEverCheckedForLVars = true;
            SendLVarRequest($"LISTLVARS");

            // If the WASM module doesn't answer, it's probably not installed in this simulator.
            if (LVarManager.Connection.ModuleStatus != LVarModuleStatus.Responding)
            {
                wasmTimeoutTimer?.Dispose();
                wasmTimeoutTimer = new Timer(_ =>
                {
                    if (LVarManager.Connection.ModuleStatus != LVarModuleStatus.Responding && simconnect != null)
                    {
                        logger.Warn("The LVar WASM module has not responded");
                        LVarManager.Connection.ModuleStatus = LVarModuleStatus.NotResponding;
                    }
                }, null, WasmResponseTimeout, Timeout.InfiniteTimeSpan);
            }
        }

        private Timer wasmTimeoutTimer;

        public event EventHandler OnInMainMenuChanged;

        private bool inMainMenu = true;
        public bool InMainMenu
        {
            get
            {
                return inMainMenu;
            }
            private set
            {
                if (inMainMenu == value)
                {
                    return;
                }

                CheckForNewLVars();
                inMainMenu = value;
                OnInMainMenuChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        // Called when MSFS quits
        private void Simconnect_OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            logger.Info("SimConnectOnRecvQuit");

            RaiseSimStateChanged(SimState.SimExited);
        }

        private void RegisterCurrentVariables()
        {
            foreach (var nau in this.variableHandlers.Keys)
            {
                SubscribeToSimConnect(nau);
            }
        }

        private List<string> incomingLVars = new();

        private WASMReaderState readerState = WASMReaderState.Neutral;

        private void SimConnect_OnRecvClientData(SimConnect sender, SIMCONNECT_RECV_CLIENT_DATA data)
        {
            lastReceivedResponseFromSim = DateTime.UtcNow;

            switch ((RequestId)data.dwRequestID)
            {
                case RequestId.WASMResponse:
                    {
                        LVarManager.Connection.ModuleStatus = LVarModuleStatus.Responding;

                        var responseString = ((ResponseString)data.dwData[0]).Data;

                        switch (responseString)
                        {
                            case RESPONSE_LVAR_START:
                                readerState = WASMReaderState.ReadingLVars;
                                incomingLVars = new();
                                return;
                            case RESPONSE_LVAR_END:
                                readerState = WASMReaderState.Neutral;
                                var newLVars = incomingLVars;
                                incomingLVars = new();
                                LVarManager.Connection.UpdateLVarList(newLVars);
                                return;
                        }

                        if (readerState == WASMReaderState.ReadingLVars)
                        {
                            incomingLVars.Add(responseString);
                            return;
                        }
                    }
                    break;
                case RequestId.WASMLVars:
                    {
                        LVarManager.Connection.ModuleStatus = LVarModuleStatus.Responding;
                        var lvarUpdate = ((LVarData)data.dwData[0]);
                        LVarManager.Connection.UpdateLVarValues(lvarUpdate);
                    }
                    break;
            }
        }

        private void Simconnect_OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            lock (this)
            {
                lastReceivedResponseFromSim = DateTime.UtcNow;

                switch ((RequestId)data.dwRequestID)
                {
                    case RequestId.AircraftTitle:
                        SetAircraft(CurrentAircraft.WithDetails(title: ((AircraftTitleStruct)data.dwData[0]).Title ?? ""));
                        return;
                    case RequestId.AircraftAtcModel:
                        SetAircraft(CurrentAircraft.WithDetails(atcModel: ((AircraftTitleStruct)data.dwData[0]).Title ?? ""));
                        return;
                    case RequestId.AircraftAtcType:
                        SetAircraft(CurrentAircraft.WithDetails(atcType: ((AircraftTitleStruct)data.dwData[0]).Title ?? ""));
                        return;
                }

                if (idToName.TryGetValue(data.dwRequestID, out NameAndUnits nau))
                {
                    var newValue = (double)data.dwData[0];

                    logger.Trace("RecvSimObjectData for {0} ({1}) = {2}", nau, data.dwRequestID, newValue);

                    variableHandlers.TryGetValue(nau, out var handlers);

                    if (handlers != null)
                    {
                        lastReportedValue[nau] = newValue;
                        var e = new ValueChangedEventArgs { NewValue = newValue };
                        handlers(this, e);
                    }
                }
                else
                {
                    logger.Trace("RecvSimObjectData for unexpected id {0}", data.dwRequestID);
                }
            }
        }

        public static IntPtr HWnd { get; set; }

        public event EventHandler<AircraftEventArgs> OnAircraftLoaded;
        public event EventHandler OnSimulatorInfoChanged;

        public SimState SimState
        {
            get { return simState; }
        }
        public event EventHandler<SimStateEventArgs> OnSimStateChanged;

        #region WASM request queue

        // The WASM module has a single 256-byte request area. Sending thousands of requests at once (e.g. when the
        // LVar scanner subscribes to every LVar) risks flooding SimConnect, so requests are queued and sent in batches.
        private readonly Queue<string> requestQueue = new();
        private readonly Timer requestQueueTimer;
        private const int RequestsPerBatch = 100;
        private static readonly TimeSpan RequestBatchInterval = TimeSpan.FromMilliseconds(25);
        private bool requestQueueTimerRunning;

        private void SendLVarRequest(string message)
        {
            lock (requestQueue)
            {
                requestQueue.Enqueue(message);
                if (!requestQueueTimerRunning)
                {
                    requestQueueTimerRunning = true;
                    requestQueueTimer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
            }
        }

        private void DrainRequestQueue()
        {
            var batch = new List<string>();
            lock (requestQueue)
            {
                while (batch.Count < RequestsPerBatch && requestQueue.Count > 0)
                {
                    batch.Add(requestQueue.Dequeue());
                }
            }

            foreach (var message in batch)
            {
                SendLVarRequestNow(message);
            }

            lock (requestQueue)
            {
                if (requestQueue.Count > 0)
                {
                    requestQueueTimer.Change(RequestBatchInterval, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    requestQueueTimerRunning = false;
                }
            }
        }

        public int PendingRequestCount
        {
            get
            {
                lock (requestQueue)
                {
                    return requestQueue.Count;
                }
            }
        }

        private void SendLVarRequestNow(string message)
        {
            logger.Trace("Sending LVarRequest {0}", message);

            var sc = simconnect;
            if (sc == null)
            {
                return;
            }

            var cmd = new RequestString(message);
            try
            {
                sc.SetClientData(
                    ClientDataId.Request,
                    DefineId.WASMRequestResponse,
                    SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT,
                    0,
                    cmd
                );
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Failed to send WASM request");
                RaiseSimStateChanged(SimState.SimExited);
            }
        }

        #endregion

        void IWASMChannel.ClearSubscriptions()
        {
            lock (requestQueue)
            {
                // Any queued subscription changes are now irrelevant
                requestQueue.Clear();
            }
            SendLVarRequest("CLEAR");
        }

        void IWASMChannel.Subscribe(short id)
        {
            var message = $"SUBSCRIBE {id.ToString(CultureInfo.InvariantCulture)}";
            SendLVarRequest(message);
        }

        void IWASMChannel.Unsubscribe(short id)
        {
            var message = $"UNSUBSCRIBE {id.ToString(CultureInfo.InvariantCulture)}";
            SendLVarRequest(message);
        }

        /// <summary>
        /// Drops the current connection (keeping our subscriptions) and opens a new one, which re-registers everything.
        /// </summary>
        private void Reconnect()
        {
            lock (this)
            {
                var old = simconnect;
                simconnect = null;
                hasEverCheckedForLVars = false;
                lock (requestQueue)
                {
                    requestQueue.Clear();
                }

                try
                {
                    old?.Dispose();
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "Failed to close the old connection");
                }

                ConnectNow();
            }
        }

        private void ExitDetectionTimerElapsed(object sender)
        {
            if (simconnect != null)
            {
                try
                {
                    simconnect.RequestSystemState(RequestId.SimState, "Sim");
                }
                catch (Exception)
                {
                    // Sim has probably exited, but wait for the timer to expire, to make sure
                    // it's not just a temporary glitch.
                }
            }

            if (lastReceivedResponseFromSim < DateTime.UtcNow.Subtract(ExitWhenNotHeardFromSimFor))
            {
                logger.Info("No response from the simulator for a while. Checking if it's still running.");

                // It's been a while since we received anything from the sim.
                // At the very least it's not responding.
                // If the FS .exe has also gone (MSFS 2020 or 2024), it's time for us to exit.
                var fsRunning = SimInstallation.AllProcessNames.Any(name => Process.GetProcessesByName(name).Length > 0);

                if (fsRunning)
                {
                    logger.Info("Simulator is still running.");

                    // A connection the simulator has never answered is no use to us; drop it and connect again.
                    if (simconnect != null && !receivedOpen && DateTime.UtcNow - connectedAt > ReconnectIfNoAnswerAfter)
                    {
                        logger.Warn("The simulator never answered our connection; reconnecting");
                        Reconnect();
                    }
                }
                else
                {
                    logger.Info("Simulator has exited (crashed?)");
                    RaiseSimStateChanged(SimState.SimExited);
                }
            }
        }
    }

    public enum SimState
    {
        SimRunning,
        SimStopped,
        SimExited
    }

    class NameAndUnits
    {
        public string Name;
        public string Units;

        public override string ToString()
        {
            return $"{Name}, {Units}";
        }
    }

    class NameAndUnitsComparer : IEqualityComparer<NameAndUnits>
    {
        public bool Equals(NameAndUnits x, NameAndUnits y)
        {
            return x.Name == y.Name && x.Units == y.Units;
        }

        public int GetHashCode([DisallowNull] NameAndUnits obj)
        {
            return obj.Name.GetHashCode();
        }
    }

    public class SimStateEventArgs : EventArgs
    {
        public SimState SimState;
    }

    class AircraftEventArgs : EventArgs
    {
        public AircraftInfo Aircraft;
    }
}
