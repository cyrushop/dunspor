using System;
using Fx9600Gpio.Core;
using Symbol.RFID3;

namespace Fx9600Gpio
{
    /// <summary>
    /// Zebra SDK wrapper: connection, GPIO, inventory and events for one FX9600.
    ///
    /// SDK events arrive on SDK threads. This class only reads tags in ReadNotify (as the
    /// SDK guide does) and raises its own events; the caller must hand them to its worker
    /// thread and make every other call from that thread.
    /// </summary>
    internal sealed class FxReader : IPortalHardware, IDisposable
    {
        private readonly PortalOptions _options;
        private RFIDReader _reader;
        private bool _inventoryRunning;

        public FxReader(PortalOptions options)
        {
            _options = options;
        }

        /// <summary>GPI port changed. Args: port, levelHigh (true = low-to-high).</summary>
        public event Action<int, bool> GpiChanged;

        /// <summary>A tag was reported. Args: EPC, antenna, peak RSSI.</summary>
        public event Action<string, int, int> TagRead;

        /// <summary>The SDK reported a disconnection. Arg: reason.</summary>
        public event Action<string> Disconnected;

        public bool IsConnected
        {
            get { return _reader != null && _reader.IsConnected; }
        }

        public int NumGpiPorts { get; private set; }
        public int NumGpoPorts { get; private set; }
        public string ReaderDescription { get; private set; }

        /// <summary>
        /// Opens a fresh LLRP connection and configures GPIO and events. Any earlier
        /// connection is closed first. Throws if the reader cannot be reached or the
        /// configured ports do not exist on it.
        /// </summary>
        public void Connect()
        {
            Close();

            var reader = new RFIDReader(_options.ReaderHost, _options.ReaderPort, _options.ReaderTimeoutMs);
            reader.Connect();
            try
            {
                NumGpiPorts = reader.ReaderCapabilities.NumGPIPorts;
                NumGpoPorts = reader.ReaderCapabilities.NumGPOPorts;
                ReaderDescription = SafeDescribe(reader);

                var errors = _options.Validate(NumGpoPorts, NumGpiPorts);
                if (errors.Count > 0)
                    throw new InvalidOperationException("Settings do not fit this reader: " + string.Join(" ", errors));

                // A GPI port must be enabled before its events are reported.
                for (int port = 1; port <= NumGpiPorts; port++)
                    reader.Config.GPI[port].Enable = true;

                reader.Events.AttachTagDataWithReadEvent = false;
                reader.Events.ReadNotify += (s, e) => OnReadNotify(reader);
                reader.Events.StatusNotify += (s, e) => OnStatusNotify(e);
                reader.Events.NotifyGPIEvent = true;
                reader.Events.NotifyReaderDisconnectEvent = true;
            }
            catch
            {
                TryDisconnect(reader);
                throw;
            }

            _reader = reader;
            _inventoryRunning = false;
        }

        /// <summary>
        /// Sets the reader's GPI debounce time through the Reader Management interface.
        /// Needs the reader's web admin login. Skipped when GpiDebounceMs is 0 or no
        /// password is configured. Returns a status message.
        /// </summary>
        public string ApplyGpiDebounce()
        {
            if (_options.GpiDebounceMs == 0) return "GPI debounce: unchanged (GpiDebounceMs=0).";
            if (string.IsNullOrEmpty(_options.RmPassword))
                return "GPI debounce: skipped, RmPassword is empty.";

            var rm = new ReaderManagement();
            try
            {
                var login = new LoginInfo
                {
                    HostName = _options.ReaderHost,
                    UserName = _options.RmUser,
                    Password = _options.RmPassword,
                    SecureMode = SECURE_MODE.HTTP,
                };
                rm.Login(login, READER_TYPE.FX);
                uint before = rm.GPIDebounceTimeMilliseconds;
                rm.GPIDebounceTimeMilliseconds = _options.GpiDebounceMs;
                return "GPI debounce: " + before + " ms -> " + rm.GPIDebounceTimeMilliseconds + " ms.";
            }
            finally
            {
                try
                {
                    if (rm.IsLoggedIn) rm.Logout();
                }
                catch (Exception)
                {
                    // Logout failure does not matter here.
                }
                rm.Dispose();
            }
        }

        // ---- IPortalHardware ----

        public void SetLamps(LampState lamps)
        {
            SetLamp(_options.RedGpo, lamps.Red);
            SetLamp(_options.GreenGpo, lamps.Green);
            SetLamp(_options.BlueGpo, lamps.Blue);
        }

        public void StartInventory()
        {
            Reader.Actions.Inventory.Perform();
            _inventoryRunning = true;
        }

        public void StopInventory()
        {
            if (!_inventoryRunning) return;
            Reader.Actions.Inventory.Stop();
            _inventoryRunning = false;
        }

        // ---- raw GPIO, for --test ----

        public void SetGpoRaw(int port, bool portStateTrue)
        {
            Reader.Config.GPO[port].PortState = portStateTrue ? GPOs.GPO_PORT_STATE.TRUE : GPOs.GPO_PORT_STATE.FALSE;
        }

        /// <summary>"HIGH", "LOW" or "UNKNOWN".</summary>
        public string GetGpiLevel(int port)
        {
            var state = Reader.Config.GPI[port].PortState;
            if (state == GPIs.GPI_PORT_STATE.GPI_PORT_STATE_HIGH) return "HIGH";
            if (state == GPIs.GPI_PORT_STATE.GPI_PORT_STATE_LOW) return "LOW";
            return "UNKNOWN";
        }

        /// <summary>Stops inventory, turns lamps off and disconnects. Never throws.</summary>
        public void Shutdown()
        {
            if (_reader == null) return;
            try
            {
                if (_reader.IsConnected)
                {
                    StopInventory();
                    SetLamps(LampState.Off);
                }
            }
            catch (Exception)
            {
                // Best effort on the way out.
            }
            Close();
        }

        public void Dispose()
        {
            Shutdown();
        }

        private RFIDReader Reader
        {
            get
            {
                if (_reader == null) throw new InvalidOperationException("Reader is not connected.");
                return _reader;
            }
        }

        private void SetLamp(int port, bool on)
        {
            bool portStateTrue = on == _options.LampOnState;
            SetGpoRaw(port, portStateTrue);
        }

        private void OnReadNotify(RFIDReader reader)
        {
            var handler = TagRead;
            try
            {
                while (true)
                {
                    TagData[] tags = reader.Actions.GetReadTags(100);
                    if (tags == null || tags.Length == 0) return;
                    foreach (var tag in tags)
                    {
                        if (tag == null || tag.OpCode != ACCESS_OPERATION_CODE.ACCESS_OPERATION_NONE) continue;
                        if (handler != null) handler(tag.TagID, tag.AntennaID, tag.PeakRSSI);
                    }
                }
            }
            catch (Exception)
            {
                // Reading tags can fail while the connection drops; the disconnect
                // event or the next reader call reports that.
            }
        }

        private void OnStatusNotify(Events.StatusEventArgs e)
        {
            var data = e.StatusEventData;
            switch (data.StatusEventType)
            {
                case Events.STATUS_EVENT_TYPE.GPI_EVENT:
                    var gpi = GpiChanged;
                    if (gpi != null) gpi(data.GPIEventData.PortNumber, data.GPIEventData.GPIEvent);
                    break;

                case Events.STATUS_EVENT_TYPE.DISCONNECTION_EVENT:
                    var disc = Disconnected;
                    if (disc != null) disc(data.DisconnectionEventData.DisconnectEventInfo.ToString());
                    break;
            }
        }

        private void Close()
        {
            var reader = _reader;
            _reader = null;
            _inventoryRunning = false;
            if (reader != null) TryDisconnect(reader);
        }

        private static void TryDisconnect(RFIDReader reader)
        {
            try
            {
                reader.Disconnect();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        private static string SafeDescribe(RFIDReader reader)
        {
            try
            {
                var id = reader.ReaderCapabilities.ReaderID;
                return reader.ReaderCapabilities.ModelName + " " + id.ID;
            }
            catch (Exception)
            {
                return reader.HostName;
            }
        }
    }
}
