using HypervCsiDriver.Hosting;
using HypervCsiDriver.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PNet.Automation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation.Runspaces;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Xunit;
using System.Threading;

namespace HypervCsiDriver.UnitTests
{
    public sealed class HypervVolumeServiceFixture : IDisposable
    {
        public IConfiguration Configuration { get; set; }

        IHypervVolumeService _service;

        public HypervVolumeServiceFixture()
        {
            var builder = new ConfigurationBuilder()
                .AddUserSecrets<HypervNodeFixture>()
                .AddEnvironmentVariables();

            Configuration = builder.Build();
        }

        public async Task<IHypervVolumeService> GetHypervVolumeSerivceAsync(string hostName)
        {
            if (_service is null)
            {
                //todo read config  Token=Configuration["somesection:somekey"]
                var options = Options.Create(new HypervCsiDriverOptions
                {
                     HostName = hostName
                });

                _service = new HypervVolumeService(options)
                {
                };
                //await services.ConnectAsync();
            }
            return _service;
        }

        public void Dispose()
        {
            (_service as IDisposable)?.Dispose();
        }
    }


    [Trait("Type", "Integration")]
    [Trait("Category", "HypervVolumeService")]
    public sealed class HypervVolumeServiceTests : IClassFixture<HypervVolumeServiceFixture>
    {
        public HypervVolumeServiceFixture Fixture { get; }

        public HypervVolumeServiceTests(HypervVolumeServiceFixture fixture)
        {
            Fixture = fixture;
        }

        [Theory]
        [InlineData("sv1505", "lnx1521")]
        public async Task query_virtualmachine_by_name(string hostName, string vmName)
        {
            var host = await Fixture.GetHypervVolumeSerivceAsync(hostName);

            var filter = new HypervVirtualMachineFilter
            {
                Name = vmName
            };

            var vms = await host.GetVirtualMachinesAsync(filter).ToListAsync();

            var vm = Assert.Single(vms);

            Assert.Equal(vmName, vm.Name, true);
        }

        [Theory]
        [InlineData("sv1505")]
        public async Task query_volume_details(string hostName, CancellationToken cancellationToken = default)
        {
            var host = await Fixture.GetHypervVolumeSerivceAsync(hostName);

            var volumeSource = host.GetVolumesAsync(null).Take(10);

            var foundVolumes = await volumeSource
                .ToListAsync(cancellationToken);

            Assert.NotEmpty(foundVolumes);

            var flows = await host.GetVolumeFlowsAsnyc(null)
                .ToListAsync(cancellationToken);

            //Assert.NotEmpty(flows);

            foreach (var foundVolume in foundVolumes)
            {
                var volumeFlows = flows.Where(n => StringComparer.OrdinalIgnoreCase.Equals(foundVolume.Path, n.Path)).ToList();

                var v = await host.GetVolumeAsync(foundVolume.Path, volumeFlows.FirstOrDefault()?.Host, cancellationToken);

                Assert.True(v.SizeBytes > 0);
                Assert.Equal(foundVolume.Path, v.Path, true);
            }
        }

        [Theory]
        //[InlineData("sv1505", "hv05", "test_create-01")]
        [InlineData("sv1505", "", "test_create-02")]
        public async Task create_delete_volume(string hostName, string storageName, string volumeName)
        {
            var service = await Fixture.GetHypervVolumeSerivceAsync(hostName);

            var filter = new HypervVolumeFilter
            {
                Name = volumeName,
                Storage = storageName,
            };

            {
                var info = await service.GetVolumesAsync(filter).FirstOrDefaultAsync();

                if (info != default)
                {
                    Assert.Equal(volumeName, info.Name);
                    Assert.NotEqual(string.Empty, info.Storage);
                    if (!string.IsNullOrEmpty(storageName))
                        Assert.Equal(storageName, info.Storage);
                        

                    var detail = await service.GetVolumeAsync(info.Path);

                    Assert.Equal(volumeName, detail.Name);

                    Assert.NotEqual(string.Empty, detail.Storage);
                    if (!string.IsNullOrEmpty(storageName))
                        Assert.Equal(storageName, detail.Storage);

                    await service.DeleteVolumeAsync(new HypervDeleteVolumeRequest
                    {
                        Id = detail.Id,
                        Path = detail.Path
                    });
                }
            }

            var volume = await service.CreateVolumeAsync(new HypervCreateVolumeRequest
            {
                Name = volumeName,
                Storage = storageName
            });

            Assert.Equal(volumeName, volume.Name);
            Assert.NotEqual(string.Empty, volume.Storage);
            if (!string.IsNullOrEmpty(storageName))
                Assert.Equal(storageName, volume.Storage);

            //Assert.Equal(10UL * 1024UL * 1024UL, volume.SizeBytes); //1GB
            Assert.True(volume.FileSizeBytes <= volume.SizeBytes);
            Assert.True(volume.FileSizeBytes > 0);
            Assert.NotEqual(Guid.Empty, volume.Id);

            await service.DeleteVolumeAsync(new HypervDeleteVolumeRequest
            {
                Id = volume.Id,
                Path = volume.Path
            });


            var notFound = await service.GetVolumesAsync(filter).FirstOrDefaultAsync();

            Assert.Null(notFound);
        }

        [Theory]
        //[InlineData("sv1505", "hv05", "influxdb-01", "lnx1519")]
        //[InlineData("sv1505", "hv05", "grafana-01", "lnx1519")]
        //[InlineData("sv1505", "hv05", "mssql-01", "lnx1519")]
        [InlineData("sv1505", "hv05", "test-volume-longname12345", "lnx1514")]
        public async Task create_and_attach_and_mount_volume(string hostName, string storageName, string volumeName, string vmName)
        {
            var service = await Fixture.GetHypervVolumeSerivceAsync(hostName);

            var filter = new HypervVolumeFilter
            {
                Name = volumeName,
                Storage = storageName,
            };

             
            var volume = await service.GetVolumesAsync(filter).FirstOrDefaultAsync();

            if (volume == default)
            {
                var detail = await service.CreateVolumeAsync(new HypervCreateVolumeRequest
                {
                    Name = volumeName,
                    Storage = storageName
                });

                Assert.Equal(volumeName, detail.Name);
                Assert.Equal(storageName, detail.Storage);

                volume = await service.GetVolumesAsync(filter).FirstAsync();
            }
            
            var vm = await service.GetVirtualMachinesAsync(new HypervVirtualMachineFilter { Name = vmName }).FirstAsync();

            Assert.Equal(vmName, vm.Name, true);
            Assert.Equal(volumeName, volume.Name, true);

            var vmVolume = await service.GetVirtualMachineVolumesAsync(vm.Id,
                new HypervVirtualMachineVolumeFilter
                {
                    VolumePath = volume.Path,
                    Host = vm.Host
                })
                .FirstOrDefaultAsync();

            vmVolume ??= await service.AttachVolumeAsync(new HypervAttachVolumeRequest
            {
                VMId = vm.Id,
                VolumePath = volume.Path,
                Host = vm.Host
            });

            Assert.Equal(vm.Id, vmVolume.VMId);
            Assert.Equal(vm.Name, vmVolume.VMName, true);
            Assert.Equal(volume.Name, vmVolume.VolumeName, true);
            Assert.Equal(volume.Path, vmVolume.VolumePath, true);

        }

        [Theory]
        [InlineData("sv1505", "lnx1521", "test-01", "hv05")]
        public async Task attach_detach_volume(string hostName, string vmName, string volumeName, string storageName)
        {
            var service = await Fixture.GetHypervVolumeSerivceAsync(hostName);

            var vm = await service.GetVirtualMachinesAsync(new HypervVirtualMachineFilter { Name = vmName })
                .FirstAsync();

            Assert.Equal(vmName, vm.Name, true);

            var foundVolume = await service.GetVolumesAsync(new HypervVolumeFilter 
            {
                Name = volumeName,
                Storage = storageName
            }).FirstAsync();

            Assert.Equal(volumeName, foundVolume.Name, true);

            HypervVirtualMachineVolumeInfo vmVolume;

            var flow = await service.GetVolumeFlowsAsnyc(new HypervVolumeFlowFilter
            {
                VolumePath = foundVolume.Path
            })
            .FirstOrDefaultAsync();

            if (flow != null)
            {
                Assert.Equal(vm.Id, flow.VMId);

                vmVolume = await service.GetVirtualMachineVolumesAsync(flow.VMId, new HypervVirtualMachineVolumeFilter
                {
                    VolumePath = flow.Path,
                    Host = flow.Host
                })
                .FirstAsync();
            }
            else
            {
                var volume = await service.GetVolumeAsync(foundVolume.Path, null);

                Assert.NotNull(vm);

                vmVolume = await service.AttachVolumeAsync(new HypervAttachVolumeRequest
                {
                    VMId = vm.Id,
                    VolumePath = volume.Path,
                    Host = vm.Host
                });
            }

            Assert.Equal(vm.Id, vmVolume.VMId);
            Assert.Equal(vm.Name, vmVolume.VMName, true);
            Assert.Equal(foundVolume.Name, vmVolume.VolumeName, true);
            Assert.Equal(foundVolume.Path, vmVolume.VolumePath, true);

            await service.DetachVolumeAsync(new HypervDetachVolumeRequest
            {
                Host = vm.Host,
                VMId = vm.Id,
                VolumePath = foundVolume.Path
            });
        }
    }
}
