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
    using RPCType = ControllerServiceCapability.Types.RPC.Types.Type;
    using VolumneAccessMode = VolumeCapability.Types.AccessMode.Types.Mode;

    public sealed class HypervCsiController : Controller.ControllerBase
    {
        private readonly IHypervVolumeService _service;

        private readonly ILogger _logger;


        public HypervCsiController(IHypervVolumeService service, ILogger<HypervCsiController> logger)
        {
            _service = service;
            _logger = logger;
        }

        public override Task<ControllerGetCapabilitiesResponse> ControllerGetCapabilities(ControllerGetCapabilitiesRequest request, ServerCallContext context)
        {
            _logger.LogDebug("get controller capabilities");

            var rsp = new ControllerGetCapabilitiesResponse
            {
            };

            rsp.Capabilities.Add(new ControllerServiceCapability
            {
                Rpc = new ControllerServiceCapability.Types.RPC
                {
                    Type = RPCType.CreateDeleteVolume
                }
            });
            rsp.Capabilities.Add(new ControllerServiceCapability
            {
                Rpc = new ControllerServiceCapability.Types.RPC
                {
                    Type = RPCType.PublishUnpublishVolume
                }
            });
            rsp.Capabilities.Add(new ControllerServiceCapability
            {
                Rpc = new ControllerServiceCapability.Types.RPC
                {
                    Type = RPCType.ListVolumes
                }
            });
            rsp.Capabilities.Add(new ControllerServiceCapability
            {
                Rpc = new ControllerServiceCapability.Types.RPC
                {
                    Type = RPCType.ListVolumesPublishedNodes
                }
            });
            rsp.Capabilities.Add(new ControllerServiceCapability
            {
                Rpc = new ControllerServiceCapability.Types.RPC
                {
                    Type = RPCType.VolumeCondition
                }
            });
            rsp.Capabilities.Add(new ControllerServiceCapability
            {
                Rpc = new ControllerServiceCapability.Types.RPC
                {
                    Type = RPCType.GetVolume
                }
            });

            //todo GET_CAPACITY
            //todo CREATE_DELETE_SNAPSHOT, LIST_SNAPSHOTS, 
            //todo CLONE_VOLUME, EXPAND_VOLUME
            //maybe PUBLISH_READONLY
            //todo SingleNodeMultiWriter

            return Task.FromResult(rsp);
        }

        public override async Task<CreateVolumeResponse> CreateVolume(CreateVolumeRequest request, ServerCallContext context)
        {
            var name = request.Name;
            var storage = string.Empty;
            var sizeBytes = 10 * 1024 * 1024UL; //10GB
            var shared = false; //todo VHDSet's

            if (request.CapacityRange != null)
            {
                var requestSize = request.CapacityRange.RequiredBytes;
                if (requestSize == 0)
                    requestSize = request.CapacityRange.LimitBytes;
                if (requestSize > 0)
                    sizeBytes = (ulong)requestSize;

                //maybe test existed volume for limit
            }

            foreach (var entry in request.Parameters)
            {
                switch (entry.Key.ToUpper())
                {
                    case "STORAGE":
                        storage = entry.Value;
                        break;
                    default:
                        //unknown parameter
                        break;
                }
            }

            foreach (var entry in request.Secrets)
            {
                switch (entry.Key)
                {
                    default:
                        //unknown secret
                        break;
                }
            }

            foreach (var entry in request.VolumeCapabilities)
            {
                switch (entry.AccessMode.Mode)
                {
                    case VolumneAccessMode.SingleNodeWriter:
                        shared = false;
                        break;
                    default:
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "not supported volume access mode"));
                }

                switch (entry.AccessTypeCase)
                {
                    case VolumeCapability.AccessTypeOneofCase.Mount:
                        //throw new RpcException(new Status(StatusCode.InvalidArgument, string.Empty),
                        //    "not supported volume access type");
                        //maybe SMB3 FileServer support
                        //entry.Mount.MountFlags;                        
                        //entry.Mount.FsType;
                        break;
                    case VolumeCapability.AccessTypeOneofCase.Block:
                        break;
                    //case VolumeCapability.AccessTypeOneofCase.None:
                    default:
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown volume access type"));
                }
            }

            _logger.LogInformation("create volume {VolumeName} on '{StorageName}' with size {VoumeSizeBytes}", name, storage, sizeBytes);

            //todo request.AccessibilityRequirements
            //todo request.VolumeContentSource

            var foundVolumes = await _service.GetVolumesAsync(new HypervVolumeFilter { Name = name }).ToListAsync(context.CancellationToken);
            if (foundVolumes.Count > 1)
                throw new RpcException(new Status(StatusCode.AlreadyExists, "volume name ambiguous"));


            HypervVolumeDetail volume;
            if (foundVolumes.Count == 1)
            {
                var foundVolume = foundVolumes[0];

                if (!string.IsNullOrEmpty(storage) && !StringComparer.OrdinalIgnoreCase.Equals(storage, foundVolume.Storage))
                    throw new RpcException(new Status(StatusCode.AlreadyExists, "volume storage mismatch"));
                if (shared != foundVolume.Shared)
                    throw new RpcException(new Status(StatusCode.AlreadyExists, "volume share mode mismatch"));

                volume = await _service.GetVolumeAsync(foundVolume.Path, context.CancellationToken);
            }
            else
            {
                volume = await _service.CreateVolumeAsync(new HypervCreateVolumeRequest
                {
                    Name = name,
                    Storage = storage,
                    SizeBytes = sizeBytes,
                    Shared = shared
                },
                context.CancellationToken);
            }

            if (request.CapacityRange != null)
            {
                if (request.CapacityRange.RequiredBytes > 0 && volume.SizeBytes < (ulong)request.CapacityRange.RequiredBytes)
                    throw new RpcException(new Status(StatusCode.AlreadyExists, "volume too small"));

                if (request.CapacityRange.LimitBytes > 0 && (ulong)request.CapacityRange.LimitBytes < volume.SizeBytes)
                    throw new RpcException(new Status(StatusCode.AlreadyExists, "volume too large"));
            }

            var rsp = new CreateVolumeResponse
            {
                Volume = new Volume
                {
                    VolumeId = volume.Name,
                    CapacityBytes = (long)volume.SizeBytes
                }
            };

            rsp.Volume.VolumeContext.Add("Id", volume.Id.ToString());
            rsp.Volume.VolumeContext.Add("Storage", volume.Storage);
            rsp.Volume.VolumeContext.Add("Path", volume.Path);
            //maybe add path

            //todo rsp.Volume.AccessibleTopology
            //todo rsp.Volume.ContentSource

            return rsp;
        }

        public override async Task<DeleteVolumeResponse> DeleteVolume(DeleteVolumeRequest request, ServerCallContext context)
        {
            var volumeName = request.VolumeId;

            //tmp fix to remove bad pvc name
            if (volumeName.StartsWith("C:\\"))
                volumeName = HypervUtils.GetFileNameWithoutExtension(volumeName);

            _logger.LogInformation("delete volume {VolumeName}", volumeName);

            var foundVolumes = await _service.GetVolumesAsync(new HypervVolumeFilter { Name = volumeName })
                .ToListAsync(context.CancellationToken);

            if (foundVolumes.Count > 1)
                throw new RpcException(new Status(StatusCode.AlreadyExists, "volume id ambiguous"));

            if (foundVolumes.Count == 1)
            {
                var volume = await _service.GetVolumeAsync(foundVolumes[0].Path, context.CancellationToken);

                if (volume.Attached)
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "volume attached"));

                //todo snapshot/parent check

                await _service.DeleteVolumeAsync(new HypervDeleteVolumeRequest
                {
                    Id = volume.Id,
                    Path = volume.Path
                },
                context.CancellationToken);
            }

            return new DeleteVolumeResponse();
        }

        public override async Task<ControllerPublishVolumeResponse> ControllerPublishVolume(ControllerPublishVolumeRequest request, ServerCallContext context)
        {
            var shared = false;

            if (request.Readonly)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "readonly attach no supported"));

            switch (request.VolumeCapability.AccessMode.Mode)
            {
                case VolumneAccessMode.SingleNodeWriter:
                    shared = false;
                    break;
                default:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "not supported volume access mode"));
            }

            switch (request.VolumeCapability.AccessTypeCase)
            {
                case VolumeCapability.AccessTypeOneofCase.Mount:
                    //throw new RpcException(new Status(StatusCode.InvalidArgument, string.Empty),
                    //    "not supported volume access type");
                    //maybe SMB3 FileServer support
                    //entry.Mount.MountFlags;                        
                    //entry.Mount.FsType;
                    break;
                case VolumeCapability.AccessTypeOneofCase.Block:
                    break;
                //case VolumeCapability.AccessTypeOneofCase.None:
                default:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown volume access type"));
            }

            var volumeName = request.VolumeId;
            var storage = request.VolumeContext["Storage"] ?? string.Empty;

            if (!Guid.TryParse(request.NodeId, out var vmId))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "node id invalid"));

            _logger.LogInformation("publich volume {VolumeName} on storage {StorageName} to node {VMId}", volumeName, storage, vmId);

            var foundVolumes = await _service.GetVolumesAsync(new HypervVolumeFilter
            {
                Name = request.VolumeId,
                Storage = request.VolumeContext["Storage"]
            })
            .ToListAsync(context.CancellationToken);

            if (foundVolumes.Count == 0)
                throw new RpcException(new Status(StatusCode.NotFound, "volume not found"));
            if (foundVolumes.Count > 1)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "volume id ambiguous"));

            var foundVolume = foundVolumes[0];

            //var volumePath = request.VolumeContext["Path"];

            HypervVirtualMachineVolumeInfo vmVolume;

            var flow = await _service.GetVolumeFlowsAsnyc(new HypervVolumeFlowFilter
            {
                VolumePath = foundVolume.Path
            })
            .FirstOrDefaultAsync(context.CancellationToken);

            if (flow != null)
            {
                _logger.LogDebug("volume {VolumeName} already attached to node {VMId}", flow.Path, vmId);

                if (!shared && flow.VMId != vmId)
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, $"volume published on node[{flow.VMId}]"));

                //todo check shared volume_capability         

                vmVolume = await _service.GetVirtualMachineVolumesAsync(flow.VMId, new HypervVirtualMachineVolumeFilter
                {
                    VolumePath = flow.Path,
                    Host = flow.Host
                })
                .FirstAsync(context.CancellationToken);
            }
            else
            {
                _logger.LogDebug("attaching volume {VolumePath} to node {VMId}", foundVolume.Path, vmId);

                var volume = await _service.GetVolumeAsync(foundVolume.Path, null, context.CancellationToken);

                if (shared != volume.Shared)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "volume sharing ambiguous"));

                //maybe check volume.Attached=false

                var vm = await _service.GetVirtualMachinesAsync(new HypervVirtualMachineFilter
                {
                    Id = vmId
                })
                .FirstOrDefaultAsync(context.CancellationToken);

                if (vm is null)
                    throw new RpcException(new Status(StatusCode.NotFound, "node not found"));

                vmVolume = await _service.AttachVolumeAsync(new HypervAttachVolumeRequest
                {
                    VMId = vm.Id,
                    VolumePath = volume.Path,
                    Host = vm.Host
                },
                context.CancellationToken);
            }

            //todo error: 
            //Volume published but is incompatible, 6 ALREADY_EXISTS
            //Max volumes attached, 8 RESOURCE_EXHAUSTED

            var rsp = new ControllerPublishVolumeResponse
            {
            };

            rsp.PublishContext.Add("ControllerNumber", vmVolume.ControllerNumber.ToString());
            rsp.PublishContext.Add("ControllerLocation", vmVolume.ControllerLocation.ToString());

            return rsp;
        }

        public override async Task<ControllerUnpublishVolumeResponse> ControllerUnpublishVolume(ControllerUnpublishVolumeRequest request, ServerCallContext context)
        {
            var volumeName = request.VolumeId;

            //tmp fix to remove bad pvc name
            if (volumeName.StartsWith("C:\\"))
                volumeName = HypervUtils.GetFileNameWithoutExtension(volumeName);

            if (!Guid.TryParse(request.NodeId, out var vmId))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "node id invalid"));

            _logger.LogInformation("unpublish volume {VolumeName} from node {VMId}", volumeName, vmId);

            var foundVolume = await _service.GetVolumesAsync(new HypervVolumeFilter
            {
                Name = volumeName
            })
            .FirstOrDefaultAsync(context.CancellationToken);

            if (foundVolume is null)
                throw new RpcException(new Status(StatusCode.NotFound, "volume not found"));

            var vm = await _service.GetVirtualMachinesAsync(new HypervVirtualMachineFilter
            {
                Id = vmId
            })
            .FirstOrDefaultAsync(context.CancellationToken);

            if (vm is null)
                throw new RpcException(new Status(StatusCode.NotFound, "node not found"));

            //todo maybe vm is deleted, spec: SHOULD return OK

            await _service.DetachVolumeAsync(new HypervDetachVolumeRequest
            {
                VMId = vm.Id,
                VolumePath = foundVolume.Path,
                Host = vm.Host
            }, context.CancellationToken);

            var rsp = new ControllerUnpublishVolumeResponse
            {
            };

            return rsp;
        }

        public override Task<ValidateVolumeCapabilitiesResponse> ValidateVolumeCapabilities(ValidateVolumeCapabilitiesRequest request, ServerCallContext context)
        {
            return base.ValidateVolumeCapabilities(request, context);
        }

        public override async Task<ListVolumesResponse> ListVolumes(ListVolumesRequest request, ServerCallContext context)
        {
            _logger.LogDebug("list volumes from index {StartIndex}", request.StartingToken);

            //todo cache query
            var volumes = await _service.GetVolumesAsync(null).ToListAsync(context.CancellationToken);

            var startIndex = 0;
            if (!string.IsNullOrEmpty(request.StartingToken))
            {
                if (!int.TryParse(request.StartingToken, out startIndex))
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid starting_token"));
            }

            var rsp = new ListVolumesResponse
            {
            };

            if (request.MaxEntries > 0 && (volumes.Count - startIndex) > request.MaxEntries)
                rsp.NextToken = (startIndex + request.MaxEntries).ToString();

            var volumeSource = volumes.AsEnumerable();

            if (startIndex > 0)
                volumeSource = volumeSource.Skip(startIndex);

            if (request.MaxEntries > 0)
                volumeSource = volumeSource.Take(request.MaxEntries);

            await foreach (var r in _service.GetVolumeDetailsAsync(volumeSource, context.CancellationToken))
            {
                var entry = new ListVolumesResponse.Types.Entry
                {
                    Volume = new Volume
                    {
                        VolumeId = r.Info.Name
                        //AccessibleTopology
                        //ContentSource 
                    },
                    Status = new ListVolumesResponse.Types.VolumeStatus
                    {
                    }
                };

                if (r.Detail is not null)
                {
                    var d = r.Detail;
                    entry.Volume.CapacityBytes = (long)(d.SizeBytes);
                    entry.Volume.VolumeContext.Add("Id", d.Id.ToString());
                    entry.Volume.VolumeContext.Add("Storage", d.Storage);
                    entry.Volume.VolumeContext.Add("Path", d.Path);
                    entry.Status.VolumeCondition = new VolumeCondition
                    {
                        Abnormal = false,
                        Message = d.Attached ? "attached" : "detached"
                    };
                }

                if (r.Nodes.Length > 0)
                    entry.Status.PublishedNodeIds.Add(r.Nodes);

                if (r.Error is not null)
                {
                    entry.Status.VolumeCondition = new VolumeCondition
                    {
                        Abnormal = true,
                        Message = r.Error.Message
                    };
                }

                rsp.Entries.Add(entry);
            }

            return rsp;
        }

        public override async Task<ControllerGetVolumeResponse> ControllerGetVolume(ControllerGetVolumeRequest request, ServerCallContext context)
        {
            _logger.LogDebug("get volume {VolumeName}", request.VolumeId);

            var foundVolumes = await _service.GetVolumesAsync(new HypervVolumeFilter { Name = request.VolumeId }).ToListAsync(context.CancellationToken);

            if (foundVolumes.Count == 0)
                throw new RpcException(new Status(StatusCode.NotFound, "volume not found"));
            if (foundVolumes.Count > 1)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "volume name ambiguous"));

            var r = await _service.GetVolumeDetailsAsync(foundVolumes)
                .SingleAsync(context.CancellationToken);

            var rsp = new ControllerGetVolumeResponse
            {
                Volume = new Volume
                {
                    VolumeId = r.Info.Name
                    //AccessibleTopology
                    //ContentSource 
                },
                Status = new ControllerGetVolumeResponse.Types.VolumeStatus
                {
                }
            };

            if (r.Detail is not null)
            {
                var d = r.Detail;
                rsp.Volume.CapacityBytes = (long)(d.SizeBytes);
                rsp.Volume.VolumeContext.Add("Id", d.Id.ToString());
                rsp.Volume.VolumeContext.Add("Storage", d.Storage);
                rsp.Volume.VolumeContext.Add("Path", d.Path);
                rsp.Status.VolumeCondition = new VolumeCondition
                {
                    Abnormal = false,
                    Message = d.Attached ? "attached" : "detached"
                };
            }

            if (r.Nodes.Length > 0)
                rsp.Status.PublishedNodeIds.Add(r.Nodes);

            if (r.Error is not null)
            {
                rsp.Status.VolumeCondition = new VolumeCondition
                {
                    Abnormal = true,
                    Message = r.Error.Message
                };
            }

            return rsp;
        }

        public override Task<GetCapacityResponse> GetCapacity(GetCapacityRequest request, ServerCallContext context)
        {
            return base.GetCapacity(request, context);
        }

        public override Task<ControllerExpandVolumeResponse> ControllerExpandVolume(ControllerExpandVolumeRequest request, ServerCallContext context)
        {
            return base.ControllerExpandVolume(request, context);
        }

        public override Task<CreateSnapshotResponse> CreateSnapshot(CreateSnapshotRequest request, ServerCallContext context)
        {
            return base.CreateSnapshot(request, context);
        }

        public override Task<DeleteSnapshotResponse> DeleteSnapshot(DeleteSnapshotRequest request, ServerCallContext context)
        {
            return base.DeleteSnapshot(request, context);
        }

        public override Task<ListSnapshotsResponse> ListSnapshots(ListSnapshotsRequest request, ServerCallContext context)
        {
            return base.ListSnapshots(request, context);
        }


    }
}
