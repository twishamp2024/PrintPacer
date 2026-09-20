using System.Runtime.InteropServices;
using PrintPacer.Core.Engine;
using Serilog;

namespace PrintPacer.App.Platform;

/// <summary>
/// Keeps the machine awake for the duration of a print job, using the documented power
/// request API rather than any of the usual tricks.
///
/// Two deliberate choices:
///
///   - Only PowerRequestSystemRequired is set. The display is left free to switch off, so
///     the user can close the laptop lid or let the monitor sleep and the job still runs.
///     That is the whole point of the feature.
///
///   - The request carries a human-readable reason, so it shows up in
///     "powercfg /requests" as PrintPacer rather than as an anonymous process holding the
///     machine awake. If someone wonders why their PC will not sleep, they can find out.
///
/// PowerCreateRequest is used when available, with SetThreadExecutionState as a fallback.
/// Neither needs administrator rights.
/// </summary>
public sealed class WindowsPowerManager : IPowerManager, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsPowerManager>();

    private readonly object _gate = new();
    private IntPtr _request = IntPtr.Zero;
    private bool _usedExecutionStateFallback;

    public bool IsHoldingRequest { get; private set; }

    public void Acquire(string reason)
    {
        lock (_gate)
        {
            if (IsHoldingRequest) return;

            try
            {
                var context = new REASON_CONTEXT
                {
                    Version = DIAGNOSTIC_REASON_VERSION,
                    Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                    SimpleReasonString = reason,
                };

                _request = PowerCreateRequest(ref context);

                if (_request != IntPtr.Zero && _request != new IntPtr(-1))
                {
                    if (PowerSetRequest(_request, PowerRequestType.PowerRequestSystemRequired))
                    {
                        IsHoldingRequest = true;
                        Log.Information("Keep-awake request acquired: {Reason}", reason);
                        return;
                    }

                    CloseHandle(_request);
                    _request = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "PowerCreateRequest unavailable; falling back");
            }

            // ES_CONTINUOUS | ES_SYSTEM_REQUIRED, deliberately without ES_DISPLAY_REQUIRED.
            var previous = SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired);
            if (previous != 0)
            {
                _usedExecutionStateFallback = true;
                IsHoldingRequest = true;
                Log.Information("Keep-awake acquired via execution state: {Reason}", reason);
            }
            else
            {
                Log.Warning("Could not keep the computer awake; it may sleep before the job finishes");
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (!IsHoldingRequest) return;

            try
            {
                if (_request != IntPtr.Zero)
                {
                    PowerClearRequest(_request, PowerRequestType.PowerRequestSystemRequired);
                    CloseHandle(_request);
                    _request = IntPtr.Zero;
                }

                if (_usedExecutionStateFallback)
                {
                    SetThreadExecutionState(ExecutionState.Continuous);
                    _usedExecutionStateFallback = false;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Releasing the keep-awake request failed");
            }
            finally
            {
                IsHoldingRequest = false;
                Log.Information("Keep-awake request released");
            }
        }
    }

    public void Dispose() => Release();

    // ---- Interop -----------------------------------------------------------

    private const uint DIAGNOSTIC_REASON_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x00000001;

    private enum PowerRequestType
    {
        PowerRequestDisplayRequired = 0,
        PowerRequestSystemRequired = 1,
        PowerRequestAwayModeRequired = 2,
        PowerRequestExecutionRequired = 3,
    }

    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState state);
}
