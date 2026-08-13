using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace sutty.Core.Terminal;

/// <summary>
/// Windows-local PowerShell terminal backed by the public ConPTY API. The child is
/// assigned to a kill-on-close job so an app crash cannot leave the shell tree behind.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class WindowsConPtyTerminal : IInteractiveTerminal
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _resizeGate = new(1, 1);
    private readonly bool _loadProfile;
    private ConPtyHost? _host;

    public WindowsConPtyTerminal(bool loadProfile = false)
    {
        _loadProfile = loadProfile;
    }

    public TerminalState TerminalState { get; private set; } = TerminalState.Closed;
    public string? LastTerminalError { get; private set; }
    public bool SupportsTerminalResize => true;

    public event EventHandler<TerminalState>? TerminalStateChanged;
    public event EventHandler<TerminalDataReceivedEventArgs>? TerminalDataReceived;

    public async Task OpenTerminalAsync(TerminalSize size, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            if (_host is not null && TerminalState == TerminalState.Open)
                return;

            await CloseCoreAsync();
            LastTerminalError = null;
            SetState(TerminalState.Opening);

            ConPtyHost? host = null;
            try
            {
                host = ConPtyHost.Create(size.Clamp(), _loadProfile);
                host.DataReceived += (_, data) =>
                {
                    if (ReferenceEquals(Volatile.Read(ref _host), host))
                        TerminalDataReceived?.Invoke(this, new TerminalDataReceivedEventArgs(data.ToArray()));
                };
                host.Ended += (_, error) => _ = RetireHostAsync(host, error);
                Volatile.Write(ref _host, host);
                host.Start();
                SetState(TerminalState.Open);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (host is not null)
                    await host.DisposeAsync();
                Volatile.Write(ref _host, null);
                SetState(TerminalState.Closed);
                throw;
            }
            catch (Exception error)
            {
                if (host is not null)
                    await host.DisposeAsync();
                Volatile.Write(ref _host, null);
                LastTerminalError = error.Message;
                SetState(TerminalState.Failed);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task SendTerminalInputAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken ct = default)
    {
        if (data.IsEmpty)
            return;

        var host = Volatile.Read(ref _host);
        if (host is null || TerminalState != TerminalState.Open)
            throw new InvalidOperationException("Local terminal is not open.");

        await _writeGate.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(host, Volatile.Read(ref _host)))
                throw new OperationCanceledException("Local terminal was replaced.", ct);
            await host.WriteAsync(data, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> ResizeTerminalAsync(
        TerminalSize size,
        CancellationToken ct = default)
    {
        var host = Volatile.Read(ref _host);
        if (host is null || TerminalState != TerminalState.Open)
            return false;

        await _resizeGate.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(host, Volatile.Read(ref _host)))
                return false;

            try
            {
                await Task.Run(() => host.Resize(size.Clamp()), ct);
                return ReferenceEquals(host, Volatile.Read(ref _host));
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
        finally
        {
            _resizeGate.Release();
        }
    }

    public async Task CloseTerminalAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await CloseCoreAsync();
            SetState(TerminalState.Closed);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RetireHostAsync(ConPtyHost host, Exception? error)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(host, Volatile.Read(ref _host)))
                return;

            Volatile.Write(ref _host, null);
            await host.DisposeAsync();
            if (error is not null)
            {
                LastTerminalError = error.Message;
                SetState(TerminalState.Failed);
            }
            else
            {
                SetState(TerminalState.Closed);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task CloseCoreAsync()
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host is not null)
            await host.DisposeAsync();
    }

    private void SetState(TerminalState state)
    {
        if (TerminalState == state)
            return;
        TerminalState = state;
        TerminalStateChanged?.Invoke(this, state);
    }

    private sealed class ConPtyHost : IAsyncDisposable
    {
        private readonly FileStream _input;
        private readonly FileStream _output;
        private readonly Process _process;
        private readonly SafeFileHandle _job;
        private readonly object _nativeGate = new();
        private readonly object _disposeGate = new();
        private IntPtr _pseudoConsole;
        private Task? _readTask;
        private Task? _disposeTask;
        private int _started;
        private int _ended;
        private int _closing;

        private ConPtyHost(
            IntPtr pseudoConsole,
            FileStream input,
            FileStream output,
            Process process,
            SafeFileHandle job)
        {
            _pseudoConsole = pseudoConsole;
            _input = input;
            _output = output;
            _process = process;
            _job = job;
        }

        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public event EventHandler<Exception?>? Ended;

        public static ConPtyHost Create(TerminalSize size, bool loadProfile)
        {
            IntPtr inputRead = IntPtr.Zero;
            IntPtr inputWrite = IntPtr.Zero;
            IntPtr outputRead = IntPtr.Zero;
            IntPtr outputWrite = IntPtr.Zero;
            IntPtr pseudoConsole = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr processHandle = IntPtr.Zero;
            IntPtr threadHandle = IntPtr.Zero;
            IntPtr jobHandle = IntPtr.Zero;
            var attributeListInitialized = false;
            FileStream? input = null;
            FileStream? output = null;
            Process? process = null;
            SafeFileHandle? job = null;

            try
            {
                var security = new SECURITY_ATTRIBUTES
                {
                    Length = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    InheritHandle = true,
                };
                ThrowIfFalse(CreatePipe(out inputRead, out inputWrite, ref security, 0));
                ThrowIfFalse(CreatePipe(out outputRead, out outputWrite, ref security, 0));
                ThrowIfFalse(SetHandleInformation(inputWrite, HandleFlagInherit, 0));
                ThrowIfFalse(SetHandleInformation(outputRead, HandleFlagInherit, 0));

                ThrowIfFailed(CreatePseudoConsole(ToCoord(size), inputRead, outputWrite, 0, out pseudoConsole));

                var startup = new STARTUPINFOEX();
                startup.StartupInfo.Cb = Marshal.SizeOf<STARTUPINFOEX>();
                // A console host may itself be launched with redirected standard
                // handles (as happens in CI and `dotnet run`). Marking the fields as
                // explicitly supplied clears those parent values so ConPTY can create
                // its own console handles for the child. No raw pipe handle is inherited.
                startup.StartupInfo.Flags = StartfUseStdHandles;
                nuint attributeBytes = 0;
                _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
                attributeList = Marshal.AllocHGlobal(checked((int)attributeBytes));
                ThrowIfFalse(InitializeProcThreadAttributeList(
                    attributeList, 1, 0, ref attributeBytes));
                attributeListInitialized = true;
                ThrowIfFalse(UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero));
                startup.AttributeList = attributeList;

                var shellPath = GetPowerShellPath();
                var profileArgument = loadProfile ? string.Empty : " -NoProfile";
                var commandLine = new StringBuilder($"\"{shellPath}\" -NoLogo{profileArgument} -NoExit");
                var workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                ThrowIfFalse(CreateProcess(
                    shellPath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startup,
                    out var processInfo));
                processHandle = processInfo.Process;
                threadHandle = processInfo.Thread;
                // ConPTY keeps these device ends only after its first hosted process is
                // created. Closing them earlier makes the child observe a broken channel.
                CloseRawHandle(ref inputRead);
                CloseRawHandle(ref outputWrite);

                jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (jobHandle == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                ThrowIfFalse(SetInformationJobObject(
                    jobHandle,
                    JobObjectExtendedLimitInformation,
                    ref limits,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()));
                ThrowIfFalse(AssignProcessToJobObject(jobHandle, processHandle));

                process = Process.GetProcessById(unchecked((int)processInfo.ProcessId));
                input = new FileStream(
                    new SafeFileHandle(inputWrite, ownsHandle: true),
                    FileAccess.Write,
                    4096,
                    isAsync: false);
                inputWrite = IntPtr.Zero;
                output = new FileStream(
                    new SafeFileHandle(outputRead, ownsHandle: true),
                    FileAccess.Read,
                    4096,
                    isAsync: false);
                outputRead = IntPtr.Zero;
                job = new SafeFileHandle(jobHandle, ownsHandle: true);
                jobHandle = IntPtr.Zero;

                var host = new ConPtyHost(pseudoConsole, input, output, process, job);
                pseudoConsole = IntPtr.Zero;
                input = null;
                output = null;
                process = null;
                job = null;
                return host;
            }
            finally
            {
                if (attributeListInitialized)
                    DeleteProcThreadAttributeList(attributeList);
                if (attributeList != IntPtr.Zero)
                    Marshal.FreeHGlobal(attributeList);
                CloseRawHandle(ref threadHandle);
                CloseRawHandle(ref processHandle);
                CloseRawHandle(ref inputRead);
                CloseRawHandle(ref inputWrite);
                CloseRawHandle(ref outputRead);
                CloseRawHandle(ref outputWrite);
                if (jobHandle != IntPtr.Zero)
                    CloseHandle(jobHandle);
                if (pseudoConsole != IntPtr.Zero)
                    ClosePseudoConsole(pseudoConsole);
                input?.Dispose();
                output?.Dispose();
                process?.Dispose();
                job?.Dispose();
            }
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;

            _readTask = Task.Run(ReadLoop);
            _process.Exited += Process_Exited;
            _process.EnableRaisingEvents = true;
            if (_process.HasExited)
                ScheduleNormalEnd();
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            var copy = data.ToArray();
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this);
                _input.Write(copy, 0, copy.Length);
                _input.Flush();
            }, ct);
        }

        public void Resize(TerminalSize size)
        {
            lock (_nativeGate)
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _closing) != 0 || _pseudoConsole == IntPtr.Zero,
                    this);
                ThrowIfFailed(ResizePseudoConsole(_pseudoConsole, ToCoord(size)));
            }
        }

        private void ReadLoop()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    var count = _output.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    DataReceived?.Invoke(this, buffer.AsMemory(0, count).ToArray());
                }
                SignalEnded(null);
            }
            catch (Exception error) when (
                Volatile.Read(ref _closing) == 0 &&
                error is IOException or ObjectDisposedException or Win32Exception)
            {
                SignalEnded(error);
            }
        }

        private void Process_Exited(object? sender, EventArgs e) => ScheduleNormalEnd();

        private void ScheduleNormalEnd() => _ = Task.Run(async () =>
        {
            // Let the reader drain the last prompt/output before closing ConPTY.
            await Task.Delay(200);
            SignalEnded(null);
        });

        private void SignalEnded(Exception? error)
        {
            if (Volatile.Read(ref _closing) != 0 || Interlocked.Exchange(ref _ended, 1) != 0)
                return;
            Ended?.Invoke(this, error);
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
                return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0)
                return;

            _process.Exited -= Process_Exited;
            try { _input.Dispose(); } catch { }
            try { _job.Dispose(); } catch { }

            IntPtr pseudoConsole;
            lock (_nativeGate)
            {
                pseudoConsole = _pseudoConsole;
                _pseudoConsole = IntPtr.Zero;
            }
            if (pseudoConsole != IntPtr.Zero)
            {
                // ClosePseudoConsole can wait for the hosted process to finish writing.
                // Keep ReadLoop alive and move the blocking native close off the caller's
                // (usually UI) thread so the output pipe is drained through teardown.
                try { await Task.Run(() => ClosePseudoConsole(pseudoConsole)); } catch { }
            }

            var readTask = Volatile.Read(ref _readTask);
            if (readTask is not null)
            {
                // Windows 11 24H2 may return from ClosePseudoConsole before the final
                // frame reaches the pipe. Give the reader a chance to observe EOF before
                // forcing the local pipe closed as a last-resort leak guard.
                try { await readTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
            try { _output.Dispose(); } catch { }
            if (readTask is not null && !readTask.IsCompleted)
            {
                try { await readTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
            _process.Dispose();
        }

        private static string GetPowerShellPath()
        {
            var path = Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            return File.Exists(path)
                ? path
                : throw new FileNotFoundException("Windows PowerShell was not found.", path);
        }

        private static COORD ToCoord(TerminalSize size) => new()
        {
            X = checked((short)Math.Clamp(size.Columns, 20u, (uint)short.MaxValue)),
            Y = checked((short)Math.Clamp(size.Rows, 5u, (uint)short.MaxValue)),
        };

        private static void ThrowIfFalse(bool result)
        {
            if (!result)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static void ThrowIfFailed(int hresult)
        {
            if (hresult < 0)
                Marshal.ThrowExceptionForHR(hresult);
        }

        private static void CloseRawHandle(ref IntPtr handle)
        {
            var value = handle;
            handle = IntPtr.Zero;
            if (value != IntPtr.Zero && value != new IntPtr(-1))
                CloseHandle(value);
        }

        private const uint HandleFlagInherit = 0x00000001;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const int StartfUseStdHandles = 0x00000100;
        private const int ProcThreadAttributePseudoConsole = 0x00020016;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int Cb;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StdInput;
            public IntPtr StdOutput;
            public IntPtr StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(
            out IntPtr readPipe,
            out IntPtr writePipe,
            ref SECURITY_ATTRIBUTES pipeAttributes,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(
            COORD size,
            IntPtr input,
            IntPtr output,
            uint flags,
            out IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr pseudoConsole, COORD size);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            nuint size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref STARTUPINFOEX startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
