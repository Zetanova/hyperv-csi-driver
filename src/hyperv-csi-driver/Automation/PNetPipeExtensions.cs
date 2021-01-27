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

        public static IObservable<PSObject> ToObservable(this Pipeline pipe)
        {
            return pipe.ToObservable(Observable.Empty<object>());
        }

        public static IObservable<PSObject> ToObservable(this Pipeline pipe, IObservable<object> input)
        {
            var stateSource = Observable.Create<PSObject>(o =>
                Observable.FromEventPattern<PipelineStateEventArgs>(
                    a => pipe.StateChanged += a,
                    a => pipe.StateChanged -= a,
                    Scheduler.CurrentThread
                    )
                    .Select(n => n.EventArgs.PipelineStateInfo)
                    .StartWith(pipe.PipelineStateInfo)
                    .Subscribe(
                        onNext: n =>
                        {
                            Debug.WriteLine($"PipeControlThread: {Thread.CurrentThread.ManagedThreadId}");

                            switch (n.State)
                            {
                                //maybe case PipelineState.Stopping:
                                case PipelineState.Stopped:
                                case PipelineState.Completed:
                                    o.OnCompleted();
                                    break;
                                case PipelineState.Failed:
                                    o.OnError(n.Reason);
                                    break;
                                case PipelineState.Disconnected:
                                    o.OnError(n.Reason); //maybe suppress call
                                    break;
                            }
                        }
                    )
                );

            var pipeSource = Observable.Create<PSObject>(o =>
            {
                var s1 = pipe.Output.ToObservable()
                    //.ObserveOn(TaskPoolScheduler.Default) //maybe not required
                    .Subscribe(
                        onNext: o.OnNext,
                        onCompleted: () =>
                        {
                            o.OnCompleted();
                        }
                    );

                var s2 = pipe.Error.ToObservable()
                    //.ObserveOn(TaskPoolScheduler.Default) //maybe not required
                    .Subscribe(n =>
                    {
                        switch (n)
                        {
                            case PSObject ps: //todo switch for termiating error 
                                o.OnNext(ps); //none termiating error
                                break;
                            case ErrorRecord error:
                                o.OnError(error.Exception);
                                break;
                            case null:
                                o.OnError(new Exception("null error"));
                                break;
                            default:
                                o.OnError(new Exception($"error[{n.GetType()}] {n}"));
                                break;
                        }
                    });

                //todo merge output and input stream
                //todo better cancellation

                return new CompositeDisposable(s1, s2);
            });

            return Observable.Create<PSObject>(o =>
            {
                var s0 = input
                    .Subscribe(
                        onNext: n => pipe.Input.Write(n),
                        onCompleted: pipe.Input.Close
                    );

                var s1 = Observable.Merge(pipeSource, stateSource, Scheduler.CurrentThread)
                    .Subscribe(o);

                pipe.InvokeAsync();

                return new CompositeDisposable(s0, s1, Disposable.Create(() =>
                {
                    if (pipe.PipelineStateInfo.State == PipelineState.Running)
                        pipe.StopAsync();
                }));
            });
        }

        public static IObservable<T> ToObservable<T>(this PipelineReader<T> reader)
        {
            return Observable.Create<T>(o => Observable.FromEventPattern(
                    a => reader.DataReady += a,
                    a => reader.DataReady -= a,
                    Scheduler.CurrentThread)
                .StartWith(new EventPattern<object>(reader, EventArgs.Empty))
                .Subscribe(_ =>
                {
                    //Debug.WriteLine($"PipeReaderThread: {Thread.CurrentThread.ManagedThreadId}");

                    do
                    {
                        foreach (var n in reader.NonBlockingRead())
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
                    while (reader.Count > 0);

                    if (reader.EndOfPipeline)
                        o.OnCompleted();
                }
            ));
        }


    }
}
