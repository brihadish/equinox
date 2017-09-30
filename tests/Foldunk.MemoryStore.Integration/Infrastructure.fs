﻿[<AutoOpen>]
module Foldunk.MemoryStore.Integration.Infrastructure

open Serilog

open Domain
open FsCheck
open Serilog
open System

type FsCheckGenerators =
    static member SkuId = Arb.generate |> Gen.map SkuId |> Arb.fromGen
    static member RequestId = Arb.generate |> Gen.map RequestId |> Arb.fromGen

type AutoDataAttribute() =
    inherit FsCheck.Xunit.PropertyAttribute(Arbitrary = [|typeof<FsCheckGenerators>|], MaxTest = 1, QuietOnSuccess = true)

// Derived from https://github.com/damianh/CapturingLogOutputWithXunit2AndParallelTests
// NB VS does not surface these atm, but other test runners / test reports do
type TestOutputAdapter(testOutput : Xunit.Abstractions.ITestOutputHelper) =
    let formatter = Serilog.Formatting.Display.MessageTemplateTextFormatter("{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] {Message}{NewLine}{Exception}", null);
    let writeSeriLogEvent logEvent =
        use writer = new System.IO.StringWriter()
        formatter.Format(logEvent, writer);
        writer |> string |> testOutput.WriteLine
    member __.Subscribe(source: IObservable<Serilog.Events.LogEvent>) =
        source.Subscribe writeSeriLogEvent

let createLogger hookObservers =
    LoggerConfiguration()
        .WriteTo.Observers(Action<_> hookObservers)
        .CreateLogger()