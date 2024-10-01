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
    internal sealed class PSHostClientInfo : RunspaceConnectionInfo
    {
        public override string ComputerName { get; set; }

        public string Executable { get; set; }

        public string Arguments { get; set; }

        public override PSCredential? Credential
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

        public PSHostClientInfo(string computerName, string executable, string arguments)
        {
            ComputerName = computerName;
            Executable = executable;
            Arguments = arguments;
        }

        public override RunspaceConnectionInfo Clone()
        {
            var connectionInfo = new PSHostClientInfo(ComputerName, Executable, Arguments);
            return connectionInfo;
        }

        public static string? GetPwshPath()
        {
            string? currentExePath = Environment.ProcessPath;

            if (currentExePath != null && Path.GetFileName(currentExePath).Contains("pwsh", StringComparison.OrdinalIgnoreCase))
            {
                return currentExePath;
            }

            string pwshExecutableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pwsh.exe" : "pwsh";

            string[]? paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);

            if (paths == null)
                return null;

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
    }

    internal sealed class PSHostClientSessionTransportMgr : ClientSessionTransportManagerBase
    {
        private readonly PSHostClientInfo _connectionInfo;
        private Process? _process = null;

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
            _process = new Process();
            _process.StartInfo.FileName = _connectionInfo.Executable;
            _process.StartInfo.Arguments = _connectionInfo.Arguments;
            _process.StartInfo.RedirectStandardInput = true;
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.RedirectStandardError = true;
            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.CreateNoWindow = true;

            _process.ErrorDataReceived += ErrorDataReceived;
            _process.OutputDataReceived += OutputDataReceived;
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            SetMessageWriter(_process.StandardInput);
            SendOneItem();
        }

        private void ErrorDataReceived(object? sender, DataReceivedEventArgs args)
        {
            HandleErrorDataReceived(args.Data);
        }

        private void OutputDataReceived(object? sender, DataReceivedEventArgs args)
        {
            HandleOutputDataReceived(args.Data);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (isDisposing)
            {
                CleanupConnection();
            }
        }

        protected override void CleanupConnection()
        {
            _process?.WaitForExit();
            _process?.Dispose();
            _process = null;
        }
    }

    [Cmdlet(VerbsCommon.New, "PSHostSession")]
    [OutputType(typeof(PSSession))]
    public sealed class NewPSHostSessionCommand : PSCmdlet
    {
        private PSHostClientInfo? _connectionInfo;
        private Runspace? _runspace;
        private ManualResetEvent? _openAsync;

        protected override void BeginProcessing()
        {
            string computerName = "localhost";
            string? executable = PSHostClientInfo.GetPwshPath();
            string arguments = "-NoLogo -NoProfile -s";

            if (executable == null)
            {
                throw new InvalidOperationException("The pwsh executable path could not be found.");
            }

            _connectionInfo = new PSHostClientInfo(computerName, executable, arguments);

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
                        transportName: "PSHostSession",
                        psCmdlet: this));
            }
            finally
            {
                _openAsync.Dispose();
            }
        }

        protected override void StopProcessing()
        {
            ReleaseWait();
        }

        private void HandleRunspaceStateChanged(object? source, RunspaceStateEventArgs stateEventArgs)
        {
            switch (stateEventArgs.RunspaceStateInfo.State)
            {
                case RunspaceState.Opened:
                case RunspaceState.Closed:
                case RunspaceState.Broken:
                    if (_runspace != null)
                    {
                        _runspace.StateChanged -= HandleRunspaceStateChanged;
                    }
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
            {

            }
        }
    }
}
