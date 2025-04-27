// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;

namespace Nethermind.Libp2p.OpenTelemetry;

public static class ServiceProviderExtensions
{
    private static readonly ActivitySource DefaultActivitySource = new("dotnet-libp2p");
    private static TracerProvider? tracerProvider;
    private static Activity? rootActivity;

    // Simple OLTP tracing settup
    public static IServiceCollection AddTracing(this IServiceCollection services, string? appName = null, ActivitySource? activitySource = null, bool createRootActivity = false)
    {
        activitySource ??= DefaultActivitySource;
        tracerProvider ??= Sdk.CreateTracerProviderBuilder()
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(
                               serviceName: appName ?? "app"))
                   .AddSource((activitySource ?? DefaultActivitySource).Name)
                   .AddOtlpExporter()
                   //.AddConsoleExporter()
                   .Build();

        //AppDomain.CurrentDomain.ProcessExit += new EventHandler((_, _) =>
        //{
        //    tracerProvider?.Dispose();
        //});

        IServiceCollection result = services;

        if (createRootActivity)
        {
            rootActivity ??= activitySource!.StartActivity("root")!;
            result = result.AddSingleton(rootActivity);
        }

        return result.AddSingleton(activitySource!)
                     .AddSingleton(tracerProvider);
    }
}
