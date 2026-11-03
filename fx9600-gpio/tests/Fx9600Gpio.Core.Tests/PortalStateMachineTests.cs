using System;
using System.Collections.Generic;
using System.Linq;
using Fx9600Gpio.Core;
using Xunit;

namespace Fx9600Gpio.Core.Tests
{
    internal sealed class FakeHardware : IPortalHardware
    {
        public readonly List<string> Calls = new List<string>();
        public LampState Lamps;
        public bool InventoryRunning;
        public string FailOn;

        public void SetLamps(LampState lamps)
        {
            Calls.Add("lamps " + lamps);
            if (FailOn == "SetLamps") throw new InvalidOperationException("boom");
            Lamps = lamps;
        }

        public void StartInventory()
        {
            Calls.Add("start");
            if (FailOn == "StartInventory") throw new InvalidOperationException("boom");
            InventoryRunning = true;
        }

        public void StopInventory()
        {
            Calls.Add("stop");
            if (FailOn == "StopInventory") throw new InvalidOperationException("boom");
            InventoryRunning = false;
        }
    }

    public class PortalStateMachineTests
    {
        // Figure 139 wiring: sensors pull GPI low when triggered.
        private const bool Low = false;
        private const bool High = true;

        private static readonly DateTime T0 = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

        private readonly FakeHardware _hw = new FakeHardware();
        private readonly List<CycleResult> _cycles = new List<CycleResult>();
        private readonly List<string> _faults = new List<string>();

        private PortalStateMachine Create(Action<PortalOptions> configure = null)
        {
            var o = new PortalOptions();
            configure?.Invoke(o);
            var sm = new PortalStateMachine(o, _hw);
            sm.CycleCompleted += _cycles.Add;
            sm.FaultRaised += _faults.Add;
            sm.Start(T0);
            return sm;
        }

        private static DateTime At(int ms) => T0.AddMilliseconds(ms);

        [Fact]
        public void Start_ShowsBlue()
        {
            var sm = Create();
            Assert.Equal(PortalState.Ready, sm.State);
            Assert.Equal(LampState.BlueOnly, _hw.Lamps);
            Assert.Null(sm.NextDeadlineUtc);
        }

        [Fact]
        public void StartSensor_StartsInventory_AndTurnsLampsOff()
        {
            var sm = Create();
            sm.OnGpi(1, Low, At(100));
            Assert.Equal(PortalState.Reading, sm.State);
            Assert.True(_hw.InventoryRunning);
            Assert.Equal(LampState.Off, _hw.Lamps);
            Assert.Equal(At(10100), sm.NextDeadlineUtc);
        }

        [Fact]
        public void StartSensorGoingInactive_InReady_DoesNothing()
        {
            var sm = Create();
            sm.OnGpi(1, High, At(100));
            Assert.Equal(PortalState.Ready, sm.State);
            Assert.DoesNotContain("start", _hw.Calls);
        }

        [Fact]
        public void TagRead_TurnsGreen_WhenMinTagsReached()
        {
            var sm = Create(o => o.MinTags = 2);
            sm.OnGpi(1, Low, At(0));
            Assert.True(sm.OnTag("E200A", At(10)));
            Assert.Equal(LampState.Off, _hw.Lamps);
            Assert.True(sm.OnTag("E200B", At(20)));
            Assert.Equal(LampState.GreenOnly, _hw.Lamps);
        }

        [Fact]
        public void DuplicateTags_CountOnce_CaseInsensitive()
        {
            var sm = Create();
            sm.OnGpi(1, Low, At(0));
            Assert.True(sm.OnTag("e200a", At(10)));
            Assert.False(sm.OnTag("E200A", At(20)));
            Assert.Equal(1, sm.TagCount);
        }

        [Fact]
        public void TagsOutsideCycle_AreIgnored()
        {
            var sm = Create();
            Assert.False(sm.OnTag("E200A", At(10)));
            Assert.Equal(0, sm.TagCount);
        }

        [Fact]
        public void StopSensor_EndsAfterStopDelay_WithOk()
        {
            var sm = Create();
            sm.OnGpi(1, Low, At(0));
            sm.OnTag("E200A", At(50));
            sm.OnGpi(2, Low, At(1000));
            Assert.Equal(PortalState.Stopping, sm.State);
            Assert.True(_hw.InventoryRunning);

            // Late tag during StopDelayMs still counts.
            Assert.True(sm.OnTag("E200B", At(1200)));

            sm.OnTick(At(1299));
            Assert.Equal(PortalState.Stopping, sm.State);
            sm.OnTick(At(1300));
            Assert.Equal(PortalState.Result, sm.State);
            Assert.False(_hw.InventoryRunning);
            Assert.Equal(LampState.GreenOnly, _hw.Lamps);

            var c = Assert.Single(_cycles);
            Assert.Equal(CycleOutcome.Ok, c.Outcome);
            Assert.Equal("stop-sensor", c.EndReason);
            Assert.Equal(new[] { "E200A", "E200B" }, c.Tags.ToArray());
            Assert.Equal(TimeSpan.FromMilliseconds(1300), c.Duration);
        }

        [Fact]
        public void Timeout_WithNoTags_ShowsRed()
        {
            var sm = Create();
            sm.OnGpi(1, Low, At(0));
            sm.OnTick(At(10000));
            Assert.Equal(PortalState.Result, sm.State);
            Assert.Equal(LampState.RedOnly, _hw.Lamps);
            var c = Assert.Single(_cycles);
            Assert.Equal(CycleOutcome.NotEnoughTags, c.Outcome);
            Assert.Equal("timeout", c.EndReason);
        }

        [Fact]
        public void StopDelay_NeverExtendsPastMaxRead()
        {
            var sm = Create(o => { o.MaxReadMs = 1000; o.StopDelayMs = 500; });
            sm.OnGpi(1, Low, At(0));
            sm.OnGpi(2, Low, At(900));
            sm.OnTick(At(1000));
            Assert.Equal(PortalState.Result, sm.State);
        }

        [Fact]
        public void Result_ReturnsToReady_AfterHold()
        {
            var sm = Create();
            sm.OnGpi(1, Low, At(0));
            sm.OnGpi(2, Low, At(100));
            sm.OnTick(At(400));
            Assert.Equal(PortalState.Result, sm.State);
            sm.OnTick(At(2399));
            Assert.Equal(PortalState.Result, sm.State);
            sm.OnTick(At(2400));
            Assert.Equal(PortalState.Ready, sm.State);
            Assert.Equal(LampState.BlueOnly, _hw.Lamps);
        }

        [Fact]
        public void StartSensor_DuringResult_StartsNextCycleImmediately()
        {
            var sm = Create();
            sm.OnGpi(1, Low, At(0));
            sm.OnTag("E200A", At(10));
            sm.OnGpi(2, Low, At(100));
            sm.OnTick(At(400));
            sm.OnGpi(1, Low, At(500));
            Assert.Equal(PortalState.Reading, sm.State);
            Assert.Equal(0, sm.TagCount);
            Assert.Equal(2, _hw.Calls.Count(c => c == "start"));
        }

        [Fact]
        public void StartSensor_RetriggerWhileReading_IsIgnored()
        {
            var sm = Create();
            sm.OnGpi(1, Low, At(0));
            sm.OnGpi(1, High, At(50));
            sm.OnGpi(1, Low, At(100));
            Assert.Equal(PortalState.Reading, sm.State);
            Assert.Equal(1, _hw.Calls.Count(c => c == "start"));
        }

        [Fact]
        public void NoStopSensor_EndsWhenStartSensorReleased()
        {
            var sm = Create(o => o.StopSensorGpi = 0);
            sm.OnGpi(1, Low, At(0));
            sm.OnGpi(2, Low, At(100)); // port 2 not used
            Assert.Equal(PortalState.Reading, sm.State);
            sm.OnGpi(1, High, At(800));
            Assert.Equal(PortalState.Stopping, sm.State);
            sm.OnTick(At(1100));
            Assert.Equal("start-sensor-released", Assert.Single(_cycles).EndReason);
        }

        [Fact]
        public void SensorActiveHigh_InvertsLogic()
        {
            var sm = Create(o => o.SensorActiveLow = false);
            sm.OnGpi(1, Low, At(0));
            Assert.Equal(PortalState.Ready, sm.State);
            sm.OnGpi(1, High, At(10));
            Assert.Equal(PortalState.Reading, sm.State);
        }

        [Fact]
        public void ZeroStopDelayAndHold_GoesStraightBackToReady()
        {
            var sm = Create(o => { o.StopDelayMs = 0; o.ResultHoldMs = 0; });
            sm.OnGpi(1, Low, At(0));
            sm.OnTag("E200A", At(10));
            sm.OnGpi(2, Low, At(20));
            Assert.Equal(PortalState.Ready, sm.State);
            Assert.Equal(CycleOutcome.Ok, Assert.Single(_cycles).Outcome);
        }

        [Fact]
        public void Disconnect_WhileReading_AbortsAndReconnectReturnsToReady()
        {
            var sm = Create();
            sm.OnGpi(1, Low, At(0));
            sm.OnTag("E200A", At(10));
            sm.OnDisconnected(At(500));
            Assert.Equal(PortalState.Fault, sm.State);
            var c = Assert.Single(_cycles);
            Assert.Equal(CycleOutcome.Aborted, c.Outcome);
            Assert.Equal("disconnected", c.EndReason);

            // Sensors are ignored while faulted.
            sm.OnGpi(1, Low, At(600));
            Assert.Equal(PortalState.Fault, sm.State);

            _hw.Calls.Clear();
            sm.OnReconnected(At(5000));
            Assert.Equal(PortalState.Ready, sm.State);
            // Lamps are re-sent after reconnect even if they were blue before.
            Assert.Contains("lamps " + LampState.BlueOnly, _hw.Calls);
        }

        [Fact]
        public void StartInventoryFailure_RaisesFault_AndShowsRedBlue()
        {
            var sm = Create();
            _hw.FailOn = "StartInventory";
            sm.OnGpi(1, Low, At(0));
            Assert.Equal(PortalState.Fault, sm.State);
            Assert.Single(_faults);
            Assert.Equal(LampState.RedBlue, _hw.Lamps);
            Assert.Equal(CycleOutcome.Aborted, Assert.Single(_cycles).Outcome);
            Assert.Equal("reader-error", _cycles[0].EndReason);
        }

        [Fact]
        public void LampFailure_InReady_RaisesFault()
        {
            _hw.FailOn = "SetLamps";
            var sm = Create();
            Assert.Equal(PortalState.Fault, sm.State);
            Assert.Single(_faults);
            Assert.Empty(_cycles);
        }

        [Fact]
        public void StopInventoryFailure_AbortsCycleOnce()
        {
            var sm = Create();
            sm.OnGpi(1, Low, At(0));
            _hw.FailOn = "StopInventory";
            sm.OnTick(At(10000));
            Assert.Equal(PortalState.Fault, sm.State);
            Assert.Equal(CycleOutcome.Aborted, Assert.Single(_cycles).Outcome);
        }

        [Fact]
        public void UnchangedLamps_AreNotResent()
        {
            var sm = Create();
            sm.OnGpi(1, Low, At(0));
            sm.OnGpi(2, Low, At(100));
            sm.OnTick(At(400));        // red
            sm.OnTick(At(2400));       // blue
            int lampCalls = _hw.Calls.Count(c => c.StartsWith("lamps"));
            sm.OnTick(At(3000));
            Assert.Equal(lampCalls, _hw.Calls.Count(c => c.StartsWith("lamps")));
        }

        [Fact]
        public void InvalidOptions_Throw()
        {
            Assert.Throws<ArgumentException>(() =>
                new PortalStateMachine(new PortalOptions { RedGpo = 2 }, _hw));
        }
    }
}
