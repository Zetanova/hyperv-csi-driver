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
        IObservable<T> Run<T>(Func<Runspace, IObservable<T>> func);
    }

    public sealed class PNetPowerShell : IPNetPowerShell, IDisposable
    {
        PNetRunspacePool _pool;

        public PNetPowerShell()
        {
            _pool = new PNetRunspacePool();
        }

        public PNetPowerShell(string hostName, string userName, string keyFile)
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
                .SubscribeOn(DefaultScheduler.Instance); //todo work with context
        }
        
        bool disposed;
        public void Dispose()
        {
            if (disposed)
                return;

            _pool?.Dispose();

            disposed = true;
        }
    }

    public sealed class PNetRunspacePool : IDisposable
    {
        RunspaceConnectionInfo _connectionInfo;

        ImmutableList<Runspace> runspaces = ImmutableList<Runspace>.Empty;

        public PNetRunspacePool()
        {
            _connectionInfo = null;
        }

        public PNetRunspacePool(RunspaceConnectionInfo connectionInfo)
        {
            _connectionInfo = connectionInfo;
        }

        public Runspace Rent()
        {
            if (disposed) throw new ObjectDisposedException(nameof(PNetRunspacePool));

            Runspace rs;
            {
                ImmutableList<Runspace> current;
                ImmutableList<Runspace> result;
                do
                {
                    current = runspaces;
                    result = current.RemoveAll(n => n.RunspaceAvailability == RunspaceAvailability.None);

                    rs = result.FirstOrDefault(n => n.RunspaceAvailability == RunspaceAvailability.Available);
                    if (rs != null)
                        result = result.Remove(rs);

                } while (Interlocked.Exchange(ref runspaces, result) != current);
            }

            if (rs is null)
                return CreateRunspace();

            rs.ResetRunspaceState();

            return CreateRunspace();
        }

        public bool Return(Runspace runspace)
        {
            if (disposed) return false;

            if (runspace.RunspaceStateInfo.State != RunspaceState.Opened)
                return false;

            {
                ImmutableList<Runspace> current;
                ImmutableList<Runspace> result;
                do
                {
                    current = runspaces;
                    result = current.Add(runspace);

                } while (Interlocked.Exchange(ref runspaces, result) != current);
            }

            return true;
        }

        Runspace CreateRunspace()
        {
            var runspace = _connectionInfo != null
                ? RunspaceFactory.CreateRunspace(_connectionInfo)
                : RunspaceFactory.CreateRunspace();

            runspace.Open();

            //Debug.WriteLine("runspace opened");

            return runspace;
        }

        private void Runspace_AvailabilityChanged(object sender, RunspaceAvailabilityEventArgs e)
        {
            //Debug.WriteLine("Runspace: " + e.RunspaceAvailability);
        }

        bool disposed = false;
        void Dispose(bool disposing)
        {
            if (disposed)
                return;
            
            if (disposing)
            {
                var current = runspaces;
                runspaces = ImmutableList<Runspace>.Empty;
                foreach (var rs in current)
                    rs.Dispose();
            }

            disposed = true;            
        }

        public void Dispose()
        {
            Dispose(true);
            //GC.SuppressFinalize(this);
        }
    }

    class PNetRunspaceContainer : IDisposable
    {
        public PNetRunspacePool Pool { get; }

        public Runspace Runspace { get; }

        public PNetRunspaceContainer(PNetRunspacePool pool, Runspace runspace)
        {
            Pool = pool;
            Runspace = runspace;
        }

        bool disposed;
        public void Dispose()
        {
            if (disposed) return;

            if (!Pool.Return(Runspace))
                Runspace.Dispose();

            disposed = true;
        }
    }

    public static class PNetPowerShellExtensions
    {
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
            return power.Run(r => r.InvokeAsync(command)).ToAsyncEnumerable();
        }

        public static IAsyncEnumerable<PSObject> InvokeAsync(this IPNetPowerShell power, Command command, IObservable<object> input)
        {
            return power.Run(r => r.InvokeAsync(command, input)).ToAsyncEnumerable();
        }

        public static IAsyncEnumerable<PSObject> InvokeAsync(this IPNetPowerShell power, IEnumerable<Command> commands, IObservable<object> input)
        {
            return power.Run(r => r.InvokeAsync(commands, input)).ToAsyncEnumerable();
        }

        public static IAsyncEnumerable<PSObject> InvokeAsync(this IPNetPowerShell power, IEnumerable<Command> commands)
        {
            return power.Run(r => r.InvokeAsync(commands)).ToAsyncEnumerable();
        }
    }
}
