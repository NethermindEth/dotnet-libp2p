# Logging

Libp2p integrates a standard `ILoggerFactory`/`ILogger` approach. When logging is not injected no logs are printed.

Such code allows logs to be displayed in the console:

```cs
new ServiceCollection()
    .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace)
                                  .AddSimpleConsole(l =>
                                  {
                                      l.SingleLine = true;
                                      l.TimestampFormat = "[HH:mm:ss.FFF]";
                                  }))
```

# Tracing using open telemetry

Tracing is implemented using standard `Activities`, check [Microsoft docs](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs#activity) for more details.

There is an optional separate package containing a helper that allows you to:
- activate telemetry sending to standard address
- inject "root" activity that will be used as a parent for all activities created in libp2p
- inject activity source required for spawning tracing activities

```sh
dotnet add package Nethermind.Libp2p.OpenTelemetry --prerelease
```

```cs
new ServiceCollection()
    .AddTracing(appName: "my app", createRootActivity: true)
```

or you can just copy and customize [tracing related dependencies injection code](../src/libp2p/Libp2p.OpenTelemetry/ServiceProviderExtensions.cs).

### Tips on `Activities` tracing

- Activities need to be disposed, otherwise they will not be sent to server. You can try to dispose `TracerProvider` instance used, and it may help. But I found some activities still not disposed and therefore not sent. Additional effort that finally fixes the issue with sending really all activity information is to track all of them and dispose, see [ActivityTracker](../src/libp2p/Libp2p.E2eTests/E2eTestSetup.cs) as a last resort.

- Pretty convenient dev tool to receive telemetry and to check graphs is jaeger: https://www.jaegertracing.io/download/ (just download, start jaeger executable from cli and navigate to http://localhost:16686/search, then run your app)
