// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using BrowserChat;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Nethermind.Libp2p;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddLibp2p(libp2p => libp2p
    .WithWebSockets()
    .WithRelay()
    .WithWebRtc()
    .WithWebRtcDirect()
    .WithPlaintextEnforced()
    .AddProtocol<BrowserChatProtocol>());
builder.Services.AddSingleton<ChatClient>();

await builder.Build().RunAsync();
