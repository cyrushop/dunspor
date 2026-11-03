using System;
using System.Collections.Generic;

namespace Fx9600Gpio.Core
{
    /// <summary>
    /// Portal logic for one FX9600:
    ///
    ///   Ready (blue) --start sensor--> Reading (lamps off, green once MinTags read)
    ///   Reading --stop sensor / start sensor released / MaxReadMs--> Stopping (StopDelayMs)
    ///   Stopping --> Result (green = OK, red = not enough tags) for ResultHoldMs --> Ready
    ///   Result --start sensor--> Reading (next item, no wait)
    ///   any --disconnect / reader error--> Fault (red + blue) --reconnect--> Ready
    ///
    /// Not thread-safe. Call every method from one thread (the app's worker loop), and call
    /// OnTick regularly so deadlines fire. Times are UTC.
    /// </summary>
    public sealed class PortalStateMachine
    {
        private readonly PortalOptions _options;
        private readonly IPortalHardware _hardware;
        private readonly List<string> _tagOrder = new List<string>();
        private readonly HashSet<string> _tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private DateTime _cycleStartUtc;
        private DateTime _readDeadlineUtc;
        private DateTime _stateDeadlineUtc;
        private string _endReason;
        private int _cycleNumber;
        private LampState? _lamps;
        private DateTime _nowUtc;

        public PortalStateMachine(PortalOptions options, IPortalHardware hardware)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (hardware == null) throw new ArgumentNullException(nameof(hardware));
            var errors = options.Validate();
            if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(options));
            _options = options;
            _hardware = hardware;
            State = PortalState.Fault;
        }

        public PortalState State { get; private set; }

        /// <summary>Unique tags read in the current cycle.</summary>
        public int TagCount
        {
            get { return _tagOrder.Count; }
        }

        /// <summary>Next time OnTick has work to do, or null if none.</summary>
        public DateTime? NextDeadlineUtc
        {
            get
            {
                switch (State)
                {
                    case PortalState.Reading: return _readDeadlineUtc;
                    case PortalState.Stopping:
                    case PortalState.Result: return _stateDeadlineUtc;
                    default: return null;
                }
            }
        }

        /// <summary>Raised when a cycle ends, including aborted cycles.</summary>
        public event Action<CycleResult> CycleCompleted;

        /// <summary>Raised when a reader call fails. The app should reconnect.</summary>
        public event Action<string> FaultRaised;

        /// <summary>Human-readable state changes, for the console log.</summary>
        public event Action<string> Log;

        /// <summary>Call once after the reader is connected and configured.</summary>
        public void Start(DateTime nowUtc)
        {
            _nowUtc = nowUtc;
            EnterReady();
        }

        /// <summary>
        /// A GPI changed level. levelHigh is GPIEventData.GPIEvent (true = low-to-high).
        /// </summary>
        public void OnGpi(int port, bool levelHigh, DateTime nowUtc)
        {
            _nowUtc = nowUtc;
            bool active = _options.SensorActiveLow ? !levelHigh : levelHigh;
            bool isStart = port == _options.StartSensorGpi;
            bool isStop = _options.StopSensorGpi != 0 && port == _options.StopSensorGpi;
            if (!isStart && !isStop) return;

            switch (State)
            {
                case PortalState.Ready:
                case PortalState.Result:
                    if (isStart && active) BeginCycle(nowUtc);
                    break;

                case PortalState.Reading:
                    if (isStop && active)
                        BeginStopping("stop-sensor", nowUtc);
                    else if (isStart && !active && _options.StopSensorGpi == 0)
                        BeginStopping("start-sensor-released", nowUtc);
                    break;

                // Stopping: already ending. Fault: ignore until reconnected.
            }
        }

        /// <summary>A tag was read. Returns true if it is new in this cycle.</summary>
        public bool OnTag(string epc, DateTime nowUtc)
        {
            _nowUtc = nowUtc;
            if (State != PortalState.Reading && State != PortalState.Stopping) return false;
            if (string.IsNullOrEmpty(epc)) return false;
            if (!_tagSet.Add(epc)) return false;

            _tagOrder.Add(epc);
            if (_tagOrder.Count == _options.MinTags) SetLamps(LampState.GreenOnly);
            return true;
        }

        /// <summary>Fires any deadline that has passed.</summary>
        public void OnTick(DateTime nowUtc)
        {
            _nowUtc = nowUtc;
            switch (State)
            {
                case PortalState.Reading:
                    if (nowUtc >= _readDeadlineUtc)
                    {
                        // MaxReadMs is a hard limit: end now, no extra StopDelayMs.
                        _endReason = "timeout";
                        EndCycle(nowUtc);
                    }
                    break;

                case PortalState.Stopping:
                    if (nowUtc >= _stateDeadlineUtc || nowUtc >= _readDeadlineUtc) EndCycle(nowUtc);
                    break;

                case PortalState.Result:
                    if (nowUtc >= _stateDeadlineUtc) EnterReady();
                    break;
            }
        }

        /// <summary>The SDK reported a disconnection. Lamps cannot be changed until reconnected.</summary>
        public void OnDisconnected(DateTime nowUtc)
        {
            _nowUtc = nowUtc;
            if (State == PortalState.Fault) return;
            AbortCycle("disconnected", nowUtc);
            State = PortalState.Fault;
            _lamps = null; // reader state unknown; re-apply on reconnect
            RaiseLog("Fault: reader disconnected.");
        }

        /// <summary>The app reconnected and reconfigured the reader.</summary>
        public void OnReconnected(DateTime nowUtc)
        {
            _nowUtc = nowUtc;
            if (State != PortalState.Fault) return;
            _lamps = null;
            EnterReady();
        }

        private void EnterReady()
        {
            ClearTags();
            State = PortalState.Ready;
            RaiseLog("Ready.");
            SetLamps(LampState.BlueOnly);
        }

        private void BeginCycle(DateTime nowUtc)
        {
            ClearTags();
            _cycleNumber++;
            _cycleStartUtc = nowUtc;
            _readDeadlineUtc = nowUtc.AddMilliseconds(_options.MaxReadMs);
            _endReason = null;
            State = PortalState.Reading;
            RaiseLog("Cycle " + _cycleNumber + ": reading.");

            if (!SetLamps(LampState.Off)) return;
            if (!TryHardware(_hardware.StartInventory, "StartInventory")) return;
        }

        private void BeginStopping(string reason, DateTime nowUtc)
        {
            _endReason = reason;
            _stateDeadlineUtc = nowUtc.AddMilliseconds(_options.StopDelayMs);
            State = PortalState.Stopping;
            if (_options.StopDelayMs == 0) EndCycle(nowUtc);
        }

        private void EndCycle(DateTime nowUtc)
        {
            if (!TryHardware(_hardware.StopInventory, "StopInventory")) return;

            var outcome = _tagOrder.Count >= _options.MinTags ? CycleOutcome.Ok : CycleOutcome.NotEnoughTags;
            State = PortalState.Result;
            _stateDeadlineUtc = nowUtc.AddMilliseconds(_options.ResultHoldMs);
            RaiseCycle(outcome, nowUtc);
            if (!SetLamps(outcome == CycleOutcome.Ok ? LampState.GreenOnly : LampState.RedOnly)) return;
            if (_options.ResultHoldMs == 0) EnterReady();
        }

        private void AbortCycle(string reason, DateTime nowUtc)
        {
            if (State != PortalState.Reading && State != PortalState.Stopping) return;
            _endReason = reason;
            RaiseCycle(CycleOutcome.Aborted, nowUtc);
        }

        private bool SetLamps(LampState lamps)
        {
            if (_lamps.HasValue && _lamps.Value.Equals(lamps)) return true;
            if (!TryHardware(() => _hardware.SetLamps(lamps), "SetLamps")) return false;
            _lamps = lamps;
            return true;
        }

        private bool TryHardware(Action call, string what)
        {
            try
            {
                call();
                return true;
            }
            catch (Exception ex)
            {
                EnterFault(what + " failed: " + ex.Message, _nowUtc);
                return false;
            }
        }

        private void EnterFault(string reason, DateTime nowUtc)
        {
            if (State == PortalState.Fault) return;
            AbortCycle("reader-error", nowUtc);
            State = PortalState.Fault;
            RaiseLog("Fault: " + reason);

            // Try to show the fault. This may fail too if the reader is unreachable.
            try
            {
                _hardware.SetLamps(LampState.RedBlue);
                _lamps = LampState.RedBlue;
            }
            catch (Exception)
            {
                _lamps = null;
            }

            var handler = FaultRaised;
            if (handler != null) handler(reason);
        }

        private void RaiseCycle(CycleOutcome outcome, DateTime nowUtc)
        {
            var result = new CycleResult
            {
                CycleNumber = _cycleNumber,
                StartUtc = _cycleStartUtc,
                EndUtc = nowUtc,
                Outcome = outcome,
                EndReason = _endReason ?? "",
                Tags = _tagOrder.ToArray(),
            };
            var handler = CycleCompleted;
            if (handler != null) handler(result);
        }

        private void ClearTags()
        {
            _tagOrder.Clear();
            _tagSet.Clear();
        }

        private void RaiseLog(string message)
        {
            var handler = Log;
            if (handler != null) handler(message);
        }
    }
}
