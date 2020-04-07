using HypervCsiDriver.Hosting;
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

        public int ControllerNumber { get; set; }

        public int ControllerLocation { get; set; }

        public string FSType { get; set; }

        public bool Readonly { get; set; }

        public string[] Options { get; set; }

        public bool Raw { get; set; }

        public string TargetPath { get; set; }
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

    public sealed class LinuxNodeService : IHypervNodeService
    {
        readonly HypervCsiDriverOptions _options;

        readonly PNetPowerShell _power;

        public LinuxNodeService(IOptions<HypervCsiDriverOptions> options)
        {
            _options = options.Value;

            if(string.IsNullOrEmpty(_options.HostName)) //local
                _power = new PNetPowerShell(); 
            else //remote for debug
                _power = new PNetPowerShell(_options.HostName, _options.UserName, _options.KeyFile); 
        }

        public LinuxNodeService(string hostName, string userName, string keyFile = null)
        {
            _power = new PNetPowerShell(hostName, userName, keyFile); //remote
        }

        public async Task MountDeviceAsync(HypervNodeMountRequest request, CancellationToken cancellationToken = default)
        {
            Command cmd;
            var commands = new List<Command>(4);

            //centos 8
            //lsblk -SJ | ConvertFrom-Json | Select-Object -ExpandProperty blockdevices | where-object -Property hctl -eq -Value "0:0:0:1"
            //cmd = new Command("lsblk -SJ", true);
            //commands.Add(cmd);

            //cmd = new Command("ConvertFrom-Json");
            //commands.Add(cmd);

            //cmd = new Command("Select-Object");
            //cmd.Parameters.Add("ExpandProperty", "blockdevices");
            //commands.Add(cmd);

            //cmd = new Command("Where-Object");
            //cmd.Parameters.Add("Property", "hctl");
            //cmd.Parameters.Add("eq");
            //cmd.Parameters.Add("Value", $"{request.ControllerNumber}:0:0:{request.ControllerLocation}");
            //commands.Add(cmd);
            
            //dynamic deviceInfo = await _power.InvokeAsync(commands).FirstOrDefaultAsync(cancellationToken);
            ///*
            //name       : sdb1
            //fstype     : ext4
            //label      : volume-test-01
            //uuid       : 7c8ee2c4-7583-4c0b-ad7f-f0a829e0344f
            //mountpoint :
            // */

            //centos 7
            cmd = new Command("lsblk -S -o 'HCTL,NAME' -nr", true);
            commands.Add(cmd);
            
            var hctl = $"{request.ControllerNumber}:0:0:{request.ControllerLocation}";
            var deviceInfo = await _power.InvokeAsync(commands)
                        .Select((dynamic n) => (string[])n.Split(" ", 2, StringSplitOptions.RemoveEmptyEntries))
                        .Where(n => n.Length == 2 && n[0] == hctl)
                        .Select(n => new
                        {
                            hctl = n[0],
                            name = n[1]
                        })
                        .FirstOrDefaultAsync(cancellationToken);            

            if (deviceInfo is null)
                throw new System.Exception("device not found");

            string deviceName = deviceInfo.name;

            if (request.Raw)
                throw new NotImplementedException("raw block device not implemented");
            
            commands.Clear();

            //centos8
            //lsblk /dev/sdb -fJ | ConvertFrom-Json | Select -ExpandProperty blockdevices | Select-Object -ExpandProperty children
            //cmd = new Command($"lsblk /dev/{deviceName} -fJ", true);
            //commands.Add(cmd);

            //cmd = new Command("ConvertFrom-Json");
            //commands.Add(cmd);

            //cmd = new Command("Select-Object");
            //cmd.Parameters.Add("ExpandProperty", "blockdevices");
            //commands.Add(cmd);

            //cmd = new Command("Select-Object");
            //cmd.Parameters.Add("ExpandProperty", "children");
            //cmd.Parameters.Add("ErrorAction", "SilentlyContinue");
            //commands.Add(cmd);

            cmd = new Command($"lsblk /dev/{deviceName} -f -nro 'NAME,FSTYPE,LABEL,UUID,MOUNTPOINT'", true);
            commands.Add(cmd);

            var partInfo = await _power.InvokeAsync(commands)
                .Skip(1) //deviceName
                .Select((dynamic n) => (string[])n.Split(" ", 5))
                .Select(n => n.Concat(Enumerable.Repeat(string.Empty, 5-n.Length)).ToList())
                .Select(n => new
                {
                    Name = n[0],
                    FSType = n[1],
                    Label = n[2],
                    UUID = n[3],
                    Mountpoint = n[4],
                })
                .FirstOrDefaultAsync(cancellationToken);
            /*
            name       : sdb1
            fstype     : ext4
            label      : volume-test-01
            uuid       : 7c8ee2c4-7583-4c0b-ad7f-f0a829e0344f
            mountpoint :
            */

            //todo select multiple partitions

            var fsType = !string.IsNullOrEmpty(request.FSType) ? request.FSType : "ext4";

            if(partInfo is null)
            {
                commands.Clear();

                //parted /dev/sdb --script mklabel gpt
                //parted /dev/sdb --script mkpart primary ext4 0% 100%
                var script = $"parted /dev/{deviceName} --script mklabel gpt mkpart primary {fsType} 0% 100%";

                cmd = new Command(script, true);
                commands.Add(cmd);

                var result = await _power.InvokeAsync(commands).ToListAsync(cancellationToken);
            }

            if (string.IsNullOrEmpty(partInfo?.FSType))
            {
                commands.Clear();

                var devicePath = $"/dev/{deviceName}1";

                //mkfs -t ext4 -G 4096 -L volume-test /dev/sdb1
                var script = $"& mkfs -t {fsType} -L {request.Name} {devicePath} 2>&1";
                //maybe add -G 4096
                
                cmd = new Command(script, true);
                commands.Add(cmd);

                //cmd = new Command("Out-String");
                ////cmd.Parameters.Add("NoNewline");
                //commands.Add(cmd);

                var result = await _power.InvokeAsync(commands).ToListAsync(cancellationToken);
            }

            if(string.IsNullOrEmpty(partInfo?.Mountpoint))
            {
                if (!string.IsNullOrEmpty(partInfo?.FSType) && partInfo.FSType != fsType)
                    throw new Exception("fsType ambiguous");

                commands.Clear();

                var devicePath = $"/dev/{deviceName}1";

                var options = new HashSet<string> { "discard", "noatime" };

                foreach (var opt in (request.Options ?? Array.Empty<string>()))
                    options.Add(opt);

                if (request.Readonly)
                    options.Add("ro");

                //mount -o "discard,noatime,ro" /dev/sdb1 /drivetest
                var script = $"mount -o \"{string.Join(",", options)}\" {devicePath} {request.TargetPath}";

                cmd = new Command(script, true);
                commands.Add(cmd);

                var result = await _power.InvokeAsync(commands).ToListAsync(cancellationToken);
            }
            else
            {
                if (partInfo.FSType != fsType)
                    throw new Exception("fsType ambiguous");

                //fix bind mount will overwrite 
                //if (partInfo.Mountpoint != request.TargetPath)
                //    throw new Exception($"already mounted at {partInfo.Mountpoint}");

                //todo mount options check
                //todo mount readonly check
            }
        }

        public async Task UnmountDeviceAsync(HypervNodeUnmountRequest request, CancellationToken cancellationToken = default)
        {
            Command cmd;
            var commands = new List<Command>(1);

            //umount /drivetest
            cmd = new Command($"umount {request.TargetPath}", true);
            commands.Add(cmd);

            var result = await _power.InvokeAsync(commands).FirstOrDefaultAsync(cancellationToken);
        }

        public async Task PublishDeviceAsync(HypervNodePublishRequest request, CancellationToken cancellationToken = default)
        {
            Command cmd;
            var commands = new List<Command>(1);

            //mount --bind /source /target
            cmd = new Command($"mount --bind {request.StagingTargetPath} {request.PublishTargetPath}", true);
            commands.Add(cmd);

            //maybe readonly required
            //mount -o remount,ro,bind /target

            var result = await _power.InvokeAsync(commands).FirstOrDefaultAsync(cancellationToken);
        }

        public async Task UnpublishDeviceAsync(HypervNodeUnpublishRequest request, CancellationToken cancellationToken = default)
        {
            Command cmd;
            var commands = new List<Command>(1);

            //umount /drivetest
            cmd = new Command($"umount {request.TargetPath}", true);
            commands.Add(cmd);

            var result = await _power.InvokeAsync(commands).FirstOrDefaultAsync(cancellationToken);
        }
    }
}
