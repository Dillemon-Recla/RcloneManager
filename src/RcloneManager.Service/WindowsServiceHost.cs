using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RcloneManager.Service;

/// <summary>
/// Implementação leve e nativa do dispatcher SCM do Windows via advapi32.dll,
/// permitindo rodar como Serviço do Windows (sc.exe create) sem pacotes NuGet externos.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsServiceHost
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlShutdown = 0x00000005;

    private delegate void ServiceMainCallback(uint dwArgc, IntPtr lpszArgv);
    private delegate uint ServiceControlHandlerEx(uint dwControl, uint dwEventType, IntPtr lpEventData, IntPtr lpContext);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        public string? lpServiceName;
        public IntPtr lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcherW(ServiceTableEntry[] lpServiceStartTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(
        string lpServiceName,
        ServiceControlHandlerEx lpHandlerProc,
        IntPtr lpContext);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr hServiceStatus, ref ServiceStatus lpServiceStatus);

    private static IntPtr _statusHandle;
    private static ServiceMainCallback? _serviceMainDelegate;
    private static ServiceControlHandlerEx? _ctrlHandlerDelegate;
    private static string _serviceName = ServiceControlHelper.WindowsServiceName;

    public static void RunWithStopCallback(string serviceName, Func<Action<Action>, Task> runHostWithStopRegistration)
    {
        _serviceName = serviceName;
        Action? registeredStop = null;

        _serviceMainDelegate = (_, _) =>
        {
            _ctrlHandlerDelegate = (control, _, _, _) =>
            {
                if (control is ServiceControlStop or ServiceControlShutdown)
                {
                    UpdateState(ServiceStopPending, 0, 5000);
                    registeredStop?.Invoke();
                }
                return 0;
            };

            _statusHandle = RegisterServiceCtrlHandlerExW(_serviceName, _ctrlHandlerDelegate, IntPtr.Zero);
            if (_statusHandle == IntPtr.Zero)
                return;

            UpdateState(ServiceStartPending, 0, 5000);
            try
            {
                UpdateState(ServiceRunning, ServiceAcceptStop | ServiceAcceptShutdown, 0);
                runHostWithStopRegistration(stopCb => registeredStop = stopCb).GetAwaiter().GetResult();
                UpdateState(ServiceStopped, 0, 0);
            }
            catch
            {
                UpdateState(ServiceStopped, 0, 0, exitCode: 1);
            }
        };

        var mainPtr = Marshal.GetFunctionPointerForDelegate(_serviceMainDelegate);
        var table = new[]
        {
            new ServiceTableEntry { lpServiceName = _serviceName, lpServiceProc = mainPtr },
            new ServiceTableEntry { lpServiceName = null, lpServiceProc = IntPtr.Zero }
        };

        StartServiceCtrlDispatcherW(table);
    }

    private static void UpdateState(uint state, uint controlsAccepted, uint waitHint, uint exitCode = 0)
    {
        if (_statusHandle == IntPtr.Zero)
            return;

        var status = new ServiceStatus
        {
            dwServiceType = ServiceWin32OwnProcess,
            dwCurrentState = state,
            dwControlsAccepted = controlsAccepted,
            dwWin32ExitCode = exitCode,
            dwServiceSpecificExitCode = 0,
            dwCheckPoint = 0,
            dwWaitHint = waitHint
        };
        SetServiceStatus(_statusHandle, ref status);
    }
}
