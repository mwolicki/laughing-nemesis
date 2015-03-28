#if INTERACTIVE
#I @"/Users/kevin/Projects/SimpleHttpProxy/packages"
#r @"Suave.0.26.1/lib/net40/Suave.dll"
#r @"FSharp.Core.3.1.2.1/lib/net40/FSharp.Core.dll"
#r @"FsPickler.1.0.16/lib/net45/FsPickler.dll"
typeof<Nessos.FsPickler.FsPickler> |> ignore
#r @"Newtonsoft.Json.6.0.5/lib/net45/Newtonsoft.Json.dll"
typeof<Newtonsoft.Json.JsonToken> |> ignore
#r @"FsPickler.Json.1.0.16/lib/net45/FsPickler.Json.dll"

#r @"EventStore.Client.3.0.2/lib/net40/EventStore.ClientAPI.dll"

#I @"/Library/Frameworks/Mono.framework/Versions/3.12.1/lib/mono/4.5"
#r @"System.Web.dll"
#r @"System.Core.dll"
#r @"System.Numerics.dll"

#endif
open Suave
open Suave.Types
open Suave.Web
open System.Net
open System

type System.IO.Stream with member this.ReadAllAsync = async { return! this.AsyncRead (int this.Length) }

[<AutoOpen>]
module Events =
    open EventStore.ClientAPI
    open Nessos.FsPickler.Json

    [<AutoOpen>]
    module private Events = 
        let connection =lazy(
                let es = EventStoreConnection.Create(IPEndPoint(IPAddress.Loopback, 1113))
                es.ConnectAsync().Wait()
                es)

        let serializer = JsonSerializer (false,true)
        let publishEvent (conn:IEventStoreConnection) stream event  = Async.AwaitTask (conn.AppendToStreamAsync(stream, ExpectedVersion.Any, event))

        let serialize data = async {
                    use mem = new System.IO.MemoryStream()
                    serializer.Serialize(mem,data,leaveOpen = true)
                    mem.Position<-0L
                    return! mem.ReadAllAsync }

    let publishGetWebsite data = async{
            let! data = serialize data
            return! publishEvent (connection.Value) "website-get" [|(EventData(Guid.NewGuid(), "http-get", false, data, [||]))|]
        }
         
[<AutoOpen>]
module Proxy =
    open Suave.Http
    open Suave.Http.Successful
    open Suave.Http.Applicatives
    open Suave.Web
    open System.Collections.Generic
    open System.Collections.Specialized
 
    let proxy: WebPart =
      fun (ctx : HttpContext) ->
        async {
          //publishGetWebsite ctx.request { BorwserIPAddress = ctx.runtime } |> Async.StartAsTask |> ignore
          let! x = (publishGetWebsite ctx.request  )
          let url = match ctx.request.host with
                    |ClientOnly host -> "http://" + host + ctx.request.url.LocalPath
                    |_->failwith "Unsupported"
          printfn "%A" url
          let request = HttpWebRequest.Create url :?>HttpWebRequest
          ctx.request.headers |> Seq.iter (fun (key, value) -> match key.ToLowerInvariant() with
                                                               | "accept" | "connection" | "content-length" | "proxy-connection" | "range" -> ()
                                                               | "content-type" -> request.ContentType <- value
                                                               | "date" -> request.Date <- DateTime.Parse(value)
                                                               | "expect" -> request.Expect <- value
                                                               | "host" -> request.Host <- value
                                                               | "if-modified-since" -> request.IfModifiedSince <- DateTime.Parse(value)
                                                               | "referer" -> request.Referer <- value
                                                               | "transfer-encoding" -> request.TransferEncoding <- value
                                                               | "user-agent" -> request.UserAgent <- value
                                                               |_ -> request.Headers.Add(key,value))
          try
              let! response = request.AsyncGetResponse()
              use stream = response.GetResponseStream()
              let! responseBytes = stream.ReadAllAsync
              let headers = response.Headers.AllKeys |> Seq.map(fun x->(x, response.Headers.[x]))|> Seq.toList

              let p =(ok responseBytes) {ctx with response = {ctx.response with headers = headers}}
              return! p
          with | :? WebException as e when e.Message.Contains("(304) Not Modified") -> return! Redirection.not_modified ctx
        }

startWebServer {defaultConfig with bindings = [ HttpBinding.mk HTTP (IPAddress.Parse "0.0.0.0") 80us ]} proxy 
