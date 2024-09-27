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
    internal sealed class PSHostClient
    {
        private volatile bool _connecting;

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
        }

        public PSHostClient()
        {

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

        }

        public void Close()
        {

        }

        public void AbortConnect()
        {

        }
    }

    internal sealed class PSHostClientInfo : RunspaceConnectionInfo
    {
        private PSHostClient _clientPipe;
        private readonly string _computerName;
        private Process _process;

        public int ProcessId
        {
            get;
            set;
        }

        public Process Process
        {
            get { return _process; }
        }

        private bool _shallow = false;

        public PSHostClientInfo()
        {
            string pwshPath = PSHostClient.GetPwshPath();

            Process _process = new Process();
            _process.StartInfo.FileName = pwshPath;
            _process.StartInfo.Arguments = "-s -NoLogo -NoProfile";
            _process.StartInfo.RedirectStandardInput = true;
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.RedirectStandardError = true;
            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.CreateNoWindow = true;
            _process.Start();
            ProcessId = _process.Id;
            _shallow = false;
            _computerName = $"PSHost:{ProcessId}";
        }

        public PSHostClientInfo(int processId)
        {
            ProcessId = processId;
            _shallow = true;
            _computerName = $"PSHost:{ProcessId}";
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
            var connectionInfo = new PSHostClientInfo(ProcessId);
            return connectionInfo;
        }

        public override BaseClientSessionTransportManager CreateClientSessionTransportManager(
            Guid instanceId,
            string sessionName,
            PSRemotingCryptoHelper cryptoHelper)
        {
            return new PSHostClientSessionTransportMgr(
                connectionInfo: this,
                runspaceId: instanceId,
                cryptoHelper: cryptoHelper);
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

    internal sealed class PSHostClientSessionTransportMgr : ClientSessionTransportManagerBase
    {
        private readonly PSHostClientInfo _connectionInfo;
        private const string _threadName = "PSHostClientCustomTransport Reader Thread";

        internal PSHostClientSessionTransportMgr(
            PSHostClientInfo connectionInfo,
            Guid runspaceId,
            PSRemotingCryptoHelper cryptoHelper)
            : base(runspaceId, cryptoHelper)
        {
            if (connectionInfo == null) { throw new PSArgumentException("connectionInfo"); }

            _connectionInfo = connectionInfo;
        }

        public override void CreateAsync()
        {
            // connect here
            Process process = _connectionInfo.Process;
            SetMessageWriter(process.StandardInput);
            StartReaderThread(process.StandardOutput);
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

    [Cmdlet(VerbsCommon.New, "PSHostSession")]
    [OutputType(typeof(PSSession))]
    public sealed class NewPSHostSessionCommand : PSCmdlet
    {
        private PSHostClientInfo _connectionInfo;
        private Runspace _runspace;
        private ManualResetEvent _openAsync;

        protected override void BeginProcessing()
        {
            _connectionInfo = new PSHostClientInfo();

            _runspace = RunspaceFactory.CreateRunspace(
                connectionInfo: _connectionInfo,
                host: Host,
                typeTable: TypeTable.LoadDefaultTypeFiles(),
                applicationArguments: null,
                name: "PSHostClient");

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
