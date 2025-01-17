﻿module Samples.Infrastructure.Storage

open Argu
open Serilog
open System

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type StorageConfig =
    // For MemoryStore, we keep the events as UTF8 arrays - we could use FsCodec.Codec.Box to remove the JSON encoding, which would improve perf but can conceal problems
    | Memory of Equinox.MemoryStore.VolatileStore<ReadOnlyMemory<byte>>
    | Cosmos of Equinox.CosmosStore.CosmosStoreContext * Equinox.CosmosStore.CachingStrategy * unfolds: bool
    | Dynamo of Equinox.DynamoStore.DynamoStoreContext * Equinox.DynamoStore.CachingStrategy * unfolds: bool
    | Es     of Equinox.EventStoreDb.EventStoreContext * Equinox.EventStoreDb.CachingStrategy option * unfolds: bool
    | Sql    of Equinox.SqlStreamStore.SqlStreamStoreContext * Equinox.SqlStreamStore.CachingStrategy option * unfolds: bool

module MemoryStore =
    type [<NoEquality; NoComparison>] Arguments =
        | [<AltCommandLine "-V">]        VerboseStore
        interface IArgParserTemplate with
            member a.Usage = a |> function
                | VerboseStore ->       "Include low level Store logging."
    let config () =
        StorageConfig.Memory (Equinox.MemoryStore.VolatileStore())

let [<Literal>] appName = "equinox-tool"

exception MissingArg of string

let private getEnvVarForArgumentOrThrow varName argName =
    match Environment.GetEnvironmentVariable varName with
    | null -> raise (MissingArg(sprintf "Please provide a %s, either as an argument or via the %s environment variable" argName varName))
    | x -> x
let private defaultWithEnvVar varName argName = function
    | None -> getEnvVarForArgumentOrThrow varName argName
    | Some x -> x

// Standing up an Equinox instance is necessary to run for test purposes; You'll need to either:
// 1) replace connection below with a connection string or Uri+Key for an initialized Equinox instance with a database and collection named "equinox-test"
// 2) Set the 3x environment variables and create a local Equinox using tools/Equinox.Tool/bin/Release/net6.0/eqx.exe `
//     init -ru 1000 cosmos -s $env:EQUINOX_COSMOS_CONNECTION -d $env:EQUINOX_COSMOS_DATABASE -c $env:EQUINOX_COSMOS_CONTAINER
module Cosmos =

    open Equinox.CosmosStore

    type [<NoEquality; NoComparison>] Arguments =
        | [<AltCommandLine "-V">]       VerboseStore
        | [<AltCommandLine "-m">]       ConnectionMode of Microsoft.Azure.Cosmos.ConnectionMode
        | [<AltCommandLine "-o">]       Timeout of float
        | [<AltCommandLine "-r">]       Retries of int
        | [<AltCommandLine "-rt">]      RetriesWaitTimeS of float
        | [<AltCommandLine "-s">]       Connection of string
        | [<AltCommandLine "-d">]       Database of string
        | [<AltCommandLine "-c">]       Container of string
        | [<AltCommandLine "-s2">]      ArchiveConnection of string
        | [<AltCommandLine "-d2">]      ArchiveDatabase of string
        | [<AltCommandLine "-c2">]      ArchiveContainer of string
        | [<AltCommandLine "-te">]      TipMaxEvents of int
        | [<AltCommandLine "-tl">]      TipMaxJsonLength of int
        | [<AltCommandLine "-b">]       QueryMaxItems of int
        interface IArgParserTemplate with
            member a.Usage = a |> function
                | VerboseStore ->       "Include low level Store logging."
                | Timeout _ ->          "specify operation timeout in seconds (default: 5)."
                | Retries _ ->          "specify operation retries (default: 1)."
                | RetriesWaitTimeS _ -> "specify max wait-time for retry when being throttled by Cosmos in seconds (default: 5)"
                | ConnectionMode _ ->   "override the connection mode. Default: Direct."
                | Connection _ ->       "specify a connection string for a Cosmos account. (optional if environment variable EQUINOX_COSMOS_CONNECTION specified)"
                | Database _ ->         "specify a database name for store. (optional if environment variable EQUINOX_COSMOS_DATABASE specified)"
                | Container _ ->        "specify a container name for store. (optional if environment variable EQUINOX_COSMOS_CONTAINER specified)"
                | ArchiveConnection _ ->"specify a connection string for Archive Cosmos account. Default: use same as Primary Connection"
                | ArchiveDatabase _ ->  "specify a database name for Archive store. Default: use same as Primary Database"
                | ArchiveContainer _ -> "specify a container name for Archive store. Default: use same as Primary Container"
                | TipMaxEvents _ ->     "specify maximum number of events to hold in Tip before calving off to a frozen Batch. Default: 256"
                | TipMaxJsonLength _ -> "specify maximum length of JSON (as measured by JSON.stringify) to hold in Tip before calving off to a frozen Batch. Default: 30,000"
                | QueryMaxItems _ ->    "specify maximum number of batches of events to retrieve in per query response. Default: 10"
    type Info(args : ParseResults<Arguments>) =
        member _.Mode =                 args.TryGetResult ConnectionMode
        member _.Connection =           args.TryGetResult Connection |> defaultWithEnvVar "EQUINOX_COSMOS_CONNECTION" "Connection"
        member _.Database =             args.TryGetResult Database   |> defaultWithEnvVar "EQUINOX_COSMOS_DATABASE"   "Database"
        member _.Container =            args.TryGetResult Container  |> defaultWithEnvVar "EQUINOX_COSMOS_CONTAINER"  "Container"
        member private _.ArchiveConnection = args.TryGetResult ArchiveConnection
        member private x.ArchiveDatabase =   args.TryGetResult ArchiveDatabase  |> Option.defaultWith (fun () -> x.Database)
        member private x.ArchiveContainer =  args.TryGetResult ArchiveContainer |> Option.defaultWith (fun () -> x.Container)
        member x.Archive =              if args.Contains ArchiveConnection || args.Contains ArchiveDatabase || args.Contains ArchiveContainer
                                        then Some (x.ArchiveConnection, x.ArchiveDatabase, x.ArchiveContainer)
                                        else None

        member x.Timeout =              args.GetResult(Timeout,5.) |> TimeSpan.FromSeconds
        member x.Retries =              args.GetResult(Retries,1)
        member x.MaxRetryWaitTime =     args.GetResult(RetriesWaitTimeS, 5.) |> TimeSpan.FromSeconds
        member x.TipMaxEvents =         args.GetResult(TipMaxEvents, 256)
        member x.TipMaxJsonLength =     args.GetResult(TipMaxJsonLength, 30_000)
        member x.QueryMaxItems =        args.GetResult(QueryMaxItems, 10)

    let logContainer (log: ILogger) name (mode, endpoint, db, container) =
        log.Information("CosmosDB {name:l} {mode} {connection} Database {database} Container {container}",
                        name, defaultArg mode Microsoft.Azure.Cosmos.ConnectionMode.Direct, endpoint, db, container)

    // NOTE: this is a big song and dance, don't blindly copy!
    // - In normal usage, you typically connect to a single container only.
    // - In hot-warm scenarios, the Archive Container will frequently be within the same account and hence can share a CosmosClient
    // For these typical purposes, CosmosStoreClient.Connect should be used to establish the Client and Connection, not custom wiring as we have here
    let createClient (a : Info) connectionString =
        let connector = CosmosStoreConnector(Discovery.ConnectionString connectionString, a.Timeout, a.Retries, a.MaxRetryWaitTime, ?mode=a.Mode)
        connector.CreateUninitialized()
    let connect (log : ILogger) (a : Info) =
        let primaryClient, primaryDatabase, primaryContainer as primary = createClient a a.Connection, a.Database, a.Container
        logContainer log "Primary" (a.Mode, primaryClient.Endpoint, primaryDatabase, primaryContainer)
        let archive =
            match a.Archive with
            | Some (Some c2, db, container) -> Some (createClient a c2, db, container)
            | Some (None, db, container) -> Some (primaryClient, db, container)
            | None -> None
        archive |> Option.iter (fun (client, db, container) -> logContainer log "Archive" (a.Mode, client.Endpoint, db, container))
        primary, archive
    let config (log : ILogger) (cache, unfolds) (a : Info) =
        let connection =
            match connect log a with
            | (client, databaseId, containerId), None ->
                CosmosStoreClient(client, databaseId, containerId)
            | (client, databaseId, containerId), Some (aClient, aDatabaseId, aContainerId) ->
                CosmosStoreClient(client, databaseId, containerId, archiveClient = aClient, archiveDatabaseId = aDatabaseId, archiveContainerId = aContainerId)
        log.Information("CosmosStore Max Events in Tip: {maxTipEvents}e {maxTipJsonLength}b Items in Query: {queryMaxItems}",
                        a.TipMaxEvents, a.TipMaxJsonLength, a.QueryMaxItems)
        let context = CosmosStoreContext(connection, a.TipMaxEvents, queryMaxItems = a.QueryMaxItems, tipMaxJsonLength = a.TipMaxJsonLength)
        let cacheStrategy = match cache with Some c -> CachingStrategy.SlidingWindow (c, TimeSpan.FromMinutes 20.) | None -> CachingStrategy.NoCaching
        StorageConfig.Cosmos (context, cacheStrategy, unfolds)

module Dynamo =

    open Equinox.DynamoStore
    let [<Literal>] SERVICE_URL =       "EQUINOX_DYNAMO_SERVICE_URL"
    let [<Literal>] ACCESS_KEY =        "EQUINOX_DYNAMO_ACCESS_KEY_ID"
    let [<Literal>] SECRET_KEY =        "EQUINOX_DYNAMO_SECRET_ACCESS_KEY"
    let [<Literal>] TABLE =             "EQUINOX_DYNAMO_TABLE"
    type [<NoEquality; NoComparison>] Arguments =
        | [<AltCommandLine "-V">]       VerboseStore
        | [<AltCommandLine "-s">]       ServiceUrl of string
        | [<AltCommandLine "-sa">]      AccessKey of string
        | [<AltCommandLine "-ss">]      SecretKey of string
        | [<AltCommandLine "-t">]       Table of string
        | [<AltCommandLine "-ta">]      ArchiveTable of string
        | [<AltCommandLine "-r">]       Retries of int
        | [<AltCommandLine "-rt">]      RetriesTimeoutS of float
        | [<AltCommandLine "-tb">]      TipMaxBytes of int
        | [<AltCommandLine "-te">]      TipMaxEvents of int
        | [<AltCommandLine "-b">]       QueryMaxItems of int
        interface IArgParserTemplate with
            member a.Usage = a |> function
                | VerboseStore ->       "Include low level Store logging."
                | ServiceUrl _ ->       "specify a server endpoint for a Dynamo account. (optional if environment variable " + SERVICE_URL + " specified)"
                | AccessKey _ ->        "specify an access key id for a Dynamo account. (optional if environment variable " + ACCESS_KEY + " specified)"
                | SecretKey _ ->        "specify a secret access key for a Dynamo account. (optional if environment variable " + SECRET_KEY + " specified)"
                | Table _ ->            "specify a table name for the primary store. (optional if environment variable " + TABLE + " specified)"
                | ArchiveTable _ ->     "specify a table name for the Archive. Default: Do not attempt to look in an Archive store as a Fallback to locate pruned events."
                | Retries _ ->          "specify operation retries (default: 1)."
                | RetriesTimeoutS _ ->  "specify max wait-time including retries in seconds (default: 5)"
                | TipMaxBytes _ ->      "specify maximum number of bytes to hold in Tip before calving off to a frozen Batch. Default: 32K"
                | TipMaxEvents _ ->     "specify maximum number of events to hold in Tip before calving off to a frozen Batch. Default: limited by Max Bytes"
                | QueryMaxItems _ ->    "specify maximum number of batches of events to retrieve in per query response. Default: 10"
    type Info(args : ParseResults<Arguments>) =
        let serviceUrl =                args.TryGetResult ServiceUrl |> defaultWithEnvVar SERVICE_URL   "ServiceUrl"
        let accessKey =                 args.TryGetResult AccessKey  |> defaultWithEnvVar ACCESS_KEY    "AccessKey"
        let secretKey =                 args.TryGetResult SecretKey  |> defaultWithEnvVar SECRET_KEY    "SecretKey"
        let retries =                   args.GetResult(Retries, 1)
        let timeout =                   args.GetResult(RetriesTimeoutS, 5.) |> TimeSpan.FromSeconds
        member val Connector =          DynamoStoreConnector(serviceUrl, accessKey, secretKey, timeout, retries)

        member val Table =              args.TryGetResult Table      |> defaultWithEnvVar TABLE         "Table"
        member val ArchiveTable =       args.TryGetResult ArchiveTable

        member x.TipMaxEvents =         args.TryGetResult TipMaxEvents
        member x.TipMaxBytes =          args.GetResult(TipMaxBytes, 32 * 1024)
        member x.QueryMaxItems =        args.GetResult(QueryMaxItems, 10)

    let logTable (log: ILogger) endpoint role table =
        log.Information("DynamoStore {name:l} {endpoint} Table {table}", role, endpoint, table)
    let createStoreClient (log : ILogger) (a : Info) =
        let client = a.Connector.CreateClient()
        let storeClient = DynamoStoreClient(client, a.Table, ?archiveTableName = a.ArchiveTable)
        logTable log a.Connector.Endpoint "Primary" a.Table
        match a.ArchiveTable with None -> () | Some at -> logTable log a.Connector.Endpoint "Archive" at
        storeClient
    let config (log : ILogger) (cache, unfolds) (a : Info) =
        let storeClient = createStoreClient log a
        log.Information("DynamoStore Tip thresholds: {maxTipBytes}b {maxTipEvents}e Query Paging {queryMaxItems} items",
                        a.TipMaxBytes, a.TipMaxEvents, a.QueryMaxItems)
        let context = DynamoStoreContext(storeClient, maxBytes = a.TipMaxBytes, queryMaxItems = a.QueryMaxItems, ?tipMaxEvents = a.TipMaxEvents)
        let cacheStrategy = match cache with Some c -> CachingStrategy.SlidingWindow (c, TimeSpan.FromMinutes 20.) | None -> CachingStrategy.NoCaching
        StorageConfig.Dynamo (context, cacheStrategy, unfolds)

/// To establish a local node to run the tests against, follow https://developers.eventstore.com/server/v21.10/installation.html#use-docker-compose
/// and/or do `docker compose up` in github.com/jet/equinox
module EventStore =

    open Equinox.EventStoreDb

    type [<NoEquality; NoComparison>] Arguments =
        | [<AltCommandLine("-V")>]      VerboseStore
        | [<AltCommandLine("-o")>]      Timeout of float
        | [<AltCommandLine("-r")>]      Retries of int
        | [<AltCommandLine("-g")>]      ConnectionString of string
        | [<AltCommandLine("-p")>]      Credentials of string
        | [<AltCommandLine("-c")>]      ConcurrentOperationsLimit of int
        | [<AltCommandLine("-h")>]      HeartbeatTimeout of float
        | [<AltCommandLine("-b")>]      MaxEvents of int
        interface IArgParserTemplate with
            member a.Usage = a |> function
                | VerboseStore ->       "include low level Store logging."
                | Timeout _ ->          "specify operation timeout in seconds (default: 5)."
                | Retries _ ->          "specify operation retries (default: 1)."
                | ConnectionString _ -> "Portion of connection string that's safe to write to console or log. default: esdb://localhost:2111,localhost:2112,localhost:2113?tls=true&tlsVerifyCert=false"
                | Credentials _ ->      "specify a sensitive portion of the connection string that should not be logged. Default: none"
                | ConcurrentOperationsLimit _ -> "max concurrent operations in flight (default: 5000)."
                | HeartbeatTimeout _ -> "specify heartbeat timeout in seconds (default: 1.5)."
                | MaxEvents _ ->        "Maximum number of Events to request per batch. Default 500."
    type Info(args : ParseResults<Arguments>) =
        member _.Host =                 args.GetResult(ConnectionString, "esdb://localhost:2111,localhost:2112,localhost:2113?tls=true&tlsVerifyCert=false")
        member _.Credentials =          args.GetResult(Credentials, null)

        member _.Timeout =              args.GetResult(Timeout,5.) |> TimeSpan.FromSeconds
        member _.Retries =              args.GetResult(Retries, 1)
        member _.HeartbeatTimeout =     args.GetResult(HeartbeatTimeout,1.5) |> float |> TimeSpan.FromSeconds
        member _.ConcurrentOperationsLimit = args.GetResult(ConcurrentOperationsLimit,5000)
        member _.MaxEvents =            args.GetResult(MaxEvents, 500)

    let private connect (log: ILogger) (connectionString, heartbeatTimeout, col) credentialsString (operationTimeout, operationRetries) =
        EventStoreConnector(reqTimeout=operationTimeout, reqRetries=operationRetries,
                // TODO heartbeatTimeout=heartbeatTimeout, concurrentOperationsLimit = col,
                // TODO log=(if log.IsEnabled(Events.LogEventLevel.Debug) then Logger.SerilogVerbose log else Logger.SerilogNormal log),
                tags=["M", Environment.MachineName; "I", Guid.NewGuid() |> string])
            .Establish(appName, Discovery.ConnectionString (String.Join(";", connectionString, credentialsString)), ConnectionStrategy.ClusterTwinPreferSlaveReads)
    let private createContext connection batchSize = EventStoreContext(connection, BatchingPolicy(maxBatchSize = batchSize))
    let config (log: ILogger, storeLog) (cache, unfolds) (args : ParseResults<Arguments>) =
        let a = Info(args)
        let timeout, retries as operationThrottling = a.Timeout, a.Retries
        let heartbeatTimeout = a.HeartbeatTimeout
        let concurrentOperationsLimit = a.ConcurrentOperationsLimit
        log.Information("EventStoreDB {host} heartbeat: {heartbeat}s timeout: {timeout}s concurrent reqs: {concurrency} retries {retries}",
            a.Host, heartbeatTimeout.TotalSeconds, timeout.TotalSeconds, concurrentOperationsLimit, retries)
        let connection = connect storeLog (a.Host, heartbeatTimeout, concurrentOperationsLimit) a.Credentials operationThrottling
        let cacheStrategy = cache |> Option.map (fun c -> CachingStrategy.SlidingWindow (c, TimeSpan.FromMinutes 20.))
        StorageConfig.Es ((createContext connection a.MaxEvents), cacheStrategy, unfolds)

module Sql =

    open Equinox.SqlStreamStore

    let cacheStrategy cache = cache |> Option.map (fun c -> CachingStrategy.SlidingWindow (c, TimeSpan.FromMinutes 20.))
    module Ms =
        type [<NoEquality; NoComparison>] Arguments =
            | [<AltCommandLine "-c"; Mandatory>] ConnectionString of string
            | [<AltCommandLine "-s">]       Schema of string
            | [<AltCommandLine "-p">]       Credentials of string
            | [<AltCommandLine "-A">]       AutoCreate
            | [<AltCommandLine "-b">]       MaxEvents of int
            interface IArgParserTemplate with
                member a.Usage = a |> function
                    | ConnectionString _ -> "Database connection string"
                    | Schema _ ->           "Database schema name"
                    | Credentials _ ->      "Database credentials"
                    | AutoCreate _ ->       "AutoCreate schema"
                    | MaxEvents _ ->        "Maximum number of Events to request per batch. Default 500."
        type Info(args : ParseResults<Arguments>) =
            member _.ConnectionString =     args.GetResult ConnectionString
            member _.Schema =               args.GetResult(Schema,null)
            member _.Credentials =          args.GetResult(Credentials,null)
            member _.AutoCreate =           args.Contains AutoCreate
            member _.MaxEvents =            args.GetResult(MaxEvents, 500)
        let connect (log : ILogger) (connectionString,schema,credentials,autoCreate) =
            let sssConnectionString = String.Join(";", connectionString, credentials)
            log.Information("SqlStreamStore MsSql Connection {connectionString} Schema {schema} AutoCreate {autoCreate}", connectionString, schema, autoCreate)
            Equinox.SqlStreamStore.MsSql.Connector(sssConnectionString,schema,autoCreate=autoCreate).Establish()
        let private createContext connection batchSize = SqlStreamStoreContext(connection, BatchingPolicy(maxBatchSize = batchSize))
        let config (log: ILogger) (cache, unfolds) (args : ParseResults<Arguments>) =
            let a = Info(args)
            let connection = connect log (a.ConnectionString, a.Schema, a.Credentials, a.AutoCreate) |> Async.RunSynchronously
            StorageConfig.Sql((createContext connection a.MaxEvents), cacheStrategy cache, unfolds)
    module My =
        type [<NoEquality; NoComparison>] Arguments =
            | [<AltCommandLine "-c"; Mandatory>] ConnectionString of string
            | [<AltCommandLine "-p">]       Credentials of string
            | [<AltCommandLine "-A">]       AutoCreate
            | [<AltCommandLine "-b">]       MaxEvents of int
            interface IArgParserTemplate with
                member a.Usage = a |> function
                    | ConnectionString _ -> "Database connection string"
                    | Credentials _ ->      "Database credentials"
                    | AutoCreate _ ->       "AutoCreate schema"
                    | MaxEvents _ ->        "Maximum number of Events to request per batch. Default 500."
        type Info(args : ParseResults<Arguments>) =
            member _.ConnectionString =     args.GetResult ConnectionString
            member _.Credentials =          args.GetResult(Credentials,null)
            member _.AutoCreate =           args.Contains AutoCreate
            member _.MaxEvents =            args.GetResult(MaxEvents, 500)
        let connect (log : ILogger) (connectionString,credentials,autoCreate) =
            let sssConnectionString = String.Join(";", connectionString, credentials)
            log.Information("SqlStreamStore MySql Connection {connectionString} AutoCreate {autoCreate}", connectionString, autoCreate)
            Equinox.SqlStreamStore.MySql.Connector(sssConnectionString,autoCreate=autoCreate).Establish()
        let private createContext connection batchSize = SqlStreamStoreContext(connection, BatchingPolicy(maxBatchSize = batchSize))
        let config (log: ILogger) (cache, unfolds) (args : ParseResults<Arguments>) =
            let a = Info(args)
            let connection = connect log (a.ConnectionString, a.Credentials, a.AutoCreate) |> Async.RunSynchronously
            StorageConfig.Sql((createContext connection a.MaxEvents), cacheStrategy cache, unfolds)
     module Pg =
        type [<NoEquality; NoComparison>] Arguments =
            | [<AltCommandLine "-c"; Mandatory>] ConnectionString of string
            | [<AltCommandLine "-s">]       Schema of string
            | [<AltCommandLine "-p">]       Credentials of string
            | [<AltCommandLine "-A">]       AutoCreate
            | [<AltCommandLine "-b">]       MaxEvents of int
            interface IArgParserTemplate with
                member a.Usage = a |> function
                    | ConnectionString _ -> "Database connection string"
                    | Schema _ ->           "Database schema name"
                    | Credentials _ ->      "Database credentials"
                    | AutoCreate _ ->       "AutoCreate schema"
                    | MaxEvents _ ->        "Maximum number of Events to request per batch. Default 500."
        type Info(args : ParseResults<Arguments>) =
            member _.ConnectionString =     args.GetResult ConnectionString
            member _.Schema =               args.GetResult(Schema,null)
            member _.Credentials =          args.GetResult(Credentials,null)
            member _.AutoCreate =           args.Contains AutoCreate
            member _.MaxEvents =            args.GetResult(MaxEvents, 500)
        let connect (log : ILogger) (connectionString,schema,credentials,autoCreate) =
            let sssConnectionString = String.Join(";", connectionString, credentials)
            log.Information("SqlStreamStore Postgres Connection {connectionString} Schema {schema} AutoCreate {autoCreate}", connectionString, schema, autoCreate)
            Equinox.SqlStreamStore.Postgres.Connector(sssConnectionString,schema,autoCreate=autoCreate).Establish()
        let private createContext connection batchSize = SqlStreamStoreContext(connection, BatchingPolicy(maxBatchSize = batchSize))
        let config (log: ILogger) (cache, unfolds) (args : ParseResults<Arguments>) =
            let a = Info(args)
            let connection = connect log (a.ConnectionString, a.Schema, a.Credentials, a.AutoCreate) |> Async.RunSynchronously
            StorageConfig.Sql((createContext connection a.MaxEvents), cacheStrategy cache, unfolds)
