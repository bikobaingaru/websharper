// $begin{copyright}
//
// This file is part of WebSharper
//
// Copyright (c) 2008-2012 IntelliFactory
//
// GNU Affero General Public License Usage
// WebSharper is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License, version 3, as published
// by the Free Software Foundation.
//
// WebSharper is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details at <http://www.gnu.org/licenses/>.
//
// If you are unsure which license is appropriate for your use, please contact
// IntelliFactory at http://intellifactory.com/contact.
//
// $end{copyright}

namespace IntelliFactory.WebSharper.Sitelets

type Content<'Action> =
    | CustomContent of (Context<'Action> -> Http.Response)
    | PageContent of (Context<'Action> -> Page)

module Content =
    open System
    open System.Collections.Generic
    open System.IO
    open System.Text.RegularExpressions
    open System.Web
    open IntelliFactory.WebSharper

    module Activator = IntelliFactory.WebSharper.Html.Activator
    module M = IntelliFactory.WebSharper.Core.Metadata
    module R = IntelliFactory.WebSharper.Core.Reflection
    module J = IntelliFactory.WebSharper.Core.Json
    module XS = IntelliFactory.Xml.SimpleXml
    module XT = IntelliFactory.Xml.Templating

    let metaJson<'T> (jP: Core.Json.Provider) (controls: seq<Control>) =
        let encode (c: Control) =
            let encoder = jP.GetEncoder(c.GetType())
            encoder.Encode c
        J.Encoded.Object [for c in controls -> (c.ID, encode c)]
        |> jP.Pack
        |> J.Stringify

    let escape (s: string) =
        Regex.Replace(s, @"[&<>']",
            new MatchEvaluator(fun m ->
                match m.Groups.[0].Value.[0] with
                | '&'-> "&amp;"
                | '<'-> "&lt;"
                | '>' -> "&gt;"
                | '\'' -> "&#39;"
                | _ -> failwith "unreachable"))

    /// Watches a file for changes.
    let watchForChanges (path: string) (recompile: unit -> unit) =
        let dir = Path.GetDirectoryName(path)
        let file = Path.GetFileName(path)
        let watcher = new FileSystemWatcher(dir, file, EnableRaisingEvents = true)
        watcher.Changed.Add(fun _ -> recompile ())
        watcher :> System.IDisposable

    type Env =
        {
            AppPath : string
            Json : Core.Json.Provider
            Meta : Core.Metadata.Info
            ResourceContext : Core.Resources.Context
        }

        static member Create<'T>(ctx: Context<'T>) =
            {
                AppPath = ctx.ApplicationPath
                Json = ctx.Json
                Meta = ctx.Metadata
                ResourceContext = ctx.ResourceContext
            }

    let writeResources (env: Env) (controls: seq<Control>) (tw: UI.HtmlTextWriter) =
        // Resolve resources for the set of types and this assembly
        let resources =
            controls
            |> Seq.map (fun x -> x.GetType())
            |> Seq.distinct
            |> Seq.map (fun t ->
                M.Node.TypeNode (R.TypeDefinition.FromType t))
            |> env.Meta.GetDependencies
        // Meta tag encoding the client side controls
        let mJson = metaJson env.Json controls
        // Render meta
        tw.WriteLine(
            "<meta id='{0}' name='{0}' content='{1}' />",
            IntelliFactory.WebSharper.Html.Activator.META_ID, 
            escape mJson
        )
        // Render resources
        for r in resources do
            r.Render env.ResourceContext tw

    let writeStartScript (tw: UI.HtmlTextWriter) =
        tw.WriteLine @"<script type='text/javascript'>"
        tw.WriteLine @"if (typeof IntelliFactory !=='undefined')"
        tw.WriteLine @"  IntelliFactory.Runtime.Start();"
        tw.WriteLine @"</script>"

    let getResourcesAndScripts env controls =
        use m = new StringWriter()
        let tw = new UI.HtmlTextWriter(m, " ")
        writeResources env controls tw
        writeStartScript tw
        m.ToString()

    let toCustomContent genPage context : Http.Response =
        let htmlPage = genPage context
        let writeBody (stream: Stream) =
            // Finds all the client side controls on the page.
            let controls =
                htmlPage.Body
                |> Seq.collect (fun elem ->
                    elem.CollectAnnotations ())
            let renderHead (tw: UI.HtmlTextWriter) =
                writeResources (Env.Create context) controls tw
                let writer = new IntelliFactory.Html.Html.Writer(tw)
                for elem in htmlPage.Head do
                    writer.Write elem
                writeStartScript tw
            let renderBody (tw: UI.HtmlTextWriter) =
                let writer = new IntelliFactory.Html.Html.Writer(tw)
                for elem in htmlPage.Body do
                    writer.Write elem
            // Create html writer from stream
            use textWriter = new StreamWriter(stream)
            textWriter.AutoFlush <- true
            use htmlWriter =
                new System.Web.UI.HtmlTextWriter(textWriter)
            htmlPage.Renderer htmlPage.Doctype htmlPage.Title
                renderHead renderBody htmlWriter
        {
            Status = Http.Status.Ok
            Headers = [Http.Header.Custom "Content-Type" "text/html; charset=utf-8"]
            WriteBody = writeBody
        }

    let ToResponse (c: Content<'T>) (ctx: Context<'T>) =
        match c with
        | CustomContent x -> x ctx
        | PageContent genPage -> toCustomContent genPage ctx

    [<Obsolete>]
    let ToCustomContent (c: Content<'T>) =
        match c with
        | CustomContent _ -> c
        | PageContent genPage -> CustomContent (toCustomContent genPage)

    let MapResponse<'T> (f: Http.Response -> Http.Response) (content: Content<'T>) =
        let genResp =
            match content with
            | CustomContent x -> x
            | PageContent genPage -> toCustomContent genPage
        CustomContent <| fun context -> f (genResp context)

    let WithHeaders<'T> (headers: seq<Http.Header>) (cont: Content<'T>) =
        cont
        |> MapResponse (fun resp ->
            let headers = (List.ofSeq headers) @ (List.ofSeq resp.Headers)
            {resp with Headers = headers})

    let SetStatus<'T> (status: Http.Status) (cont: Content<'T>) =
        cont
        |> MapResponse (fun resp -> {resp with Status = status})

    let Redirect<'T> (action: 'T) =
        CustomContent <| fun ctx ->
            {
                Status = Http.Status.Custom 301 (Some "Moved Permanently")
                Headers = [Http.Header.Custom "Location" (ctx.Link action)]
                WriteBody = ignore
            }

    let RedirectToUrl (url: string) =
        CustomContent <| fun ctx ->
            {
                Status = Http.Status.Custom 301 (Some "Moved Permanently")
                Headers = [Http.Header.Custom "Location" url]
                WriteBody = ignore
            }

    /// Constructs a status code response.
    let httpStatusContent<'T> status : Content<'T> =
        CustomContent <| fun ctx ->
            {
                Status = status
                Headers = []
                WriteBody = ignore
            }

    let Unauthorized<'T> : Content<'T> =
        httpStatusContent Http.Status.Unauthorized

    let Forbidden<'T> : Content<'T> =
        httpStatusContent Http.Status.Forbidden

    let NotFound<'T> : Content<'T> =
        httpStatusContent Http.Status.NotFound

    let ServerError<'T> : Content<'T> =
        httpStatusContent Http.Status.InternalServerError

    module H = IntelliFactory.Html.Html
    type HtmlElement = H.Element<Control>

    type Hole<'T> =
        | SH of ('T -> string)
        | EH of ('T -> seq<HtmlElement>)

    type Wrapper<'T> =
        {
            appPath : string
            extra : Dictionary<string, seq<XS.INode>>
            value : 'T
        }

    let rec toXml (node: HtmlElement) : XS.INode =
        match node with
        | H.TagContent x ->
            let attrs = Dictionary()
            for attr in x.Attributes do
                attrs.[XS.Name.Create attr.Name] <- attr.Value
            XS.ElementNode {
                Name = XS.Name.Create x.Name
                Children = Seq.toArray (Seq.map toXml x.Contents) :> seq<_>
                Attributes = attrs
            } :> _
        | H.TextContent x -> XS.TextNode x :> _
        | H.VerbatimContent x -> XS.CDataNode x :> _
        | H.CommentContent x -> XS.TextNode "" :> _

    [<Literal>]
    let SCRIPTS = "SCRIPTS"

    module Template =
        type LoadFrequency =
            | Once
            | PerRequest
            | WhenChanged

    [<Sealed>]
    type CustomXml private () =
        static let self = CustomXml()
        static member Instance = self
        interface XT.IXml<HtmlElement,HtmlElement> with
            member this.Text x = H.TextContent x
            member this.CData x = H.VerbatimContent x
            member this.ElementNode x = x
            member this.Element(name, attrs, children) =
                children
                |> Seq.map (fun x -> x :> H.INode<_>)
                |> Seq.append
                    (attrs
                    |> Seq.map (fun (KeyValue (k, v)) ->
                        H.NewAttribute k.Local v :> H.INode<_>))
                |> H.NewElement name.Local

    /// Decides if an attribute should contain a URL by HTML rules.
    let isUrlAttribute : XS.Element -> XS.Name -> bool =
        let d =
            Dictionary
                (Map
                    ([
                        "a", "href"
                        "applet", "codebase"
                        "area", "href"
                        "audio", "src"
                        "base", "url"
                        "blockquote", "cite"
                        "body", "background"
                        "button", "formaction"
                        "command", "icon"
                        "del", "cite"
                        "embed", "src"
                        "form", "action"
                        "frame", "src"
                        "head", "profile"
                        "html", "manifest"
                        "iframe", "src"
                        "img", "src"
                        "input", "src"
                        "ins", "cite"
                        "link", "href"
                        "q", "cite"
                        "script", "src"
                        "source", "src"
                        "video", "src"
                    ]))
        fun elem name ->
            match d.TryGetValue(elem.Name.Local) with
            | true, v -> v = name.Local
            | _ -> false

    /// Replaces `~` with `appPath` in URL positions.
    let postProcess (appPath: string) (element: XS.Element) =
        let rec n (node: XS.INode) : XS.INode =
            match node.Node with
            | XS.ElementNode x -> XS.ElementNode (e x) :> _
            | _ -> node
        and e (element: XS.Element) : XS.Element =
            let attributes =
                let key = Seq.tryFind (isUrlAttribute element) element.Attributes.Keys
                match key with
                | Some key ->
                    let value = element.Attributes.[key]
                    if value.StartsWith("~") then
                        let d = Dictionary(element.Attributes)
                        d.[key] <- joinWithSlash appPath (value.Substring(1))
                        d :> IDictionary<_,_>
                    else
                        element.Attributes
                | None ->
                    element.Attributes
            {
                Attributes = attributes
                Children = Seq.map n element.Children
                Name = element.Name
            }
        e element

    [<Sealed>]
    type Template<'T>(path: string, freq: Template.LoadFrequency, holes: Map<string,Hole<'T>>) =
        let pageTemplate =
            let mutable t = XT.Template<Wrapper<'T>>()
            for (KeyValue (k, v)) in holes do
                match v with
                | SH f -> t <- t.With(k, fun x -> f x.value)
                | EH f -> t <- t.With(k, fun x -> x.extra.[k])
            t <- t.With(SCRIPTS, fun x -> x.extra.[SCRIPTS])
            t <- t.With(SCRIPTS.ToLower(), fun x -> x.extra.[SCRIPTS])
            t
        let basicTemplate =
            let mutable t = XT.CustomTemplate<HtmlElement,HtmlElement,'T>(CustomXml.Instance)
            for (KeyValue (k, v)) in holes do
                match v with
                | SH f -> t <- t.With(k, f)
                | EH f -> t <- t.With(k, f)
            t

        let getTemplate (parse: string -> _) =
            match freq with
            | Template.Once ->
                let t = lazy parse path
                fun () -> t.Value
            | Template.PerRequest ->
                fun () -> parse path
            | Template.WhenChanged ->
                let cell = ref None
                let read () =
                    try Choice1Of2 (parse path)
                    with e -> Choice2Of2 e
                let load () =
                    let v = read ()
                    cell := Some v
                    v
                let reload () = lock cell (load >> ignore)
                let unpack x =
                    match x with
                    | Choice1Of2 x -> x
                    | Choice2Of2 (e: exn) -> raise e
                fun () ->
                    lock cell <| fun () ->
                        match !cell with
                        | Some x -> unpack x
                        | None ->
                            // NOTE: resource leak here, watcher
                            // does not get disposed. Not a problem if
                            // template object is static.
                            let watcher = watchForChanges path reload
                            unpack (load ())

        let getBasicTemplate = getTemplate basicTemplate.ParseFragmentFile
        let getPageTemplate = getTemplate pageTemplate.Parse

        new (path) = Template(path, Template.WhenChanged, Map.empty)
        new (path, freq) = Template(path, freq, Map.empty)

        member this.With(name: string, f: Func<'T,string>) =
            Template(path, freq, Map.add name (SH f.Invoke) holes)

        member this.With(name: string, f: Func<'T,HtmlElement>) =
            let h = EH (fun x -> Seq.singleton (f.Invoke(x)))
            Template(path, freq, Map.add name h holes)

        member this.With(name: string, f: Func<'T,#seq<HtmlElement>>) =
            let h = EH (fun x -> Seq.cast(f.Invoke(x)))
            Template(path, freq, Map.add name h holes)

        member this.Compile() =
            getBasicTemplate()
            |> ignore
            this

        member this.Run(value: 'T) : seq<HtmlElement> =
            getBasicTemplate().Run(value)

        member this.CheckPageTemplate() =
            ignore (getPageTemplate ())

        member this.Run(env: Env, x: 'T) : XS.Element =
            let tpl = getPageTemplate ()
            let controls = Queue()
            let extra = Dictionary()
            for KeyValue (k, v) in holes do
                match v with
                | SH _ -> ()
                | EH es ->
                    let children = es x
                    let div = H.NewElement "div" children
                    div.CollectAnnotations()
                    |> Seq.iter controls.Enqueue
                    extra.[k] <-
                        Seq.map toXml children
                        |> Seq.toArray
                        :> seq<_>
            extra.[SCRIPTS] <-
                getResourcesAndScripts env controls
                |> XS.CDataNode :> XS.INode
                |> Seq.singleton
            tpl.Run {
                appPath = env.AppPath
                extra = extra
                value = x
            }
            |> postProcess env.AppPath

    let WithTemplate<'Action,'T>
        (template: Template<'T>)
        (content: Context<'Action> -> 'T) : Content<'Action> =
        template.CheckPageTemplate()
        CustomContent (fun ctx ->
            let xml = template.Run(Env.Create ctx, content ctx)
            {
                Status = Http.Status.Ok
                Headers =
                    [
                        Http.Header.Custom "Content-Type"
                            "text/html; charset=utf-8"
                    ]
                WriteBody = fun s ->
                    use w = new System.IO.StreamWriter(s)
                    w.WriteLine("<!DOCTYPE html>")
                    XS.Node.RenderHtml w xml
            })