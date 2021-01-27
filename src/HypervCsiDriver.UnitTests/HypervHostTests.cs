using HypervCsiDriver.Infrastructure;
using Microsoft.Extensions.Configuration;
using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Xunit;

namespace HypervCsiDriver.UnitTests
{
    public sealed class HypervNodeFixture : IDisposable
    {
        public IConfiguration Configuration { get; set; }

        IHypervHost _host;

        public HypervNodeFixture()
        {
            var builder = new ConfigurationBuilder()
                .AddUserSecrets<HypervNodeFixture>()
                .AddEnvironmentVariables();

            Configuration = builder.Build();
        }

        public async Task<IHypervHost> GetHypervHostAsync(string hostName)
        {
            if (_host is null)
            {
                //todo read config  Token=Configuration["somesection:somekey"]

                _host = new HypervHost(hostName, "Administrator", null)
                {
                };
                //await services.ConnectAsync();
            }
            return _host;
        }

        public void Dispose()
        {
            (_host as IDisposable)?.Dispose();
        }
    }


    [Trait("Type", "Integration")]
    [Trait("Category", "HypervHost")]
    public sealed class HypervHostTests : IClassFixture<HypervNodeFixture>
    {
        public HypervNodeFixture Fixture { get; }

        public HypervHostTests(HypervNodeFixture fixture)
        {
            Fixture = fixture;
        }

        [Theory]
        //[InlineData("sv1501", "hv05", "test-01")]
        //[InlineData("sv1503", "", "test-01")]
        //[InlineData("sv1505", "hv05", "")]
        [InlineData("sv1501", "hv05", "pvc-0219c36f-b7d9-45e3-a18a-6275a22ebd0e")]
        public async Task query_volumes_filtered(string hostName, string storageName, string volumeName)
        {
            var host = await Fixture.GetHypervHostAsync(hostName);

            var filter = new HypervVolumeFilter
            {
                Name = volumeName,
                Storage = storageName,
            };

            var volumes = await host.GetVolumesAsync(filter).ToListAsync();

            Assert.NotEmpty(volumes);

            if (!string.IsNullOrEmpty(volumeName))
                Assert.All(volumes, n => StringComparer.OrdinalIgnoreCase.Equals(n.Name, volumeName));

            if (!string.IsNullOrEmpty(storageName))
                Assert.All(volumes, n => StringComparer.OrdinalIgnoreCase.Equals(n.Storage, storageName));
        }

        [Theory]
        [InlineData("sv1506", "hv05", "test-01", @"C:\ClusterStorage\hv05\Volumes\test-01.vhdx")]
        public async Task query_volume_detail(string hostName, string storageName, string volumeName, string volumePath)
        {
            var host = await Fixture.GetHypervHostAsync(hostName);

            var volume = await host.GetVolumeAsync(volumePath);

            Assert.Equal(volumePath, volume.Path, true);
            Assert.Equal(volumeName, volume.Name);
            Assert.Equal(storageName, volume.Storage, true);

            Assert.Equal(10UL * 1024UL * 1024UL * 1024UL, volume.SizeBytes); //100GB
            Assert.True(volume.FileSizeBytes <= volume.SizeBytes);
            Assert.True(volume.FileSizeBytes > 0);
            Assert.NotEqual(Guid.Empty, volume.Id);
        }

        [Theory]
        [InlineData("sv1506", "hv05", "test_create-01")]
        public async Task create_delete_volume(string hostName, string storageName, string volumeName)
        {
            var host = await Fixture.GetHypervHostAsync(hostName);

            var filter = new HypervVolumeFilter
            {
                Name = volumeName,
                Storage = storageName,
            };

            {
                var info = await host.GetVolumesAsync(filter).FirstOrDefaultAsync();

                if (info != default)
                {
                    Assert.Equal(volumeName, info.Name);
                    Assert.Equal(storageName, info.Storage);

                    var detail = await host.GetVolumeAsync(info.Path);

                    Assert.Equal(volumeName, detail.Name);
                    Assert.Equal(storageName, detail.Storage);

                    await host.DeleteVolumeAsync(new HypervDeleteVolumeRequest
                    {
                        Id = detail.Id,
                        Path = detail.Path
                    });
                }
            }

            var volume = await host.CreateVolumeAsync(new HypervCreateVolumeRequest
            {
                Name = volumeName,
                Storage = storageName
            });

            Assert.Equal(volumeName, volume.Name);
            Assert.Equal(storageName, volume.Storage);

            Assert.Equal(10UL * 1024UL * 1024UL, volume.SizeBytes); //10GB
            Assert.True(volume.FileSizeBytes <= volume.SizeBytes);
            Assert.True(volume.FileSizeBytes > 0);
            Assert.NotEqual(Guid.Empty, volume.Id);

            await host.DeleteVolumeAsync(new HypervDeleteVolumeRequest
            {
                Id = volume.Id,
                Path = volume.Path
            });


            var notFound = await host.GetVolumesAsync(filter).FirstOrDefaultAsync();

            Assert.Null(notFound);
        }

        [Theory]
        [InlineData("sv1506", "lnx1521")]
        public async Task query_virtualmachine_filtered(string hostName, string virtualMachineName)
        {
            var host = await Fixture.GetHypervHostAsync(hostName);

            var filter = new HypervVirtualMachineFilter
            {
                Name = virtualMachineName,
            };

            var volumes = await host.GetVirtualMachinesAsync(filter).ToListAsync();

            var info = Assert.Single(volumes);

            Assert.Equal(virtualMachineName, info.Name, true);
            Assert.Equal(hostName, info.Host, true);
        }

        [Theory]
        [InlineData("sv1501", "lnx1521", "test-01")]
        public async Task attach_detach_volume(string hostName, string vmName, string volumeName)
        {
            var host = await Fixture.GetHypervHostAsync(hostName);

            var vm = await host.GetVirtualMachinesAsync(new HypervVirtualMachineFilter { Name = vmName }).FirstAsync();

            Assert.Equal(vmName, vm.Name, true);

            var volume = await host.GetVolumesAsync(new HypervVolumeFilter { Name = volumeName }).FirstAsync();

            Assert.Equal(volumeName, volume.Name, true);

            var vmVolume = await host.GetVirtualMachineVolumesAsync(vm.Id, new HypervVirtualMachineVolumeFilter { VolumePath = volume.Path }).FirstOrDefaultAsync();

            if (vmVolume == null)
            {
                vmVolume = await host.AttachVolumeAsync(new HypervAttachVolumeRequest
                {
                    VMId = vm.Id,
                    VolumePath = volume.Path
                });
            }

            Assert.Equal(vm.Id, vmVolume.VMId);
            Assert.Equal(vm.Name, vmVolume.VMName, true);
            Assert.Equal(volume.Name, vmVolume.VolumeName, true);
            Assert.Equal(volume.Path, vmVolume.VolumePath, true);

            await host.DetachVolumeAsync(new HypervDetachVolumeRequest
            {
                VMId = vm.Id,
                VolumePath = volume.Path
            });
        }

        [Theory]
        //[InlineData("sv1501", "lnx1521", @"C:\ClusterStorage\hv05\disks\lnx1521.vhdx")]
        [InlineData("sv1501", "lnx1512", @"C:\ClusterStorage\hv05\Volumes\pvc-0219c36f-b7d9-45e3-a18a-6275a22ebd0e.vhdx")]
        public async Task query_volume_flow_single(string hostName, string vmName, string volumePath)
        {
            var host = await Fixture.GetHypervHostAsync(hostName);

            var filter = new HypervVolumeFlowFilter
            {
                VolumePath = volumePath
            };

            var flow = await host.GetVolumeFlowsAsnyc(filter).FirstOrDefaultAsync();

            //var flow = Assert.Single(flows);

            Assert.Equal(volumePath, flow.Path, true);
            Assert.Equal(vmName, flow.VMName, true);
        }

        [Theory]
        [InlineData("sv1506", "4698482a-b361-49f6-a9b1-7334e184a9a1", "lnx1521")]
        public async Task query_volume_flow_by_id(string hostName, Guid vmId, string vmName)
        {
            var host = await Fixture.GetHypervHostAsync(hostName);

            var filter = new HypervVolumeFlowFilter
            {
                VMId = vmId
            };

            await foreach (var flow in host.GetVolumeFlowsAsnyc(filter))
            {
                Assert.Equal(vmId, flow.VMId);
                Assert.Equal(vmName, flow.VMName, true);
            }
        }
    }
}
