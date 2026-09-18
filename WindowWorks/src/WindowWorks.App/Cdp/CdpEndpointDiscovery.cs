using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// Attempts to discover whether a Chromium-family browser has a live DevTools HTTP endpoint
    /// reachable (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 3).
    ///
    /// WindowWorks never launches or relaunches a browser to add <c>--remote-debugging-port</c> —
    /// per the plan's explicit constraint, the target browser must already be running with that
    /// flag set. There is no reliable, dependency-free way to read an arbitrary already-running
    /// process's command line from this project today (no WMI/System.Management usage exists
    /// elsewhere in the codebase), so rather than adding that dependency for a single lookup,
    /// this probes the conventional default debugging port (9222) that essentially all
    /// documentation, tooling, and this project's own CDP proof-of-concept already assume. If a
    /// user launched their browser with a different port, DevTools correlation simply won't be
    /// available and the UI reports why (per the plan's "clearly tell the user" requirement) —
    /// no error is raised for the always-present UIA section.
    ///
    /// Critically, a live endpoint at the default port is NOT sufficient on its own: any other
    /// Chromium instance on the machine (e.g. a leftover test browser, or an unrelated app the
    /// user happens to have running with that flag) could be the one actually listening on 9222,
    /// which would otherwise cause DevTools properties from a completely different browser
    /// window/process to be shown for the picked element. <see cref="TryFindDebuggerHttpBaseUrlAsync"/>
    /// therefore also verifies that the port is owned by the same top-level process as the
    /// browser window the user actually picked from, via the IP Helper API's TCP connection
    /// table (no netstat.exe shell-out, no WMI dependency).
    /// </summary>
    internal static class CdpEndpointDiscovery
    {
        private const string DefaultDebuggerHttpBaseUrl = "http://127.0.0.1:9222";
        private const int DefaultDebuggerPort = 9222;
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(2000);

        /// <summary>
        /// Returns the DevTools HTTP base URL (e.g. <c>http://localhost:9222</c>) if a live
        /// DevTools endpoint responds AND that endpoint's owning process matches
        /// <paramref name="expectedProcessId"/> (the picked browser window's own process id, or
        /// one of its ancestors up to the top-level browser process - Chromium's DevTools port is
        /// opened by the main browser process, not by renderer/GPU/utility child processes).
        /// Returns <c>null</c> if no endpoint is reachable, or if a reachable endpoint belongs to
        /// a different (unrelated) browser process than the one the user picked from.
        /// </summary>
        public static async Task<string?> TryFindDebuggerHttpBaseUrlAsync(
            uint expectedProcessId,
            CancellationToken cancellationToken = default)
        {
            bool haveOwner = TryGetTcpListenerOwningProcessId(DefaultDebuggerPort, out uint owningProcessId);
            if (!haveOwner || !IsSameProcessTree(owningProcessId, expectedProcessId))
            {
                // Either nothing is listening on the default debug port, or something is, but it
                // isn't the browser process the user actually picked an element from.
                return null;
            }

            using var http = new HttpClient { Timeout = ProbeTimeout };
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(ProbeTimeout);
                using var response = await http
                    .GetAsync(DefaultDebuggerHttpBaseUrl.TrimEnd('/') + "/json/version", cts.Token)
                    .ConfigureAwait(false);
                return response.IsSuccessStatusCode ? DefaultDebuggerHttpBaseUrl : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // No DevTools endpoint reachable at the default port - not an error, just means
                // the DevTools section will be absent for this selection.
                return null;
            }
        }

        /// <summary>
        /// Chromium's DevTools port is opened by the main browser process, but the picked window's
        /// own process id (from <c>GetWindowThreadProcessId</c>) is sometimes a renderer process
        /// for site-isolated content, whose parent is the browser's main process. Walk up the
        /// parent-process chain (bounded) looking for a match against the port's owning process,
        /// rather than requiring an exact single-pid match.
        /// </summary>
        private static bool IsSameProcessTree(uint owningProcessId, uint startProcessId)
        {
            uint current = startProcessId;
            for (int i = 0; i < 8 && current != 0; i++)
            {
                if (current == owningProcessId)
                {
                    return true;
                }

                if (!TryGetParentProcessId(current, out current))
                {
                    break;
                }
            }

            return false;
        }

        private static bool TryGetParentProcessId(uint processId, out uint parentProcessId)
        {
            parentProcessId = 0;
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var info = new PROCESS_BASIC_INFORMATION();
                int status = NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
                if (status != 0)
                {
                    return false;
                }

                parentProcessId = (uint)info.InheritedFromUniqueProcessId.ToInt64();
                return true;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static bool TryGetTcpListenerOwningProcessId(int port, out uint processId)
        {
            processId = 0;
            int bufferSize = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AfInet, TcpTableOwnerPidListener, 0);
            IntPtr tablePtr = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (GetExtendedTcpTable(tablePtr, ref bufferSize, false, AfInet, TcpTableOwnerPidListener, 0) != 0)
                {
                    return false;
                }

                int rowCount = Marshal.ReadInt32(tablePtr);
                IntPtr rowPtr = IntPtr.Add(tablePtr, 4);
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(IntPtr.Add(rowPtr, i * rowSize));
                    int localPort = ((row.localPort1 << 8) | row.localPort2) & 0xFFFF;
                    if (localPort == port)
                    {
                        processId = row.owningPid;
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(tablePtr);
            }
        }

        private const int AfInet = 2;
        private const int TcpTableOwnerPidListener = 3;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public byte localPort1;
            public byte localPort2;
            public byte localPort3;
            public byte localPort4;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr tcpTable,
            ref int size,
            bool sort,
            int ipVersion,
            int tableClass,
            int reserved);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength,
            out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}

