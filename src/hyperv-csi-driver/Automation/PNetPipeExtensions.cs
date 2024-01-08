using System;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Runspaces;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

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

        public static IObservable<PSObject> ToObservable(this Pipeline pipe, IObservable<object>? input = null)
        {
            Runspace? defaultRunspace = null;

            var runspace = pipe.Runspace;

            var pipeStateSource = Observable.FromEventPattern<PipelineStateEventArgs>(
                    a => pipe.StateChanged += a,
                    a => pipe.StateChanged -= a)
                    .Select(n => (Pipe: (Pipeline)n.Sender!, State: n.EventArgs.PipelineStateInfo));

            var runspaceStateSource = Observable.FromEventPattern<RunspaceStateEventArgs>(
                   a => runspace.StateChanged += a,
                   a => runspace.StateChanged -= a)
                   .Select(n => n.EventArgs.RunspaceStateInfo);

            var runspaceAvailabilitySource = Observable.FromEventPattern<RunspaceAvailabilityEventArgs>(
                   a => runspace.AvailabilityChanged += a,
                   a => runspace.AvailabilityChanged -= a)
                   .Select(n => n.EventArgs.RunspaceAvailability);

            var pipeControlSource = Observable.Create<PSObject>(o =>
               Observable.CombineLatest(
                   pipeStateSource.StartWith((Pipe: pipe, State: pipe.PipelineStateInfo)),
                   runspaceStateSource.StartWith(runspace.RunspaceStateInfo),
                   runspaceAvailabilitySource.StartWith(runspace.RunspaceAvailability),
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
                            if (n.pipe.Runspace.RunspaceIsRemote)
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
                                    {
                                        //Debug.WriteLine($"Pipe[{n.pipe.InstanceId}]: starting");
                                        n.pipe.InvokeAsync();
                                        //Debug.WriteLine($"Pipe[{n.pipe.InstanceId}]: started");

                                        //o.OnNext(n.pipe.PipelineStateInfo);
                                    }
                                    break;
                                //case PipelineState.Running:
                                //    Debug.WriteLine($"Pipe[{n.pipe.InstanceId}]: fake running");
                                //    break;
                                //case PipelineState.Stopping:
                                //    break;
                                case PipelineState.Stopped:
                                    o.OnCompleted();
                                    break;
                                case PipelineState.Completed:
                                    //if (n.pipe.HadErrors && !n.pipe.Error.EndOfPipeline)
                                    //{
                                    //    //workaround to signal errors after closed readers
                                    //    o.OnNext(new PSObject(new ErrorRecord(new Exception("dirty-pipe"), "pipe_completed", ErrorCategory.FromStdErr, null)));
                                    //}
                                    //if (!n.pipe.Output.EndOfPipeline)
                                    //{
                                    //    //workaround to signal unread items
                                    //    o.OnNext(new PSObject(new ErrorRecord(new Exception("clogged-pipe"), "pipe_completed", ErrorCategory.OperationStopped, null)));
                                    //}
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
                        else if (n.ra == RunspaceAvailability.Busy)
                        {
                            switch (n.ps.State)
                            {
                                case PipelineState.Running:
                                    //Debug.WriteLine($"Pipe[{n.pipe.InstanceId}]: running");
                                    //o.OnNext(n.ps);
                                    break;
                                case PipelineState.Stopping:
                                    //Debug.WriteLine($"Pipe[{n.pipe.InstanceId}]: stopping");
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

            var pipeSource = Observable.Merge(pipe.Output.ToObservable(), errorSource, Scheduler.CurrentThread);

            return Observable.Create<PSObject>(o =>
            {
                Debug.WriteLine($"Pipe[{pipe.InstanceId}]: {string.Join(" | ", pipe.Commands.Select(n => n.ToString()))}");

                var s0 = Disposable.Empty;
                if (input is not null)
                {
                    s0 = input.Do(n => pipe.Input.Write(n))
                        .Finally(() => pipe.Input.Close())
                        .Subscribe();
                }
                else
                {
                    //if (pipe.Commands.Count > 1)
                    //    pipe.Input.Write(AutomationNull.Value);

                    //Debug.WriteLine($"Pipe[{pipe.InstanceId}]: input closed");
                    pipe.Input.Close();
                }

                var s1 = Observable.Merge(pipeSource, pipeControlSource, Scheduler.CurrentThread)
                    .ObserveOn(Scheduler.Default) //required to unsubscribe in DataAdded event handler
                                                  //.Finally(() => Debug.WriteLine($"Pipe[{pipe.InstanceId}]: completed in state '{pipe.PipelineStateInfo.State}'"))
                    .Subscribe(o);

                var s3 = new CompositeDisposable(1);

                var s2 = Disposable.Create(() =>
                {
                    if (pipe.Input.IsOpen)
                    {
                        //Debug.WriteLine($"Pipe[{pipe.InstanceId}]: input closed");
                        pipe.Input.Close();
                    }

                    if (pipe.PipelineStateInfo.State == PipelineState.Running)
                    {
                        //Debug.WriteLineIf(!pipe.Output.EndOfPipeline, $"Pipe[{pipe.InstanceId}] Thread[{Thread.CurrentThread.ManagedThreadId}]: output not complete: {pipe.Output.Count} count");

                        s3.Add(new ScheduledDisposable(Scheduler.Default, Disposable.Create(() =>
                        {
                            if (pipe.PipelineStateInfo.State != PipelineState.Running)
                                return;

                            Debug.WriteLineIf(!pipe.Output.EndOfPipeline, $"Pipe[{pipe.InstanceId}] Thread[{Thread.CurrentThread.ManagedThreadId}]: output not complete: {pipe.Output.Count} count");

                            try
                            {
                                pipe.Stop();
                            }
                            catch (ObjectDisposedException)
                            {
                                //ignore
                                //Debug.WriteLine($"Pipe[{pipe.InstanceId}]: stop error");
                            }

                            defaultRunspace?.Dispose();
                        })));
                    }
                    else
                    {
                        //Debug.WriteLine($"Pipe[{pipe.InstanceId}]: unsubscribed");

                        defaultRunspace?.Dispose();
                    }
                });

                return new CompositeDisposable(s0, s1, s2, s3);
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
            }));
        }
    }
}
