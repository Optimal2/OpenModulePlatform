using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.HostAgent.Sentinel
{
    internal sealed class AgentSnapshot
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public bool ProcessAlive { get; set; }
        public DateTime StartedUtc { get; set; }
    }

    internal sealed class Observation
    {
        public int EventId { get; }
        public string Message { get; }
        public Observation(int eventId, string message) { EventId = eventId; Message = message; }
    }

    internal static class SentinelPolicy
    {
        private static readonly Regex AgentName = new Regex(@"\AOMP\.HostAgent\.\d+\.\d+\.\d+\z",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        public static bool IsAgent(string name) => name != null && AgentName.IsMatch(name);

        public static Observation Evaluate(IEnumerable<AgentSnapshot> services)
        {
            var agents = services.Where(s => IsAgent(s.Name)).ToArray();
            if (agents.Length == 0) return new Observation(102, "No versioned HostAgent service exists.");
            if (agents.Length > 1) return new Observation(101,
                "Multiple HostAgent services: " + string.Join(", ", agents.Select(s => s.Name)) + ".");
            var agent = agents[0];
            if (agent.Status != "Running" || !agent.ProcessAlive)
                return new Observation(100, "HostAgent " + agent.Name + " status=" + agent.Status
                    + "; process alive=" + agent.ProcessAlive + ".");
            return new Observation(0, "OK, HostAgent " + agent.Name + " Running since "
                + agent.StartedUtc.ToString("O") + " (process start UTC).");
        }

        public static Observation DatabaseHeartbeat(DateTime? lastSeenUtc, DateTime nowUtc, int staleMinutes)
        {
            if (!lastSeenUtc.HasValue || nowUtc - lastSeenUtc.Value > TimeSpan.FromMinutes(staleMinutes))
                return new Observation(103, "HostAgent database heartbeat missing or stale for this machine.");
            return null;
        }
    }

    // The caller persists Faulted before stopping and restores it at the next service start.
    internal sealed class SentinelState
    {
        private readonly DateTime startedUtc;
        private readonly int graceSeconds;
        private readonly int confirmations;
        private DateTime? lastOkUtc;
        private int pendingEventId;
        private int pendingCount;
        public bool Faulted { get; private set; }
        // True while a fault has been observed but not yet reported: the caller samples
        // again after the short recheck delay instead of the full poll interval.
        public bool Pending { get { return pendingCount > 0; } }
        // The fault reported by the latest Apply call, or 0. Only a reported fault may
        // stop the service; a held or unconfirmed observation never does.
        public int ReportedEventId { get; private set; }

        public SentinelState(bool faulted) : this(faulted, DateTime.MinValue, 0, 1) { }

        public SentinelState(bool faulted, DateTime startedUtc, int graceSeconds, int confirmations)
        {
            Faulted = faulted;
            this.startedUtc = startedUtc;
            this.graceSeconds = graceSeconds;
            this.confirmations = confirmations;
        }

        public int[] Apply(Observation observation, DateTime nowUtc, int heartbeatMinutes)
        {
            ReportedEventId = 0;
            if (observation.EventId != 0)
            {
                // A fault that was already reported (also one restored from the state file
                // after a self-stop) is repeated on every sample, so the SCM restart chain
                // keeps its documented 300 s rhythm. Only a fresh fault is debounced.
                if (!Faulted)
                {
                    pendingCount = pendingEventId == observation.EventId ? pendingCount + 1 : 1;
                    pendingEventId = observation.EventId;
                    if (nowUtc - startedUtc < TimeSpan.FromSeconds(graceSeconds) || pendingCount < confirmations)
                        return new int[0];
                }
                pendingCount = 0;
                pendingEventId = 0;
                Faulted = true;
                ReportedEventId = observation.EventId;
                return new[] { observation.EventId };
            }
            pendingCount = 0;
            pendingEventId = 0;
            var events = new List<int>();
            if (Faulted) events.Add(1);
            if (Faulted || !lastOkUtc.HasValue || nowUtc - lastOkUtc.Value >= TimeSpan.FromMinutes(heartbeatMinutes))
            {
                events.Add(10);
                lastOkUtc = nowUtc;
            }
            Faulted = false;
            return events.ToArray();
        }

        public static bool ShouldStop(int eventId, bool stopSelf) => stopSelf && (eventId == 100 || eventId == 102);
    }
}
