namespace WebSocket

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Runtime.Serialization
open System.Security.Cryptography

module ServerUtil =

    let guid6455 = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
    let endOfLine = "\r\n"
    let line txt = txt + endOfLine

    let getKey (key: String) arr =
        try
            let f (s: String) = s.StartsWith(key)
            (Array.find f arr).[key.Length + 1..]
        with _ -> ""

    let isWebSocketsUpgrade (lines: string array) =
        [| "GET /timer HTTP/1.1"
           "Upgrade: websocket"
           "Connection: Upgrade" |]
        |> Array.map (fun x ->
            lines
            |> Array.exists (fun y -> x.ToLower() = y.ToLower()))
        |> Array.reduce (fun x y -> x && y)

    let getLines (bytes: Byte []) len =
        if len > 8 then
            bytes.[..(len - 9)]
            |> UTF8Encoding.UTF8.GetString
            |> fun hs ->
                hs.Split([| endOfLine |], StringSplitOptions.RemoveEmptyEntries)
        else
            [||]

    let calcWSAccept6455 (secWebSocketKey: string) =
        let sha1 = SHA1CryptoServiceProvider.Create()

        secWebSocketKey + guid6455
        |> Encoding.ASCII.GetBytes
        |> sha1.ComputeHash
        |> Convert.ToBase64String

    let createAcceptString6455 acceptCode =
        line "HTTP/1.1 101 Switching Protocols" +
        line "Upgrade: websocket" +
        line "Connection: Upgrade" +
        line ("Sec-WebSocket-Accept: " + acceptCode) +
        line ""

    let wsResponse lines =
        (match lines with
         | [||] -> ""
         | _ ->
             getKey "Sec-WebSocket-Key:" lines
             |> calcWSAccept6455
             |> createAcceptString6455)
        |> Encoding.ASCII.GetBytes

    let webSocket (lines: string array) =
        (isWebSocketsUpgrade lines, wsResponse lines)

    let makeFrame_ShortTxt (P: byte array) =
        let message = new MemoryStream()

        try
            message.WriteByte(byte 0x81)
            message.WriteByte(byte P.Length)
            message.Write(P, 0, P.Length)
            message.ToArray()
        finally
            message.Close()

open ServerUtil

[<DataContract>]
type Time =
    { [<DataMember(Name = "hour")>]
      mutable Hour: int
      [<DataMember(Name = "minute")>]
      mutable Minute: int
      [<DataMember(Name = "second")>]
      mutable Second: int
    }
    static member New(dt: DateTime) =
        { Hour = dt.Hour
          Minute = dt.Minute
          Second = dt.Second }

type Msg =
    | Connect of MailboxProcessor<Time> * NetworkStream
    | Disconnect of MailboxProcessor<Time>
    | Tick of Time

module WebSocketServer =

    let port = 1900
    let ipAddress = IPAddress.Loopback.ToString()

    let startMailboxProcessor ct f =
        MailboxProcessor.Start(f, cancellationToken = ct)

    let writeTime (ns: NetworkStream)
                  (time: Time)
                  =
        async {
            let json =
                System.Runtime.Serialization.Json.DataContractJsonSerializer
                    (typeof<Time>)

            let payload = new MemoryStream()
            json.WriteObject(payload, time)
            let df = makeFrame_ShortTxt <| payload.ToArray()
            do! ns.AsyncWrite(df, 0, df.Length)
        }

    let runTelemetry (ns: NetworkStream)
                     (inbox: MailboxProcessor<Time>)
                     (ct: CancellationToken)
                     (ctrl: MailboxProcessor<Msg>)
                     =
        async {
            try
                while not ct.IsCancellationRequested do
                    let! _ = inbox.Receive()
                    do! writeTime ns (Time.New(DateTime.Now))
            finally
                printfn "PUSHER DIE"
                ctrl.Post(Disconnect <| inbox)
                ns.Close()
        }

    let runLoop (ns: NetworkStream)
                (size: int)
                (inbox: MailboxProcessor<Time>)
                (ct: CancellationToken)
                (ctrl: MailboxProcessor<Msg>)
                =
        async {
            try
                while not ct.IsCancellationRequested do
                    let bytes = Array.create size (byte 0)

                    let ws =
                        WebSocket.CreateFromStream
                            ((ns :> Stream), true, "n2o", TimeSpan(1, 0, 0))

                    let! (result: WebSocketReceiveResult) =
                        ws.ReceiveAsync(ArraySegment<byte>(bytes), ct)
                        |> Async.AwaitTask

                    let len = result.Count

                    let _ =
                        match (int result.MessageType) with
                        | 2 ->
                            printfn "HANDLE CLOSE"
                        | 1 ->
                            printfn "HANDLE BINARY %A" bytes.[0..len]
                        | 0 ->
                            let text = BitConverter.ToString(bytes.[0..len])
                            printfn "HANDLE TEXT %s" text
                        | x ->
                            printfn "HANDLE %A" x

                    do! writeTime ns (Time.New(DateTime.Now))
            finally
                printfn "LOOP DIE"
                ctrl.Post(Disconnect <| inbox)
                ns.Close()
        }


    let heartbeat (interval: int)
                  (ct: CancellationToken)
                  (ctrl: MailboxProcessor<Msg>)
                  =
        async {
            while not ct.IsCancellationRequested do
                do! Async.Sleep interval
                ctrl.Post(Tick <| Time.New(DateTime.Now))
        }

    let runController (ct: CancellationToken) =
        startMailboxProcessor ct (fun (inbox: MailboxProcessor<Msg>) ->
            let listeners = ResizeArray<_>()

            async {
                while not ct.IsCancellationRequested do
                    let! msg = inbox.Receive()

                    match msg with
                    | Connect (l, ns) ->
                        printfn "Connect: %A %A" l ns
                        listeners.Add(l)
                    | Disconnect l ->
                        Console.WriteLine "Disconnect"
                        listeners.Remove(l) |> ignore
                    | Tick msg -> listeners.ForEach(fun l -> l.Post msg)
            })

    let runWorkers (tcp: TcpClient)
                   (ctrl: MailboxProcessor<Msg>)
                   (ct: CancellationToken)
                   =
        startMailboxProcessor ct (fun (inbox: MailboxProcessor<Time>) ->
            async {
                let ns = tcp.GetStream()
                let size = tcp.ReceiveBufferSize
                let bytes = Array.create size (byte 0)
                let! len =
                    ns.ReadAsync(bytes, 0, bytes.Length)
                    |> Async.AwaitTask

                match webSocket (getLines bytes len) with
                | (true, upgrade) ->
                    do! ns.AsyncWrite upgrade
                    ctrl.Post(Connect(inbox, ns))
                    Async.Start(runTelemetry ns inbox ct ctrl, ct)
                    Async.Start(runLoop ns size inbox ct ctrl, ct)
                | _ -> tcp.Close()
            })

    let acceptLoop (listener: TcpListener)
                   (ct: CancellationToken)
                   (ctrl: MailboxProcessor<Msg>)
                   =
        async {
            try
                let! client =
                     Async.FromBeginEnd
                         (listener.BeginAcceptTcpClient,
                         listener.EndAcceptTcpClient)

                client.NoDelay <- true
                runWorkers client ctrl ct |> ignore
            finally
                listener.Stop()
        }

    let runRequestDispatcher () =
        let cts = new CancellationTokenSource()
        let token = cts.Token
        let controller = runController token
        let listener = TcpListener(IPAddress.Parse(ipAddress), port)

        try
            listener.Start(10)
        with
        | :? SocketException ->
            failwithf "ERROR: %s/%i is using by another program" ipAddress port
        | err ->
            failwithf "ERROR: %s" err.Message

        Async.Start(acceptLoop listener token controller, token)
        Async.Start(heartbeat 1000 token controller, token)

        { new IDisposable with
            member x.Dispose() = cts.Cancel() }

    let start () =
        use dispose = runRequestDispatcher ()
        printfn "press any key to stop..."
        Console.ReadKey() |> ignore

module Program =

    [<EntryPoint>]
    let main _ =
        let mutable exitCode = 0

        try
            WebSocketServer.start ()
        with exn ->
            Console.WriteLine exn.Message
            exitCode <- 1

        exitCode
