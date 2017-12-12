module Conference.Ws

open Fable.Import.Browser
open Fable.Core.JsInterop
open Conference.Types
open Server.ServerTypes
open Infrastructure.Types
open Conference.Api

let mutable private sendPerWebsocket : ClientMsg<Domain.Commands.Command,API.QueryParameter,API.QueryResult> -> unit =
  fun _ -> failwith "WebSocket not connected"

let startWs dispatch =
  let onMsg : System.Func<MessageEvent, obj> =
    (fun (wsMsg : MessageEvent) ->
      let msg =
        ofJson<ServerMsg<Domain.Events.Event,API.QueryResult>> <| unbox wsMsg.data

      Msg.Received msg |> dispatch

      null
    ) |> unbox // temporary fix until Fable WS Import is upgraded to Fable 1.*

  let ws = Fable.Import.Browser.WebSocket.Create("ws://127.0.0.1:8085/conferenceWebsocket")

  let send msg =
    ws.send (toJson msg)

  ws.onopen <- (fun _ -> send (ClientMsg.Connect) ; null)
  ws.onmessage <- onMsg
  ws.onerror <- (fun err -> printfn "%A" err ; null)

  sendPerWebsocket <- send

  ()


let wsCmd cmd =
  [fun _ -> sendPerWebsocket cmd]

let transactionId() =
  TransactionId <| System.Guid.NewGuid()

let createQuery query =
  {
    Query.Id = QueryId <| System.Guid.NewGuid()
    Query.Parameter = query
  }
