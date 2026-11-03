using System;
using System.Collections.Generic;
using Fx9600Gpio.Core;
using Xunit;

namespace Fx9600Gpio.Core.Tests
{
    public class PortalOptionsTests
    {
        [Fact]
        public void Defaults_MatchFigure139_AndAreValid()
        {
            var o = new PortalOptions();
            Assert.Equal(1, o.RedGpo);
            Assert.Equal(2, o.GreenGpo);
            Assert.Equal(3, o.BlueGpo);
            Assert.Equal(1, o.StartSensorGpi);
            Assert.Equal(2, o.StopSensorGpi);
            Assert.True(o.SensorActiveLow);
            Assert.Empty(o.Validate());
        }

        [Fact]
        public void FromSettings_ParsesValues_AndKeepsDefaultsForBlanks()
        {
            var o = PortalOptions.FromSettings(new Dictionary<string, string>
            {
                ["ReaderHost"] = " 10.0.0.5 ",
                ["LampOnState"] = "false",
                ["StopSensorGpi"] = "0",
                ["MinTags"] = "3",
                ["GpiDebounceMs"] = "50",
                ["MaxReadMs"] = "",
                ["RmPassword"] = "p@ss ",
                ["SomethingElse"] = "ignored",
            });
            Assert.Equal("10.0.0.5", o.ReaderHost);
            Assert.False(o.LampOnState);
            Assert.Equal(0, o.StopSensorGpi);
            Assert.Equal(3, o.MinTags);
            Assert.Equal(50u, o.GpiDebounceMs);
            Assert.Equal(10000, o.MaxReadMs);
            Assert.Equal("p@ss ", o.RmPassword);
            Assert.Empty(o.Validate());
        }

        [Theory]
        [InlineData("MinTags", "abc")]
        [InlineData("ReaderPort", "-1")]
        [InlineData("SensorActiveLow", "maybe")]
        public void FromSettings_BadValue_Throws(string key, string value)
        {
            var ex = Assert.Throws<FormatException>(() =>
                PortalOptions.FromSettings(new Dictionary<string, string> { [key] = value }));
            Assert.Contains(key, ex.Message);
        }

        [Fact]
        public void Validate_ReportsEachProblem()
        {
            var o = new PortalOptions
            {
                ReaderHost = " ",
                RedGpo = 5,
                GreenGpo = 5,
                StartSensorGpi = 2,
                StopSensorGpi = 2,
                MinTags = 0,
                MaxReadMs = 0,
            };
            var errors = o.Validate();
            Assert.Contains(errors, e => e.Contains("ReaderHost"));
            Assert.Contains(errors, e => e.StartsWith("RedGpo=5"));
            Assert.Contains(errors, e => e.Contains("must be different"));
            Assert.Contains(errors, e => e.Contains("StopSensorGpi must differ"));
            Assert.Contains(errors, e => e.Contains("MinTags"));
            Assert.Contains(errors, e => e.Contains("MaxReadMs"));
        }

        [Fact]
        public void Validate_UsesReaderPortCounts()
        {
            var o = new PortalOptions { BlueGpo = 3 };
            Assert.Contains(o.Validate(numGpoPorts: 2), e => e.StartsWith("BlueGpo=3"));
        }
    }
}
