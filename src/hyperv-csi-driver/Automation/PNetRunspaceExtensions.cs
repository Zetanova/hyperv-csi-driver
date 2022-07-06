using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reactive.Linq;
using System.Runtime.ExceptionServices;

namespace PNet.Automation
{
    //todo publish to sperate repository

    public static class PNetRunspaceExtensions
    {
        const string ErrorRecordTypeName = "System.Management.Automation.ErrorRecord";

        public static IObservable<T> Pipe<T>(this Runspace runspace, Func<Pipeline, IObservable<T>> func)
        {
            return Observable.Using(runspace.CreatePipeline, func);
        }

        public static IObservable<PSObject> InvokeAsync(this Runspace runspace, Command command)
        {
            return runspace.Pipe(pipe =>
            {
                pipe.Commands.Add(command);
                return pipe.ToObservable();
            });
        }

        public static IObservable<PSObject> InvokeAsync(this Runspace runspace, Command command, IObservable<Object> input)
        {
            return runspace.Pipe(pipe =>
            {
                pipe.Commands.Add(command);

                return pipe.ToObservable(input);
            });
        }

        public static IObservable<PSObject> InvokeAsync(this Runspace runspace, IEnumerable<Command> commands, IObservable<Object> input)
        {
            return runspace.Pipe(pipe =>
            {
                foreach (var c in commands)
                    pipe.Commands.Add(c);
                return pipe.ToObservable(input);
            });
        }

        public static IObservable<PSObject> InvokeAsync(this Runspace runspace, IEnumerable<Command> commands)
        {
            return runspace.Pipe(pipe =>
            {
                foreach (var c in commands)
                    pipe.Commands.Add(c);
                return pipe.ToObservable();
            });
        }

        public static async IAsyncEnumerable<PSObject> UntilError(this IAsyncEnumerable<PSObject> source)
        {
            await foreach (var msg in source)
            {
                yield return msg;
                if (msg.BaseObject is ErrorRecord || (msg.BaseObject is null && msg.TypeNames.Contains(ErrorRecordTypeName)))
                    yield break;
            }
        }

        public static async IAsyncEnumerable<PSObject> UntilError(this IAsyncEnumerable<PSObject> source, Predicate<ErrorRecord> terminate)
        {
            await foreach (var msg in source)
            {
                yield return msg;
                if (msg.BaseObject is ErrorRecord error && terminate(error))
                    yield break;
                else if (msg.BaseObject is null && msg.TypeNames.Contains(ErrorRecordTypeName))
                {
                    error = (ErrorRecord)(msg as dynamic);
                    if (terminate(error))
                        yield break;
                }
            }
        }

        public static async IAsyncEnumerable<PSObject> ThrowOnError(this IAsyncEnumerable<PSObject> source)
        {
            await foreach (var msg in source)
            {
                if (msg.BaseObject is ErrorRecord error)
                {
                    ExceptionDispatchInfo.Capture(error.Exception).Throw();
                    yield break;
                }
                else if (msg.BaseObject is null && msg.TypeNames.Contains(ErrorRecordTypeName))
                {
                    Exception ex = (msg as dynamic).Exception;
                    ExceptionDispatchInfo.Capture(ex).Throw();
                    yield break;
                }
                else
                {
                    yield return msg;
                }
            }
        }

        public static async IAsyncEnumerable<PSObject> ThrowOnError(this IAsyncEnumerable<PSObject> source, Predicate<ErrorRecord> terminate)
        {
            await foreach (var msg in source)
            {
                if (msg.BaseObject is ErrorRecord error && terminate(error))
                {
                    ExceptionDispatchInfo.Capture(error.Exception).Throw();
                    yield break;
                }
                else if (msg.BaseObject is null && msg.TypeNames.Contains(ErrorRecordTypeName))
                {
                    error = (ErrorRecord)(msg as dynamic);
                    if (terminate(error))
                    {
                        ExceptionDispatchInfo.Capture(error.Exception).Throw();
                        yield break;
                    }
                }
                else
                {
                    yield return msg;
                }
            }
        }

    }
}
