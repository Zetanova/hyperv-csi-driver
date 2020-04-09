using Microsoft.Extensions.Configuration;
using PNet.Automation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation.Runspaces;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Xunit;

namespace HypervCsiDriver.UnitTests
{
    public sealed class PNetPowershellFixture : IDisposable
    {
        public IConfiguration Configuration { get; set; }

        IPNetPowerShell power;

        public PNetPowershellFixture()
        {
            var builder = new ConfigurationBuilder()
                .AddUserSecrets<PNetPowershellFixture>()
                .AddEnvironmentVariables();

            Configuration = builder.Build();

        }

        public async Task<IPNetPowerShell> GetPowerShellAsync()
        {
            if (power is null)
            {
                //todo read config  Token=Configuration["tg:key"]

                power = new PNetPowerShell("sv1501", "administrator", null)
                {
                };
                //await services.ConnectAsync();
            }
            return power;
        }

        public void Dispose()
        {
            //power?.Dispose()
        }
    }


    [Trait("Type", "Integration")]
    [Trait("Category", "Powershell")]
    public sealed class PNetPowerShellTests : IClassFixture<PNetPowershellFixture>
    {
        public PNetPowershellFixture Fixture { get; }

        public PNetPowerShellTests(PNetPowershellFixture fixture)
        {
            Fixture = fixture;
        }

        [Fact]
        public async Task connect_to_server()
        {
            var power = await Fixture.GetPowerShellAsync();

            var host1Task = power.InvokeAsync("Get-Host").FirstAsync();

            var host2Task = power.InvokeAsync("Get-Host").FirstAsync();

            var host1 = await host1Task;
            dynamic host2 = await host2Task;

            var hostName = host2.Name;

            Assert.Equal("ServerRemoteHost", hostName);
        }

        [Fact]
        public async Task query_virtual_machines()
        {
            var power = await Fixture.GetPowerShellAsync();

            Command cmd;
            var commands = new List<Command>(2);

            cmd = new Command("Get-VM");
            commands.Add(cmd);

            cmd = new Command("Select-Object");
            cmd.Parameters.Add("Property", new[] { "Id", "Name", "State" });
            commands.Add(cmd);

            var vmItems = power.InvokeAsync(commands).ThrowOnError();
            /*
            Name             : Lnx1234
            State            : Running
            CpuUsage         : 1
            MemoryAssigned   : 4294967296
            MemoryDemand     : 3607101440
            MemoryStatus     : OK
            Uptime           : 1.17:05:26.0970000
            Status           : Operating normally
            ReplicationState : Disabled
            Generation       : 2 
            */

            var names = new List<string>();
            await vmItems.ForEachAsync((dynamic vm) =>
            {
                names.Add(vm.Name);
            });

            Assert.NotEmpty(names);
        }


        [Fact]
        public async Task throw_on_errors_from_local_native_commands()
        {
            var power = new PNetPowerShell();

            Command cmd;
            var commands = new List<Command>(2);

            cmd = new Command("& more -?", true);
            commands.Add(cmd);

            await Assert.ThrowsAsync<System.Management.Automation.RemoteException>(async () =>
            {
                var result = await power.InvokeAsync(commands).ThrowOnError()
                    .FirstOrDefaultAsync();
            });
        }

        [Fact]
        public async Task redirect_stderr_from_local_native_commands()
        {
            var power = new PNetPowerShell();

            Command cmd;
            var commands = new List<Command>(2);

            cmd = new Command("& more -? 2>&1", true);
            commands.Add(cmd);

            var results = await power.InvokeAsync(commands)
                    .ToListAsync();

            Assert.NotEmpty(results);
            Assert.Equal("Cannot access file -?", results.Last());
        }
    }
}
