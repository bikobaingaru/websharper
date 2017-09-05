#load "tools/WebSharper.Fake.fsx"
#r "packages/AjaxMin/lib/net40/AjaxMin.dll"

open System.IO
open Fake
open WebSharper.Fake

let version = "4.0"
let pre = Some "rc"

let baseVersion =
    version + match pre with None -> "" | Some x -> "-" + x
    |> Paket.SemVer.Parse

let targets = MakeTargets {
    WSTargets.Default (ComputeVersion (Some baseVersion)) with
        SolutionFile = "WebSharper.sln"
}

let NeedsBuilding input output =
    let i = FileInfo(input)
    let o = FileInfo(output)
    not o.Exists || o.LastWriteTimeUtc < i.LastWriteTimeUtc

let Minify () =
    let minify (path: string) =
        let min = Microsoft.Ajax.Utilities.Minifier()
        let out = Path.ChangeExtension(path, ".min.js")
        if NeedsBuilding path out then
            let raw = File.ReadAllText(path)
            let mjs = min.MinifyJavaScript(raw)
            File.WriteAllText(Path.ChangeExtension(path, ".min.js"), mjs)
            stdout.WriteLine("Written {0}", out)
    minify "src/compiler/WebSharper.Core.JavaScript/Runtime.js"
    minify "src/stdlib/WebSharper.Main/Json.js"
    minify "src/stdlib/WebSharper.Main/AnimFrame.js"

let SetVersion () =
    let v = targets.ComputedVersion
    ["msbuild/AssemblyInfo.fs"]
    |> List.map (fun f -> File.Copy(f + ".in", f, true); f)
    |> processTemplates [
        "{version}", sprintf "%i.%i.%i.%s" v.Major v.Minor v.Patch v.Build
        "{assemblyversion}", sprintf "%i.%i.0.0" v.Major v.Minor
    ]

Target "Prepare" <| fun () ->
    Minify()
    SetVersion()
targets.AddPrebuild "Prepare"

Target "Build" DoNothing
targets.BuildDebug ==> "Build"

Target "CI-Release" DoNothing
targets.Publish ==> "CI-Release"

Target "Run" <| fun () ->
    shellExec {
        defaultParams with
            Program = @"C:\Program Files (x86)\Microsoft Visual Studio 14.0\Common7\IDE\devenv.exe"
            CommandLine = "/r WebSharper.sln"
    }
    |> ignore

"Build" ==> "Run"

RunTargetOrDefault "Build"
