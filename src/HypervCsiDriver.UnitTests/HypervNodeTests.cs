using HypervCsiDriver.Infrastructure;
using HypervCsiDriver.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.PowerShell.Commands;
using PNet.Automation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace HypervCsiDriver.UnitTests
{
    public sealed class HypervNodeFixture : IDisposable
    {
        PNetPowerShell _power;

        public PNetPowerShell GetPower(string hostName)
        {
            if (_power is null)
            {
                _power = new PNetPowerShell(hostName, "root", null); //remote

            }
            return _power;
        }

        public void Dispose()
        {
            (_power as IDisposable)?.Dispose();
        }
    }

    [Trait("Type", "Integration")]
    [Trait("Category", "HypervNode")]
    public sealed class HypervNodeTests : IClassFixture<HypervNodeFixture>
    {
        public HypervNodeFixture Fixture { get; }

        public HypervNodeTests(HypervNodeFixture fixture)
        {
            Fixture = fixture;
        }

        [Theory]
        [InlineData("lnx1512", "ea0bf947-27d7-4d85-b23b-987e9c2393f2")]
        public async Task GetDeviceByVhdIdAsync(string hostName, Guid vhdId)
        {
            var power = Fixture.GetPower(hostName);

            Command cmd;
            var commands = new List<Command>(2);

            var diskFilter = HypervUtils.GetDiskFilter(vhdId);

            cmd = new Command("Get-ChildItem");
            cmd.Parameters.Add("Path", diskFilter);
            commands.Add(cmd);

            cmd = new Command("Select-Object");
            cmd.Parameters.Add("Property", new [] { "Directory", "Target" });
            commands.Add(cmd);

            //hack until Join-Path in pipe
            dynamic obj = await power.InvokeAsync(commands).ThrowOnError()
                   .FirstOrDefaultAsync();

            var blockDeviceName = string.Empty;
            if (obj is not null)
            {
                commands.Clear();

                cmd = new Command("Join-Path");
                cmd.Parameters.Add("Path", obj.Directory);
                cmd.Parameters.Add("ChildPath", obj.Target);
                cmd.Parameters.Add("Resolve");
                commands.Add(cmd);

                blockDeviceName = await power.InvokeAsync(commands).ThrowOnError()
                    .Select(n => (string)n.BaseObject)
                   .FirstOrDefaultAsync();

            }

            Assert.StartsWith("/dev/", blockDeviceName);
        }

        [Theory]
        [InlineData("lnx1512", "/dev/sdb", "/notexist", false)]
        public async Task FindMountByDeviceAsync(string hostName, string deviceName, string targetPath, bool result)
        {
            var power = Fixture.GetPower(hostName);

            Command cmd;
            var commands = new List<Command>(2);

            cmd = new Command($"findmnt -fnr {deviceName} {targetPath}", true);
            commands.Add(cmd);

            var mountpoint = await power.InvokeAsync(commands).ThrowOnError()
                .Select(n => n.BaseObject).OfType<string>()
                .FirstOrDefaultAsync();

            Assert.Equal(result, !string.IsNullOrEmpty(mountpoint));
        }

        [Theory]
        [InlineData("lnx1513", "/dev/sdd1")]
        public async Task EnumDeviceLabelsAsync(string hostName, string deviceName)
        {
            var power = Fixture.GetPower(hostName);

            Command cmd;
            var commands = new List<Command>(2);

            cmd = new Command($"blkid -o export -c /dev/null -D {deviceName}", true);
            commands.Add(cmd);

            await foreach (var line in power.InvokeAsync(commands).ThrowOnError()
                .Select(n => n.BaseObject).OfType<string>()
                .TakeWhile(n => !string.IsNullOrEmpty(n)))
            {
                var name = line;
                var value = string.Empty;

                int i = line.IndexOf('=');
                if (i > -1)
                {
                    name = line.Substring(0, i);
                    value = line.Substring(i + 1);
                }

                switch (name)
                {
                    case "DEVNAME" when deviceName != value:
                        throw new Exception("invalid device info");
                    case "LABEL":
                        Assert.NotEmpty(value);
                        break;
                    case "UUID":
                        Assert.NotEmpty(value);
                        break;
                    case "TYPE":
                        Assert.NotEmpty(value);
                        break;
                }
            }
        }

        /// <summary>
        /// https://github.com/PowerShell/PowerShell/issues/17772
        /// </summary>
        [Fact]        
        public async Task RemoteExitCode()
        {
            

            //hack for remote connection
            if (Runspace.DefaultRunspace == null)
            {
                var defaultRunspace = RunspaceFactory.CreateRunspace();
                defaultRunspace.Open();

                Runspace.DefaultRunspace = defaultRunspace;
            }

            var localRs = RunspaceFactory.CreateRunspace();

            var localPipe = localRs.CreatePipeline("pwsh -c 'exit 123'");

            localRs.Open();

            var localResult = localPipe.Invoke();

            Assert.True(localPipe.HadErrors);
            Assert.Empty(localResult);

            localRs.Close();


            var connectionInfo = new SSHConnectionInfo("root", "lnx1513", null);
            var remoteRS = RunspaceFactory.CreateRunspace(connectionInfo);

            var remotePipe = remoteRS.CreatePipeline("pwsh -c 'exit 123'");

            remoteRS.Open();

            var remoteResult = remotePipe.Invoke();

            Assert.True(remotePipe.HadErrors);
            Assert.Empty(remoteResult);

            remoteRS.Close();
        }
    }
}
