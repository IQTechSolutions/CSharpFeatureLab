using FeatureLab.Client;
using FeatureLab.Client.Features.Tenancy;
using FeatureLab.Client.Features.WorkItems;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
builder.Services.AddScoped<IWorkItemApi, HttpWorkItemApi>();
builder.Services.AddScoped<ITenantInvitationApi, HttpTenantInvitationApi>();

await builder.Build().RunAsync();
