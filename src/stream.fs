namespace N2O

open System
open System.Text
open System.Threading
open System.Net.WebSockets

// MailboxProcessor-based Tick pusher and pure Async WebSocket looper

[<AutoOpen>]
module Stream =

    let mutable protocol: Req -> Msg -> Res = fun _ _ -> Ok

    let sendBytes (ws: WebSocket) ct bytes =
        ws.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, ct)
        |> Async.AwaitTask

    let sendMsg ws ct (msg: Msg) = async {
        match msg with
        | Text text -> do! sendBytes ws ct (Encoding.UTF8.GetBytes text)
        | Bin arr -> do! sendBytes ws ct arr
        | Nope -> ()
    }

    let send (ws: WebSocket) ct (res: Res) = async {
        match res with
        | Error err -> do!
            ws.CloseAsync(WebSocketCloseStatus.InternalServerError, err, ct)
            |> Async.AwaitTask
        | Reply msg -> do! sendMsg ws ct msg
        | Ok -> ()
    }

    let telemetry (ws: WebSocket) (inbox: MailboxProcessor<Msg>)
        (ct: CancellationToken) (sup: MailboxProcessor<Sup>) =
        async {
            try
                while not ct.IsCancellationRequested do
                    let! _ = inbox.Receive()
                    do! sendMsg ws ct (Text "TICK")
            finally
                sup.Post(Disconnect <| inbox)

                ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "TELEMETRY", ct)
                |> ignore
        }

    let looper (ws: WebSocket) (req: Req) (bufferSize: int)
        (ct: CancellationToken) (sup: MailboxProcessor<Sup>) =
        async {
            try
                let mutable bytes = Array.create bufferSize (byte 0)
                while not ct.IsCancellationRequested do
                    let! result =
                        ws.ReceiveAsync(ArraySegment<byte>(bytes), ct)
                        |> Async.AwaitTask

                    let recv = bytes.[0..result.Count - 1]

                    match (result.MessageType) with
                    | WebSocketMessageType.Text ->
                        do! protocol req (Text (Encoding.UTF8.GetString recv))
                            |> send ws ct
                    | WebSocketMessageType.Binary ->
                        do! protocol req (Bin recv)
                            |> send ws ct
                    | WebSocketMessageType.Close -> ()
                    | _ -> printfn "PROTOCOL VIOLATION"
            finally
                sup.Post(Close <| ws)

                ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "LOOPER", ct)
                |> ignore
        }

