using Grpc.Core;
using HypervCsiDriver.Hosting;
using csi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;

namespace HypervCsiDriver
{
    using ServiceType = PluginCapability.Types.Service.Types.Type;
    using VolumeExpansionType = PluginCapability.Types.VolumeExpansion.Types.Type;

    public sealed class HypervCsiIdentity : Identity.IdentityBase
    {
        private readonly HypervCsiDriverOptions _options;

        private readonly ILogger _logger;
        public HypervCsiIdentity(IOptions<HypervCsiDriverOptions> options, ILogger<HypervCsiIdentity> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public override Task<GetPluginInfoResponse> GetPluginInfo(GetPluginInfoRequest request, ServerCallContext context)
        {
            _logger.LogDebug("get plugin info request");

            var rsp = new GetPluginInfoResponse
            {
                Name = "eu.zetanova.csi.hyperv",
                VendorVersion = "1.19.0"
            };

            rsp.Manifest.Add("url", "https://github.com/Zetanova/hyperv-csi-driver");

            return Task.FromResult(rsp);
        }

        public override Task<GetPluginCapabilitiesResponse> GetPluginCapabilities(GetPluginCapabilitiesRequest request, ServerCallContext context)
        {
            var rsp = new GetPluginCapabilitiesResponse
            {
            };

            switch(_options.Type)
            {
                case HypervCsiDriverType.Controller:
                    rsp.Capabilities.Add(new PluginCapability
                    {
                        Service = new PluginCapability.Types.Service
                        {
                            Type = ServiceType.ControllerService
                        }
                    });
                    break;
                case HypervCsiDriverType.Node:

                    break;
            }

            //todo add support for single hyperv host with disk migration
            //rsp.Capabilities.Add(new PluginCapability
            //{
            //    Service = new PluginCapability.Types.Service
            //    {
            //        Type = ServiceType.VolumeAccessibilityConstraints
            //    }
            //});

            //rsp.Capabilities.Add(new PluginCapability
            //{
            //    VolumeExpansion = new PluginCapability.Types.VolumeExpansion
            //    {
            //        Type = VolumeExpansionType.Online
            //    }
            //});

            return Task.FromResult(rsp);
        }

        public override Task<ProbeResponse> Probe(ProbeRequest request, ServerCallContext context)
        {
            var rsp = new ProbeResponse
            {
                //todo ready check
                //Ready = true 
            };

            return Task.FromResult(rsp);
        }
    }
}
