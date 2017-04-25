open System.Web.Util

// Learn more about F# at http://fsharp.org. See the 'F# Tutorial' project
// for more guidance on F# programming.

// Define your library scripting code here

let run a = Async.Start a

let (_time_) = System.DateTime.Now

let DoSomethingAsync =
        async {
            printfn "%A, Hello before await" _time_
            do! Async.Sleep(2000)
            printfn "%A, Hello after await" _time_
        }

let s =
     printfn "%A, Hello before async work" _time_
     run DoSomethingAsync
     printfn "%A, Hello from s" _time_