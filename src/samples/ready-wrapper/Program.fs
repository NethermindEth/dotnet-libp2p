namespace RandomNamespace
module Entry =
   let [<EntryPoint>] main _ =
      async {
         do! Sample.Types.Run()
      } |> Async.RunSynchronously
      0
