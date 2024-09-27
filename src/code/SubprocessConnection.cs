// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Remoting;
using System.Management.Automation.Remoting.Client;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
using System.Threading;

namespace Microsoft.PowerShell.CustomNamedPipeConnection
{
    internal sealed class SubprocessClient
    {
        private NamedPipeClientStream _clientPipeStream;
        private volatile bool _connecting;

        public StreamReader TextReader { get; private set; }

        public StreamWriter TextWriter { get; private set; }

        public string PipeName { get; private set; }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            if (TextReader != null)
            {
                try { TextReader.Dispose(); }
                catch (ObjectDisposedException) { }

                TextReader = null;
            }

            if (TextWriter != null)
            {
                try { TextWriter.Dispose(); }
                catch (ObjectDisposedException) { }

                TextWriter = null;
            }

            if (_clientPipeStream != null)
            {
                try { _clientPipeStream.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }

        private SubprocessClient()
        { }

        public SubprocessClient(int processId, string pipeName)
        {
            PipeName = pipeName;

            if (string.IsNullOrEmpty(PipeName))
            {
                PipeName = CreateProcessPipeName(processId);
            }
        }

        private static string CreateProcessPipeName(int processId)
        {
            Process proc = Process.GetProcessById(processId);
            System.Text.StringBuilder pipeNameBuilder = new System.Text.StringBuilder();
            pipeNameBuilder.Append(@"PSHost.");

            if (OperatingSystem.IsWindows())
            {
                pipeNameBuilder.Append(proc.StartTime.ToFileTime().ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                pipeNameBuilder.Append(proc.StartTime.ToFileTime().ToString("X8").AsSpan(1, 8));
            }

            pipeNameBuilder.Append('.')
                .Append(proc.Id.ToString(CultureInfo.InvariantCulture))
                .Append('.')
                .Append(@"DefaultAppDomain")
                .Append('.')
                .Append(proc.ProcessName);

            return pipeNameBuilder.ToString();
        }

        public static string GetPwshPath()
        {
            string currentExePath = Process.GetCurrentProcess().MainModule.FileName;

            if (currentExePath != null && Path.GetFileName(currentExePath).Contains("pwsh", StringComparison.OrdinalIgnoreCase))
            {
                return currentExePath;
            }

            string pwshExecutableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pwsh.exe" : "pwsh";

            string[] paths = Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator);
            foreach (string path in paths)
            {
                string fullPath = Path.Combine(path, pwshExecutableName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }

            return null;
        }

        public void Connect(int timeout)
        {
            _clientPipeStream = DoConnect(timeout);
            TextReader = new StreamReader(_clientPipeStream);
            TextWriter = new StreamWriter(_clientPipeStream);
            TextWriter.AutoFlush = true;
        }

        public void Close()
        {
            if (_clientPipeStream != null)
            {
                _clientPipeStream.Dispose();
            }
        }

        public void AbortConnect()
        {
            _connecting = false;
        }

        private NamedPipeClientStream DoConnect(int timeout)
        {
            // Repeatedly attempt connection to pipe until timeout expires.
            int startTime = Environment.TickCount;
            int elapsedTime = 0;
            _connecting = true;

            NamedPipeClientStream namedPipeClientStream = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);

            namedPipeClientStream.ConnectAsync(timeout);

            do
            {
                if (!namedPipeClientStream.IsConnected)
                {
                    Thread.Sleep(100);
                    elapsedTime = unchecked(Environment.TickCount - startTime);
                    continue;
                }

                _connecting = false;
                return namedPipeClientStream;
            } while (_connecting && (elapsedTime < timeout));

            _connecting = false;

            throw new TimeoutException(@"Timeout expired before connection could be made to named pipe.");
        }
    }

    internal sealed class SubprocessInfo : RunspaceConnectionInfo
    {
        private SubprocessClient _clientPipe;
        private readonly string _computerName;

        public int ProcessId
        {
            get;
            set;
        }

        public int ConnectionTimeout
        {
            get;
            set;
        }

        public string CustomPipeName
        {
            get;
            set;
        }

        private Process _process = null;
        private bool _shallow = false;

        private SubprocessInfo()
        { }

        public SubprocessInfo(
            int processId,
            int connectionTimeout,
            string customPipeName)
        {
            if (processId < 0)
            {
                string pwshPath = SubprocessClient.GetPwshPath();
                _process = new Process();
                _process.StartInfo.FileName = pwshPath;
                _process.StartInfo.Arguments = "-NoLogo -NoProfile";
                _process.StartInfo.RedirectStandardInput = true;
                _process.StartInfo.RedirectStandardOutput = true;
                _process.StartInfo.RedirectStandardError = true;
                _process.StartInfo.UseShellExecute = false;
                _process.StartInfo.CreateNoWindow = true;
                _process.Start();
                ProcessId = _process.Id;
                _shallow = false;
            }
            else
            {
                ProcessId = processId;
                _shallow = true;
            }

            CustomPipeName = customPipeName;
            ConnectionTimeout = connectionTimeout;
            _computerName = $"Subprocess:{ProcessId}";
            _clientPipe = new SubprocessClient(ProcessId, CustomPipeName);
        }

        public override string ComputerName
        {
            get { return _computerName; }
            set { throw new NotImplementedException(); }
        }

        public override PSCredential Credential
        {
            get { return null; }
            set { throw new NotImplementedException(); }
        }

        public override AuthenticationMechanism AuthenticationMechanism
        {
            get { return AuthenticationMechanism.Default; }
            set { throw new NotImplementedException(); }
        }

        public override string CertificateThumbprint
        {
            get { return string.Empty; }
            set { throw new NotImplementedException(); }
        }

        public override RunspaceConnectionInfo Clone()
        {
            var connectionInfo = new SubprocessInfo(ProcessId, ConnectionTimeout, CustomPipeName);
            connectionInfo._clientPipe = _clientPipe;
            return connectionInfo;
        }

        public override BaseClientSessionTransportManager CreateClientSessionTransportManager(
            Guid instanceId,
            string sessionName,
            PSRemotingCryptoHelper cryptoHelper)
        {
            return new SubprocessClientSessionTransportMgr(
                connectionInfo: this,
                runspaceId: instanceId,
                cryptoHelper: cryptoHelper);
        }

        public void Connect(
            out StreamWriter textWriter,
            out StreamReader textReader)
        {
            _clientPipe.Connect(
                timeout: ConnectionTimeout > -1 ? ConnectionTimeout : int.MaxValue);

            textWriter = _clientPipe.TextWriter;
            textReader = _clientPipe.TextReader;
        }

        public void StopConnect()
        {
            _clientPipe?.AbortConnect();
            _clientPipe?.Close();
            _clientPipe?.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopConnect();

                if (!_shallow && _process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.Dispose();
                    _process = null;
                }
            }
        }
    }

    internal sealed class SubprocessClientSessionTransportMgr : ClientSessionTransportManagerBase
    {
        private readonly SubprocessInfo _connectionInfo;
        private const string _threadName = "SubprocessCustomTransport Reader Thread";

        internal SubprocessClientSessionTransportMgr(
            SubprocessInfo connectionInfo,
            Guid runspaceId,
            PSRemotingCryptoHelper cryptoHelper)
            : base(runspaceId, cryptoHelper)
        {
            if (connectionInfo == null) { throw new PSArgumentException("connectionInfo"); }

            _connectionInfo = connectionInfo;
        }

        public override void CreateAsync()
        {
            _connectionInfo.Connect(
                out StreamWriter pipeTextWriter,
                out StreamReader pipeTextReader);

            SetMessageWriter(pipeTextWriter);
            StartReaderThread(pipeTextReader);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (isDisposing)
            {
                CloseConnection();
            }
        }

        protected override void CleanupConnection()
        {
            CloseConnection();
        }

        private void CloseConnection()
        {
            _connectionInfo.StopConnect();
        }

        private void HandleSSHError(PSRemotingTransportException ex)
        {
            RaiseErrorHandler(
                new TransportErrorOccuredEventArgs(
                    ex,
                    TransportMethodEnum.CloseShellOperationEx));

            CloseConnection();
        }

        private void StartReaderThread(
            StreamReader reader)
        {
            Thread readerThread = new Thread(ProcessReaderThread);
            readerThread.Name = _threadName;
            readerThread.IsBackground = true;
            readerThread.Start(reader);
        }

        private void ProcessReaderThread(object state)
        {
            try
            {
                StreamReader reader = state as StreamReader;

                // Send one fragment.
                SendOneItem();

                // Start reader loop.
                while (true)
                {
                    string data = reader.ReadLine();
                    if (data == null)
                    {
                        // End of stream indicates that the SSH transport is broken.
                        // SSH will return the appropriate error in StdErr stream so
                        // let the error reader thread report the error.
                        break;
                    }

                    HandleDataReceived(data);
                }
            }
            catch (ObjectDisposedException)
            {
                // Normal reader thread end.
            }
            catch (Exception e)
            {
                string errorMsg = e.Message ?? string.Empty;
                HandleSSHError(new PSRemotingTransportException(
                    $"The SSH client session has ended reader thread with message: {errorMsg}"));
            }
        }
    }

    [Cmdlet(VerbsCommon.New, "SubprocessSession")]
    [OutputType(typeof(PSSession))]
    public sealed class NewSubprocessSessionCommand : PSCmdlet
    {
        private SubprocessInfo _connectionInfo;
        private Runspace _runspace;
        private ManualResetEvent _openAsync;

        [Parameter]
        [ValidateNotNullOrEmpty]
        public int ProcessId { get; set; } = -1;

        [Parameter]
        [ValidateRange(-1, 86400)]
        public int ConnectionTimeout { get; set; } = 5000;

        [Parameter]
        [ValidateNotNullOrEmpty]
        public string Name { get; set; }

        [Parameter]
        [ValidateNotNullOrEmpty]
        public string CustomPipeName { get; set; }

        protected override void BeginProcessing()
        {
            _connectionInfo = new SubprocessInfo(ProcessId, ConnectionTimeout, CustomPipeName);

            _runspace = RunspaceFactory.CreateRunspace(
                connectionInfo: _connectionInfo,
                host: Host,
                typeTable: TypeTable.LoadDefaultTypeFiles(),
                applicationArguments: null,
                name: Name);

            _openAsync = new ManualResetEvent(false);
            _runspace.StateChanged += HandleRunspaceStateChanged;

            try
            {
                _runspace.OpenAsync();
                _openAsync.WaitOne();

                WriteObject(
                    PSSession.Create(
                        runspace: _runspace,
                        transportName: "Subprocess",
                        psCmdlet: this));
            }
            finally
            {
                _openAsync.Dispose();
            }
        }

        protected override void StopProcessing()
        {
            _connectionInfo?.StopConnect();
        }

        private void HandleRunspaceStateChanged(
            object source,
            RunspaceStateEventArgs stateEventArgs)
        {
            switch (stateEventArgs.RunspaceStateInfo.State)
            {
                case RunspaceState.Opened:
                case RunspaceState.Closed:
                case RunspaceState.Broken:
                    _runspace.StateChanged -= HandleRunspaceStateChanged;
                    ReleaseWait();
                    break;
            }
        }

        private void ReleaseWait()
        {
            try
            {
                _openAsync?.Set();
            }
            catch (ObjectDisposedException)
            { }
        }
    }
}
