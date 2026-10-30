using System;
using System.IO;

namespace OpenModulePlatform.HostAgent.Sentinel
{
    internal static class SentinelFaultHandler
    {
        internal static void Handle(
            Exception monitoringFailure,
            Action persistFaultState,
            Action<int, string> writeEvent,
            Action setFailureExitCode,
            Action stop)
        {
            // Remember configuration faults too, when the state directory is usable.
            var statePersistenceFailure = string.Empty;
            try { persistFaultState(); }
            catch (IOException)
            {
                // Marker persistence is best effort; report its failure with the alarm below.
                statePersistenceFailure =
                    " Fault-state persistence failed (IOException); restart fault restoration is unavailable.";
            }
            catch (UnauthorizedAccessException)
            {
                // Marker persistence is best effort; report its failure with the alarm below.
                statePersistenceFailure =
                    " Fault-state persistence failed (UnauthorizedAccessException); restart fault restoration is unavailable.";
            }

            // Never include exception messages or connection strings: these may contain secrets.
            try
            {
                writeEvent(200, "Sentinel configuration or monitoring failure (" + monitoringFailure.GetType().Name
                    + "). Check app.config, event source, WMI access and state-directory permissions. Exit code 200."
                    + statePersistenceFailure);
            }
            finally
            {
                setFailureExitCode();
                stop();
            }
        }
    }
}
