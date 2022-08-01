using csi;
using Grpc.Core;
using HypervCsiDriver.Infrastructure;
using HypervCsiDriver.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace HypervCsiDriver
{
    using RPCType = NodeServiceCapability.Types.RPC.Types.Type;

    public sealed class HypervCsiNode : Node.NodeBase
    {
        private readonly IHypervNodeService _service;

        private readonly ILogger _logger;
        public HypervCsiNode(IHypervNodeService service, ILogger<HypervCsiNode> logger)
        {
            _service = service;
            _logger = logger;
        }

        public override Task<NodeGetCapabilitiesResponse> NodeGetCapabilities(NodeGetCapabilitiesRequest request, ServerCallContext context)
        {
            var rsp = new NodeGetCapabilitiesResponse
            {
            };

            rsp.Capabilities.Add(new NodeServiceCapability
            {
                Rpc = new NodeServiceCapability.Types.RPC
                {
                    Type = RPCType.StageUnstageVolume
                }
            });

            //todo GET_VOLUME_STATS, EXPAND_VOLUME

            return Task.FromResult(rsp);
        }

        public override async Task<NodeGetInfoResponse> NodeGetInfo(NodeGetInfoRequest request, ServerCallContext context)
        {
            //KVP daemon for KVP transfers to and from the host
            //centos: sudo yum install -y hyperv-daemons

            //read hostname from kvp values
            //strings /var/lib/hyperv/.kvp_pool_3|sed -n '/PhysicalHostNameFullyQualified/ {n;p}'

            //KVP byte array of fix sized fields key:512,value:2048 /0 filled
            //The byte array contains a UTF-8 encoded string, 
            //which is padded out to the max size with null characters. 
            //However, null string termination is not guaranteed (see kvp_send_key).
            //https://stackoverflow.com/questions/17530460/code-sample-for-reading-values-from-hyper-v-kvp-component-on-linux-aka-kvp-data

            var vmId = string.Empty;

            await foreach (var entry in HypervUtils.ReadKvpPoolAsync().WithCancellation(context.CancellationToken))
            {
                switch (entry.Name)
                {
                    case "VirtualMachineId":
                        vmId = entry.Value;
                        break;
                    //case "VirtualMachineName":
                    //case "PhysicalHostNameFullyQualified":
                    default:
                        break;
                }
            }

            if (string.IsNullOrEmpty(vmId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "hyperv kvp could not be read"));
            }

            var rsp = new NodeGetInfoResponse
            {
                NodeId = vmId,
                //todo MaxVolumesPerNode = 4*64 -1 //todo query by lsscsi
                //maybe AccessibleTopology from FailoverCluster query
            };

            return rsp;
        }

        public override async Task<NodeStageVolumeResponse> NodeStageVolume(NodeStageVolumeRequest request, ServerCallContext context)
        {
            if (!int.TryParse(request.PublishContext["ControllerNumber"], out var controllerNumber))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "argument controllerNumber invalid"));

            if (!int.TryParse(request.PublishContext["ControllerLocation"], out var controllerLocation))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "argument ControllerLocation invalid"));

            if (!Guid.TryParse(request.VolumeContext["Id"], out var vhdId))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "argument VHD Id invalid"));

            var ro = false;
            var fsType = string.Empty;
            var mountFlogs = Array.Empty<string>();
            var raw = false;

            switch (request.VolumeCapability.AccessMode.Mode)
            {
                case VolumeCapability.Types.AccessMode.Types.Mode.SingleNodeWriter:
                    break;
                case VolumeCapability.Types.AccessMode.Types.Mode.SingleNodeReaderOnly:
                    ro = true;
                    break;
                default:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "not supported volume access mode"));
            }

            switch (request.VolumeCapability.AccessTypeCase)
            {
                case VolumeCapability.AccessTypeOneofCase.Mount:
                    //maybe SMB3 FileServer support
                    fsType = request.VolumeCapability.Mount.FsType;
                    mountFlogs = request.VolumeCapability.Mount.MountFlags.ToArray();
                    break;
                case VolumeCapability.AccessTypeOneofCase.Block:
                    raw = true;
                    break;
                //case VolumeCapability.AccessTypeOneofCase.None:
                default:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown volume access type"));
            }

            await _service.MountDeviceAsync(new HypervNodeMountRequest
            {
                Name = request.VolumeId,
                VhdId = vhdId,
                ControllerNumber = controllerNumber,
                ControllerLocation = controllerLocation,
                FSType = fsType,
                Options = mountFlogs,
                Readonly = ro,
                Raw = raw,
                TargetPath = request.StagingTargetPath
            },
            context.CancellationToken);

            var rsp = new NodeStageVolumeResponse
            {
            };

            return rsp;
        }

        public override async Task<NodeUnstageVolumeResponse> NodeUnstageVolume(NodeUnstageVolumeRequest request, ServerCallContext context)
        {
            await _service.UnmountDeviceAsync(new HypervNodeUnmountRequest
            {
                Name = request.VolumeId,
                TargetPath = request.StagingTargetPath
            },
            context.CancellationToken);

            var rsp = new NodeUnstageVolumeResponse
            {
            };

            return rsp;
        }

        public override async Task<NodePublishVolumeResponse> NodePublishVolume(NodePublishVolumeRequest request, ServerCallContext context)
        {
            //todo check capabilities, readonly

            await _service.PublishDeviceAsync(new HypervNodePublishRequest
            {
                Name = request.VolumeId,
                StagingTargetPath = request.StagingTargetPath,
                PublishTargetPath = request.TargetPath
            },
            context.CancellationToken);

            var rsp = new NodePublishVolumeResponse
            {
            };
            return rsp;
        }

        public override async Task<NodeUnpublishVolumeResponse> NodeUnpublishVolume(NodeUnpublishVolumeRequest request, ServerCallContext context)
        {
            await _service.UnpublishDeviceAsync(new HypervNodeUnpublishRequest
            {
                Name = request.VolumeId,
                TargetPath = request.TargetPath
            },
            context.CancellationToken);

            var rsp = new NodeUnpublishVolumeResponse
            {
            };
            return rsp;
        }

        public override Task<NodeGetVolumeStatsResponse> NodeGetVolumeStats(NodeGetVolumeStatsRequest request, ServerCallContext context)
        {
            return base.NodeGetVolumeStats(request, context);
        }

        public override Task<NodeExpandVolumeResponse> NodeExpandVolume(NodeExpandVolumeRequest request, ServerCallContext context)
        {
            return base.NodeExpandVolume(request, context);
        }


    }
}
