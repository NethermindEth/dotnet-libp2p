// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nethermind.Libp2p.Protocols.AutoTls.Internal;

namespace Nethermind.Libp2p.Protocols.AutoTls;

public static class AutoTlsServiceCollectionExtensions
{
    public static IServiceCollection AddAutoTls(this IServiceCollection services, Action<AutoTlsOptions>? configure = null)
    {
        services.AddOptions<AutoTlsOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHttpClient<P2pForgeClient>();

        services.TryAddSingleton<FileCertificateStore>();
        services.TryAddSingleton<AcmeFlow>();
        services.TryAddSingleton<AutoTlsCertificateManager>();
        services.TryAddSingleton<ITlsCertificateProvider>(sp => sp.GetRequiredService<AutoTlsCertificateManager>());
        services.AddHostedService(sp => sp.GetRequiredService<AutoTlsCertificateManager>());

        return services;
    }
}
