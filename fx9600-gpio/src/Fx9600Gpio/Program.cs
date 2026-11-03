using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Text;
using System.Threading;
using Fx9600Gpio.Core;

namespace Fx9600Gpio
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            bool testMode = false;
            string hostOverride = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--test":
                        testMode = true;
                        break;
                    case "--host":
                        if (i + 1 >= args.Length) return Usage("--host needs an IP address.");
                        hostOverride = args[++i];
                        break;
                    case "-h":
                    case "--help":
                    case "/?":
                        return Usage(null);
                    default:
                        return Usage("Unknown argument: " + args[i]);
                }
            }

            PortalOptions options;
            try
            {
                options = LoadOptions(hostOverride);
            }
            catch (Exception ex)
            {
                Log.Error("Settings error: " + ex.Message);
                return 2;
            }

            var errors = options.Validate();
            if (errors.Count > 0)
            {
                foreach (var e in errors) Log.Error("Settings error: " + e);
                return 2;
            }

            return testMode ? WiringTest.Run(options) : RunPortal(options);
        }

        private static int Usage(string error)
        {
            if (error != null) Log.Error(error);
            Console.WriteLine("Usage: Fx9600Gpio.exe [--host <reader IP>] [--test]");
            Console.WriteLine("  (no option)  Run the portal: sensors start/stop reading, lamps show the result.");
            Console.WriteLine("  --test       Interactive wiring test: lamp polarity and sensor levels.");
            Console.WriteLine("  --host       Override ReaderHost from Fx9600Gpio.exe.config.");
            return error == null ? 0 : 2;
        }

        private static PortalOptions LoadOptions(string hostOverride)
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var app = ConfigurationManager.AppSettings;
            foreach (string key in app.AllKeys) settings[key] = app[key];
            if (!string.IsNullOrWhiteSpace(hostOverride)) settings["ReaderHost"] = hostOverride;
            return PortalOptions.FromSettings(settings);
        }

        private static int RunPortal(PortalOptions options)
        {
            // Every reader call and state change happens on this thread. SDK events are
            // queued here as actions.
            var queue = new BlockingCollection<Action>();
            var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                stop.Set();
                queue.Add(() => { });
            };

            var cycleLog = new CycleLog(options.CycleLogDir);
            using (var reader = new FxReader(options))
            {
                var sm = new PortalStateMachine(options, reader);
                bool needConnect = true;
                bool connectedOnce = false;
                DateTime nextConnectUtc = DateTime.UtcNow;

                reader.GpiChanged += (port, high) =>
                    queue.Add(() =>
                    {
                        Log.Info("GPI " + port + " -> " + (high ? "HIGH" : "LOW"));
                        sm.OnGpi(port, high, DateTime.UtcNow);
                    });
                reader.TagRead += (epc, antenna, rssi) =>
                    queue.Add(() =>
                    {
                        if (sm.OnTag(epc, DateTime.UtcNow))
                            Log.Info("  tag " + epc + "  ant " + antenna + "  RSSI " + rssi + "  (" + sm.TagCount + ")");
                    });
                reader.Disconnected += reason =>
                    queue.Add(() =>
                    {
                        Log.Warn("Reader disconnected: " + reason);
                        sm.OnDisconnected(DateTime.UtcNow);
                        needConnect = true;
                        nextConnectUtc = DateTime.UtcNow.AddMilliseconds(options.ReconnectIntervalMs);
                    });

                sm.Log += msg => Log.Info(msg);
                sm.FaultRaised += reason =>
                {
                    // Already on the worker thread. Reconnect after a pause.
                    needConnect = true;
                    nextConnectUtc = DateTime.UtcNow.AddMilliseconds(options.ReconnectIntervalMs);
                };
                sm.CycleCompleted += result =>
                {
                    Log.Info(string.Format("Cycle {0}: {1}, {2} tag(s), {3:0} ms, {4}.",
                        result.CycleNumber, result.Outcome, result.Tags.Count,
                        result.Duration.TotalMilliseconds, result.EndReason));
                    cycleLog.Write(result);
                };

                Log.Info("Reader " + options.ReaderHost + ". Press Ctrl+C to stop.");

                while (!stop.IsSet)
                {
                    var now = DateTime.UtcNow;
                    if (needConnect && now >= nextConnectUtc)
                    {
                        needConnect = !TryConnect(reader, options);
                        if (needConnect)
                        {
                            nextConnectUtc = DateTime.UtcNow.AddMilliseconds(options.ReconnectIntervalMs);
                        }
                        else if (!connectedOnce)
                        {
                            connectedOnce = true;
                            sm.Start(DateTime.UtcNow);
                        }
                        else
                        {
                            sm.OnReconnected(DateTime.UtcNow);
                        }
                    }

                    Action work;
                    if (queue.TryTake(out work, WaitMs(sm, needConnect, nextConnectUtc)))
                    {
                        try
                        {
                            work();
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Unexpected error: " + ex);
                        }
                    }
                    sm.OnTick(DateTime.UtcNow);
                }

                Log.Info("Stopping: lamps off, disconnecting.");
                reader.Shutdown();
            }
            return 0;
        }

        private static bool TryConnect(FxReader reader, PortalOptions options)
        {
            try
            {
                Log.Info("Connecting to " + options.ReaderHost + " ...");
                reader.Connect();
                Log.Info("Connected: " + reader.ReaderDescription + ", " +
                         reader.NumGpiPorts + " GPI / " + reader.NumGpoPorts + " GPO.");
                try
                {
                    Log.Info(reader.ApplyGpiDebounce());
                }
                catch (Exception ex)
                {
                    Log.Warn("GPI debounce not set: " + ex.Message);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Connect failed: " + ex.Message + " Retrying in " + options.ReconnectIntervalMs + " ms.");
                return false;
            }
        }

        private static int WaitMs(PortalStateMachine sm, bool needConnect, DateTime nextConnectUtc)
        {
            var now = DateTime.UtcNow;
            double wait = 250;
            var deadline = sm.NextDeadlineUtc;
            if (deadline.HasValue) wait = Math.Min(wait, (deadline.Value - now).TotalMilliseconds);
            if (needConnect) wait = Math.Min(wait, (nextConnectUtc - now).TotalMilliseconds);
            return Math.Max(0, (int)Math.Ceiling(wait));
        }
    }
}
