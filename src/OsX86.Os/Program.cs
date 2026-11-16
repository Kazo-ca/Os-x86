using Kazo.Os.Ecosystem;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OsX86.Os;
using OsX86.Vm;

// The whole OS comes from Kazo.Os.Ecosystem.Host; the apps are the packages referenced by the
// project, aggregated into this assembly by the build (KazoOsAppHost).
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.AddKazoOsBrowserHost(HostAppCatalog.Load(typeof(Program).Assembly));
// ADR 0182: apps installed from packages (kazopkg) join the catalog before the OS starts.
WebAssemblyHost host = builder.Build();
// Work item 6b: the x86 command gets no IJSRuntime (ADR 0070 of Kazo); the dev host gives it the v86
// engine, the image store of the OPFS and the packs built in images/build/ as the origin of the
// images, before any command runs. The Feature of work item 8 takes this over.
var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
VmHost.Configure(VmHost.ForBrowser(host.Services.GetRequiredService<IJSRuntime>(), await DevImages.LoadCatalogAsync(baseAddress, CancellationToken.None)));
await host.LoadInstalledAppsAsync();
await host.RunAsync();
