﻿module Equinox.Tool.Program

open Argu
open Domain.Infrastructure
open Equinox.Tool.Infrastructure
open FSharp.Control
open FSharp.UMX
open Microsoft.Extensions.DependencyInjection
open Samples.Infrastructure
open Serilog
open Serilog.Events
open System
open System.Net.Http
open System.Threading

module CosmosInit = Equinox.CosmosStore.Core.Initialization

let [<Literal>] appName = "equinox-tool"

[<NoEquality; NoComparison>]
type Arguments =
    | [<AltCommandLine "-V">]               Verbose
    | [<AltCommandLine "-C">]               VerboseConsole
    | [<AltCommandLine "-S">]               LocalSeq
    | [<AltCommandLine "-l">]               LogFile of string
    | [<CliPrefix(CliPrefix.None); Last>]   Run of ParseResults<TestArguments>
    | [<CliPrefix(CliPrefix.None); Last>]   Init of ParseResults<InitArguments>
    | [<CliPrefix(CliPrefix.None); Last>]   InitAws of ParseResults<TableArguments>
    | [<CliPrefix(CliPrefix.None); Last>]   Config of ParseResults<ConfigArguments>
    | [<CliPrefix(CliPrefix.None); Last>]   Stats of ParseResults<StatsArguments>
    | [<CliPrefix(CliPrefix.None); Last>]   Dump of ParseResults<DumpArguments>
    interface IArgParserTemplate with
        member a.Usage = a |> function
            | Verbose ->                    "Include low level logging regarding specific test runs."
            | VerboseConsole ->             "Include low level test and store actions logging in on-screen output to console."
            | LocalSeq ->                   "Configures writing to a local Seq endpoint at http://localhost:5341, see https://getseq.net"
            | LogFile _ ->                  "specify a log file to write the result breakdown into (default: eqx.log)."
            | Run _ ->                      "Run a load test"
            | Init _ ->                     "Initialize Store/Container (supports `cosmos` stores; also handles RU/s provisioning adjustment)."
            | InitAws _ ->                  "Initialize DynamoDB Table (supports `dynamo` stores; also handles RU/s provisioning adjustment)."
            | Config _ ->                   "Initialize Database Schema (supports `mssql`/`mysql`/`postgres` SqlStreamStore stores)."
            | Stats _ ->                    "inspect store to determine numbers of streams/documents/events (supports `cosmos` stores)."
            | Dump _ ->                     "Load and show events in a specified stream (supports all stores)."
and [<NoComparison; NoEquality>]InitArguments =
    | [<AltCommandLine "-ru">]              Rus of int
    | [<AltCommandLine "-A">]               Autoscale
    | [<AltCommandLine "-m">]               Mode of CosmosModeType
    | [<AltCommandLine "-P">]               SkipStoredProc
    | [<CliPrefix(CliPrefix.None)>]         Cosmos of ParseResults<Storage.Cosmos.Arguments>
    interface IArgParserTemplate with
        member a.Usage = a |> function
            | Rus _ ->                      "Specify RU/s level to provision for the Container (Default: 400 RU/s or 4000 RU/s if autoscaling)."
            | Autoscale ->                  "Autoscale provisioned throughput. Use --rus to specify the maximum RU/s."
            | Mode _ ->                     "Configure RU mode to use Container-level RU, Database-level RU, or Serverless allocations (Default: Use Container-level allocation)."
            | SkipStoredProc ->             "Inhibit creation of stored procedure in specified Container."
            | Cosmos _ ->                   "Cosmos Connection parameters."
and CosmosInitInfo(args : ParseResults<InitArguments>) =
    member _.ProvisioningMode =
        let throughput () =
            if args.Contains Autoscale
            then CosmosInit.Throughput.Autoscale (args.GetResult(Rus, 4000))
            else CosmosInit.Throughput.Manual (args.GetResult(Rus, 400))
        match args.GetResult(Mode, CosmosModeType.Container) with
        | CosmosModeType.Container ->       CosmosInit.Provisioning.Container (throughput ())
        | CosmosModeType.Db ->              CosmosInit.Provisioning.Database (throughput ())
        | CosmosModeType.Serverless ->
            if args.Contains Rus || args.Contains Autoscale then raise (Storage.MissingArg "Cannot specify RU/s or Autoscale in Serverless mode")
            CosmosInit.Provisioning.Serverless
and [<NoComparison; NoEquality>] TableArguments =
    | [<AltCommandLine "-D">]               OnDemand
    | [<AltCommandLine "-S">]               Streaming of Equinox.DynamoStore.Core.Initialization.StreamingMode
    | [<AltCommandLine "-rru">]             ReadCu of int64
    | [<AltCommandLine "-wru">]             WriteCu of int64
    | [<CliPrefix(CliPrefix.None)>]         Dynamo of ParseResults<Storage.Dynamo.Arguments>
    interface IArgParserTemplate with
        member a.Usage = a |> function
            | OnDemand ->                   "Specify On-Demand Capacity Mode."
            | Streaming _ ->                "Specify Streaming Mode. Default NEW_IMAGE"
            | ReadCu _ ->                   "Specify Read Capacity Units to provision for the Table. (Ignored in On-Demand mode)"
            | WriteCu _ ->                  "Specify Write Capacity Units to provision for the Table. (Ignored in On-Demand mode)"
            | Dynamo _ ->                   "DynamoDB Connection parameters."
and DynamoInitInfo(args : ParseResults<TableArguments>) =
    let streaming =                         args.GetResult(Streaming, Equinox.DynamoStore.Core.Initialization.StreamingMode.Default)
    let onDemand =                          args.Contains OnDemand
    let readCu =                            args.GetResult ReadCu
    let writeCu =                           args.GetResult WriteCu
    member _.ProvisioningMode =             streaming, if onDemand then None else Some (readCu, writeCu)
and [<NoComparison; NoEquality>]ConfigArguments =
    | [<CliPrefix(CliPrefix.None); Last; AltCommandLine "ms">] MsSql    of ParseResults<Storage.Sql.Ms.Arguments>
    | [<CliPrefix(CliPrefix.None); Last; AltCommandLine "my">] MySql    of ParseResults<Storage.Sql.My.Arguments>
    | [<CliPrefix(CliPrefix.None); Last; AltCommandLine "pg">] Postgres of ParseResults<Storage.Sql.Pg.Arguments>
    interface IArgParserTemplate with
        member a.Usage = a |> function
            | MsSql _ ->                    "Configure Sql Server Store."
            | MySql _ ->                    "Configure MySql Store."
            | Postgres _ ->                 "Configure Postgres Store."
and [<NoComparison; NoEquality>]StatsArguments =
    | [<AltCommandLine "-E"; Unique>]       Events
    | [<AltCommandLine "-S"; Unique>]       Streams
    | [<AltCommandLine "-D"; Unique>]       Documents
    | [<AltCommandLine "-P"; Unique>]       Parallel
    | [<CliPrefix(CliPrefix.None)>]         Cosmos of ParseResults<Storage.Cosmos.Arguments>
    interface IArgParserTemplate with
        member a.Usage = a |> function
            | Events _ ->                   "Count the number of Events in the store."
            | Streams _ ->                  "Count the number of Streams in the store. (Default action if no others supplied)"
            | Documents _ ->                "Count the number of Documents in the store."
            | Parallel _ ->                 "Run in Parallel (CAREFUL! can overwhelm RU allocations)."
            | Cosmos _ ->                   "Cosmos Connection parameters."
and [<NoComparison; NoEquality>]DumpArguments =
    | [<AltCommandLine "-s"; MainCommand>]  Stream of FsCodec.StreamName
    | [<AltCommandLine "-C"; Unique>]       Correlation
    | [<AltCommandLine "-B"; Unique>]       Blobs
    | [<AltCommandLine "-J"; Unique>]       JsonSkip
    | [<AltCommandLine "-P"; Unique>]       Pretty
    | [<AltCommandLine "-F"; Unique>]       FlattenUnfolds
    | [<AltCommandLine "-T"; Unique>]       TimeRegular
    | [<AltCommandLine "-U"; Unique>]       UnfoldsOnly
    | [<AltCommandLine "-E"; Unique >]      EventsOnly
    | [<CliPrefix(CliPrefix.None)>]                            Cosmos   of ParseResults<Storage.Cosmos.Arguments>
    | [<CliPrefix(CliPrefix.None)>]                            Dynamo   of ParseResults<Storage.Dynamo.Arguments>
    | [<CliPrefix(CliPrefix.None); Last>]                      Es       of ParseResults<Storage.EventStore.Arguments>
    | [<CliPrefix(CliPrefix.None); Last; AltCommandLine "ms">] MsSql    of ParseResults<Storage.Sql.Ms.Arguments>
    | [<CliPrefix(CliPrefix.None); Last; AltCommandLine "my">] MySql    of ParseResults<Storage.Sql.My.Arguments>
    | [<CliPrefix(CliPrefix.None); Last; AltCommandLine "pg">] Postgres of ParseResults<Storage.Sql.Pg.Arguments>
    interface IArgParserTemplate with
        member a.Usage = a |> function
            | Stream _ ->                   "Specify stream(s) to dump."
            | Correlation ->                "Include Correlation/Causation identifiers"
            | Blobs ->                      "Don't assume Data/Metadata is UTF-8 text"
            | JsonSkip ->                   "Don't assume Data/Metadata is JSON"
            | Pretty ->                     "Pretty print the JSON over multiple lines"
            | FlattenUnfolds ->             "Don't pretty print the JSON over multiple lines for Unfolds"
            | TimeRegular ->                "Don't humanize time intervals between events"
            | UnfoldsOnly ->                "Exclude Events. Default: show both Events and Unfolds"
            | EventsOnly ->                 "Exclude Unfolds/Snapshots. Default: show both Events and Unfolds."
            | Es _ ->                       "Parameters for EventStore."
            | Cosmos _ ->                   "Parameters for CosmosDB."
            | Dynamo _ ->                   "Parameters for DynamoDB."
            | MsSql _ ->                    "Parameters for Sql Server."
            | MySql _ ->                    "Parameters for MySql."
            | Postgres _ ->                 "Parameters for Postgres."
and DumpInfo(args: ParseResults<DumpArguments>) =
    member _.ConfigureStore(log : ILogger, createStoreLog) =
        let storeConfig = None, true
        match args.TryGetSubCommand() with
        | Some (DumpArguments.Cosmos sargs) ->
            let storeLog = createStoreLog <| sargs.Contains Storage.Cosmos.Arguments.VerboseStore
            storeLog, Storage.Cosmos.config log storeConfig (Storage.Cosmos.Info sargs)
        | Some (DumpArguments.Dynamo sargs) ->
            let storeLog = createStoreLog <| sargs.Contains Storage.Dynamo.Arguments.VerboseStore
            storeLog, Storage.Dynamo.config log storeConfig (Storage.Dynamo.Info sargs)
        | Some (DumpArguments.Es sargs) ->
            let storeLog = createStoreLog <| sargs.Contains Storage.EventStore.Arguments.VerboseStore
            storeLog, Storage.EventStore.config (log, storeLog) storeConfig sargs
        | Some (DumpArguments.MsSql sargs) ->
            let storeLog = createStoreLog false
            storeLog, Storage.Sql.Ms.config log storeConfig sargs
        | Some (DumpArguments.MySql sargs) ->
            let storeLog = createStoreLog false
            storeLog, Storage.Sql.My.config log storeConfig sargs
        | Some (DumpArguments.Postgres sargs) ->
            let storeLog = createStoreLog false
            storeLog, Storage.Sql.Pg.config log storeConfig sargs
        | _ -> failwith "please specify a `cosmos`, `dynamo`, `es`,`ms`,`my` or `pg` endpoint"
and [<NoComparison>]WebArguments =
    | [<AltCommandLine("-u")>] Endpoint of string
    interface IArgParserTemplate with
        member a.Usage = a |> function
            | Endpoint _ ->                 "Target address. Default: https://localhost:5001"
and [<NoComparison; NoEquality>]TestArguments =
    | [<AltCommandLine "-t"; Unique>]       Name of Test
    | [<AltCommandLine "-s">]               Size of int
    | [<AltCommandLine "-C">]               Cached
    | [<AltCommandLine "-U">]               Unfolds
    | [<AltCommandLine "-f">]               TestsPerSecond of int
    | [<AltCommandLine "-d">]               DurationM of float
    | [<AltCommandLine "-e">]               ErrorCutoff of int64
    | [<AltCommandLine "-i">]               ReportIntervalS of int
    | [<CliPrefix(CliPrefix.None); Last>]                      Cosmos   of ParseResults<Storage.Cosmos.Arguments>
    | [<CliPrefix(CliPrefix.None); Last>]                      Dynamo   of ParseResults<Storage.Dynamo.Arguments>
    | [<CliPrefix(CliPrefix.None); Last>]                      Es       of ParseResults<Storage.EventStore.Arguments>
    | [<CliPrefix(CliPrefix.None); Last>]                      Memory   of ParseResults<Storage.MemoryStore.Arguments>
    | [<CliPrefix(CliPrefix.None); Last; AltCommandLine "ms">] MsSql    of ParseResults<Storage.Sql.Ms.Arguments>
    | [<CliPrefix(CliPrefix.None); Last; AltCommandLine "my">] MySql    of ParseResults<Storage.Sql.My.Arguments>
    | [<CliPrefix(CliPrefix.None); Last; AltCommandLine "pg">] Postgres of ParseResults<Storage.Sql.Pg.Arguments>
    | [<CliPrefix(CliPrefix.None); Last>]                      Web      of ParseResults<WebArguments>
    interface IArgParserTemplate with
        member a.Usage = a |> function
            | Name _ ->                     "specify which test to run. (default: Favorite)."
            | Size _ ->                     "For `-t Todo`: specify random title length max size to use (default 100)."
            | Cached ->                     "employ a 50MB cache, wire in to Stream configuration."
            | Unfolds ->                    "employ a store-appropriate Rolling Snapshots and/or Unfolding strategy."
            | TestsPerSecond _ ->           "specify a target number of requests per second (default: 1000)."
            | DurationM _ ->                "specify a run duration in minutes (default: 30)."
            | ErrorCutoff _ ->              "specify an error cutoff; test ends when exceeded (default: 10000)."
            | ReportIntervalS _ ->          "specify reporting intervals in seconds (default: 10)."
            | Es _ ->                       "Run transactions in-process against EventStore."
            | Cosmos _ ->                   "Run transactions in-process against CosmosDB."
            | Dynamo _ ->                   "Run transactions in-process against DynamoDB."
            | Memory _ ->                   "target in-process Transient Memory Store (Default if not other target specified)."
            | MsSql _ ->                    "Run transactions in-process against Sql Server."
            | MySql _ ->                    "Run transactions in-process against MySql."
            | Postgres _ ->                 "Run transactions in-process against Postgres."
            | Web _ ->                      "Run transactions against a Web endpoint."
and TestInfo(args: ParseResults<TestArguments>) =
    member _.Options =                     args.GetResults Cached @ args.GetResults Unfolds
    member x.Cache =                       x.Options |> List.exists (function Cached ->  true | _ -> false)
    member x.Unfolds =                     x.Options |> List.exists (function Unfolds -> true | _ -> false)
    member _.Test =                        args.GetResult(Name, Test.Favorite)
    member _.ErrorCutoff =                 args.GetResult(ErrorCutoff, 10000L)
    member _.TestsPerSecond =              args.GetResult(TestsPerSecond, 1000)
    member _.Duration =                    args.GetResult(DurationM, 30.) |> TimeSpan.FromMinutes
    member x.ReportingIntervals =
        match args.GetResults(ReportIntervalS) with
        | [] -> TimeSpan.FromSeconds 10.|> Seq.singleton
        | intervals -> seq { for i in intervals -> TimeSpan.FromSeconds(float i) }
        |> fun intervals -> [| yield x.Duration; yield! intervals |]
    member x.ConfigureStore(log : ILogger, createStoreLog) =
        let cache = if x.Cache then Equinox.Cache(appName, sizeMb = 50) |> Some else None
        match args.TryGetSubCommand() with
        | Some (Cosmos sargs) ->
            let storeLog = createStoreLog <| sargs.Contains Storage.Cosmos.Arguments.VerboseStore
            log.Information("Running transactions in-process against CosmosDB with storage options: {options:l}", x.Options)
            storeLog, Storage.Cosmos.config log (cache, x.Unfolds) (Storage.Cosmos.Info sargs)
        | Some (Dynamo sargs) ->
            let storeLog = createStoreLog <| sargs.Contains Storage.Dynamo.Arguments.VerboseStore
            log.Information("Running transactions in-process against DynamoDB with storage options: {options:l}", x.Options)
            storeLog, Storage.Dynamo.config log (cache, x.Unfolds) (Storage.Dynamo.Info sargs)
        | Some (Es sargs) ->
            let storeLog = createStoreLog <| sargs.Contains Storage.EventStore.Arguments.VerboseStore
            log.Information("Running transactions in-process against EventStore with storage options: {options:l}", x.Options)
            storeLog, Storage.EventStore.config (log, storeLog) (cache, x.Unfolds) sargs
        | Some (MsSql sargs) ->
            let storeLog = createStoreLog false
            log.Information("Running transactions in-process against MsSql with storage options: {options:l}", x.Options)
            storeLog, Storage.Sql.Ms.config log (cache, x.Unfolds) sargs
        | Some (MySql sargs) ->
            let storeLog = createStoreLog false
            log.Information("Running transactions in-process against MySql with storage options: {options:l}", x.Options)
            storeLog, Storage.Sql.My.config log (cache, x.Unfolds) sargs
        | Some (Postgres sargs) ->
            let storeLog = createStoreLog false
            log.Information("Running transactions in-process against Postgres with storage options: {options:l}", x.Options)
            storeLog, Storage.Sql.Pg.config log (cache, x.Unfolds) sargs
        | _  | Some (Memory _) ->
            log.Warning("Running transactions in-process against Volatile Store with storage options: {options:l}", x.Options)
            createStoreLog false, Storage.MemoryStore.config ()
    member _.Tests =
        match args.GetResult(Name, Favorite) with
        | Favorite ->     Tests.Favorite
        | SaveForLater -> Tests.SaveForLater
        | Todo ->         Tests.Todo (args.GetResult(Size, 100))
and Test = Favorite | SaveForLater | Todo
and CosmosModeType = Container | Db | Serverless

let createStoreLog verbose verboseConsole maybeSeqEndpoint =
    let c = LoggerConfiguration().Destructure.FSharpTypes()
    let c = if verbose then c.MinimumLevel.Debug() else c
    let c = c.WriteTo.Sink(Equinox.CosmosStore.Core.Log.InternalMetrics.Stats.LogSink())
    let c = c.WriteTo.Sink(Equinox.DynamoStore.Core.Log.InternalMetrics.Stats.LogSink())
    let c = c.WriteTo.Sink(Equinox.EventStoreDb.Log.InternalMetrics.Stats.LogSink())
    let c = c.WriteTo.Sink(Equinox.SqlStreamStore.Log.InternalMetrics.Stats.LogSink())
    let level =
        match verbose, verboseConsole with
        | true, true -> LogEventLevel.Debug
        | false, true -> LogEventLevel.Information
        | _ -> LogEventLevel.Warning
    let outputTemplate = "{Timestamp:T} {Level:u1} {Message:l} {Properties}{NewLine}{Exception}"
    let c = c.WriteTo.Console(level, outputTemplate, theme = Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code)
    let c = match maybeSeqEndpoint with None -> c | Some endpoint -> c.WriteTo.Seq(endpoint)
    c.CreateLogger() :> ILogger

let dumpStats storeConfig log =
    match storeConfig with
    | Some (Storage.StorageConfig.Cosmos _) ->
        Equinox.CosmosStore.Core.Log.InternalMetrics.dump log
    | Some (Storage.StorageConfig.Dynamo _) ->
        Equinox.DynamoStore.Core.Log.InternalMetrics.dump log
    | Some (Storage.StorageConfig.Es _) ->
        Equinox.EventStoreDb.Log.InternalMetrics.dump log
    | Some (Storage.StorageConfig.Sql _) ->
        Equinox.SqlStreamStore.Log.InternalMetrics.dump log
    | _ -> ()

module LoadTest =

    open Equinox.Tools.TestHarness

    let private runLoadTest log testsPerSecond duration errorCutoff reportingIntervals (clients : ClientId[]) runSingleTest =
        let mutable idx = -1L
        let selectClient () =
            let clientIndex = Interlocked.Increment(&idx) |> int
            clients[clientIndex % clients.Length]
        let selectClient = async { return async { return selectClient() } }
        Local.runLoadTest log reportingIntervals testsPerSecond errorCutoff duration selectClient runSingleTest
    let private decorateWithLogger (domainLog : ILogger, verbose) (run: 't -> Async<unit>) =
        let execute clientId =
            if not verbose then run clientId
            else async {
                domainLog.Information("Executing for client {sessionId}", clientId)
                try return! run clientId
                with e -> domainLog.Warning(e, "Test threw an exception"); e.Reraise () }
        execute
    let private createResultLog fileName = LoggerConfiguration().WriteTo.File(fileName).CreateLogger()
    let run (log: ILogger) (verbose, verboseConsole, maybeSeq) reportFilename (args: ParseResults<TestArguments>) =
        let createStoreLog verboseStore = createStoreLog verboseStore verboseConsole maybeSeq
        let a = TestInfo args
        let storeLog, storeConfig, httpClient: ILogger * Storage.StorageConfig option * HttpClient option =
            match args.TryGetSubCommand() with
            | Some (Web wargs) ->
                let uri = wargs.GetResult(WebArguments.Endpoint,"https://localhost:5001") |> Uri
                log.Information("Running web test targeting: {url}", uri)
                createStoreLog false, None, new HttpClient(BaseAddress = uri) |> Some
            | _ ->
                let storeLog, storeConfig = a.ConfigureStore(log, createStoreLog)
                storeLog, Some storeConfig, None
        let test, duration = a.Tests, a.Duration
        let runSingleTest : ClientId -> Async<unit> =
            match storeConfig, httpClient with
            | None, Some client ->
                let execForClient = Tests.executeRemote client test
                decorateWithLogger (log, verbose) execForClient
            | Some storeConfig, _ ->
                let services = ServiceCollection()
                Services.register(services, storeConfig, storeLog)
                let container = services.BuildServiceProvider()
                let execForClient = Tests.executeLocal container test
                decorateWithLogger (log, verbose) execForClient
            | None, None -> invalidOp "impossible None, None"
        let clients = Array.init (a.TestsPerSecond * 2) (fun _ -> % Guid.NewGuid())

        let renderedIds = clients |> Seq.map ClientId.toString |> if verboseConsole then id else Seq.truncate 5
        log.ForContext((if verboseConsole then "clientIds" else "clientIdsExcerpt"),renderedIds)
            .Information("Running {test} for {duration} @ {tps} hits/s across {clients} clients; Max errors: {errorCutOff}, reporting intervals: {ri}, report file: {report}",
            test, a.Duration, a.TestsPerSecond, clients.Length, a.ErrorCutoff, a.ReportingIntervals, reportFilename)
        // Reset the start time based on which the shared global metrics will be computed
        let _ = Equinox.CosmosStore.Core.Log.InternalMetrics.Stats.LogSink.Restart()
        let _ = Equinox.DynamoStore.Core.Log.InternalMetrics.Stats.LogSink.Restart()
        let _ = Equinox.EventStoreDb.Log.InternalMetrics.Stats.LogSink.Restart()
        let _ = Equinox.SqlStreamStore.Log.InternalMetrics.Stats.LogSink.Restart()
        let results = runLoadTest log a.TestsPerSecond (duration.Add(TimeSpan.FromSeconds 5.)) a.ErrorCutoff a.ReportingIntervals clients runSingleTest |> Async.RunSynchronously

        let resultFile = createResultLog reportFilename
        for r in results do
            resultFile.Information("Aggregate: {aggregate}", r)
        log.Information("Run completed; Current memory allocation: {bytes:n2} MiB", (GC.GetTotalMemory(true) |> float) / 1024./1024.)
        dumpStats storeConfig log

let createDomainLog verbose verboseConsole maybeSeqEndpoint =
    let c = LoggerConfiguration().Destructure.FSharpTypes().Enrich.FromLogContext()
    let c = if verbose then c.MinimumLevel.Debug() else c
    let c = c.WriteTo.Sink(Equinox.CosmosStore.Core.Log.InternalMetrics.Stats.LogSink())
    let c = c.WriteTo.Sink(Equinox.DynamoStore.Core.Log.InternalMetrics.Stats.LogSink())
    let c = c.WriteTo.Sink(Equinox.EventStoreDb.Log.InternalMetrics.Stats.LogSink())
    let c = c.WriteTo.Sink(Equinox.SqlStreamStore.Log.InternalMetrics.Stats.LogSink())
    let outputTemplate = "{Timestamp:T} {Level:u1} {Message:l} {Properties}{NewLine}{Exception}"
    let c = c.WriteTo.Console((if verboseConsole then LogEventLevel.Debug else LogEventLevel.Information), outputTemplate, theme = Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code)
    let c = match maybeSeqEndpoint with None -> c | Some endpoint -> c.WriteTo.Seq(endpoint)
    c.CreateLogger()

module CosmosInit =

    let connect log (sargs : ParseResults<Storage.Cosmos.Arguments>) =
        Storage.Cosmos.connect log (Storage.Cosmos.Info sargs) |> fst

    let containerAndOrDb log (iargs: ParseResults<InitArguments>) = async {
        match iargs.TryGetSubCommand() with
        | Some (InitArguments.Cosmos sargs) ->
            let skipStoredProc = iargs.Contains InitArguments.SkipStoredProc
            let client, dName, cName = connect log sargs
            let mode = (CosmosInitInfo iargs).ProvisioningMode
            match mode with
            | CosmosInit.Provisioning.Container ru ->
                let modeStr = "Container"
                log.Information("Provisioning `Equinox.CosmosStore` Store at {mode:l} level for {rus:n0} RU/s", modeStr, ru)
            | CosmosInit.Provisioning.Database ru ->
                let modeStr = "Database"
                log.Information("Provisioning `Equinox.CosmosStore` Store at {mode:l} level for {rus:n0} RU/s", modeStr, ru)
            | CosmosInit.Provisioning.Serverless ->
                let modeStr = "Serverless"
                log.Information("Provisioning `Equinox.CosmosStore` Store in {mode:l} mode with automatic RU/s as configured in account", modeStr)
            return! CosmosInit.init log client (dName, cName) mode skipStoredProc
        | _ -> failwith "please specify a `cosmos` endpoint" }

module DynamoInit =

    open Equinox.DynamoStore

    let table (log : ILogger) (args : ParseResults<TableArguments>) = async {
        match args.TryGetSubCommand() with
        | Some (TableArguments.Dynamo sa) ->
            let info = Storage.Dynamo.Info sa
            let client = info.Connector.CreateClient()
            let streaming, throughput = (DynamoInitInfo args).ProvisioningMode
            let tableName = info.Table
            match throughput with
            | Some (rcu, wcu) ->
                log.Information("Provisioning `Equinox.DynamoStore` Table {table} with {read}/{write}CU; streaming {streaming}", tableName, rcu, wcu, streaming)
                do! Core.Initialization.provision client tableName (Throughput.Provisioned (ProvisionedThroughput(rcu, wcu)), streaming)
            | None ->
                log.Information("Provisioning `Equinox.DynamoStore` Table {table} with On-Demand capacity management; streaming {streaming}", tableName, streaming)
                do! Core.Initialization.provision client tableName (Throughput.OnDemand, streaming)
        | _ -> failwith "please specify a `dynamo` endpoint" }

module SqlInit =

    let databaseOrSchema (log: ILogger) (iargs: ParseResults<ConfigArguments>) = async {
        match iargs.TryGetSubCommand() with
        | Some (ConfigArguments.MsSql sargs) ->
            let a = Storage.Sql.Ms.Info(sargs)
            Storage.Sql.Ms.connect log (a.ConnectionString, a.Credentials, a.Schema, true) |> Async.Ignore |> Async.RunSynchronously
        | Some (ConfigArguments.MySql sargs) ->
            let a = Storage.Sql.My.Info(sargs)
            Storage.Sql.My.connect log (a.ConnectionString, a.Credentials, true) |> Async.Ignore |> Async.RunSynchronously
        | Some (ConfigArguments.Postgres sargs) ->
            let a = Storage.Sql.Pg.Info(sargs)
            Storage.Sql.Pg.connect log (a.ConnectionString, a.Credentials, a.Schema, true) |> Async.Ignore |> Async.RunSynchronously
        | _ -> failwith "please specify a `ms`,`my` or `pg` endpoint" }

module CosmosStats =

    type Microsoft.Azure.Cosmos.Container with // NB DO NOT CONSIDER PROMULGATING THIS HACK
        member container.QueryValue<'T>(sqlQuery : string) =
            let query : Microsoft.Azure.Cosmos.FeedResponse<'T> = container.GetItemQueryIterator<'T>(sqlQuery).ReadNextAsync() |> Async.AwaitTaskCorrect |> Async.RunSynchronously
            query |> Seq.exactlyOne
    let run (log : ILogger, _verboseConsole, _maybeSeq) (args : ParseResults<StatsArguments>) = async {
        match args.TryGetSubCommand() with
        | Some (StatsArguments.Cosmos sargs) ->
            let doS, doD, doE = args.Contains StatsArguments.Streams, args.Contains StatsArguments.Documents, args.Contains StatsArguments.Events
            let doS = doS || (not doD && not doE) // default to counting streams only unless otherwise specified
            let inParallel = args.Contains Parallel
            let client, dName, cName = CosmosInit.connect log sargs
            let container = client.GetContainer(dName, cName)
            let ops =
                [   if doS then yield "Streams",   """SELECT VALUE COUNT(1) FROM c WHERE c.id="-1" """
                    if doD then yield "Documents", """SELECT VALUE COUNT(1) FROM c"""
                    if doE then yield "Events",    """SELECT VALUE SUM(c.n) FROM c WHERE c.id="-1" """ ]
            log.Information("Computing {measures} ({mode})", Seq.map fst ops, (if inParallel then "in parallel" else "serially"))
            ops |> Seq.map (fun (name, sql) -> async {
                    log.Debug("Running query: {sql}", sql)
                    let res = container.QueryValue<int>(sql)
                    log.Information("{stat}: {result:N0}", name, res)})
                |> if inParallel then Async.Parallel else Async.Sequential
                |> Async.Ignore
                |> Async.RunSynchronously
        | _ -> failwith "please specify a `cosmos` endpoint" }

module Dump =

    let run (log : ILogger, verboseConsole, maybeSeq) (args : ParseResults<DumpArguments>) =
        let a = DumpInfo args
        let createStoreLog verboseStore = createStoreLog verboseStore verboseConsole maybeSeq
        let storeLog, storeConfig = a.ConfigureStore(log, createStoreLog)
        let doU, doE = not (args.Contains EventsOnly), not (args.Contains UnfoldsOnly)
        let doC, doJ, doS, doT = args.Contains Correlation, not (args.Contains JsonSkip), not (args.Contains Blobs), not (args.Contains TimeRegular)
        let cat = Services.StreamResolver(storeConfig)

        let streams = args.GetResults DumpArguments.Stream
        log.ForContext("streams",streams).Information("Reading...")
        let initial = List.empty
        let fold state events = (events, state) ||> Seq.foldBack (fun e l -> e :: l)
        let tryDecode (x : FsCodec.ITimelineEvent<ReadOnlyMemory<byte>>) = Some x
        let idCodec = FsCodec.Codec.Create((fun _ -> failwith "No encoding required"), tryDecode, (fun _ -> failwith "No mapCausation"))
        let isOriginAndSnapshot = (fun (event : FsCodec.ITimelineEvent<_>) -> not doE && event.IsUnfold), fun _state -> failwith "no snapshot required"
        let formatUnfolds, formatEvents =
            let indentedOptions = FsCodec.SystemTextJson.Options.Create(indent = true)
            let prettify (json : string) =
                use parsed = System.Text.Json.JsonDocument.Parse json
                System.Text.Json.JsonSerializer.Serialize(parsed, indentedOptions)
            if args.Contains FlattenUnfolds then id else prettify
            , if args.Contains Pretty then prettify else id
        let mutable payloadBytes = 0
        let render format (data : ReadOnlyMemory<byte>) =
            payloadBytes <- payloadBytes + data.Length
            if data.IsEmpty then null
            elif not doS then $"%6d{data.Length}b"
            else try let s = System.Text.Encoding.UTF8.GetString data.Span
                     if doJ then try format s
                                 with e -> log.ForContext("str", s).Warning(e, "JSON Parse failure - use --JsonSkip option to inhibit"); reraise()
                     else $"(%d{s.Length} chars)"
                 with e -> log.Warning(e, "UTF-8 Parse failure - use --Blobs option to inhibit"); reraise()
        let readStream (streamName : FsCodec.StreamName) = async {
            let stream = cat.Resolve(idCodec, fold, initial, isOriginAndSnapshot) streamName
            let! token, events = stream.Load(storeLog, allowStale = false)
            let mutable prevTs = None
            for x in events |> Seq.filter (fun e -> (e.IsUnfold && doU) || (not e.IsUnfold && doE)) do
                let ty, render = if x.IsUnfold then "U", render formatUnfolds else "E", render formatEvents
                let interval =
                    match prevTs with Some p when not x.IsUnfold -> Some (x.Timestamp - p) | _ -> None
                    |> function
                    | None -> if doT then "n/a" else "0"
                    | Some (i : TimeSpan) when not doT -> i.ToString()
                    | Some (i : TimeSpan) when i.TotalDays >= 1. -> i.ToString "d\dhh\hmm\m"
                    | Some i when i.TotalHours >= 1. -> i.ToString "h\hmm\mss\s"
                    | Some i when i.TotalMinutes >= 1. -> i.ToString "m\mss\.ff\s"
                    | Some i -> i.ToString("s\.fff\s")
                prevTs <- Some x.Timestamp
                if not doC then log.Information("{i,4}@{t:u}+{d,9} {u:l} {e:l} {data:l} {meta:l}",
                                    x.Index, x.Timestamp, interval, ty, x.EventType, render x.Data, render x.Meta)
                else log.Information("{i,4}@{t:u}+{d,9} Corr {corr} Cause {cause} {u:l} {e:l} {data:l} {meta:l}",
                         x.Index, x.Timestamp, interval, x.CorrelationId, x.CausationId, ty, x.EventType, render x.Data, render x.Meta)
            match token.streamBytes with -1L -> () | x -> log.Information("ISyncContext.StreamEventBytes {kib:n1}KiB", float x / 1024.) }
        streams
        |> Seq.map readStream
        |> Async.Parallel
        |> Async.Ignore<unit[]>
        |> Async.RunSynchronously

        log.Information("Total Event Bodies Payload {kib:n1}KiB", float payloadBytes / 1024.)
        if verboseConsole then
            dumpStats (Some storeConfig) log

[<EntryPoint>]
let main argv =
    let programName = System.Reflection.Assembly.GetEntryAssembly().GetName().Name
    let parser = ArgumentParser.Create<Arguments>(programName = programName)
    try let args = parser.ParseCommandLine argv
        let verboseConsole = args.Contains VerboseConsole
        let maybeSeq = if args.Contains LocalSeq then Some "http://localhost:5341" else None
        let verbose = args.Contains Verbose
        use log = createDomainLog verbose verboseConsole maybeSeq
        try match args.GetSubCommand() with
            | Init iargs -> CosmosInit.containerAndOrDb log iargs |> Async.RunSynchronously
            | InitAws targs -> DynamoInit.table log targs |> Async.RunSynchronously
            | Config cargs -> SqlInit.databaseOrSchema log cargs |> Async.RunSynchronously
            | Dump dargs -> Dump.run (log, verboseConsole, maybeSeq) dargs
            | Stats sargs -> CosmosStats.run (log, verboseConsole, maybeSeq) sargs |> Async.RunSynchronously
            | Run rargs ->
                let reportFilename = args.GetResult(LogFile, programName + ".log") |> fun n -> System.IO.FileInfo(n).FullName
                LoadTest.run log (verbose, verboseConsole, maybeSeq) reportFilename rargs
            | _ -> failwith "Please specify a valid subcommand :- init, initAws, config, dump, stats or run"
            0
        with e -> log.Debug(e, "Fatal error; exiting"); reraise ()
    with :? ArguParseException as e -> eprintfn "%s" e.Message; 1
        | Storage.MissingArg msg -> eprintfn "%s" msg; 1
        | e -> eprintfn "%s" e.Message; 1
