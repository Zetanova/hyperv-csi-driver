using HypervCsiDriver.Hosting;
using HypervCsiDriver.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PNet.Automation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;

namespace HypervCsiDriver.Infrastructure
{
    public interface IHypervNodeService
    {
        Task MountDeviceAsync(HypervNodeMountRequest request, CancellationToken cancellationToken = default);

        Task UnmountDeviceAsync(HypervNodeUnmountRequest request, CancellationToken cancellationToken = default);

        Task PublishDeviceAsync(HypervNodePublishRequest request, CancellationToken cancellationToken = default);

        Task UnpublishDeviceAsync(HypervNodeUnpublishRequest request, CancellationToken cancellationToken = default);

    }

    public sealed class HypervNodeMountRequest
    {
        public string Name { get; set; }

        public Guid VhdId { get; set; }

        public int ControllerNumber { get; set; }

        public int ControllerLocation { get; set; }

        public string FSType { get; set; }

        public bool Readonly { get; set; }

        public string[] Options { get; set; }

        public bool Raw { get; set; }

        public string TargetPath { get; set; }

        public bool ValidateLabel { get; set; } = true;
    }

    public sealed class HypervNodeUnmountRequest
    {
        public string Name { get; set; }

        public string TargetPath { get; set; }
    }

    public sealed class HypervNodePublishRequest
    {
        public string Name { get; set; }

        public string StagingTargetPath { get; set; }

        public string PublishTargetPath { get; set; }
    }

    public sealed class HypervNodeUnpublishRequest
    {
        public string Name { get; set; }

        //public string StagePath { get; set; }

        public string TargetPath { get; set; }
    }

    public sealed class LinuxNodeService : IHypervNodeService, IDisposable
    {
        readonly HypervCsiDriverOptions _options;

        readonly PNetPowerShell _power;

        readonly ILogger _logger;

        public LinuxNodeService(IOptions<HypervCsiDriverOptions> options, ILogger<LinuxNodeService>? logger)
        {
            _logger = logger ?? (ILogger)NullLogger.Instance;

            _options = options.Value;

            if (string.IsNullOrEmpty(_options.HostName)) //local
                _power = new PNetPowerShell();
            else //remote for debug
                _power = new PNetPowerShell(_options.HostName, _options.UserName, _options.KeyFile);
        }

        public LinuxNodeService(string hostName, string userName, string? keyFile = null, ILogger<LinuxNodeService>? logger = null)
        {
            _logger = logger ?? (ILogger)NullLogger.Instance;

            _power = new PNetPowerShell(hostName, userName, keyFile); //remote
        }

        public async Task MountDeviceAsync(HypervNodeMountRequest request, CancellationToken cancellationToken = default)
        {
            using var scope = _logger.BeginScope("mounting {VolumeName}", request.Name);
            
            Command cmd;
            var commands = new List<Command>(4);

            var blockDeviceName = string.Empty;

            if (request.VhdId != Guid.Empty)
            {
                var diskFilter = HypervUtils.GetDiskFilter(request.VhdId);

                cmd = new Command("Get-ChildItem");
                cmd.Parameters.Add("Path", diskFilter);
                commands.Add(cmd);

                cmd = new Command("Select-Object");
                cmd.Parameters.Add("Property", new[] { "Directory", "Target" });
                commands.Add(cmd);

                //hack until Join-Path in pipe
                dynamic obj = await _power.InvokeAsync(commands).ThrowOnError()
                       .FirstOrDefaultAsync();

                if (obj is not null)
                {
                    commands.Clear();

                    cmd = new Command("Join-Path");
                    cmd.Parameters.Add("Path", obj.Directory);
                    cmd.Parameters.Add("ChildPath", obj.Target);
                    cmd.Parameters.Add("Resolve");
                    commands.Add(cmd);

                    blockDeviceName = await _power.InvokeAsync(commands).ThrowOnError()
                        .Select(n => (string)n.BaseObject)
                       .FirstOrDefaultAsync();
                }
            }

            if (string.IsNullOrEmpty(blockDeviceName))
            {
                //over controller number

                commands.Clear();

                cmd = new Command("lsblk -S -o 'HCTL,NAME' -nr", true);
                commands.Add(cmd);

                var hctl = $"{request.ControllerNumber}:0:0:{request.ControllerLocation}";
                var deviceInfo = await _power.InvokeAsync(commands).ThrowOnError()
                            .Select((dynamic n) => (string[])n.Split(" ", 2, StringSplitOptions.RemoveEmptyEntries))
                            .Where(n => n.Length == 2 && n[0] == hctl)
                            .Select(n => new
                            {
                                hctl = n[0],
                                name = n[1]
                            })
                            .FirstOrDefaultAsync(cancellationToken);

                if (deviceInfo is null)
                    throw new Exception($"device[{hctl}] not found");

                blockDeviceName = $"/dev/{deviceInfo.name}";
            }

            if (string.IsNullOrEmpty(blockDeviceName))
                throw new Exception($"block device not found");

            //todo check name of blockDeviceName 

            _logger.LogDebug("BlockDevice '{BlockDeviceName}' found", blockDeviceName);

            if (request.Raw)
                throw new NotImplementedException("raw block device not implemented");

            commands.Clear();

            var deviceName = $"{blockDeviceName}1";
            var deviceLabel = string.Empty;
            var deviceUUID = string.Empty;
            var deviceFSType = string.Empty;

            cmd = new Command($"blkid -o export -c /dev/null {deviceName}", true);
            commands.Add(cmd);

            //BLKID_EXIT_NOTFOUND=2

            /*
                DEVNAME=/dev/sdb1
                LABEL=pvc-5a344250-ca2
                UUID=978388ae-6a2e-479a-8658-48ea495adda3
                TYPE=ext4
                PARTLABEL=primary
                PARTUUID=90580121-4dd5-485a-9d07-e20b73cba4bf
            */

            await foreach (var line in _power.InvokeAsync(commands).ThrowOnError()
                .Select(n => n.BaseObject).OfType<string>()
                .TakeWhile(n => !string.IsNullOrEmpty(n))
                .WithCancellation(cancellationToken))
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
                        deviceLabel = value;
                        break;
                    case "UUID":
                        deviceUUID = value;
                        break;
                    case "TYPE":
                        deviceFSType = value;
                        break;
                }
            }

            //_logger.LogDebug("{@Device}", deviceLabel);

            //invalid device mount protection
            if (request.ValidateLabel && !string.IsNullOrEmpty(deviceLabel) && !request.Name.StartsWith(deviceLabel))
                throw new Exception("device label ambiguous");

            //todo select multiple partitions

            var fsType = !string.IsNullOrEmpty(request.FSType) ? request.FSType : "ext4";
            var setPermissions = false;

            if (string.IsNullOrEmpty(deviceUUID))
            {
                _logger.LogInformation("creating partition on {BlockDeviceName} with {FSType}", blockDeviceName, deviceFSType);

                commands.Clear();

                //parted /dev/sdb --script mklabel gpt
                //parted /dev/sdb --script mkpart primary ext4 0% 100%
                var script = $"parted --align=opt {blockDeviceName} --script mklabel gpt mkpart primary {fsType} 1MiB 100%";

                cmd = new Command(script, true);
                commands.Add(cmd);

                var result = await _power.InvokeAsync(commands).ThrowOnError().ToListAsync(cancellationToken);
            }

            if (string.IsNullOrEmpty(deviceFSType))
            {
                _logger.LogInformation("creating filesystem on {deviceName} with {FSType}", deviceName, deviceFSType);

                commands.Clear();

                deviceLabel = request.Name ?? string.Empty;

                //labels are max 16-chars long (maybe for xfs max 12) 
                if (deviceLabel.Length > 16)
                    deviceLabel = deviceLabel.Substring(0, 16);

                //mkfs -t ext4 -G 4096 -L volume-test /dev/sdb1
                //maybe add -G 4096
                cmd = new Command($"& mkfs -t {fsType} -L \"{deviceLabel}\" {deviceName} 2>&1", true);
                commands.Add(cmd);

                //cmd = new Command("Out-String");
                ////cmd.Parameters.Add("NoNewline");
                //commands.Add(cmd);

                var results = await _power.InvokeAsync(commands)
                    .ThrowOnError(error => !error.Exception.Message.StartsWith("mke2fs"))
                    .ToListAsync(cancellationToken);

                deviceFSType = fsType;
                setPermissions = true;
            }

            if (deviceFSType != fsType)
                throw new Exception("fsType ambiguous");

            _logger.LogDebug("try to find mount {DeviceName} to {TargetPath}", deviceName, request.TargetPath);

            commands.Clear();

            cmd = new Command($"findmnt -fnr {deviceName} {request.TargetPath}", true);
            commands.Add(cmd);

            ///tmp/testdrive /dev/sdb1 ext4 rw,noatime,seclabel,discard
            var mountpoint = await _power.InvokeAsync(commands).ThrowOnError() 
                .Select(n => n.BaseObject).OfType<string>()
                .FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrEmpty(mountpoint))
            {
                _logger.LogDebug("create {TargetPath}", request.TargetPath);
                
                commands.Clear();

                cmd = new Command("New-Item");
                cmd.Parameters.Add("ItemType", "directory");
                cmd.Parameters.Add("Path", request.TargetPath);
                cmd.Parameters.Add("ErrorAction", "SilentlyContinue");
                commands.Add(cmd);

                _ = await _power.InvokeAsync(commands).ThrowOnError()
                    .FirstOrDefaultAsync(cancellationToken);

                _logger.LogInformation("mounting {DeviceName} to {TargetPath}", deviceName, request.TargetPath);

                commands.Clear();

                var options = new HashSet<string> { "discard", "noatime" };

                foreach (var opt in (request.Options ?? Array.Empty<string>()))
                    options.Add(opt);

                if (request.Readonly)
                    options.Add("ro");

                //mount -o "discard,noatime,ro" /dev/sdb1 /drivetest
                cmd = new Command($"mkdir -p {request.TargetPath} && mount -o \"{string.Join(",", options)}\" {deviceName} {request.TargetPath}", true);
                commands.Add(cmd);

                //Labels are normaly only 16-chars long
                //Warning: label too long; will be truncated to 'pvc-5a344250-ca2'

                _ = await _power.InvokeAsync(commands).ThrowOnError()
                    .ToListAsync(cancellationToken);

                mountpoint = request.TargetPath;
            }

            if(!setPermissions)
            {
                _logger.LogDebug("empty check of {Mountpoint}", mountpoint);

                commands.Clear();

                cmd = new Command("Get-ChildItem");
                cmd.Parameters.Add("Path", request.TargetPath);
                commands.Add(cmd);

                try
                {
                    var hasFiles = await _power.InvokeAsync(commands).ThrowOnError()
                        .AnyAsync((dynamic n) => n.Name switch
                        {
                            "lost+found" => false,
                            _ => true
                        }, cancellationToken);

                    setPermissions = !hasFiles;
                } 
                catch(Exception ex)
                {
                    _logger.LogWarning("empty check error", ex);
                }
            }

            if (setPermissions)
            {
                _logger.LogInformation("set permission on {Mountpoint}", mountpoint);

                commands.Clear();

                //chmod -R 770 /tmp/mountpoint
                cmd = new Command($"chmod -R 770 {mountpoint}", true);
                commands.Add(cmd);

                _ = await _power.InvokeAsync(commands).ThrowOnError().ToListAsync(cancellationToken);
            }
        }

        public async Task UnmountDeviceAsync(HypervNodeUnmountRequest request, CancellationToken cancellationToken = default)
        {
            using var scope = _logger.BeginScope("unmounting {VolumeName}", request.Name);

            Command cmd;
            var commands = new List<Command>(1);

            _logger.LogDebug("unmount {TargetPath}", request.TargetPath);

            //umount /drivetest
            cmd = new Command($"& umount {request.TargetPath} 2>&1", true);
            commands.Add(cmd);

            _ = await _power.InvokeAsync(commands)
                .FirstOrDefaultAsync(cancellationToken);


            _logger.LogDebug("delete {TargetPath}", request.TargetPath);

            commands.Clear();

            cmd = new Command("Remove-Item");
            cmd.Parameters.Add("Path", request.TargetPath);
            cmd.Parameters.Add("ErrorAction", "SilentlyContinue");
            commands.Add(cmd);

            _ = await _power.InvokeAsync(commands).ThrowOnError()
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task PublishDeviceAsync(HypervNodePublishRequest request, CancellationToken cancellationToken = default)
        {
            using var scope = _logger.BeginScope("publishing {VolumeName}", request.Name);

            Command cmd;
            var commands = new List<Command>(1);

            _logger.LogDebug("create {TargetPath}", request.PublishTargetPath);

            //create target dir
            cmd = new Command("New-Item");
            cmd.Parameters.Add("ItemType", "directory");
            cmd.Parameters.Add("Path", request.PublishTargetPath);
            cmd.Parameters.Add("ErrorAction", "SilentlyContinue");
            commands.Add(cmd);

            _ = await _power.InvokeAsync(commands).ThrowOnError()
               .FirstOrDefaultAsync(cancellationToken);
             

            _logger.LogDebug("mount {SourcePath} to {TargetPath}", request.StagingTargetPath, request.PublishTargetPath);

            commands.Clear();

            //mount --bind /source /target
            cmd = new Command($"mkdir -p {request.PublishTargetPath} && mount --bind {request.StagingTargetPath} {request.PublishTargetPath}", true);
            commands.Add(cmd);

            //maybe readonly required
            //mount -o remount,ro,bind /target

            _ = await _power.InvokeAsync(commands).ThrowOnError()
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task UnpublishDeviceAsync(HypervNodeUnpublishRequest request, CancellationToken cancellationToken = default)
        {
            using var scope = _logger.BeginScope("unpublishing {VolumeName}", request.Name);

            Command cmd;
            var commands = new List<Command>(1);

            _logger.LogDebug("unmount {TargetPath}", request.TargetPath);

            //umount /drivetest
            cmd = new Command($"& umount {request.TargetPath} 2>&1", true);
            commands.Add(cmd);

            _ = await _power.InvokeAsync(commands)
                .FirstOrDefaultAsync(cancellationToken);
              

            _logger.LogDebug("delete {TargetPath}", request.TargetPath);

            commands.Clear();

            //delete target dir
            cmd = new Command("Remove-Item");
            cmd.Parameters.Add("Path", request.TargetPath);
            cmd.Parameters.Add("ErrorAction", "SilentlyContinue");
            commands.Add(cmd);

            _ = await _power.InvokeAsync(commands).ThrowOnError()
                .FirstOrDefaultAsync(cancellationToken);
        }

        public void Dispose()
        {
            _power.Dispose();
        }
    }
}
