using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;

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
        readonly PNetRunspacePool _pool;

        public int RunspaceCount => _pool.InstanceCount;

        public PNetPowerShellOptions Options { get; } = new PNetPowerShellOptions
        {
        };

        public PNetPowerShell()
        {
            _pool = new PNetRunspacePool();
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

        PNetRunspaceContainer GetRunspace()
        {
            //hack for remote connection
            if (Runspace.DefaultRunspace == null)
            {
                var defaultRunspace = RunspaceFactory.CreateRunspace();
                defaultRunspace.Open();

                Runspace.DefaultRunspace = defaultRunspace;
            }

            var runspace = _pool.Rent();

            return new PNetRunspaceContainer(_pool, runspace);
        }

        public IObservable<T> Run<T>(Func<Runspace, IObservable<T>> func)
        {
            return Observable.Using(GetRunspace, c => func(c.Runspace))
                .SubscribeOn(Scheduler.Default); //todo work with context
        }

        bool _disposed;
        public void Dispose()
        {
            if (_disposed)
                return;

            _pool?.Dispose();

            _disposed = true;
        }
    }

    public sealed class PNetRunspacePool : IDisposable
    {
        readonly RunspaceConnectionInfo? _connectionInfo = null;

        ImmutableList<Runspace> _runspaces = ImmutableList<Runspace>.Empty;

        int _instanceCount = 0;

        public int MaxSize { get; set; } = 3;

        public int InstanceCount => Volatile.Read(ref _instanceCount);

        public PNetRunspacePool()
        {
        }

        public PNetRunspacePool(RunspaceConnectionInfo connectionInfo)
        {
            _connectionInfo = connectionInfo;
        }

        public Runspace Rent()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PNetRunspacePool));

            Runspace? rs;

            ImmutableList<Runspace> current;
            ImmutableList<Runspace> result;
            var builder = ImmutableList.CreateBuilder<Runspace>();
            var removeCount = 0;

            do
            {
                current = _runspaces;

                //cleanup
                builder.Clear();
                removeCount = 0;

                foreach (var r in current)
                {
                    switch (r.RunspaceStateInfo.State, r.RunspaceAvailability)
                    {
                        case (RunspaceState.Opened, RunspaceAvailability.Available):
                            builder.Add(r);
                            break;
                        //case RunspaceState.Closed: 
                        //maybe reopen
                        //break;
                        default:
                            try
                            {
                                removeCount++;
                                r.Dispose();
                            }
                            catch
                            {
                                //ignore
                            }
                            break;
                    }
                }

                rs = builder.FirstOrDefault();

                if (rs is not null)
                    builder.Remove(rs);

                result = builder.ToImmutable();
            }
            while (Interlocked.Exchange(ref _runspaces, result) != current);

            Interlocked.Add(ref _instanceCount, -removeCount);

            rs ??= CreateRunspace();

            return rs;
        }

        public bool Return(Runspace runspace)
        {
            if (_disposed)
            {
                Interlocked.Decrement(ref _instanceCount);
                return false;
            }

            switch (runspace.RunspaceStateInfo.State, runspace.RunspaceAvailability)
            {
                //case (RunspaceState.BeforeOpen, _):
                //    break;
                case (RunspaceState.Opened, RunspaceAvailability.Available):
                    break;
                default:
                    Interlocked.Decrement(ref _instanceCount);
                    return false;
            }

            try
            {
                runspace.ResetRunspaceState();
            }
            catch
            {
                //hack racy 
                Interlocked.Decrement(ref _instanceCount);
                return false;
            }

            ImmutableList<Runspace> current;
            ImmutableList<Runspace> result;
            do
            {
                current = _runspaces;

                if (current.Contains(runspace))
                    break;

                if (current.Count >= MaxSize)
                {
                    Interlocked.Decrement(ref _instanceCount);
                    return false;
                }

                result = current.Add(runspace);
            }
            while (Interlocked.Exchange(ref _runspaces, result) != current);

            return true;
        }

        Runspace CreateRunspace()
        {
            var runspace = _connectionInfo != null
                ? RunspaceFactory.CreateRunspace(_connectionInfo)
                : RunspaceFactory.CreateRunspace();

            runspace.ApartmentState = ApartmentState.MTA;

            //runspace.ThreadOptions = PSThreadOptions.ReuseThread;

            //runspace.OpenAsync();

            Interlocked.Increment(ref _instanceCount);

            return runspace;
        }

        bool _disposed = false;
        public void Dispose()
        {
            if (_disposed)
                return;

            var current = _runspaces;
            _runspaces = ImmutableList<Runspace>.Empty;
            foreach (var rs in current)
            {
                rs.Dispose();
                _instanceCount--;
            }

            _disposed = true;
        }
    }

    sealed class PNetRunspaceContainer : IDisposable
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
        public void Dispose()
        {
            if (_disposed) return;

            var rs = _runspace;
            _runspace = null;
            if (rs is not null && !Pool.Return(rs))
            {
                rs.Dispose();
            }

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
            return power.Run(r => r.InvokeAsync(commands), power.Options.DefaultTimeout).ToAsyncEnumerable(); ;
        }
    }
}
