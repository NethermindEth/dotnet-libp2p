// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;

namespace Nethermind.Libp2p.OpenTelemetry;

public static class ServiceProviderExtensions
{
    private static readonly ActivitySource DefaultActivitySource = new("dotnet-libp2p");
    private static TracerProvider? tracerProvider;
    private static Activity? rootActivity;

    // Simple OLTP tracing setup
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

    private static MeterProvider? meterProvider;

    public static IServiceCollection AddMetrics(this IServiceCollection services, string? appName = null)
    {
        meterProvider ??= Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(
                serviceName: appName ?? "app"))
            .AddMeter(Libp2pMetrics.MeterName)
            .AddOtlpExporter()
            .Build();

        return services.AddSingleton(meterProvider);
    }
}
