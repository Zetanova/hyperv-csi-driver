using System;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Runspaces;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.ExceptionServices;

namespace PNet.Automation
{
    //todo publish to sperate repository

    public static class PNetPipelineExtensions
    {
        [DebuggerStepThrough]
        public static void ThrowErrors(this Pipeline pipe)
        {
            if (pipe.Error.Count > 0)
            {
                //todo AggregateException
                var error = pipe.Error.Read() as ErrorRecord;

                ExceptionDispatchInfo.Capture(error.Exception).Throw();
            }
        }

        public static IObservable<PSObject> ToObservable(this Pipeline pipe)
        {
            return pipe.ToObservable(Observable.Empty<object>());
        }

        public static IObservable<PSObject> ToObservable(this Pipeline pipe, IObservable<object> input)
        {
            Runspace? defaultRunspace = null;

            var pipeStateSource = Observable.FromEventPattern<PipelineStateEventArgs>(
                    a => pipe.StateChanged += a,
                    a => pipe.StateChanged -= a)
                    .Select(n => (Pipe: (Pipeline)n.Sender!, State: n.EventArgs.PipelineStateInfo));

            var runspaceStateSource = Observable.FromEventPattern<RunspaceStateEventArgs>(
                   a => pipe.Runspace.StateChanged += a,
                   a => pipe.Runspace.StateChanged -= a)
                   .Select(n => n.EventArgs.RunspaceStateInfo);

            var runspaceAvailabilitySource = Observable.FromEventPattern<RunspaceAvailabilityEventArgs>(
                   a => pipe.Runspace.AvailabilityChanged += a,
                   a => pipe.Runspace.AvailabilityChanged -= a)
                   .Select(n => n.EventArgs.RunspaceAvailability);


            var pipeControlSource = Observable.Create<PSObject>(o =>
               Observable.CombineLatest(
                   pipeStateSource.StartWith((Pipe: pipe, State: pipe.PipelineStateInfo)),
                   runspaceStateSource.StartWith(pipe.Runspace.RunspaceStateInfo),
                   runspaceAvailabilitySource.StartWith(pipe.Runspace.RunspaceAvailability),
                    (pa, rs, ra) => (rs, ra, pipe: pa.Pipe, ps: pa.State))
                   .Subscribe(onNext: n => OnNextPipeControl(o, n))
               );

            void OnNextPipeControl(IObserver<PSObject> o, (RunspaceStateInfo rs, RunspaceAvailability ra, Pipeline pipe, PipelineStateInfo ps) n)
            {
                //Debug.WriteLine($"Control: Thread[{Thread.CurrentThread.ManagedThreadId}], {n.rs.State}, {n.ra}, {n.ps.State}");

                switch (n.rs.State)
                {
                    case RunspaceState.BeforeOpen:
                        if (n.ra == RunspaceAvailability.None)
                        {
                            if(n.pipe.Runspace.RunspaceIsRemote)
                            {
                                //maybe only required if a ssh-keyfile is used
                                defaultRunspace = RunspaceFactory.CreateRunspace();
                                defaultRunspace.Open();
                                Runspace.DefaultRunspace = defaultRunspace;
                            }
                            n.pipe.Runspace.OpenAsync();
                        }
                        break;
                    case RunspaceState.Opening:
                        break;
                    case RunspaceState.Opened:
                        if (n.ra == RunspaceAvailability.Available)
                        {
                            switch (n.ps.State)
                            {
                                case PipelineState.NotStarted:
                                    if (n.pipe.PipelineStateInfo.State == PipelineState.NotStarted
                                        && n.pipe.Runspace.RunspaceAvailability == RunspaceAvailability.Available)
                                        n.pipe.InvokeAsync();
                                    break;
                                case PipelineState.Running:
                                    break;
                                case PipelineState.Stopped:
                                    o.OnCompleted();
                                    break;
                                case PipelineState.Completed:
                                    if (n.pipe.HadErrors && !n.pipe.Error.EndOfPipeline)
                                    {
                                        //workaround to signal errors after closed readers
                                        o.OnNext(new PSObject(new ErrorRecord(new Exception("dirty-pipe"), "pipe_completed", ErrorCategory.FromStdErr, null)));
                                    }
                                    o.OnCompleted();
                                    break;
                                case PipelineState.Failed:
                                    o.OnError(n.ps.Reason);
                                    break;
                                case PipelineState.Disconnected:
                                    o.OnError(n.ps.Reason); //maybe suppress call
                                    break;
                            }
                        }
                        break;
                    case RunspaceState.Connecting:
                    case RunspaceState.Disconnecting:
                        break;
                    case RunspaceState.Disconnected:
                        o.OnError(n.rs.Reason);
                        break;
                    case RunspaceState.Closing:
                        break;
                    case RunspaceState.Closed:
                        o.OnCompleted();
                        break;
                    case RunspaceState.Broken:
                        o.OnError(n.rs.Reason);
                        break;
                    default:
                        break;
                }

            }

            var errorSource = pipe.Error.ToObservable()
                .Materialize()
                .Select(n => n.Kind switch
                {
                    NotificationKind.OnNext => n.Value switch
                    {
                        PSObject o => n,
                        ErrorRecord r => Notification.CreateOnError<object>(r.Exception),
                        null => Notification.CreateOnError<object>(new Exception("null error")),
                        _ => Notification.CreateOnError<object>(new Exception($"error[{n.GetType()}] {n}"))
                    },
                    _ => n
                })
                .Dematerialize()
                .OfType<PSObject>();

            var pipeSource = Observable.Merge(errorSource, pipe.Output.ToObservable(), Scheduler.CurrentThread);

            return Observable.Create<PSObject>(o =>
            {
                var s0 = input
                    .SubscribeOn(Scheduler.Default)
                    .Subscribe(
                        onNext: n => pipe.Input.Write(n),
                        onCompleted: pipe.Input.Close,
                        onError: ex => pipe.Input.Close()
                    );

                var s1 = Observable.Merge(Scheduler.CurrentThread, pipeControlSource, pipeSource)
                    .Subscribe(o);

                return new CompositeDisposable(s0, s1, Disposable.Create(() =>
                {
                    if (pipe.Input.IsOpen)
                        pipe.Input.Close();

                    if (pipe.PipelineStateInfo.State == PipelineState.Running)
                    {
                        try
                        {
                            pipe.StopAsync();
                        }
                        catch (PSObjectDisposedException)
                        {
                            //ignore
                        }
                    }

                    defaultRunspace?.Dispose();

                    //Debug.WriteLine("pipe invoke disposed");
                }));
            });
        }

        public static IObservable<T> ToObservable<T>(this PipelineReader<T> reader)
        {
            var dataReadySource = Observable.FromEventPattern(
                    a => reader.DataReady += a,
                    a => reader.DataReady -= a)
                .StartWith(new EventPattern<object>(reader, EventArgs.Empty));

            return Observable.Create<T>(o => dataReadySource.Subscribe(a =>
            {
                var r = (PipelineReader<T>)a.Sender!;

                //Debug.WriteLine($"PipeReader: Thread[{Thread.CurrentThread.ManagedThreadId}], {r.Count} count to read");

                do
                {
                    foreach (var n in r.NonBlockingRead())
                    {
                        if (!AutomationNull.Value.Equals(n))
                        {
                            o.OnNext(n);
                        }
                        else
                        {
                            ;//maybe multiple null inside stream are allowed
                        }
                    }
                }
                while (r.Count > 0);

                if (r.EndOfPipeline)
                    o.OnCompleted();
            }
            ));
        }
    }
}
