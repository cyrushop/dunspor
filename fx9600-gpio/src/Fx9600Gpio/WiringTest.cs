using System;
using System.Collections.Generic;
using System.Threading;
using Fx9600Gpio.Core;

namespace Fx9600Gpio
{
    /// <summary>
    /// --test: interactive check of the Figure 139 wiring.
    ///   1. Lights each lamp GPO with PortState TRUE and asks whether it is on. The answers
    ///      give the LampOnState setting (FX9600 output polarity is not documented in the SDK).
    ///   2. Shows GPI levels and events while the user triggers each sensor. The level seen
    ///      when triggered gives the SensorActiveLow setting.
    /// </summary>
    internal static class WiringTest
    {
        public static int Run(PortalOptions options)
        {
            using (var reader = new FxReader(options))
            {
                try
                {
                    Log.Info("Connecting to " + options.ReaderHost + " ...");
                    reader.Connect();
                }
                catch (Exception ex)
                {
                    Log.Error("Connect failed: " + ex.Message);
                    return 1;
                }
                Log.Info("Connected: " + reader.ReaderDescription + ", " +
                         reader.NumGpiPorts + " GPI / " + reader.NumGpoPorts + " GPO.");

                try
                {
                    bool? lampOnState = TestLamps(reader, options);
                    TestSensors(reader, options);

                    Console.WriteLine();
                    Console.WriteLine("=== Suggested Fx9600Gpio.exe.config settings ===");
                    if (lampOnState.HasValue)
                        Console.WriteLine("  <add key=\"LampOnState\" value=\"" + (lampOnState.Value ? "true" : "false") + "\" />");
                    else
                        Console.WriteLine("  LampOnState: answers were mixed. Check the lamp wiring, then run --test again.");
                    Console.WriteLine("  SensorActiveLow: true if the sensor showed LOW when triggered, false if HIGH.");
                }
                catch (Exception ex)
                {
                    Log.Error("Test failed: " + ex.Message);
                    return 1;
                }
            }
            return 0;
        }

        private static bool? TestLamps(FxReader reader, PortalOptions o)
        {
            var lamps = new[]
            {
                new KeyValuePair<string, int>("RED", o.RedGpo),
                new KeyValuePair<string, int>("GREEN", o.GreenGpo),
                new KeyValuePair<string, int>("BLUE", o.BlueGpo),
            };

            Console.WriteLine();
            Console.WriteLine("=== Step 1: lamps ===");
            Console.WriteLine("Each lamp's GPO is set to PortState TRUE (the others FALSE). Answer y if THAT lamp is on.");

            int yes = 0, no = 0;
            foreach (var lamp in lamps)
            {
                foreach (var other in lamps) reader.SetGpoRaw(other.Value, other.Value == lamp.Value);
                Thread.Sleep(300);
                if (AskYesNo("  " + lamp.Key + " lamp (GPO " + lamp.Value + " = TRUE): is the " + lamp.Key + " lamp on? [y/n] "))
                    yes++;
                else
                    no++;
            }
            foreach (var lamp in lamps) reader.SetGpoRaw(lamp.Value, false);

            if (yes == lamps.Length)
            {
                Console.WriteLine("  -> PortState TRUE lights the lamp. LampOnState=true. All GPOs are now FALSE (lamps off).");
                return true;
            }
            if (no == lamps.Length)
            {
                Console.WriteLine("  -> Output is inverted: PortState FALSE lights the lamp. LampOnState=false.");
                Console.WriteLine("     (All GPOs are now FALSE, so all lamps are probably on.)");
                return false;
            }
            Console.WriteLine("  -> Mixed answers. Check each lamp's '-' wire goes to its GP O/P pin and '+' to the supply.");
            return null;
        }

        private static void TestSensors(FxReader reader, PortalOptions o)
        {
            Console.WriteLine();
            Console.WriteLine("=== Step 2: sensors ===");
            for (int port = 1; port <= reader.NumGpiPorts; port++)
            {
                string role = port == o.StartSensorGpi ? " (start sensor)" : port == o.StopSensorGpi ? " (stop sensor)" : "";
                Console.WriteLine("  GPI " + port + role + ": " + reader.GetGpiLevel(port));
            }
            Console.WriteLine("Trigger each sensor a few times and watch the level change. Press Enter when done.");

            Action<int, bool> onGpi = (port, high) =>
                Log.Info("  GPI " + port + " -> " + (high ? "HIGH" : "LOW"));
            reader.GpiChanged += onGpi;
            try
            {
                Console.ReadLine();
            }
            finally
            {
                reader.GpiChanged -= onGpi;
            }
        }

        private static bool AskYesNo(string prompt)
        {
            while (true)
            {
                Console.Write(prompt);
                string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (answer == "y" || answer == "yes") return true;
                if (answer == "n" || answer == "no") return false;
            }
        }
    }
}
