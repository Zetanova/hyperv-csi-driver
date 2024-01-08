using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PNet.Automation
{
    //todo publish to sperate repository

    public interface IPNetPowerShell
    {
        public PNetPowerShellOptions Options { get; }

        IObservable<T> Run<T>(Func<Runspace, IObservable<T>> func);
    }

    public sealed record PNetPowerShellOptions
    {
        public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(65);
    }

    public sealed class PNetPowerShell : IPNetPowerShell, IDisposable
    {
        readonly PNetRunspacePool _pool = new PNetRunspacePool();

        public int RunspaceCount => _pool.InstanceCount;

        public PNetPowerShellOptions Options { get; } = new PNetPowerShellOptions
        {
        };

        public PNetPowerShell()
        {
        }

        public PNetPowerShell(string hostName, string userName, string? keyFile)
        {
            if (string.IsNullOrEmpty(hostName))
                throw new ArgumentNullException(nameof(hostName));
            if (string.IsNullOrEmpty(userName))
                throw new ArgumentNullException(nameof(userName));

            var conInfo = new SSHConnectionInfo(userName, hostName, keyFile);

            _pool = new PNetRunspacePool(conInfo);
        }

        async Task<PNetRunspaceContainer> GetRunspaceAsync(CancellationToken cancellationToken = default)
        {
            var runspace = await _pool.RentAsync(cancellationToken);

            return new PNetRunspaceContainer(_pool, runspace);
        }

        public IObservable<T> Run<T>(Func<Runspace, IObservable<T>> func)
        {
            return Observable.Using(c => GetRunspaceAsync(c), (n, _) => Task.FromResult(func(n.Runspace)))
                .SubscribeOn(Scheduler.Default); //todo work with context
        }

        bool _disposed;
        public void Dispose()
        {
            if (_disposed)
                return;

            _pool.Dispose();

            _disposed = true;
        }
    }

    public sealed class PNetRunspacePool : IDisposable
    {
        readonly RunspaceConnectionInfo? _connectionInfo = null;

        readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        List<RunspaceEntry> _entries = new List<RunspaceEntry>(10);

        TaskCompletionSource<RunspaceEntry> _tcs = null;

        public int MaxSize { get; set; } = 10;

        public int MinSize { get; set; } = 0;

        public int MaxRentCount { get; set; } = 20;

        public int InstanceCount => _entries.Count;

        public PNetRunspacePool()
        {
        }

        public PNetRunspacePool(RunspaceConnectionInfo connectionInfo)
        {
            _connectionInfo = connectionInfo;
        }

        public async Task<Runspace> RentAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PNetRunspacePool));

            await _lock.WaitAsync(cancellationToken);

            try
            {
                RunspaceEntry? entry;

                //clean up runspaces
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    entry = _entries[i];
                    if (!entry.Rented)
                    {
                        switch (entry.Runspace.RunspaceStateInfo.State, entry.Runspace.RunspaceAvailability)
                        {
                            case (RunspaceState.BeforeOpen, _):
                                break;
                            case (RunspaceState.Opened, RunspaceAvailability.Available):
                                break;
                            default:
                                _entries.RemoveAt(i);
                                entry.Runspace.Dispose();
                                break;
                        }
                    }
                }

                entry = _entries.FirstOrDefault(n => !n.Rented);

                if (entry is null)
                {
                    if (_entries.Count >= MaxSize)
                    {
                        do
                        {
                            _tcs = new TaskCompletionSource<RunspaceEntry>();

                            _lock.Release();

                            entry = await _tcs.Task;

                            await _lock.WaitAsync(cancellationToken);
                        }
                        while (entry.Rented);
                    }
                    else
                    {
                        var rs = _connectionInfo != null
                            ? RunspaceFactory.CreateRunspace(_connectionInfo)
                            : RunspaceFactory.CreateRunspace();

                        //rs.ApartmentState = ApartmentState.MTA;

                        entry = new RunspaceEntry
                        {
                            Runspace = rs
                        };
                        _entries.Add(entry);
                    }
                }

                entry.Rented = true;
                if(++entry.RentCount >= MaxRentCount)
                    _entries.Remove(entry); //final rent

                return entry.Runspace;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool> ReturnAsync(Runspace runspace, CancellationToken cancellationToken = default)
        {
            if (_disposed) return false;

            await _lock.WaitAsync(cancellationToken);

            try
            {
                var entry = _entries.FirstOrDefault(n => n.Runspace == runspace);

                if (entry is null)
                    return false;

                switch (runspace.RunspaceStateInfo.State, runspace.RunspaceAvailability)
                {
                    case (RunspaceState.BeforeOpen, _):
                        break;
                    case (RunspaceState.Opened, RunspaceAvailability.Available):
                        break;
                    default:
                        _entries.Remove(entry);
                        return false;
                }

                Debug.Assert(entry.Rented);

                try
                {
                    runspace.ResetRunspaceState();
                }
                catch
                {
                    //hacky
                    _entries.Remove(entry);
                    return false;
                }

                entry.Rented = false;
                _tcs?.TrySetResult(entry);
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public bool Return(Runspace runspace)
        {
            if (_disposed) return false;

            var result = _lock.Wait(3000);

            if (!result)
                return false;

            try
            {
                var entry = _entries.FirstOrDefault(n => n.Runspace == runspace);

                if (entry is null)
                    return false;

                switch (runspace.RunspaceStateInfo.State, runspace.RunspaceAvailability)
                {
                    case (RunspaceState.BeforeOpen, _):
                        break;
                    case (RunspaceState.Opened, RunspaceAvailability.Available):
                        break;
                    default:
                        _entries.Remove(entry);
                        return false;
                }

                Debug.Assert(entry.Rented);

                try
                {
                    runspace.ResetRunspaceState();
                }
                catch
                {
                    //hacky
                    _entries.Remove(entry);
                    return false;
                }

                entry.Rented = false;
                _tcs?.TrySetResult(entry);
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        bool _disposed = false;
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _lock.Dispose();

            var entries = _entries;
            _entries = null;

            if (entries is not null)
            {
                foreach (var entry in entries)
                    entry.Runspace.Dispose();
            }
        }

        sealed class RunspaceEntry
        {
            public Runspace Runspace { get; init; }

            public bool Rented { get; set; }

            public int RentCount { get; set; } = 0;
        }
    }

    sealed class PNetRunspaceContainer : IAsyncDisposable, IDisposable
    {
        Runspace? _runspace;

        public PNetRunspacePool Pool { get; }

        public Runspace Runspace => _runspace!;

        public PNetRunspaceContainer(PNetRunspacePool pool, Runspace runspace)
        {
            Pool = pool;
            _runspace = runspace;
        }

        bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            var rs = _runspace;
            _runspace = null;

            if (rs is null)
                return;

            var r = await Pool.ReturnAsync(rs);
            if (!r) rs.Dispose();

            _disposed = true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            var rs = _runspace;
            _runspace = null;

            if (rs is null)
                return;

            var r = Pool.Return(rs);
            if (!r) rs.Dispose();

            _disposed = true;
        }
    }

    public static class PNetPowerShellExtensions
    {
        public static IObservable<T> Run<T>(this IPNetPowerShell power, Func<Runspace, IObservable<T>> func, TimeSpan timeout)
        {
            var source = power.Run(func);

            if (timeout != Timeout.InfiniteTimeSpan)
                source = source.Timeout(timeout);

            return source;
        }

        public static IAsyncEnumerable<PSObject> InvokeAsync(this IPNetPowerShell power, string command)
        {
            return power.InvokeAsync(new Command(command));
        }

        public static IAsyncEnumerable<PSObject> InvokeAsync(this IPNetPowerShell power, string command, IDictionary<string, object> parameters)
        {
            var cmd = new Command(command);

            foreach (var entry in parameters)
                cmd.Parameters.Add(entry.Key, entry.Value);

            return power.InvokeAsync(cmd);
        }

        public static IAsyncEnumerable<PSObject> InvokeAsync(this IPNetPowerShell power, Command command)
        {
            return power.Run(r => r.InvokeAsync(command), power.Options.DefaultTimeout).ToAsyncEnumerable();
        }

        public static IAsyncEnumerable<PSObject> InvokeAsync(this IPNetPowerShell power, Command command, IObservable<object> input)
        {
            return power.Run(r => r.InvokeAsync(command, input), power.Options.DefaultTimeout).ToAsyncEnumerable();
        }

        public static IAsyncEnumerable<PSObject> InvokeAsync(this IPNetPowerShell power, IEnumerable<Command> commands, IObservable<object> input)
        {
            return power.Run(r => r.InvokeAsync(commands, input), power.Options.DefaultTimeout).ToAsyncEnumerable();
        }

        public static IAsyncEnumerable<PSObject> InvokeAsync(this IPNetPowerShell power, IEnumerable<Command> commands, TimeSpan timeout)
        {
            return power.Run(r => r.InvokeAsync(commands), timeout).ToAsyncEnumerable();
        }

        public static IAsyncEnumerable<PSObject> InvokeAsync(this IPNetPowerShell power, IEnumerable<Command> commands)
        {
            return power.Run(r => r.InvokeAsync(commands), power.Options.DefaultTimeout).ToAsyncEnumerable();
        }
    }
}
