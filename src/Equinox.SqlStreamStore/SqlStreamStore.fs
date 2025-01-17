﻿namespace Equinox.SqlStreamStore

open Equinox.Core
open Serilog
open System
open SqlStreamStore
open SqlStreamStore.Streams

type EventBody = ReadOnlyMemory<byte>
type EventData = NewStreamMessage
type IEventStoreConnection = IStreamStore
type ResolvedEvent = StreamMessage
type StreamEventsSlice = ReadStreamPage

[<RequireQualifiedAccess>]
type Direction = Forward | Backward with
    override this.ToString() = match this with Forward -> "Forward" | Backward -> "Backward"

module Log =

    /// <summary>Name of Property used for <c>Metric</c> in <c>LogEvent</c>s.</summary>
    let [<Literal>] PropertyTag = "ssEvt"

    [<NoEquality; NoComparison>]
    type Measurement = { stream : string; interval : StopwatchInterval; bytes : int; count : int }
    [<NoEquality; NoComparison>]
    type Metric =
        | WriteSuccess of Measurement
        | WriteConflict of Measurement
        | Slice of Direction * Measurement
        | Batch of Direction * slices : int * Measurement
    let prop name value (log : ILogger) = log.ForContext(name, value)
    let propEvents name (kvps : System.Collections.Generic.KeyValuePair<string, string> seq) (log : ILogger) =
        let items = seq { for kv in kvps do yield sprintf "{\"%s\": %s}" kv.Key kv.Value }
        log.ForContext(name, sprintf "[%s]" (String.concat ",\n\r" items))
    let propEventData name (events : EventData[]) (log : ILogger) =
        log |> propEvents name (seq {
            for x in events do
                yield System.Collections.Generic.KeyValuePair<_, _>(x.Type, x.JsonData) })
    let propResolvedEvents name (events : ResolvedEvent[]) (log : ILogger) =
        log |> propEvents name (seq {
            for x in events do
                let data = x.GetJsonData() |> Async.AwaitTask |> Async.RunSynchronously
                yield System.Collections.Generic.KeyValuePair<_, _>(x.Type, data) })

    open Serilog.Events
    /// Attach a property to the log context to hold the metrics
    // Sidestep Log.ForContext converting to a string; see https://github.com/serilog/serilog/issues/1124
    let event (value : Metric) (log : ILogger) =
        let enrich (e : LogEvent) = e.AddPropertyIfAbsent(LogEventProperty(PropertyTag, ScalarValue(value)))
        log.ForContext({ new Serilog.Core.ILogEventEnricher with member _.Enrich(evt, _) = enrich evt })
    let withLoggedRetries<'t> retryPolicy (contextLabel : string) (f : ILogger -> Async<'t>) log : Async<'t> =
        match retryPolicy with
        | None -> f log
        | Some retryPolicy ->
            let withLoggingContextWrapping count =
                let log = if count = 1 then log else log |> prop contextLabel count
                f log
            retryPolicy withLoggingContextWrapping
    let (|BlobLen|) = function null -> 0 | (x : byte[]) -> x.Length
    let (|StrLen|) = function null -> 0 | (x : string) -> x.Length

    /// NB Caveat emptor; this is subject to unlimited change without the major version changing - while the `dotnet-templates` repo will be kept in step, and
    /// the ChangeLog will mention changes, it's critical to not assume that the presence or nature of these helpers be considered stable
    module InternalMetrics =

        module Stats =
            let inline (|Stats|) ({ interval = i } : Measurement) = let e = i.Elapsed in int64 e.TotalMilliseconds

            let (|Read|Write|Resync|Rollup|) = function
                | Slice (_, Stats s) -> Read s
                | WriteSuccess (Stats s) -> Write s
                | WriteConflict (Stats s) -> Resync s
                // slices are rolled up into batches so be sure not to double-count
                | Batch (_, _, Stats s) -> Rollup s
            let (|SerilogScalar|_|) : LogEventPropertyValue -> obj option = function
                | :? ScalarValue as x -> Some x.Value
                | _ -> None
            let (|EsMetric|_|) (logEvent : LogEvent) : Metric option =
                match logEvent.Properties.TryGetValue(PropertyTag) with
                | true, SerilogScalar (:? Metric as e) -> Some e
                | _ -> None
            type Counter =
                { mutable count : int64; mutable ms : int64 }
                static member Create() = { count = 0L; ms = 0L }
                member x.Ingest(ms) =
                    System.Threading.Interlocked.Increment(&x.count) |> ignore
                    System.Threading.Interlocked.Add(&x.ms, ms) |> ignore
            type LogSink() =
                static let epoch = System.Diagnostics.Stopwatch.StartNew()
                static member val Read = Counter.Create() with get, set
                static member val Write = Counter.Create() with get, set
                static member val Resync = Counter.Create() with get, set
                static member Restart() =
                    LogSink.Read <- Counter.Create()
                    LogSink.Write <- Counter.Create()
                    LogSink.Resync <- Counter.Create()
                    let span = epoch.Elapsed
                    epoch.Restart()
                    span
                interface Serilog.Core.ILogEventSink with
                    member _.Emit logEvent = logEvent |> function
                        | EsMetric (Read stats) -> LogSink.Read.Ingest stats
                        | EsMetric (Write stats) -> LogSink.Write.Ingest stats
                        | EsMetric (Resync stats) -> LogSink.Resync.Ingest stats
                        | EsMetric (Rollup _) -> ()
                        | _ -> ()

        /// Relies on feeding of metrics from Log through to Stats.LogSink
        /// Use Stats.LogSink.Restart() to reset the start point (and stats) where relevant
        let dump (log : ILogger) =
            let stats =
              [ "Read", Stats.LogSink.Read
                "Write", Stats.LogSink.Write
                "Resync", Stats.LogSink.Resync ]
            let logActivity name count lat =
                log.Information("{name}: {count:n0} requests; Average latency: {lat:n0}ms",
                    name, count, (if count = 0L then Double.NaN else float lat/float count))
            let mutable rows, totalCount, totalMs = 0, 0L, 0L
            for name, stat in stats do
                if stat.count <> 0L then
                    totalCount <- totalCount + stat.count
                    totalMs <- totalMs + stat.ms
                    logActivity name stat.count stat.ms
                    rows <- rows + 1
            // Yes, there's a minor race here between the use of the values and the reset
            let duration = Stats.LogSink.Restart()
            if rows > 1 then logActivity "TOTAL" totalCount totalMs
            let measures : (string * (TimeSpan -> float)) list = [ "s", fun x -> x.TotalSeconds(*; "m", fun x -> x.TotalMinutes; "h", fun x -> x.TotalHours*) ]
            let logPeriodicRate name count = log.Information("rp{name} {count:n0}", name, count)
            for uom, f in measures do let d = f duration in if d <> 0. then logPeriodicRate uom (float totalCount/d |> int64)

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type EsSyncResult = Written of AppendResult | ConflictUnknown

module private Write =
    /// Yields `EsSyncResult.Written` or `EsSyncResult.Conflict` to signify WrongExpectedVersion
    let private writeEventsAsync (log : ILogger) (conn : IEventStoreConnection) (streamName : string) (version : int64) (events : EventData[])
        : Async<EsSyncResult> = async {
        try let! wr = conn.AppendToStream(StreamId streamName, (if version = -1L then ExpectedVersion.NoStream else int version), events) |> Async.AwaitTaskCorrect
            return EsSyncResult.Written wr
        with :? WrongExpectedVersionException as ex ->
            log.Information(ex, "SqlEs TrySync WrongExpectedVersionException writing {EventTypes}, expected {ExpectedVersion}",
                [| for x in events -> x.Type |], version)
            return EsSyncResult.ConflictUnknown }
    let eventDataBytes events =
        let eventDataLen (x : NewStreamMessage) = match x.JsonData |> System.Text.Encoding.UTF8.GetBytes, x.JsonMetadata |> System.Text.Encoding.UTF8.GetBytes with Log.BlobLen bytes, Log.BlobLen metaBytes -> bytes + metaBytes
        events |> Array.sumBy eventDataLen
    let private writeEventsLogged (conn : IEventStoreConnection) (streamName : string) (version : int64) (events : EventData[]) (log : ILogger)
        : Async<EsSyncResult> = async {
        let log = if (not << log.IsEnabled) Events.LogEventLevel.Debug then log else log |> Log.propEventData "Json" events
        let bytes, count = eventDataBytes events, events.Length
        let log = log |> Log.prop "bytes" bytes
        let writeLog = log |> Log.prop "stream" streamName |> Log.prop "expectedVersion" version |> Log.prop "count" count
        let! t, result = writeEventsAsync writeLog conn streamName version events |> Stopwatch.Time
        let reqMetric : Log.Measurement = { stream = streamName; interval = t; bytes = bytes; count = count}
        let resultLog, evt =
            match result, reqMetric with
            | EsSyncResult.ConflictUnknown, m ->
                log, Log.WriteConflict m
            | EsSyncResult.Written x, m ->
                log |> Log.prop "currentVersion" x.CurrentVersion |> Log.prop "currentPosition" x.CurrentPosition, Log.WriteSuccess m
        (resultLog |> Log.event evt).Information("SqlEs{action:l} count={count} conflict={conflict}",
            "Write", events.Length, match evt with Log.WriteConflict _ -> true | _ -> false)
        return result }
    let writeEvents (log : ILogger) retryPolicy (conn : IEventStoreConnection) (streamName : string) (version : int64) (events : EventData[])
        : Async<EsSyncResult> =
        let call = writeEventsLogged conn streamName version events
        Log.withLoggedRetries retryPolicy "writeAttempt" call log

module private Read =
    open FSharp.Control
    let private readSliceAsync (conn : IEventStoreConnection) (streamName : string) (direction : Direction) (batchSize : int) (startPos : int64)
        : Async<StreamEventsSlice> = async {
        let call =
            match direction with
            | Direction.Forward ->  conn.ReadStreamForwards(streamName, int startPos, batchSize)
            | Direction.Backward -> conn.ReadStreamBackwards(streamName, int startPos, batchSize)
        return! call |> Async.AwaitTaskCorrect }
    let (|ResolvedEventLen|) (x : StreamMessage) =
        let data = x.GetJsonData() |> Async.AwaitTaskCorrect |> Async.RunSynchronously
        match data, x.JsonMetadata with Log.StrLen bytes, Log.StrLen metaBytes -> bytes + metaBytes
    let private loggedReadSlice conn streamName direction batchSize startPos (log : ILogger) : Async<ReadStreamPage> = async {
        let! t, slice = readSliceAsync conn streamName direction batchSize startPos |> Stopwatch.Time
        let bytes, count = slice.Messages |> Array.sumBy (|ResolvedEventLen|), slice.Messages.Length
        let reqMetric : Log.Measurement ={ stream = streamName; interval = t; bytes = bytes; count = count}
        let evt = Log.Slice (direction, reqMetric)
        let log = if (not << log.IsEnabled) Events.LogEventLevel.Debug then log else log |> Log.propResolvedEvents "Json" slice.Messages
        (log |> Log.prop "startPos" startPos |> Log.prop "bytes" bytes |> Log.event evt).Information("SqlEs{action:l} count={count} version={version}",
            "Read", count, slice.LastStreamVersion)
        return slice }
    let private readBatches (log : ILogger) (readSlice : int64 -> ILogger -> Async<StreamEventsSlice>)
            (maxPermittedBatchReads : int option) (startPosition : int64)
        : AsyncSeq<int64 option * ResolvedEvent[]> =
        let rec loop batchCount pos : AsyncSeq<int64 option * ResolvedEvent[]> = asyncSeq {
            match maxPermittedBatchReads with
            | Some mpbr when batchCount >= mpbr -> log.Information "batch Limit exceeded"; invalidOp "batch Limit exceeded"
            | _ -> ()

            let batchLog = log |> Log.prop "batchIndex" batchCount
            let! slice = readSlice pos batchLog
            match slice.Status with
            | PageReadStatus.StreamNotFound -> yield Some (int64 ExpectedVersion.EmptyStream), Array.empty // NB NoStream in ES version= -1
            | PageReadStatus.Success ->
                let version = if batchCount = 0 then Some (int64 slice.LastStreamVersion) else None
                yield version, slice.Messages
                if not slice.IsEnd then
                    yield! loop (batchCount + 1) (int64 slice.NextStreamVersion)
            | x -> raise <| ArgumentOutOfRangeException("SliceReadStatus", x, "Unknown result value") }
        loop 0 startPosition
    let resolvedEventBytes events = events |> Array.sumBy (|ResolvedEventLen|)
    let logBatchRead direction streamName t events batchSize version (log : ILogger) =
        let bytes, count = resolvedEventBytes events, events.Length
        let reqMetric : Log.Measurement = { stream = streamName; interval = t; bytes = bytes; count = count}
        let batches = (events.Length - 1)/batchSize + 1
        let action = match direction with Direction.Forward -> "LoadF" | Direction.Backward -> "LoadB"
        let evt = Log.Metric.Batch (direction, batches, reqMetric)
        (log |> Log.prop "bytes" bytes |> Log.event evt).Information(
            "SqlEs{action:l} stream={stream} count={count}/{batches} version={version}",
            action, streamName, count, batches, version)
    let loadForwardsFrom (log : ILogger) retryPolicy conn batchSize maxPermittedBatchReads streamName startPosition
        : Async<int64 * ResolvedEvent[]> = async {
        let mergeBatches (batches : AsyncSeq<int64 option * ResolvedEvent[]>) = async {
            let mutable versionFromStream = None
            let! (events : ResolvedEvent[]) =
                batches
                |> AsyncSeq.map (function None, events -> events | Some _ as reportedVersion, events -> versionFromStream <- reportedVersion; events)
                |> AsyncSeq.concatSeq
                |> AsyncSeq.toArrayAsync
            let version = match versionFromStream with Some version -> version | None -> invalidOp "no version encountered in event batch stream"
            return version, events }
        let call pos = loggedReadSlice conn streamName Direction.Forward batchSize pos
        let retryingLoggingReadSlice pos = Log.withLoggedRetries retryPolicy "readAttempt" (call pos)
        let direction = Direction.Forward
        let log = log |> Log.prop "batchSize" batchSize |> Log.prop "direction" direction |> Log.prop "stream" streamName
        let batches : AsyncSeq<int64 option * ResolvedEvent[]> = readBatches log retryingLoggingReadSlice maxPermittedBatchReads startPosition
        let! t, (version, events) = mergeBatches batches |> Stopwatch.Time
        log |> logBatchRead direction streamName t events batchSize version
        return version, events }
    let partitionPayloadFrom firstUsedEventNumber : ResolvedEvent[] -> int * int =
        let acc (tu, tr) (ResolvedEventLen bytes as y) = if y.Position < firstUsedEventNumber then tu, tr + bytes else tu + bytes, tr
        Array.fold acc (0, 0)
    let loadBackwardsUntilCompactionOrStart (log : ILogger) retryPolicy conn batchSize maxPermittedBatchReads streamName (tryDecode, isOrigin)
        : Async<int64 * (ResolvedEvent * 'event option)[]> = async {
        let mergeFromCompactionPointOrStartFromBackwardsStream (log : ILogger) (batchesBackward : AsyncSeq<int64 option * ResolvedEvent[]>)
            : Async<int64 * (ResolvedEvent*'event option)[]> = async {
            let versionFromStream, lastBatch = ref None, ref None
            let! tempBackward =
                batchesBackward
                |> AsyncSeq.map (fun batch ->
                    match batch with
                    | None, events -> lastBatch.Value <- Some events; events
                    | Some _ as reportedVersion, events -> versionFromStream.Value <- reportedVersion; lastBatch.Value <- Some events; events
                    |> Array.map (fun e -> e, tryDecode e))
                |> AsyncSeq.concatSeq
                |> AsyncSeq.takeWhileInclusive (function
                    | x, Some e when isOrigin e ->
                        match lastBatch.Value with
                        | None -> log.Information("SqlEsStop stream={stream} at={eventNumber}", streamName, x.Position)
                        | Some batch ->
                            let used, residual = batch |> partitionPayloadFrom x.Position
                            log.Information("SqlEsStop stream={stream} at={eventNumber} used={used} residual={residual}", streamName, x.Position, used, residual)
                        false
                    | _ -> true) // continue the search
                |> AsyncSeq.toArrayAsync
            let eventsForward = Array.Reverse(tempBackward); tempBackward // sic - relatively cheap, in-place reverse of something we own
            let version = match versionFromStream.Value with Some version -> version | None -> invalidOp "no version encountered in event batch stream"
            return version, eventsForward }
        let call pos = loggedReadSlice conn streamName Direction.Backward batchSize pos
        let retryingLoggingReadSlice pos = Log.withLoggedRetries retryPolicy "readAttempt" (call pos)
        let log = log |> Log.prop "batchSize" batchSize |> Log.prop "stream" streamName
        let startPosition = int64 Position.End
        let direction = Direction.Backward
        let readlog = log |> Log.prop "direction" direction
        let batchesBackward : AsyncSeq<int64 option * ResolvedEvent[]> = readBatches readlog retryingLoggingReadSlice maxPermittedBatchReads startPosition
        let! t, (version, events) = mergeFromCompactionPointOrStartFromBackwardsStream log batchesBackward |> Stopwatch.Time
        log |> logBatchRead direction streamName t (Array.map fst events) batchSize version
        return version, events }

module UnionEncoderAdapters =

    let (|Bytes|) = function null -> null | (s : string) -> System.Text.Encoding.UTF8.GetBytes s
    let encodedEventOfResolvedEvent (e : StreamMessage) : FsCodec.ITimelineEvent<EventBody> =
        let (Bytes data) = e.GetJsonData() |> Async.AwaitTaskCorrect |> Async.RunSynchronously
        let (Bytes meta) = e.JsonMetadata
        // TOCONSIDER wire x.CorrelationId, x.CausationId into x.Meta.["$correlationId"] and .["$causationId"]
        // https://eventstore.org/docs/server/metadata-and-reserved-names/index.html#event-metadata
        FsCodec.Core.TimelineEvent.Create(int64 e.StreamVersion, e.Type, data, meta, e.MessageId, null, null, let ts = e.CreatedUtc in DateTimeOffset ts)
    let eventDataOfEncodedEvent (x : FsCodec.IEventData<EventBody>) =
        // SQLStreamStore rejects IsNullOrEmpty data value.
        // TODO: Follow up on inconsistency with ES
        let mapData (x : EventBody) = if x.IsEmpty then "{}" else System.Text.Encoding.UTF8.GetString(x.Span)
        let mapMeta (x : EventBody) = if x.IsEmpty then null else System.Text.Encoding.UTF8.GetString(x.Span)
        // TOCONSIDER wire x.CorrelationId, x.CausationId into x.Meta.["$correlationId"] and .["$causationId"]
        // https://eventstore.org/docs/server/metadata-and-reserved-names/index.html#event-metadata
        NewStreamMessage(x.EventId, x.EventType, mapData x.Data, mapMeta x.Meta)

type Position = { streamVersion : int64; compactionEventNumber : int64 option; batchCapacityLimit : int option }
type Token = { pos : Position }

module Token =
    let private create compactionEventNumber batchCapacityLimit streamVersion : StreamToken =
        {   value = box { pos = { streamVersion = streamVersion; compactionEventNumber = compactionEventNumber; batchCapacityLimit = batchCapacityLimit } }
            // In this impl, the StreamVersion matches the SqlStreamStore (and EventStore) StreamVersion in being -1-based
            // Version however is the representation that needs to align with ISyncContext.Version
            version = streamVersion + 1L
            // TOCONSIDER Could implement accumulating the size as it's loaded (but should stop counting when it hits the origin event)
            streamBytes = -1 }
    /// No batching / compaction; we only need to retain the StreamVersion
    let ofNonCompacting streamVersion : StreamToken =
        create None None streamVersion
    // headroom before compaction is necessary given the stated knowledge of the last (if known) `compactionEventNumberOption`
    let private batchCapacityLimit compactedEventNumberOption unstoredEventsPending (batchSize : int) (streamVersion : int64) : int =
        match compactedEventNumberOption with
        | Some (compactionEventNumber : int64) -> (batchSize - unstoredEventsPending) - int (streamVersion - compactionEventNumber + 1L) |> max 0
        | None -> (batchSize - unstoredEventsPending) - (int streamVersion + 1) - 1 |> max 0
    let (*private*) ofCompactionEventNumber compactedEventNumberOption unstoredEventsPending batchSize streamVersion : StreamToken =
        let batchCapacityLimit = batchCapacityLimit compactedEventNumberOption unstoredEventsPending batchSize streamVersion
        create compactedEventNumberOption (Some batchCapacityLimit) streamVersion
    /// Assume we have not seen any compaction events; use the batchSize and version to infer headroom
    let ofUncompactedVersion batchSize streamVersion : StreamToken =
        ofCompactionEventNumber None 0 batchSize streamVersion
    let (|Unpack|) (x : StreamToken) : Position = let t = unbox<Token> x.value in t.pos
    /// Use previousToken plus the data we are adding and the position we are adding it to infer a headroom
    let ofPreviousTokenAndEventsLength (Unpack previousToken) eventsLength batchSize streamVersion : StreamToken =
        let compactedEventNumber = previousToken.compactionEventNumber
        ofCompactionEventNumber compactedEventNumber eventsLength batchSize streamVersion
    /// Use an event just read from the stream to infer headroom
    let ofCompactionResolvedEventAndVersion (compactionEvent : ResolvedEvent) batchSize streamVersion : StreamToken =
        ofCompactionEventNumber (compactionEvent.StreamVersion |> int64 |> Some) 0 batchSize streamVersion
    /// Use an event we are about to write to the stream to infer headroom
    let ofPreviousStreamVersionAndCompactionEventDataIndex (Unpack token) compactionEventDataIndex eventsLength batchSize streamVersion' : StreamToken =
        ofCompactionEventNumber (Some (token.streamVersion + 1L + int64 compactionEventDataIndex)) eventsLength batchSize streamVersion'
    let supersedes (Unpack current) (Unpack x) =
        let currentVersion, newVersion = current.streamVersion, x.streamVersion
        newVersion > currentVersion

type SqlStreamStoreConnection(readConnection, [<O; D(null)>]?writeConnection, [<O; D(null)>]?readRetryPolicy, [<O; D(null)>]?writeRetryPolicy) =
    member _.ReadConnection = readConnection
    member _.ReadRetryPolicy = readRetryPolicy
    member _.WriteConnection = defaultArg writeConnection readConnection
    member _.WriteRetryPolicy = writeRetryPolicy

type BatchingPolicy(getMaxBatchSize : unit -> int, [<O; D(null)>]?batchCountLimit) =
    new (maxBatchSize) = BatchingPolicy(fun () -> maxBatchSize)
    member _.BatchSize = getMaxBatchSize()
    member _.MaxBatches = batchCountLimit

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type GatewaySyncResult = Written of StreamToken | ConflictUnknown

type SqlStreamStoreContext(connection : SqlStreamStoreConnection, batching : BatchingPolicy) =
    let isResolvedEventEventType (tryDecode, predicate) (e : StreamMessage) =
        let data = e.GetJsonData() |> Async.AwaitTask |> Async.RunSynchronously
        predicate (tryDecode data)
    let tryIsResolvedEventEventType predicateOption = predicateOption |> Option.map isResolvedEventEventType
    member _.TokenEmpty = Token.ofUncompactedVersion batching.BatchSize -1L
    member _.LoadBatched streamName log (tryDecode, isCompactionEventType) : Async<StreamToken * 'event[]> = async {
        let! version, events = Read.loadForwardsFrom log connection.ReadRetryPolicy connection.ReadConnection batching.BatchSize batching.MaxBatches streamName 0L
        match tryIsResolvedEventEventType isCompactionEventType with
        | None -> return Token.ofNonCompacting version, Array.choose tryDecode events
        | Some isCompactionEvent ->
            match events |> Array.tryFindBack isCompactionEvent with
            | None -> return Token.ofUncompactedVersion batching.BatchSize version, Array.choose tryDecode events
            | Some resolvedEvent -> return Token.ofCompactionResolvedEventAndVersion resolvedEvent batching.BatchSize version, Array.choose tryDecode events }
    member _.LoadBackwardsStoppingAtCompactionEvent streamName log (tryDecode, isOrigin) : Async<StreamToken * 'event []> = async {
        let! version, events =
            Read.loadBackwardsUntilCompactionOrStart log connection.ReadRetryPolicy connection.ReadConnection batching.BatchSize batching.MaxBatches streamName (tryDecode, isOrigin)
        match Array.tryHead events |> Option.filter (function _, Some e -> isOrigin e | _ -> false) with
        | None -> return Token.ofUncompactedVersion batching.BatchSize version, Array.choose snd events
        | Some (resolvedEvent, _) -> return Token.ofCompactionResolvedEventAndVersion resolvedEvent batching.BatchSize version, Array.choose snd events }
    member _.LoadFromToken useWriteConn streamName log (Token.Unpack token as streamToken) (tryDecode, isCompactionEventType)
        : Async<StreamToken * 'event[]> = async {
        let streamPosition = token.streamVersion + 1L
        let connToUse = if useWriteConn then connection.WriteConnection else connection.ReadConnection
        let! version, events = Read.loadForwardsFrom log connection.ReadRetryPolicy connToUse batching.BatchSize batching.MaxBatches streamName streamPosition
        match isCompactionEventType with
        | None -> return Token.ofNonCompacting version, Array.choose tryDecode events
        | Some isCompactionEvent ->
            match events |> Array.tryFindBack (fun re -> match tryDecode re with Some e -> isCompactionEvent e | _ -> false) with
            | None -> return Token.ofPreviousTokenAndEventsLength streamToken events.Length batching.BatchSize version, Array.choose tryDecode events
            | Some resolvedEvent -> return Token.ofCompactionResolvedEventAndVersion resolvedEvent batching.BatchSize version, Array.choose tryDecode events }
    member _.TrySync log streamName (Token.Unpack pos as streamToken) (events, encodedEvents : EventData array) isCompactionEventType : Async<GatewaySyncResult> = async {
        match! Write.writeEvents log connection.WriteRetryPolicy connection.WriteConnection streamName pos.streamVersion encodedEvents with
        | EsSyncResult.ConflictUnknown ->
            return GatewaySyncResult.ConflictUnknown
        | EsSyncResult.Written wr ->
            let version' = wr.CurrentVersion |> int64
            let token =
                match isCompactionEventType with
                | None -> Token.ofNonCompacting version'
                | Some isCompactionEvent ->
                    match events |> Array.ofList |> Array.tryFindIndexBack isCompactionEvent with
                    | None -> Token.ofPreviousTokenAndEventsLength streamToken encodedEvents.Length batching.BatchSize version'
                    | Some compactionEventIndex ->
                        Token.ofPreviousStreamVersionAndCompactionEventDataIndex streamToken compactionEventIndex encodedEvents.Length batching.BatchSize version'
            return GatewaySyncResult.Written token }
    member _.Sync(log, streamName, streamVersion, events : FsCodec.IEventData<EventBody>[]) : Async<GatewaySyncResult> = async {
        let encodedEvents : EventData[] = events |> Array.map UnionEncoderAdapters.eventDataOfEncodedEvent
        match! Write.writeEvents log connection.WriteRetryPolicy connection.WriteConnection streamName streamVersion encodedEvents with
        | EsSyncResult.ConflictUnknown ->
            return GatewaySyncResult.ConflictUnknown
        | EsSyncResult.Written wr ->
            let version' = wr.CurrentVersion |> int64
            let token = Token.ofNonCompacting version'
            return GatewaySyncResult.Written token }

[<NoComparison; NoEquality; RequireQualifiedAccess>]
type AccessStrategy<'event, 'state> =
    /// Load only the single most recent event defined in <c>'event</c> and trust that doing a <c>fold</c> from any such event
    /// will yield a correct and complete state
    /// In other words, the <c>fold</c> function should not need to consider either the preceding <c>'state</state> or <c>'event</c>s.
    | LatestKnownEvent
    /// Ensures a snapshot/compaction event from which the state can be reconstituted upon decoding is always present
    /// (embedded in the stream as an event), generated every <c>batchSize</c> events using the supplied <c>toSnapshot</c> function
    /// Scanning for events concludes when any event passes the <c>isOrigin</c> test.
    /// Related: https://eventstore.org/docs/event-sourcing-basics/rolling-snapshots/index.html
    | RollingSnapshots of isOrigin : ('event -> bool) * toSnapshot : ('state -> 'event)

type private CompactionContext(eventsLen : int, capacityBeforeCompaction : int) =
    /// Determines whether writing a Compaction event is warranted (based on the existing state and the current accumulated changes)
    member _.IsCompactionDue = eventsLen > capacityBeforeCompaction

type private Category<'event, 'state, 'context>(context : SqlStreamStoreContext, codec : FsCodec.IEventCodec<_, _, 'context>, ?access : AccessStrategy<'event, 'state>) =
    let tryDecode (e : ResolvedEvent) = e |> UnionEncoderAdapters.encodedEventOfResolvedEvent |> codec.TryDecode
    let compactionPredicate =
        match access with
        | None -> None
        | Some AccessStrategy.LatestKnownEvent -> Some (fun _ -> true)
        | Some (AccessStrategy.RollingSnapshots (isValid, _)) -> Some isValid
    let isOrigin =
        match access with
        | None | Some AccessStrategy.LatestKnownEvent -> fun _ -> true
        | Some (AccessStrategy.RollingSnapshots (isValid, _)) -> isValid
    let loadAlgorithm load streamName initial log =
        let batched = load initial (context.LoadBatched streamName log (tryDecode, None))
        let compacted = load initial (context.LoadBackwardsStoppingAtCompactionEvent streamName log (tryDecode, isOrigin))
        match access with
        | None -> batched
        | Some AccessStrategy.LatestKnownEvent
        | Some (AccessStrategy.RollingSnapshots _) -> compacted
    let load (fold : 'state -> 'event seq -> 'state) initial f = async {
        let! token, events = f
        return token, fold initial events }
    member _.Load(fold : 'state -> 'event seq -> 'state) (initial : 'state) (streamName : string) (log : ILogger) : Async<StreamToken * 'state> =
        loadAlgorithm (load fold) streamName initial log
    member _.LoadFromToken (fold : 'state -> 'event seq -> 'state) (state : 'state) (streamName : string) token (log : ILogger) : Async<StreamToken * 'state> =
        (load fold) state (context.LoadFromToken false streamName log token (tryDecode, compactionPredicate))
    member _.TrySync<'context>
        (   log : ILogger, fold : 'state -> 'event seq -> 'state,
            streamName, (Token.Unpack token as streamToken), state : 'state, events : 'event list, ctx : 'context option) : Async<SyncResult<'state>> = async {
        let encode e = codec.Encode(ctx, e)
        let events =
            match access with
            | None | Some AccessStrategy.LatestKnownEvent -> events
            | Some (AccessStrategy.RollingSnapshots (_, compact)) ->
                let cc = CompactionContext(List.length events, token.batchCapacityLimit.Value)
                if cc.IsCompactionDue then events @ [fold state events |> compact] else events
        let encodedEvents : EventData[] = events |> Seq.map (encode >> UnionEncoderAdapters.eventDataOfEncodedEvent) |> Array.ofSeq
        match! context.TrySync log streamName streamToken (events, encodedEvents) compactionPredicate with
        | GatewaySyncResult.ConflictUnknown ->
            return SyncResult.Conflict  (load fold state (context.LoadFromToken true streamName log streamToken (tryDecode, compactionPredicate)))
        | GatewaySyncResult.Written token' ->
            return SyncResult.Written   (token', fold state (Seq.ofList events)) }

type private Folder<'event, 'state, 'context>(category : Category<'event, 'state, 'context>, fold : 'state -> 'event seq -> 'state, initial : 'state, ?readCache) =
    let batched log streamName = category.Load fold initial streamName log
    interface ICategory<'event, 'state, string, 'context> with
        member _.Load(log, streamName, allowStale) : Async<StreamToken * 'state> =
            match readCache with
            | None -> batched log streamName
            | Some (cache : ICache, prefix : string) -> async {
                match! cache.TryGet(prefix + streamName) with
                | None -> return! batched log streamName
                | Some tokenAndState when allowStale -> return tokenAndState
                | Some (token, state) -> return! category.LoadFromToken fold state streamName token log }
        member _.TrySync(log : ILogger, streamName, streamToken, initialState, events : 'event list, context) : Async<SyncResult<'state>> = async {
            match! category.TrySync(log, fold, streamName, streamToken, initialState, events, context) with
            | SyncResult.Conflict resync ->         return SyncResult.Conflict resync
            | SyncResult.Written (token', state') -> return SyncResult.Written (token', state') }

/// For SqlStreamStore, caching is less critical than it is for e.g. CosmosDB
/// As such, it can often be omitted, particularly if streams are short, or events are small and/or database latency aligns with request latency requirements
[<NoComparison; NoEquality; RequireQualifiedAccess>]
type CachingStrategy =
    /// Retain a single 'state per streamName.
    /// Each cache hit for a stream renews the retention period for the defined <c>window</c>.
    /// Upon expiration of the defined <c>window</c> from the point at which the cache was entry was last used, a full reload is triggered.
    /// Unless <c>LoadOption.AllowStale</c> is used, each cache hit still incurs a roundtrip to load any subsequently-added events.
    | SlidingWindow of ICache * window : TimeSpan
    /// Retain a single 'state per streamName
    /// Upon expiration of the defined <c>period</c>, a full reload is triggered.
    /// Unless <c>LoadOption.AllowStale</c> is used, each cache hit still incurs a roundtrip to load any subsequently-added events.
    | FixedTimeSpan of ICache * period : TimeSpan
    /// Prefix is used to segregate multiple folds per stream when they are stored in the cache.
    /// Semantics are identical to <c>SlidingWindow</c>.
    | SlidingWindowPrefixed of ICache * window : TimeSpan * prefix : string

type SqlStreamStoreCategory<'event, 'state, 'context>
    (   context : SqlStreamStoreContext, codec : FsCodec.IEventCodec<_, _, 'context>, fold, initial,
        // Caching can be overkill for EventStore esp considering the degree to which its intrinsic caching is a first class feature
        // e.g., A key benefit is that reads of streams more than a few pages long get completed in constant time after the initial load
        [<O; D(null)>]?caching,
        [<O; D(null)>]?access) =
    do  match access with
        | Some AccessStrategy.LatestKnownEvent when Option.isSome caching ->
            "Equinox.SqlStreamStore does not support (and it would make things _less_ efficient even if it did)"
            + "mixing AccessStrategy.LatestKnownEvent with Caching at present."
            |> invalidOp
        | _ -> ()

    let inner = Category<'event, 'state, 'context>(context, codec, ?access = access)
    let readCacheOption =
        match caching with
        | None -> None
        | Some (CachingStrategy.SlidingWindow (cache, _))
        | Some (CachingStrategy.FixedTimeSpan (cache, _)) -> Some (cache, null)
        | Some (CachingStrategy.SlidingWindowPrefixed (cache, _, prefix)) -> Some (cache, prefix)
    let folder = Folder<'event, 'state, 'context>(inner, fold, initial, ?readCache = readCacheOption)
    let category : ICategory<_, _, _, 'context> =
        match caching with
        | None -> folder :> _
        | Some (CachingStrategy.SlidingWindow (cache, window)) ->
            Caching.applyCacheUpdatesWithSlidingExpiration cache null window folder Token.supersedes
        | Some (CachingStrategy.FixedTimeSpan (cache, period)) ->
            Caching.applyCacheUpdatesWithFixedTimeSpan cache null period folder Token.supersedes
        | Some (CachingStrategy.SlidingWindowPrefixed (cache, window, prefix)) ->
            Caching.applyCacheUpdatesWithSlidingExpiration cache prefix window folder Token.supersedes
    let resolve streamName = category, FsCodec.StreamName.toString streamName, None
    let empty = context.TokenEmpty, initial
    let storeCategory = StoreCategory<'event, 'state, FsCodec.StreamName, 'context>(resolve, empty)
    member _.Resolve(streamName : FsCodec.StreamName, [<O; D null>] ?context : 'context) = storeCategory.Resolve(streamName, ?context = context)

[<AbstractClass>]
type ConnectorBase([<O; D(null)>]?readRetryPolicy, [<O; D(null)>]?writeRetryPolicy) =

    abstract member Connect : unit -> Async<IStreamStore>

    member x.Establish() : Async<SqlStreamStoreConnection> = async {
        let! store = x.Connect()
        return SqlStreamStoreConnection(readConnection=store, writeConnection=store, ?readRetryPolicy=readRetryPolicy, ?writeRetryPolicy=writeRetryPolicy)
    }
