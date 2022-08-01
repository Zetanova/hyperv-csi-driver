using csi;
using Grpc.Core;
using HypervCsiDriver.Infrastructure;
using HypervCsiDriver.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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

            //todo GET_CAPACITY
            //todo CREATE_DELETE_SNAPSHOT, LIST_SNAPSHOTS, 
            //todo CLONE_VOLUME, EXPAND_VOLUME
            //maybe PUBLISH_READONLY

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
                        throw new RpcException(new Status(StatusCode.InvalidArgument, string.Empty),
                            "not supported volume access mode");
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
                        throw new RpcException(new Status(StatusCode.InvalidArgument, string.Empty),
                            "unknown volume access type");
                }
            }

            //todo request.AccessibilityRequirements
            //todo request.VolumeContentSource

            var foundVolumes = await _service.GetVolumesAsync(new HypervVolumeFilter { Name = name }).ToListAsync(context.CancellationToken);
            if (foundVolumes.Count > 1)
                throw new RpcException(new Status(StatusCode.AlreadyExists, string.Empty), "volume name ambiguous");


            HypervVolumeDetail volume;
            if (foundVolumes.Count == 1)
            {
                var foundVolume = foundVolumes[0];

                if (!string.IsNullOrEmpty(storage) && !StringComparer.OrdinalIgnoreCase.Equals(storage, foundVolume.Storage))
                    throw new RpcException(new Status(StatusCode.AlreadyExists, string.Empty), "volume storage mismatch");
                if (shared != foundVolume.Shared)
                    throw new RpcException(new Status(StatusCode.AlreadyExists, string.Empty), "volume share mode mismatch");

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
                    throw new RpcException(new Status(StatusCode.AlreadyExists, string.Empty), "volume too small");

                if (request.CapacityRange.LimitBytes > 0 && (ulong)request.CapacityRange.LimitBytes < volume.SizeBytes)
                    throw new RpcException(new Status(StatusCode.AlreadyExists, string.Empty), "volume too large");
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

            var foundVolumes = await _service.GetVolumesAsync(new HypervVolumeFilter { Name = volumeName })
                .ToListAsync(context.CancellationToken);

            if (foundVolumes.Count > 1)
                throw new RpcException(new Status(StatusCode.AlreadyExists, string.Empty), "volume id ambiguous");

            if (foundVolumes.Count == 1)
            {
                var volume = await _service.GetVolumeAsync(foundVolumes[0].Path, context.CancellationToken);

                if (volume.Attached)
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, string.Empty), "volume attached");

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
                throw new RpcException(new Status(StatusCode.InvalidArgument, string.Empty), "readonly attach no supported");

            switch (request.VolumeCapability.AccessMode.Mode)
            {
                case VolumneAccessMode.SingleNodeWriter:
                    shared = false;
                    break;
                default:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, string.Empty),
                        "not supported volume access mode");
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
                    throw new RpcException(new Status(StatusCode.InvalidArgument, string.Empty),
                        "unknown volume access type");
            }

            //request.VolumeId
            //request.VolumeContext

            var foundVolume = await _service.GetVolumesAsync(new HypervVolumeFilter
            {
                Name = request.VolumeId,
                Storage = request.VolumeContext["Storage"]
            })
            .FirstOrDefaultAsync(context.CancellationToken);

            if (foundVolume is null)
                throw new RpcException(new Status(StatusCode.NotFound, string.Empty),
                    "volume not found");

            var vmId = Guid.Parse(request.NodeId);

            //var volumePath = request.VolumeContext["Path"];

            HypervVirtualMachineVolumeInfo vmVolume;

            var flow = await _service.GetVolumeFlowsAsnyc(new HypervVolumeFlowFilter
            {
                VolumePath = foundVolume.Path
            })
            .FirstOrDefaultAsync(context.CancellationToken);

            if (flow != null)
            {
                if (!shared && flow.VMId != vmId)
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, string.Empty),
                        $"volume published on node[{flow.VMId}]");

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
                var volume = await _service.GetVolumeAsync(foundVolume.Path, null, context.CancellationToken);

                if (shared != volume.Shared)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, string.Empty),
                        "volume sharing ambiguous");

                //maybe check volume.Attached=false

                var vm = await _service.GetVirtualMachinesAsync(new HypervVirtualMachineFilter
                {
                    Id = vmId
                })
                .FirstOrDefaultAsync(context.CancellationToken);

                if (vm is null)
                    throw new RpcException(new Status(StatusCode.NotFound, string.Empty),
                        "node not found");

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

            var foundVolume = await _service.GetVolumesAsync(new HypervVolumeFilter
            {
                Name = volumeName
            })
            .FirstOrDefaultAsync(context.CancellationToken);

            if (foundVolume is null)
                throw new RpcException(new Status(StatusCode.NotFound, string.Empty),
                    "volume not found");

            var vmId = Guid.Parse(request.NodeId);

            var vm = await _service.GetVirtualMachinesAsync(new HypervVirtualMachineFilter
            {
                Id = vmId
            })
            .FirstOrDefaultAsync(context.CancellationToken);

            if (vm is null)
                throw new RpcException(new Status(StatusCode.NotFound, string.Empty),
                    "node not found");

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
            //todo cache query
            var volumes = await _service.GetVolumesAsync(null).ToListAsync(context.CancellationToken);

            var flows = await _service.GetVolumeFlowsAsnyc(null)
                .ToListAsync(context.CancellationToken);

            var startIndex = 0;
            if (!string.IsNullOrEmpty(request.StartingToken))
            {
                if (!int.TryParse(request.StartingToken, out startIndex))
                    throw new RpcException(new Status(StatusCode.Aborted, string.Empty),
                        "invalid starting_token");
            }

            var rsp = new ListVolumesResponse
            {                
            };

            if (request.MaxEntries > 0 && (volumes.Count-startIndex) > request.MaxEntries)
                rsp.NextToken = (startIndex + request.MaxEntries).ToString();


            var volumeSource = volumes.AsEnumerable();

            if(startIndex > 0)
                volumeSource = volumeSource.Skip(startIndex);

            if (request.MaxEntries > 0)
                volumeSource = volumeSource.Take(request.MaxEntries);

            foreach (var foundVolume in volumeSource)
            {
                var volumeFlows = flows.Where(n => StringComparer.OrdinalIgnoreCase.Equals(foundVolume.Path, n.Path)).ToList();

                var hostNames = volumeFlows.Select(n => n.Host)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .DefaultIfEmpty(null);

                var errors = new List<Exception>();
                HypervVolumeDetail? v = null;

                foreach (var hostName in hostNames)
                {
                    //maybe stale record after remount to other node

                    try
                    {
                        v = await _service.GetVolumeAsync(foundVolume.Path, hostName, context.CancellationToken);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new Exception($"Getting volume details of '{foundVolume.Name}' at host '{hostName ?? "default"}' with path '{foundVolume.Path}' failed.", ex));
                    }
                }

                if (v is null)
                {
                    throw errors.Count switch
                    {
                        1 => errors[0],
                        _ => new AggregateException($"Getting volume details of '{foundVolume.Name}' failed.", errors),
                    };
                }

                var entry = new ListVolumesResponse.Types.Entry
                {
                    Volume = new Volume
                    {
                        VolumeId = foundVolume.Name,
                        CapacityBytes = (long)v.SizeBytes,
                        //AccessibleTopology
                        //ContentSource 
                    },
                    Status = new ListVolumesResponse.Types.VolumeStatus
                    {
                    }
                };
                entry.Volume.VolumeContext.Add("Id", v.Id.ToString());
                entry.Volume.VolumeContext.Add("Storage", v.Storage);
                entry.Volume.VolumeContext.Add("Path", v.Path);

                entry.Status.PublishedNodeIds.Add(volumeFlows.Select(n => n.VMId.ToString()));

                rsp.Entries.Add(entry);
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
