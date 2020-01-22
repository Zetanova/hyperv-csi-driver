using PNet.Automation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HypervCsiDriver.Infrastructure
{
    public interface IHypervHost
    {
        Task<HypervVolumeDetail> CreateVolumeAsync(HypervCreateVolumeRequest request, CancellationToken cancellationToken = default);

        Task DeleteVolumeAsync(HypervDeleteVolumeRequest request, CancellationToken cancellationToken = default);

        IAsyncEnumerable<HypervVolumeInfo> GetVolumesAsync(HypervVolumeFilter filter);

        Task<HypervVolumeDetail> GetVolumeAsync(string path, CancellationToken cancellationToken = default);

        IAsyncEnumerable<HypervVirtualMachineInfo> GetVirtualMachinesAsync(HypervVirtualMachineFilter filter);

        Task<HypervVirtualMachineVolumeInfo> AttachVolumeAsync(HypervAttachVolumeRequest request, CancellationToken cancellationToken = default);

        Task DetachVolumeAsync(HypervDetachVolumeRequest request, CancellationToken cancellationToken = default);

        IAsyncEnumerable<HypervVirtualMachineVolumeInfo> GetVirtualMachineVolumesAsync(Guid vmId, HypervVirtualMachineVolumeFilter filter);

        IAsyncEnumerable<HypervVolumeFlowInfo> GetVolumeFlowsAsnyc(HypervVolumeFlowFilter filter);
    }

    public sealed class HypervCreateVolumeRequest
    {
        public string Name { get; set; } //required

        public string Storage { get; set; } = string.Empty;

        public bool Shared { get; set; } = false;

        public ulong SizeBytes { get; set; } = 10 * 1024 * 1024; //10GB

        public uint BlockSizeBytes { get; set; } = 1024 * 1024; //1M

        public bool Dynamic { get; set; } = true;

        //todo public Guid Parent { get; set; } = Guid.Empty;
    }

    public sealed class HypervDeleteVolumeRequest
    {
        public Guid Id { get; set; }
        public string Path { get; set; }

        //maybe public bool/DateTimeOffset Retain { get; set; }
    }

    public sealed class HypervAttachVolumeRequest
    {
        public Guid VMId { get; set; }
        public string VolumePath { get; set; }
        public string Host { get; set; }
    }

    public sealed class HypervDetachVolumeRequest
    {
        public Guid VMId { get; set; }
        public string VolumePath { get; set; }
        public string Host { get; set; }
    }

    public sealed class HypervVolumeFilter
    {
        //public Guid Id { get; set; } 

        public string Name { get; set; }

        public string Storage { get; set; }

        //todo public Guid Parent { get; set; }

        //maybe public string Path { get; set; }
    }

    public sealed class HypervVolumeInfo
    {
        public string Name { get; set; }

        public string Storage { get; set; }

        public string Path { get; set; }

        public long FileSizeBytes { get; set; }

        public bool Shared { get; set; }
    }

    public sealed class HypervVolumeDetail
    {
        public Guid Id { get; set; } //DiskIdentifier

        public string Name { get; set; }

        public string Storage { get; set; }

        public string Path { get; set; }

        public bool Shared { get; set; }

        public ulong FileSizeBytes { get; set; }

        public ulong SizeBytes { get; set; }

        public uint BlockSizeBytes { get; set; }

        public bool Dynamic { get; set; }

        public bool Attached { get; set; }

        //todo public Guid Parent { get; }

        //maybe FragmentationPercentage

    }

    public sealed class HypervVirtualMachineFilter
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        //maybe public string Volume { get; set; }
    }

    public sealed class HypervVirtualMachineInfo
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public string Host { get; set; }
    }

    public sealed class HypervVirtualMachineVolumeInfo
    {
        public Guid VMId { get; set; }

        public string VMName { get; set; }

        public string VolumeName { get; set; }

        public string VolumePath { get; set; }

        public string Host { get; set; }

        public int ControllerNumber { get; set; }

        public int ControllerLocation { get; set; }
    }

    public sealed class HypervVirtualMachineVolumeFilter
    {
        public string VolumePath { get; set; }
    
        public string Host { get; set; }
    }

    public sealed class HypervVolumeFlowInfo
    {
        public Guid VMId { get; set; }

        public string VMName { get; set; }

        public string Host { get; set; }

        public string Path { get; set; }

        //todo Iops values 
    }

    public sealed class HypervVolumeFlowFilter
    {
        public Guid VMId { get; set; }

        public string VMName { get; set; }

        public string VolumePath { get; set; }
    }

    public sealed class HypervHost : IHypervHost
    {
        readonly PNetPowerShell _power;

        public HypervHost(string hostName, string userName, string keyFile = null)
        {
            _power = new PNetPowerShell(hostName, userName, keyFile);
        }

        public async Task<HypervVolumeDetail> CreateVolumeAsync(HypervCreateVolumeRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(request.Name))
                throw new ArgumentNullException(nameof(request.Name));

            //todo VHDSet switch
            if (request.Shared)
                throw new NotImplementedException("shared disk not implemented");

            var storage = !string.IsNullOrEmpty(request.Storage) 
                    ? request.Storage 
                    : await FindFreeStoragesAsync(request.SizeBytes).FirstAsync();

            var path = $@"{HypervDefaults.ClusterStoragePath}\{storage}\Volumes\{request.Name}.vhdx";

            Command cmd;
            var commands = new List<Command>(2);

            cmd = new Command("New-VHD");
            cmd.Parameters.Add("Path", path);
            cmd.Parameters.Add("SizeBytes", request.SizeBytes);
            cmd.Parameters.Add("Dynamic", request.Dynamic);
            cmd.Parameters.Add("BlockSizeBytes", request.BlockSizeBytes);
            cmd.Parameters.Add("LogicalSectorSizeBytes", 4096);
            cmd.Parameters.Add("PhysicalSectorSizeBytes", 4096);
            commands.Add(cmd);

            cmd = new Command("Select-Object");
            cmd.Parameters.Add("Property", new[] { "DiskIdentifier", "Path", "FileSize", "Size", "BlockSize", "VhdType", "Attached" });
            //todo ParentPath, FragmentationPercentage, VHDFormat
            commands.Add(cmd);

            dynamic item = await _power.InvokeAsync(commands).FirstAsync(cancellationToken);

            return new HypervVolumeDetail
            {
                Id = Guid.Parse((string)item.DiskIdentifier),
                Name = Path.GetFileNameWithoutExtension((string)item.Path),
                Path = item.Path,
                FileSizeBytes = item.FileSize,
                SizeBytes = item.Size,
                Attached = item.Attached,
                BlockSizeBytes = item.BlockSize,
                Dynamic = item.VhdType switch
                {
                    "Dynamic" => true,
                    _ => false
                },
                Storage = Directory.GetParent((string)item.Path).Parent.Name,
                Shared = false //todo .vhds                    
            };
        }

        public async Task DeleteVolumeAsync(HypervDeleteVolumeRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Id == Guid.Empty)
                throw new ArgumentNullException(nameof(request.Id));
            if (string.IsNullOrEmpty(request.Path))
                throw new ArgumentNullException(nameof(request.Path));

            //maybe check path in storage 

            Command cmd;
            var commands = new List<Command>(3);

            //todo VHDSet switch
            //todo Snapshots check

            cmd = new Command("Get-VHD");
            cmd.Parameters.Add("Path", request.Path);
            commands.Add(cmd);

            cmd = new Command("Select-Object");
            cmd.Parameters.Add("Property", new[] { "DiskIdentifier", "Path", "FileSize", "Size", "BlockSize", "VhdType", "Attached" });
            //todo ParentPath, FragmentationPercentage, VHDFormat
            commands.Add(cmd);

            cmd = new Command("Remove-Item");
            commands.Add(cmd);

            var result = await _power.InvokeAsync(commands).FirstOrDefaultAsync(cancellationToken);

            //todo check result
        }

        public async Task<HypervVolumeDetail> GetVolumeAsync(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            //maybe check path in storage 

            Command cmd;
            var commands = new List<Command>(2);

            //todo VHDSet switch

            cmd = new Command("Get-VHD");
            cmd.Parameters.Add("Path", path);
            commands.Add(cmd);

            cmd = new Command("Select-Object");
            cmd.Parameters.Add("Property", new[] { "DiskIdentifier", "Path", "FileSize", "Size", "BlockSize", "VhdType", "Attached" });
            //todo ParentPath, FragmentationPercentage, VHDFormat
            commands.Add(cmd);

            dynamic item = await _power.InvokeAsync(commands).FirstAsync(cancellationToken);

            return new HypervVolumeDetail
            {
                Id = Guid.Parse((string)item.DiskIdentifier),
                Name = Path.GetFileNameWithoutExtension((string)item.Path),
                Path = item.Path,
                FileSizeBytes = item.FileSize,
                SizeBytes = item.Size,
                Attached = item.Attached,
                BlockSizeBytes = item.BlockSize,
                Dynamic = item.VhdType switch
                {
                    "Dynamic" => true,
                    _ => false
                },
                Storage = Directory.GetParent((string)item.Path).Parent.Name,
                Shared = false //todo .vhds                    
            };
        }

        public IAsyncEnumerable<HypervVolumeInfo> GetVolumesAsync(HypervVolumeFilter filter = null)
        {
            Command cmd;
            var commands = new List<Command>(5);

            cmd = new Command("Get-ChildItem");
            cmd.Parameters.Add("Path", @"C:\ClusterStorage");
            if (!string.IsNullOrEmpty(filter?.Storage))
                cmd.Parameters.Add("Filter", $"{filter.Storage}");
            commands.Add(cmd);

            cmd = new Command("Get-ChildItem");
            cmd.Parameters.Add("Filter", "Volumes");
            commands.Add(cmd);

            cmd = new Command("Get-ChildItem");
            if (!string.IsNullOrEmpty(filter?.Name))
                cmd.Parameters.Add("Filter", $"{filter.Name}.*");
            commands.Add(cmd);

            cmd = new Command("Where-Object"); //maybe over script to include .vhds
            cmd.Parameters.Add("Property", "Extension");
            cmd.Parameters.Add("eq");
            cmd.Parameters.Add("Value", ".vhdx");
            commands.Add(cmd);

            cmd = new Command("Select-Object");
            cmd.Parameters.Add("Property", new[] { "BaseName", "FullName", "Length" });
            commands.Add(cmd);

            return _power.InvokeAsync(commands)
                .Select((dynamic n) => new HypervVolumeInfo
                {
                    Name = n.BaseName,
                    Path = n.FullName,
                    FileSizeBytes = n.Length,
                    Storage = Directory.GetParent((string)n.FullName).Parent.Name,
                    Shared = false //todo .vhds                    
                });       
        }

        public IAsyncEnumerable<HypervVirtualMachineInfo> GetVirtualMachinesAsync(HypervVirtualMachineFilter filter)
        {
            Command cmd;
            var commands = new List<Command>(2);

            cmd = new Command("Get-VM");
            if (filter?.Id != Guid.Empty)
                cmd.Parameters.Add("Id", filter.Id);
            if (!string.IsNullOrEmpty(filter?.Name))
                cmd.Parameters.Add("Name", filter.Name);
            commands.Add(cmd);

            cmd = new Command("Select-Object");
            cmd.Parameters.Add("Property", new[] { "Id", "Name", "ComputerName" });
            commands.Add(cmd);

            return _power.InvokeAsync(commands)
                .Select((dynamic n) => new HypervVirtualMachineInfo
                {
                    Id = n.Id,
                    Name = n.Name,
                    Host = n.ComputerName
                });
        }

        public async Task<HypervVirtualMachineVolumeInfo> AttachVolumeAsync(HypervAttachVolumeRequest request, CancellationToken cancellationToken = default)
        {
            if (request.VMId == Guid.Empty)
                throw new ArgumentNullException(nameof(request.VMId));
            if (string.IsNullOrEmpty(request.VolumePath))
                throw new ArgumentNullException(nameof(request.VolumePath));

            //maybe check path in storage 

            Command cmd;
            var commands = new List<Command>(3);

            //todo VHDSet switch

            cmd = new Command("Get-VM");
            cmd.Parameters.Add("Id", request.VMId);
            commands.Add(cmd);

            cmd = new Command("Add-VMHardDiskDrive");
            cmd.Parameters.Add("Path", request.VolumePath);
            cmd.Parameters.Add("Passthru");
            commands.Add(cmd);

            cmd = new Command("Select-Object");
            cmd.Parameters.Add("Property", new[] { 
                "VMId", "VMName", "ComputerName", "Path", 
                "ControllerNumber", "ControllerLocation" 
                //todo VMSnapshotId, VMSnapshotName, MaximumIOPS, MinimumIOPS
            });            
            commands.Add(cmd);

            dynamic item = await _power.InvokeAsync(commands).FirstAsync(cancellationToken);

            return new HypervVirtualMachineVolumeInfo
            {
                VMId = item.VMId,
                VMName = item.VMName,
                VolumeName = Path.GetFileNameWithoutExtension((string)item.Path),
                VolumePath = item.Path,
                Host = item.ComputerName,
                ControllerNumber = item.ControllerNumber,
                ControllerLocation = item.ControllerLocation                    
            };
        }

        public async Task DetachVolumeAsync(HypervDetachVolumeRequest request, CancellationToken cancellationToken = default)
        {
            if (request.VMId == Guid.Empty)
                throw new ArgumentNullException(nameof(request.VMId));
            if (string.IsNullOrEmpty(request.VolumePath))
                throw new ArgumentNullException(nameof(request.VolumePath));

            //maybe check path in storage 

            Command cmd;
            var commands = new List<Command>(4);

            //todo VHDSet switch

            cmd = new Command("Get-VM");
            cmd.Parameters.Add("Id", request.VMId);
            commands.Add(cmd);

            cmd = new Command("Get-VMHardDiskDrive");
            commands.Add(cmd);

            cmd = new Command("Where-Object"); //maybe over script to include .vhds
            cmd.Parameters.Add("Property", "Path");
            cmd.Parameters.Add("eq");
            cmd.Parameters.Add("Value", request.VolumePath);
            commands.Add(cmd);

            cmd = new Command("Remove-VMHardDiskDrive");
            //cmd.Parameters.Add("Passthru");
            commands.Add(cmd);

            var result = await _power.InvokeAsync(commands).FirstOrDefaultAsync(cancellationToken);
        }

        public IAsyncEnumerable<HypervVirtualMachineVolumeInfo> GetVirtualMachineVolumesAsync(Guid vmId, HypervVirtualMachineVolumeFilter filter)
        {
            if (vmId == Guid.Empty)
                throw new ArgumentNullException(nameof(vmId));

            //maybe check path in storage 
            //todo VHDSet switch

            Command cmd;
            var commands = new List<Command>(4);

            cmd = new Command("Get-VM");
            cmd.Parameters.Add("Id", vmId);
            commands.Add(cmd);

            cmd = new Command("Get-VMHardDiskDrive");
            commands.Add(cmd);

            if(!string.IsNullOrEmpty(filter?.VolumePath))
            {
                cmd = new Command("Where-Object");
                cmd.Parameters.Add("Property", "Path");
                cmd.Parameters.Add("eq");
                cmd.Parameters.Add("Value", filter.VolumePath);
                commands.Add(cmd);
            }

            cmd = new Command("Select-Object");
            cmd.Parameters.Add("Property", new[] {
                "VMId", "VMName", "ComputerName", "Path",
                "ControllerNumber", "ControllerLocation" 
                //todo VMSnapshotId, VMSnapshotName, MaximumIOPS, MinimumIOPS
            });
            commands.Add(cmd);

            return _power.InvokeAsync(commands)
                .Select((dynamic n) => new HypervVirtualMachineVolumeInfo
                {
                    VMId = n.VMId,
                    VMName = n.VMName,
                    VolumeName = Path.GetFileNameWithoutExtension((string)n.Path),
                    VolumePath = n.Path,
                    Host = n.ComputerName,
                    ControllerNumber = n.ControllerNumber,
                    ControllerLocation = n.ControllerLocation
                });
        }

        public IAsyncEnumerable<HypervVolumeFlowInfo> GetVolumeFlowsAsnyc(HypervVolumeFlowFilter filter)
        {
            Command cmd;
            var commands = new List<Command>(2);

            cmd = new Command("Get-StorageQoSFlow");
            if(filter != null && filter.VMId != Guid.Empty)
                cmd.Parameters.Add("InitiatorId", filter.VMId);
            if (!string.IsNullOrEmpty(filter?.VMName))
                cmd.Parameters.Add("InitiatorName", filter.VMName);
            if (!string.IsNullOrEmpty(filter?.VolumePath))
                cmd.Parameters.Add("FilePath", filter.VolumePath);
            //maybe cmd.Parameters.Add("Status", "Ok");
            commands.Add(cmd);

            cmd = new Command("Select-Object");
            cmd.Parameters.Add("Property", new[] {
                "InitiatorId", "InitiatorName", "InitiatorNodeName", "FilePath"
                //todo IOPS, MaximumIOPS, MinimumIOPS
            });
            commands.Add(cmd);

            return _power.InvokeAsync(commands)
                .Select((dynamic n) => new HypervVolumeFlowInfo
                {
                    VMId = n.InitiatorId,
                    VMName = n.InitiatorName,
                    Host = n.InitiatorNodeName,
                    Path = n.FilePath
                });
        }

        async IAsyncEnumerable<string> FindFreeStoragesAsync(ulong requiredSize)
        {
            //todo cluster query
            /*
            Invoke-WinCommand -ScriptBlock { 
                Get-ClusterSharedVolume | Select-Object Name,OwnerNode -ExpandProperty SharedVolumeInfo | ForEach-Object {
                    $csv = $_
                    New-Object PSObject -Property @{
                        Name = $csv.Name
                        Owner = $csv.OwnerNode
                        Path = $csv.FriendlyVolumeName
                        Size = $csv.Partition.Size
                        FreeSpace = $csv.Partition.FreeSpace
                        UsedSpace = $csv.Partition.UsedSpace
                        PercentFree = $csv.Partition.PercentFree
                    }
                }
            }

            //todo filter by csv.State=Online, 
            //csv.SharedVolumeInfo { MaintenanceMode=False, FaultState=NoFaults }
            */

            yield return "hv05"; //todo free storage lookup
        }
    }
}
