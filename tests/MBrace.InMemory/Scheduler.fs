﻿namespace Nessos.MBrace.InMemory

    #nowarn "444"

    open System.Threading
    open System.Threading.Tasks

    open Nessos.MBrace
    open Nessos.MBrace.Runtime

    [<AutoOpen>]
    module private SchedulerInternals =

        type Latch (init : int) =
            [<VolatileField>]
            let mutable value = init

            member __.Increment() = Interlocked.Increment &value
            member __.Decrement() = Interlocked.Decrement &value
            member __.Value = value

        let mkLinkedCts (parent : CancellationToken) = CancellationTokenSource.CreateLinkedTokenSource [| parent |]

        let schedule context resource sc ec cc ct wf =
            let execute () =
                try
                    let ctx = { Resource = resource ; scont = sc ; econt = ec ; ccont = cc ; CancellationToken = ct }
                    Cloud.StartImmediate(wf, ctx)
                with _ -> ()

            match context with
            | Sequential -> execute ()
            | ThreadParallel | Distributed -> Task.Factory.StartNew execute |> ignore

        let Parallel mode (computations : seq<Cloud<'T>>) =
            Cloud.FromContinuations(fun ctx ->
                match (try Seq.toArray computations |> Choice1Of2 with e -> Choice2Of2 e) with
                | Choice2Of2 e -> ctx.econt e
                | Choice1Of2 computations ->
                    if computations.Length = 0 then ctx.scont [||] else
                    
                    let results = Array.zeroCreate<'T> computations.Length
                    let innerCts = mkLinkedCts ctx.CancellationToken
                    let exceptionLatch = new Latch(0)
                    let completionLatch = new Latch(0)

                    let onSuccess i (t : 'T) =
                        results.[i] <- t
                        if completionLatch.Increment() = results.Length then
                            ctx.scont results

                    let onException e =
                        if exceptionLatch.Increment() = 1 then
                            innerCts.Cancel ()
                            ctx.econt e

                    let onCancellation ce =
                        if exceptionLatch.Increment() = 1 then
                            innerCts.Cancel ()
                            ctx.ccont ce

                    for i = 0 to computations.Length - 1 do
                        schedule mode ctx.Resource (onSuccess i) onException onCancellation innerCts.Token computations.[i])

        let Choice mode (computations : seq<Cloud<'T option>>) =
            Cloud.FromContinuations(fun ctx ->
                match (try Seq.toArray computations |> Choice1Of2 with e -> Choice2Of2 e) with
                | Choice2Of2 e -> ctx.econt e
                | Choice1Of2 computations ->
                    if computations.Length = 0 then ctx.scont None else

                    let innerCts = mkLinkedCts ctx.CancellationToken
                    let completionLatch = new Latch(0)
                    let exceptionLatch = new Latch(0)

                    let onSuccess (topt : 'T option) =
                        if Option.isSome topt then
                            if exceptionLatch.Increment() = 1 then
                                ctx.scont topt
                        else
                            if completionLatch.Increment () = computations.Length then
                                ctx.scont None

                    let onException e =
                        if exceptionLatch.Increment() = 1 then
                            innerCts.Cancel ()
                            ctx.econt e

                    let onCancellation ce =
                        if exceptionLatch.Increment() = 1 then
                            innerCts.Cancel ()
                            ctx.ccont ce

                    for i = 0 to computations.Length - 1 do
                        schedule mode ctx.Resource onSuccess onException onCancellation innerCts.Token computations.[i])


        let StartChild (computation : Cloud<'T>) : Cloud<Cloud<'T>> =
            Cloud.FromContinuations(fun ctx ->
                let task =
                    try Cloud.StartAsTask(computation, resources = ctx.Resource, cancellationToken = ctx.CancellationToken) |> Choice1Of2
                    with e -> Choice2Of2 e

                match task with
                | Choice2Of2 e -> ctx.econt e
                | Choice1Of2 t -> ctx.scont <| Cloud.AwaitTask t)


    type internal InMemoryScheduler private (context : SchedulingContext) =

        let taskId = System.Guid.NewGuid().ToString()

        static member Create () = new InMemoryScheduler(ThreadParallel)
        
        interface IRuntimeProvider with
            member __.ProcessId = "in memory process"
            member __.TaskId = taskId
            member __.GetAvailableWorkers () = async {
                return raise <| new System.NotImplementedException("'GetAvailableWorkers not supported in InMemory runtime.")
            }

            member __.CurrentWorker =
                {
                    new IWorkerRef with
                        member __.Type = "ThreadPool worker"
                        member __.Id = sprintf "ThreadId:%d" System.Threading.Thread.CurrentThread.ManagedThreadId
                }

            member __.SchedulingContext = context
            member __.WithSchedulingContext newContext = 
                let newContext =
                    match newContext with
                    | Distributed -> ThreadParallel
                    | c -> c

                new InMemoryScheduler(newContext) :> IRuntimeProvider

            member __.ScheduleParallel computations = Parallel context computations
            member __.ScheduleChoice computations = Choice context computations
            member __.ScheduleStartChild (workflow, ?target:IWorkerRef, ?timeoutMilliseconds:int) = StartChild workflow