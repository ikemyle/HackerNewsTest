using HackerNewsBestStories.Client;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Settings come from wwwroot/appsettings.json, which the browser downloads at startup.
var apiSettings = builder.Configuration.GetSection("Api").Get<ApiSettings>()
    ?? throw new InvalidOperationException("The Api section is missing from wwwroot/appsettings.json.");
if (apiSettings.BaseAddress is null)
{
    throw new InvalidOperationException("Api:BaseAddress is not set in wwwroot/appsettings.json.");
}

builder.Services.AddSingleton(apiSettings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped(_ => new BestStoriesApiClient(new HttpClient { BaseAddress = apiSettings.BaseAddress }));
builder.Services.AddMudServices();

await builder.Build().RunAsync();
