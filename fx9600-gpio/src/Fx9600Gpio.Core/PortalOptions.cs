using System;
using System.Collections.Generic;
using System.Globalization;

namespace Fx9600Gpio.Core
{
    /// <summary>
    /// All settings for the portal. Defaults match the wiring in FX9600 Integration Guide
    /// Figure 139: red/green/blue lamps on GP O/P #1/#2/#3, sensors on GP I/P #1/#2.
    /// </summary>
    public sealed class PortalOptions
    {
        // Reader connection
        public string ReaderHost { get; set; } = "192.168.1.100";
        /// <summary>LLRP port. 0 = SDK default (5084).</summary>
        public uint ReaderPort { get; set; }
        /// <summary>Response timeout in ms. 0 = SDK default (5000).</summary>
        public uint ReaderTimeoutMs { get; set; }
        public int ReconnectIntervalMs { get; set; } = 5000;

        // Lamps (GPO port numbers, 1-based)
        public int RedGpo { get; set; } = 1;
        public int GreenGpo { get; set; } = 2;
        public int BlueGpo { get; set; } = 3;
        /// <summary>
        /// GPO PortState that lights a lamp. true = GPO_PORT_STATE.TRUE lights it.
        /// Not verified for FX9600; run the program with --test to find the right value.
        /// </summary>
        public bool LampOnState { get; set; } = true;

        // Sensors (GPI port numbers, 1-based; 0 = not used)
        public int StartSensorGpi { get; set; } = 1;
        public int StopSensorGpi { get; set; } = 2;
        /// <summary>
        /// true = sensor is "active" when its GPI reads LOW (contact closed to GPIO GND,
        /// as wired in Figure 139).
        /// </summary>
        public bool SensorActiveLow { get; set; } = true;

        // Read cycle
        /// <summary>Hard limit for one read cycle.</summary>
        public int MaxReadMs { get; set; } = 10000;
        /// <summary>Keep reading this long after the stop condition, to catch late tags.</summary>
        public int StopDelayMs { get; set; } = 300;
        /// <summary>Unique tags needed for an OK result.</summary>
        public int MinTags { get; set; } = 1;
        /// <summary>How long the green/red result lamp stays on.</summary>
        public int ResultHoldMs { get; set; } = 2000;

        // Optional reader management (GPI debounce needs an admin login)
        /// <summary>0 = leave the reader's debounce setting unchanged.</summary>
        public uint GpiDebounceMs { get; set; }
        public string RmUser { get; set; } = "admin";
        public string RmPassword { get; set; } = "";

        /// <summary>Folder for the per-day cycle CSV. Empty = no CSV.</summary>
        public string CycleLogDir { get; set; } = "logs";

        /// <summary>
        /// Reads options from key/value pairs (App.config appSettings). Missing keys keep
        /// their defaults. Unknown keys are ignored. Bad values throw FormatException.
        /// </summary>
        public static PortalOptions FromSettings(IDictionary<string, string> settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var o = new PortalOptions();
            string v;

            if (Get(settings, "ReaderHost", out v)) o.ReaderHost = v.Trim();
            if (Get(settings, "ReaderPort", out v)) o.ReaderPort = ParseUInt("ReaderPort", v);
            if (Get(settings, "ReaderTimeoutMs", out v)) o.ReaderTimeoutMs = ParseUInt("ReaderTimeoutMs", v);
            if (Get(settings, "ReconnectIntervalMs", out v)) o.ReconnectIntervalMs = ParseInt("ReconnectIntervalMs", v);

            if (Get(settings, "RedGpo", out v)) o.RedGpo = ParseInt("RedGpo", v);
            if (Get(settings, "GreenGpo", out v)) o.GreenGpo = ParseInt("GreenGpo", v);
            if (Get(settings, "BlueGpo", out v)) o.BlueGpo = ParseInt("BlueGpo", v);
            if (Get(settings, "LampOnState", out v)) o.LampOnState = ParseBool("LampOnState", v);

            if (Get(settings, "StartSensorGpi", out v)) o.StartSensorGpi = ParseInt("StartSensorGpi", v);
            if (Get(settings, "StopSensorGpi", out v)) o.StopSensorGpi = ParseInt("StopSensorGpi", v);
            if (Get(settings, "SensorActiveLow", out v)) o.SensorActiveLow = ParseBool("SensorActiveLow", v);

            if (Get(settings, "MaxReadMs", out v)) o.MaxReadMs = ParseInt("MaxReadMs", v);
            if (Get(settings, "StopDelayMs", out v)) o.StopDelayMs = ParseInt("StopDelayMs", v);
            if (Get(settings, "MinTags", out v)) o.MinTags = ParseInt("MinTags", v);
            if (Get(settings, "ResultHoldMs", out v)) o.ResultHoldMs = ParseInt("ResultHoldMs", v);

            if (Get(settings, "GpiDebounceMs", out v)) o.GpiDebounceMs = ParseUInt("GpiDebounceMs", v);
            if (settings.TryGetValue("RmUser", out v) && v != null) o.RmUser = v.Trim();
            if (settings.TryGetValue("RmPassword", out v) && v != null) o.RmPassword = v;

            if (settings.TryGetValue("CycleLogDir", out v) && v != null) o.CycleLogDir = v.Trim();
            return o;
        }

        /// <summary>
        /// Checks the options. Port numbers are checked against the reader's capability
        /// counts; pass 4/4 (FX9600) before connecting.
        /// </summary>
        public IList<string> Validate(int numGpoPorts = 4, int numGpiPorts = 4)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(ReaderHost)) errors.Add("ReaderHost is empty.");

            CheckPort(errors, "RedGpo", RedGpo, numGpoPorts, false);
            CheckPort(errors, "GreenGpo", GreenGpo, numGpoPorts, false);
            CheckPort(errors, "BlueGpo", BlueGpo, numGpoPorts, false);
            if (RedGpo == GreenGpo || RedGpo == BlueGpo || GreenGpo == BlueGpo)
                errors.Add("RedGpo, GreenGpo and BlueGpo must be different ports.");

            CheckPort(errors, "StartSensorGpi", StartSensorGpi, numGpiPorts, false);
            CheckPort(errors, "StopSensorGpi", StopSensorGpi, numGpiPorts, true);
            if (StopSensorGpi != 0 && StopSensorGpi == StartSensorGpi)
                errors.Add("StopSensorGpi must differ from StartSensorGpi (or be 0).");

            if (MaxReadMs <= 0) errors.Add("MaxReadMs must be > 0.");
            if (StopDelayMs < 0) errors.Add("StopDelayMs must be >= 0.");
            if (MinTags < 1) errors.Add("MinTags must be >= 1.");
            if (ResultHoldMs < 0) errors.Add("ResultHoldMs must be >= 0.");
            if (ReconnectIntervalMs < 500) errors.Add("ReconnectIntervalMs must be >= 500.");
            return errors;
        }

        private static void CheckPort(List<string> errors, string name, int port, int max, bool allowZero)
        {
            if (allowZero && port == 0) return;
            if (port < 1 || port > max)
                errors.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}={1} is out of range 1..{2}{3}.", name, port, max, allowZero ? " (or 0)" : ""));
        }

        private static bool Get(IDictionary<string, string> s, string key, out string value)
        {
            return s.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value);
        }

        private static int ParseInt(string key, string v)
        {
            int r;
            if (!int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r))
                throw new FormatException(key + " must be an integer, got '" + v + "'.");
            return r;
        }

        private static uint ParseUInt(string key, string v)
        {
            uint r;
            if (!uint.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r))
                throw new FormatException(key + " must be a non-negative integer, got '" + v + "'.");
            return r;
        }

        private static bool ParseBool(string key, string v)
        {
            switch (v.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": return true;
                case "false": case "0": case "no": return false;
                default: throw new FormatException(key + " must be true or false, got '" + v + "'.");
            }
        }
    }
}
