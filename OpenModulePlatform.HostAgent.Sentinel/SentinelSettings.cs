using System;
using System.Collections.Specialized;
using System.Globalization;

namespace OpenModulePlatform.HostAgent.Sentinel
{
    internal sealed class SentinelSettings
    {
        public int PollIntervalSeconds { get; private set; }
        public int OkHeartbeatMinutes { get; private set; }
        public bool StopSelfWhenHostAgentDown { get; private set; }
        public bool CheckDatabaseHeartbeat { get; private set; }
        public int HeartbeatStaleMinutes { get; private set; }
        public string DatabaseConnectionString { get; private set; }
        // A fault seen this soon after Sentinel started is held, never reported: at boot
        // Sentinel usually starts before HostAgent has reached Running.
        public int StartupGraceSeconds { get; private set; }
        // A fault is reported only after this many consecutive samples agree; samples
        // taken FaultRecheckSeconds apart. Faults already reported are repeated at once.
        public int FaultConfirmations { get; private set; }
        public int FaultRecheckSeconds { get; private set; }

        public static SentinelSettings Read(NameValueCollection values)
        {
            return new SentinelSettings
            {
                PollIntervalSeconds = Number(values, "PollIntervalSeconds", 30, 1, 3600),
                OkHeartbeatMinutes = Number(values, "OkHeartbeatMinutes", 10, 1, 1440),
                StopSelfWhenHostAgentDown = Boolean(values, "StopSelfWhenHostAgentDown", true),
                CheckDatabaseHeartbeat = Boolean(values, "CheckDatabaseHeartbeat", false),
                HeartbeatStaleMinutes = Number(values, "HeartbeatStaleMinutes", 15, 1, 10080),
                DatabaseConnectionString = (values["DatabaseConnectionString"] ?? "").Trim(),
                StartupGraceSeconds = Number(values, "StartupGraceSeconds", 120, 0, 3600),
                FaultConfirmations = Number(values, "FaultConfirmations", 2, 1, 10),
                FaultRecheckSeconds = Number(values, "FaultRecheckSeconds", 10, 1, 3600)
            };
        }

        public bool DatabaseEnabled => CheckDatabaseHeartbeat && DatabaseConnectionString.Length > 0;

        private static int Number(NameValueCollection values, string key, int fallback, int min, int max)
        {
            if (values[key] == null) return fallback;
            int result;
            if (!int.TryParse(values[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                || result < min || result > max)
                throw new ArgumentException(key + " must be between " + min + " and " + max + ".");
            return result;
        }

        private static bool Boolean(NameValueCollection values, string key, bool fallback)
        {
            if (values[key] == null) return fallback;
            bool result;
            if (!bool.TryParse(values[key], out result))
                throw new ArgumentException(key + " must be true or false.");
            return result;
        }
    }
}
