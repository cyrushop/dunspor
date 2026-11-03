using System;
using System.Collections.Generic;

namespace Fx9600Gpio.Core
{
    public enum PortalState
    {
        /// <summary>Waiting for the start sensor. Blue lamp.</summary>
        Ready,
        /// <summary>Inventory running.</summary>
        Reading,
        /// <summary>Stop condition met; still reading for StopDelayMs.</summary>
        Stopping,
        /// <summary>Showing the result lamp for ResultHoldMs.</summary>
        Result,
        /// <summary>Reader disconnected or a reader call failed. Waiting for reconnect.</summary>
        Fault,
    }

    public enum CycleOutcome
    {
        /// <summary>At least MinTags unique tags were read.</summary>
        Ok,
        /// <summary>The cycle ended with fewer than MinTags unique tags.</summary>
        NotEnoughTags,
        /// <summary>The cycle was cut short by a disconnection or reader error.</summary>
        Aborted,
    }

    /// <summary>Which lamps should be on.</summary>
    public struct LampState : IEquatable<LampState>
    {
        public LampState(bool red, bool green, bool blue)
        {
            Red = red;
            Green = green;
            Blue = blue;
        }

        public bool Red { get; }
        public bool Green { get; }
        public bool Blue { get; }

        public static readonly LampState Off = new LampState(false, false, false);
        public static readonly LampState BlueOnly = new LampState(false, false, true);
        public static readonly LampState GreenOnly = new LampState(false, true, false);
        public static readonly LampState RedOnly = new LampState(true, false, false);
        public static readonly LampState RedBlue = new LampState(true, false, true);

        public bool Equals(LampState other)
        {
            return Red == other.Red && Green == other.Green && Blue == other.Blue;
        }

        public override bool Equals(object obj)
        {
            return obj is LampState && Equals((LampState)obj);
        }

        public override int GetHashCode()
        {
            return (Red ? 1 : 0) | (Green ? 2 : 0) | (Blue ? 4 : 0);
        }

        public override string ToString()
        {
            return "R=" + (Red ? "on" : "off") + " G=" + (Green ? "on" : "off") + " B=" + (Blue ? "on" : "off");
        }
    }

    public sealed class CycleResult
    {
        public int CycleNumber { get; internal set; }
        public DateTime StartUtc { get; internal set; }
        public DateTime EndUtc { get; internal set; }
        public CycleOutcome Outcome { get; internal set; }
        /// <summary>stop-sensor, start-sensor-released, timeout, disconnected or reader-error.</summary>
        public string EndReason { get; internal set; }
        public IReadOnlyList<string> Tags { get; internal set; }

        public TimeSpan Duration
        {
            get { return EndUtc - StartUtc; }
        }
    }

    /// <summary>
    /// What the state machine needs from the reader. The Zebra SDK implementation lives in
    /// the Fx9600Gpio app; tests use a fake. Calls may throw; the state machine catches
    /// the exception and goes to Fault.
    /// </summary>
    public interface IPortalHardware
    {
        void SetLamps(LampState lamps);
        void StartInventory();
        void StopInventory();
    }
}
