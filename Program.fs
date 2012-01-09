open System
open System.Diagnostics
open System.Web.Script.Serialization

type EventJson = {
    datetime : string
    eventId : int
    level : string
    logName : string
    source : string
    category : string
    message : string
}

let appSettings = Configuration.ConfigurationSettings.get_AppSettings()
let url = appSettings.["url"]
let excludeEventIds = appSettings.["excludeEventIds"].Split([|';';','|], StringSplitOptions.RemoveEmptyEntries) 
                      |> Array.map int |> Set.ofArray

let logException (ex: exn) = 
    let msg = sprintf "Exception sending to Loggly: %s" <| ex.ToString()
    Console.Error.WriteLine(msg)
    Trace.WriteLine(msg)

let logAgent = MailboxProcessor.Start (fun (mb: MailboxProcessor<EventLogEntry * string>) -> async {
    Net.ServicePointManager.DefaultConnectionLimit <- 16
    Net.ServicePointManager.Expect100Continue <- false
    while true do
        try
            let! e, logName = mb.Receive()
            if excludeEventIds.Contains(e.get_EventID()) then () else
            let t = e.TimeGenerated.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")
            let je = { datetime = t; eventId = e.get_EventID(); level = (string e.EntryType); 
                       logName = logName; source = e.Source; category = e.Category; message = e.Message }
            let jss = Web.Script.Serialization.JavaScriptSerializer()
            let data = jss.Serialize(je) |> Text.Encoding.UTF8.GetBytes
            let req = Net.HttpWebRequest.Create(url) :?> Net.HttpWebRequest
            req.Method <- "POST"
            req.KeepAlive <- true
            req.ContentType <- "application/json"
            req.Pipelined <- true
            let handle = async {
                try 
                    use rs = req.GetRequestStream()
                    rs.Write(data, 0, data.Length)
                    use! res = req.AsyncGetResponse()
                    ()
                with ex -> logException ex
            }
            Async.Start handle
        with ex -> logException ex
})

let run() = 
    let logNames = appSettings.["logNames"].Split([|';';','|], StringSplitOptions.RemoveEmptyEntries)
    let logs = logNames |> Array.map(fun x -> new EventLog(x, EnableRaisingEvents = true))
    logs |> Array.iter (fun l -> l.EntryWritten |> Event.add (fun e -> logAgent.Post (e.Entry, l.Log)))

type EvtService() =
    inherit ServiceProcess.ServiceBase()
    do base.ServiceName <- "Event Loggly"
    override x.OnStart(args) = run()
    override x.OnStop() = Environment.Exit(0)

let runDebug() =
    printfn "Starting..."
    run()
    printfn "Running, type quit to quit."
    while Console.ReadLine() <> "quit" do ()
    printfn "Stopping..."
    0

[<EntryPoint>]
let main args = 
    match Environment.UserInteractive with
    | true -> runDebug()
    | _    -> ServiceProcess.ServiceBase.Run(new EvtService()); 0
