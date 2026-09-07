using System;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace OpenModulePlatform.HostAgent.Sentinel
{
    internal static class Program
    {
        private static void Main() { ServiceBase.Run(new SentinelService()); }
    }

    internal sealed class SentinelService : ServiceBase
    {
        internal const string Identity = "OMP.HostAgent.Sentinel";
        private readonly ManualResetEvent stopping = new ManualResetEvent(false);
        private Thread worker;
        private readonly string stateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OMP", "HostAgentSentinel", "fault.state");

        public SentinelService() { ServiceName = Identity; AutoLog = false; }

        protected override void OnStart(string[] args)
        {
            // AutoLog is disabled: only the installer may register an event source.
            if (!EventLog.SourceExists(Identity) || EventLog.LogNameFromSourceName(Identity, ".") != "Application")
                throw new InvalidOperationException("Run Install-HostAgentSentinel.ps1 to register the Application event source.");
            stopping.Reset();
            worker = new Thread(Monitor) { IsBackground = true, Name = "HostAgent Sentinel" };
            worker.Start();
        }

        protected override void OnStop()
        {
            stopping.Set();
            if (worker != null && Thread.CurrentThread != worker && !worker.Join(TimeSpan.FromSeconds(20)))
                ExitCode = 200;
        }

        private void Monitor()
        {
            try
            {
                var settings = SentinelSettings.Read(ConfigurationManager.AppSettings);
                var connectionString = ValidateConnection(settings);
                var state = new SentinelState(File.Exists(stateFile), DateTime.UtcNow,
                    settings.StartupGraceSeconds, settings.FaultConfirmations);
                do
                {
                    var observation = ReadAgent();
                    if (observation.EventId == 0 && settings.DatabaseEnabled)
                        observation = ReadDatabase(connectionString, settings.HeartbeatStaleMinutes) ?? observation;
                    var events = state.Apply(observation, DateTime.UtcNow, settings.OkHeartbeatMinutes);
                    var stop = SentinelState.ShouldStop(state.ReportedEventId, settings.StopSelfWhenHostAgentDown);
                    if (state.Faulted && !File.Exists(stateFile)) File.WriteAllText(stateFile, state.ReportedEventId.ToString());
                    foreach (var eventId in events)
                        WriteEvent(eventId, (eventId == 1 ? "Recovered. " : "") + observation.Message + (stop
                            ? " Sentinel now stops with SCM exit code " + eventId + " so that the configured recovery"
                              + " restarts it; the System log (7023) renders that code as an unrelated Win32 text."
                            : ""));
                    if (!state.Faulted && File.Exists(stateFile)) File.Delete(stateFile);
                    if (stop)
                    {
                        // ServiceBase exposes the SCM Win32 exit code. Nonzero plus failureflag=1
                        // enables recovery for this orderly, intentional alarm stop.
                        ExitCode = state.ReportedEventId;
                        Stop();
                        return;
                    }
                } while (!stopping.WaitOne(TimeSpan.FromSeconds(
                    state.Pending ? settings.FaultRecheckSeconds : settings.PollIntervalSeconds)));
            }
            catch (Exception ex)
            {
                // Remember configuration faults too, when the state directory is usable.
                try { if (!File.Exists(stateFile)) File.WriteAllText(stateFile, "200"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                // Never include exception messages or connection strings: these may contain secrets.
                try { WriteEvent(200, "Sentinel configuration or monitoring failure (" + ex.GetType().Name
                    + "). Check app.config, event source, WMI access and state-directory permissions. Exit code 200."); }
                finally { ExitCode = 200; Stop(); }
            }
        }

        private static Observation ReadAgent()
        {
            var snapshots = new System.Collections.Generic.List<AgentSnapshot>();
            foreach (var service in ServiceController.GetServices())
            {
                using (service)
                {
                    if (!SentinelPolicy.IsAgent(service.ServiceName)) continue;
                    var snapshot = new AgentSnapshot { Name = service.ServiceName, Status = "Unknown" };
                    snapshots.Add(snapshot);
                    try
                    {
                        snapshot.Status = service.Status.ToString();
                        if (service.Status != ServiceControllerStatus.Running) continue;
                        using (var query = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Service WHERE Name='"
                            + service.ServiceName + "'"))
                        {
                            query.Options.Timeout = TimeSpan.FromSeconds(5);
                            using (var results = query.Get())
                            foreach (ManagementObject row in results)
                            using (row)
                            {
                                var processId = Convert.ToInt32(row["ProcessId"]);
                                if (processId <= 0) continue;
                                using (var process = Process.GetProcessById(processId))
                                {
                                    snapshot.StartedUtc = process.StartTime.ToUniversalTime();
                                    snapshot.ProcessAlive = !process.HasExited;
                                }
                            }
                        }
                        service.Refresh();
                        snapshot.Status = service.Status.ToString();
                    }
                    catch (ArgumentException) { snapshot.ProcessAlive = false; }
                    catch (InvalidOperationException) { snapshot.ProcessAlive = false; }
                    catch (Win32Exception) { snapshot.ProcessAlive = false; }
                }
            }
            return SentinelPolicy.Evaluate(snapshots);
        }

        private static string ValidateConnection(SentinelSettings settings)
        {
            if (!settings.DatabaseEnabled) return null;
            var builder = new SqlConnectionStringBuilder(settings.DatabaseConnectionString);
            if (!builder.IntegratedSecurity || builder.UserID.Length > 0 || builder.Password.Length > 0
                || builder.AttachDBFilename.Length > 0 || builder.UserInstance
                || builder.DataSource.Length == 0 || builder.InitialCatalog.Length == 0)
                throw new ArgumentException("Use an existing database with Integrated Security and no credentials.");
            builder.ConnectTimeout = 5;
            builder.ApplicationName = Identity;
            return builder.ConnectionString;
        }

        private static Observation ReadDatabase(string connectionString, int staleMinutes)
        {
            try
            {
                using (var connection = new SqlConnection(connectionString))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT LastSeenUtc FROM omp.Hosts WHERE HostKey = @hostKey";
                    command.CommandTimeout = 5;
                    command.Parameters.Add("@hostKey", SqlDbType.NVarChar, 200).Value = Environment.MachineName;
                    connection.Open();
                    var result = command.ExecuteScalar();
                    var lastSeen = result == null || result == DBNull.Value ? (DateTime?)null
                        : DateTime.SpecifyKind((DateTime)result, DateTimeKind.Utc);
                    return SentinelPolicy.DatabaseHeartbeat(lastSeen, DateTime.UtcNow, staleMinutes);
                }
            }
            catch (SqlException)
            {
                return new Observation(103, "HostAgent database heartbeat could not be read. Check SQL availability and Windows account permissions.");
            }
        }

        private static void WriteEvent(int eventId, string message)
        {
            // EventLog.WriteEntry can implicitly CREATE a missing source. The native writer
            // only opens a logging handle and never modifies source registration, even if
            // an operator removes the source after OnStart has checked it.
            var handle = RegisterEventSource(null, Identity);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var type = eventId >= 100 ? EventLogEntryType.Error : EventLogEntryType.Information;
                if (!ReportEvent(handle, (ushort)type, 0, (uint)eventId, IntPtr.Zero, 1, 0,
                    new[] { message }, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { DeregisterEventSource(handle); }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RegisterEventSource(string server, string source);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReportEvent(IntPtr handle, ushort type, ushort category, uint eventId,
            IntPtr userSid, ushort stringCount, uint dataSize, string[] strings, IntPtr rawData);
        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeregisterEventSource(IntPtr handle);
    }
}
