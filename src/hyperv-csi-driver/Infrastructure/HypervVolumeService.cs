using HypervCsiDriver.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HypervCsiDriver.Infrastructure
{
    public interface IHypervVolumeService
    {
        IAsyncEnumerable<HypervVolumeInfo> GetVolumesAsync(HypervVolumeFilter filter);

        Task<HypervVolumeDetail> GetVolumeAsync(string path, CancellationToken cancellationToken = default);

        Task<HypervVolumeDetail> GetVolumeAsync(string path, string hostName, CancellationToken cancellationToken = default);

        Task<HypervVolumeDetail> CreateVolumeAsync(HypervCreateVolumeRequest request, CancellationToken cancellationToken = default);

        Task DeleteVolumeAsync(HypervDeleteVolumeRequest request, CancellationToken cancellationToken = default);

        Task<HypervVirtualMachineVolumeInfo> AttachVolumeAsync(HypervAttachVolumeRequest request, CancellationToken cancellationToken = default);

        Task DetachVolumeAsync(HypervDetachVolumeRequest request, CancellationToken cancellationToken = default);

        IAsyncEnumerable<HypervVirtualMachineVolumeInfo> GetVirtualMachineVolumesAsync(Guid vmId, HypervVirtualMachineVolumeFilter filter);

        IAsyncEnumerable<HypervVirtualMachineInfo> GetVirtualMachinesAsync(HypervVirtualMachineFilter filter, CancellationToken cancellationToken = default);

        IAsyncEnumerable<HypervVolumeFlowInfo> GetVolumeFlowsAsnyc(HypervVolumeFlowFilter filter);
    }

    public sealed class HypervVolumeService : IHypervVolumeService
    {
        readonly HypervCsiDriverOptions _options;

        ImmutableDictionary<string, HypervHost> _hosts = ImmutableDictionary.Create<string, HypervHost>(StringComparer.OrdinalIgnoreCase);

        public HypervVolumeService(IOptions<HypervCsiDriverOptions> options)
        {
            _options = options.Value;
        }

        HypervHost GetHost(string hostName)
        {
            if (string.IsNullOrEmpty(hostName))
                throw new ArgumentNullException(nameof(hostName));

            hostName = hostName.ToLower();

            if (!_hosts.TryGetValue(hostName, out var host))
            {
                host = new HypervHost(hostName, _options.UserName, _options.KeyFile)
                {
                    DefaultStorage = _options.DefaultStorage
                };

                ImmutableDictionary<string, HypervHost> current;
                ImmutableDictionary<string, HypervHost> result;
                do
                {
                    current = _hosts;
                    result = current.SetItem(hostName, host);

                } while (Interlocked.Exchange(ref _hosts, result) != current);
            }
            return host;
        }

        public Task<HypervVolumeDetail> CreateVolumeAsync(HypervCreateVolumeRequest request, CancellationToken cancellationToken = default)
        {
            return GetHost(_options.HostName).CreateVolumeAsync(request, cancellationToken);
        }

        public Task DeleteVolumeAsync(HypervDeleteVolumeRequest request, CancellationToken cancellationToken = default)
        {
            return GetHost(_options.HostName).DeleteVolumeAsync(request, cancellationToken);
        }

        public Task<HypervVirtualMachineVolumeInfo> AttachVolumeAsync(HypervAttachVolumeRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(request.Host))
                throw new ArgumentNullException(nameof(request.Host));

            return GetHost(request.Host).AttachVolumeAsync(request, cancellationToken);
        }

        public Task DetachVolumeAsync(HypervDetachVolumeRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(request.Host))
                throw new ArgumentNullException(nameof(request.Host));

            return GetHost(request.Host).DetachVolumeAsync(request, cancellationToken);
        }

        public async IAsyncEnumerable<HypervVirtualMachineInfo> GetVirtualMachinesAsync(HypervVirtualMachineFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            HypervVolumeFlowInfo flow = null;
            if (filter != null && (filter.Id != Guid.Empty || !string.IsNullOrEmpty(filter.Name)))
            {
                flow = await GetVolumeFlowsAsnyc(new HypervVolumeFlowFilter { VMId = filter.Id, VMName = filter.Name })
                    .FirstOrDefaultAsync(cancellationToken);   
            }

            if (flow != null)
            {
                yield return new HypervVirtualMachineInfo
                {
                    Id = flow.VMId,
                    Name = flow.VMName,
                    Host = flow.Host
                };
            }   
            else
            {
                //todo improve hostName set to know of deleted vm's

                var hostNames = await GetVolumeFlowsAsnyc(null)
                    .Select(n => n.Host).Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToListAsync(cancellationToken);

                foreach (var h in hostNames)
                {
                    await foreach (var vm in GetHost(h).GetVirtualMachinesAsync(filter).WithCancellation(cancellationToken))
                        yield return vm;
                }
            }
        }

        public IAsyncEnumerable<HypervVirtualMachineVolumeInfo> GetVirtualMachineVolumesAsync(Guid vmId, HypervVirtualMachineVolumeFilter filter)
        {
            if (string.IsNullOrEmpty(filter.Host))
                throw new ArgumentNullException(nameof(filter.Host));

            return GetHost(filter.Host).GetVirtualMachineVolumesAsync(vmId, filter);
        }

        public async Task<HypervVolumeDetail> GetVolumeAsync(string path, CancellationToken cancellationToken = default)
        {
            //todo short lifed flow cache
            var flow = await GetVolumeFlowsAsnyc(new HypervVolumeFlowFilter { VolumePath = path })
                    .FirstOrDefaultAsync(cancellationToken);

            return await GetHost(flow?.Host ?? _options.HostName).GetVolumeAsync(path, cancellationToken);
        }

        public async Task<HypervVolumeDetail> GetVolumeAsync(string path, string hostName, CancellationToken cancellationToken = default)
        {
            return await GetHost(hostName ?? _options.HostName).GetVolumeAsync(path, cancellationToken);
        }

        public IAsyncEnumerable<HypervVolumeFlowInfo> GetVolumeFlowsAsnyc(HypervVolumeFlowFilter filter)
        {
            return GetHost(_options.HostName).GetVolumeFlowsAsnyc(filter);
        }

        public IAsyncEnumerable<HypervVolumeInfo> GetVolumesAsync(HypervVolumeFilter filter)
        {
            return GetHost(_options.HostName).GetVolumesAsync(filter);
        }
    }
}
