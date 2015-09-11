﻿namespace MBrace.Thespian.Tests

open System.IO

open NUnit.Framework

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Core.Tests
open MBrace.Thespian

[<Category("CloudStreams.Cluster")>]
type ``MBrace Thespian Flow Tests`` () =
    inherit ``CloudFlow tests`` ()

    let session = new RuntimeSession(workerCount = 4)

    [<TestFixtureSetUp>]
    member __.Init () = session.Start()

    [<TestFixtureTearDown>]
    member __.Fini () = session.Stop()
      
    override __.FsCheckMaxNumberOfTests = 10  
    override __.FsCheckMaxNumberOfIOBoundTests = 10
    override __.IsSupportedStorageLevel level = session.Runtime.GetResource<ICloudValueProvider>().IsSupportedStorageLevel level
    override __.Run(expr : Cloud<'T>) : 'T = session.Runtime.Run(expr, faultPolicy = FaultPolicy.NoRetry)
    override __.RunOnCurrentProcess(expr : Cloud<'T>) : 'T = session.Runtime.RunOnCurrentProcess(expr)
