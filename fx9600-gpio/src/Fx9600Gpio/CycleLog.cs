using System;
using System.Globalization;
using System.IO;
using System.Text;
using Fx9600Gpio.Core;

namespace Fx9600Gpio
{
    /// <summary>
    /// Appends one CSV row per read cycle to &lt;dir&gt;\cycles-yyyyMMdd.csv (local date).
    /// Columns: cycle,start,end,duration_ms,outcome,end_reason,tag_count,tags
    /// Tags are separated by ';'. Write errors are logged and do not stop the portal.
    /// </summary>
    internal sealed class CycleLog
    {
        private const string Header = "cycle,start,end,duration_ms,outcome,end_reason,tag_count,tags";
        private readonly string _dir;
        private bool _warned;

        public CycleLog(string dir)
        {
            // A relative folder is taken from the .exe folder, not the current directory.
            _dir = string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dir);
        }

        public void Write(CycleResult r)
        {
            if (_dir == null) return;
            try
            {
                var startLocal = r.StartUtc.ToLocalTime();
                Directory.CreateDirectory(_dir);
                string path = Path.Combine(_dir, "cycles-" + startLocal.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv");
                bool isNew = !File.Exists(path);

                var line = new StringBuilder();
                line.Append(r.CycleNumber.ToString(CultureInfo.InvariantCulture)).Append(',');
                line.Append(startLocal.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',');
                line.Append(r.EndUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',');
                line.Append(((long)r.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)).Append(',');
                line.Append(r.Outcome).Append(',');
                line.Append(r.EndReason).Append(',');
                line.Append(r.Tags.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
                line.Append(string.Join(";", r.Tags));

                using (var w = new StreamWriter(path, true, new UTF8Encoding(false)))
                {
                    if (isNew) w.WriteLine(Header);
                    w.WriteLine(line.ToString());
                }
            }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Log.Warn("Cannot write cycle log in '" + _dir + "': " + ex.Message);
            }
        }
    }
}
