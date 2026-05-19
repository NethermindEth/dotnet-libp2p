# Logging and tracing

## Logging

Libp2p uses the standard `ILoggerFactory`/`ILogger` approach. When logging is not injected, no logs are printed.

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

## Tracing using OpenTelemetry

Tracing is implemented with standard `Activities`. Check [Microsoft docs](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs#activity) for more details.

There is an optional separate package containing a helper that allows you to:

- activate telemetry export to the standard endpoint
- inject a root activity that is used as a parent for all activities created in libp2p
- inject the activity source required for spawning tracing activities

```sh
dotnet add package Nethermind.Libp2p.OpenTelemetry --prerelease
```

```cs
new ServiceCollection()
    .AddTracing(appName: "my app", createRootActivity: true)
```

or copy and customize the [tracing-related dependency injection code](../src/libp2p/Libp2p.OpenTelemetry/ServiceProviderExtensions.cs).

## Tips on `Activities` tracing

- Activities need to be disposed; otherwise they might not be exported. Disposing the `TracerProvider` instance can help. If some activities are still not sent, track and dispose them explicitly, as shown by [ActivityTracker](../src/libp2p/Libp2p.E2eTests/E2eTestSetup.cs).

- Jaeger is a convenient development tool for receiving telemetry and checking graphs: https://www.jaegertracing.io/download/. Start the Jaeger executable from the CLI, navigate to http://localhost:16686/search, then run your app.
