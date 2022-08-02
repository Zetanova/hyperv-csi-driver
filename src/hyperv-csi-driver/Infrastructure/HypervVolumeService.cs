using HypervCsiDriver.Hosting;
using Microsoft.Extensions.DependencyInjection;
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

        Task<HypervVolumeDetail> GetVolumeAsync(string path, string? hostName, CancellationToken cancellationToken = default);

        Task<HypervVolumeDetail> CreateVolumeAsync(HypervCreateVolumeRequest request, CancellationToken cancellationToken = default);

        Task DeleteVolumeAsync(HypervDeleteVolumeRequest request, CancellationToken cancellationToken = default);

        Task<HypervVirtualMachineVolumeInfo> AttachVolumeAsync(HypervAttachVolumeRequest request, CancellationToken cancellationToken = default);

        Task DetachVolumeAsync(HypervDetachVolumeRequest request, CancellationToken cancellationToken = default);

        IAsyncEnumerable<HypervVirtualMachineVolumeInfo> GetVirtualMachineVolumesAsync(Guid vmId, HypervVirtualMachineVolumeFilter filter);

        IAsyncEnumerable<HypervVirtualMachineInfo> GetVirtualMachinesAsync(HypervVirtualMachineFilter filter, CancellationToken cancellationToken = default);

        IAsyncEnumerable<HypervVolumeFlowInfo> GetVolumeFlowsAsnyc(HypervVolumeFlowFilter filter);

        IAsyncEnumerable<HypervVolumeDetailResult> GetVolumeDetailsAsync(IEnumerable<HypervVolumeInfo> volumes, CancellationToken cancellationToken = default);
    }

    public sealed class HypervVolumeService : IHypervVolumeService, IDisposable
    {
        readonly HypervCsiDriverOptions _options;

        readonly IServiceScope _scope;

        ImmutableDictionary<string, HypervHost> _hosts = ImmutableDictionary.Create<string, HypervHost>(StringComparer.OrdinalIgnoreCase);

        public HypervVolumeService(IServiceProvider services, IOptions<HypervCsiDriverOptions> options)
        {
            _options = options.Value;

            _scope = services.CreateScope();
        }

        HypervHost GetHost(string hostName)
        {
            if (string.IsNullOrEmpty(hostName))
                throw new ArgumentNullException(nameof(hostName));

            hostName = hostName.ToLower();

            if (!_hosts.TryGetValue(hostName, out var host))
            {
                var options = new HyperVHostOptions
                {
                    HostName = hostName,
                    UserName = _options.UserName,
                    KeyFile = _options.KeyFile,
                    DefaultStorage = _options.DefaultStorage
                };

                host = ActivatorUtilities.CreateInstance<HypervHost>(_scope.ServiceProvider, Options.Create(options));

                ImmutableDictionary<string, HypervHost> current;
                ImmutableDictionary<string, HypervHost> result;
                do
                {
                    current = _hosts;
                    result = current.SetItem(hostName, host);
                }
                while (Interlocked.Exchange(ref _hosts, result) != current);
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
                    var vmSource = GetHost(h).GetVirtualMachinesAsync(filter);

                    await foreach (var vm in vmSource.WithCancellation(cancellationToken))
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

        public async Task<HypervVolumeDetail> GetVolumeAsync(string path, string? hostName, CancellationToken cancellationToken = default)
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



        public void Dispose()
        {
            var hosts = _hosts;
            _hosts = ImmutableDictionary<string, HypervHost>.Empty;

            foreach (var host in hosts.Values)
                host.Dispose();

            _scope.Dispose();
        }

        public async IAsyncEnumerable<HypervVolumeDetailResult> GetVolumeDetailsAsync(IEnumerable<HypervVolumeInfo> volumes, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var flows = await GetVolumeFlowsAsnyc(null)
                .ToListAsync(cancellationToken);

            foreach (var foundVolume in volumes)
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
                        v = await GetVolumeAsync(foundVolume.Path, hostName, cancellationToken);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new Exception($"Getting volume details of '{foundVolume.Name}' at host '{hostName ?? "default"}' with path '{foundVolume.Path}' failed.", ex));
                    }
                }

                yield return new HypervVolumeDetailResult
                {
                    Info = foundVolume,
                    Detail = v,
                    Nodes = volumeFlows.Select(n => n.VMId.ToString()).Distinct().ToArray(),
                    Error = (v is null, errors.Count) switch
                    {
                        (false, _) => null,
                        (_, 1) => errors[0],
                        _ => new AggregateException($"Getting volume details of '{foundVolume.Name}' failed.", errors)
                    }
                };
            }
        }
    }
}
