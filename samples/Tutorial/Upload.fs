/// Simple example of how one might have multiple instances of an uploader app agree/share a common UploadId for a given OrderId
module Upload

open FSharp.UMX
open System

type PurchaseOrderId = int<purchaseOrderId>
and [<Measure>] purchaseOrderId
module PurchaseOrderId =
    let toString (value : PurchaseOrderId) : string = string %value

type CompanyId = string<companyId>
and [<Measure>] companyId
module CompanyId =
    let toString (value : CompanyId) : string = %value

let [<Literal>] Category = "Upload"
let streamName (companyId, purchaseOrderId) = FsCodec.StreamName.compose Category [PurchaseOrderId.toString purchaseOrderId; CompanyId.toString companyId]

type UploadId = string<uploadId>
and [<Measure>] uploadId
module UploadId =
    let toString (value : UploadId) : string = %value

// NOTE - these types and the union case names reflect the actual storage formats and hence need to be versioned with care
module Events =

    type IdAssigned = { value : UploadId }
    type Event =
        | IdAssigned of IdAssigned
        interface TypeShape.UnionContract.IUnionContract
    let codec = EventCodec.create<Event> ()
    let codecJe = EventCodec.createJson<Event> ()

module Fold =

    type State = UploadId option
    let initial = None
    let private evolve _ignoreState = function
        | Events.IdAssigned e -> Some e.value
    let fold (state: State) (events: seq<Events.Event>) : State =
        events |> Seq.tryLast |> Option.fold evolve state

let decide (value : UploadId) (state : Fold.State) : Choice<UploadId,UploadId> * Events.Event list =
    match state with
    | None -> Choice1Of2 value, [Events.IdAssigned { value = value}]
    | Some value -> Choice2Of2 value, []

type Service internal (resolve : CompanyId * PurchaseOrderId -> Equinox.Decider<Events.Event, Fold.State>) =

    member _.Sync(companyId, purchaseOrderId, value) : Async<Choice<UploadId,UploadId>> =
        let decider = resolve (companyId, purchaseOrderId)
        decider.Transact(decide value)

let create resolveStream =
    let resolve ids =
        let streamName = streamName ids
        Equinox.Decider(Serilog.Log.ForContext<Service>(), resolveStream streamName, maxAttempts = 3)
    Service(resolve)

module Cosmos =

    open Equinox.CosmosStore
    let create (context,cache) =
        let cacheStrategy = CachingStrategy.SlidingWindow (cache, TimeSpan.FromMinutes 20.) // OR CachingStrategy.NoCaching
        let category = CosmosStoreCategory(context, Events.codecJe, Fold.fold, Fold.initial, cacheStrategy, AccessStrategy.LatestKnownEvent)
        create category.Resolve

module EventStore =
    open Equinox.EventStoreDb
    let create context =
        let cat = EventStoreCategory(context, Events.codec, Fold.fold, Fold.initial, access=AccessStrategy.LatestKnownEvent)
        create cat.Resolve
