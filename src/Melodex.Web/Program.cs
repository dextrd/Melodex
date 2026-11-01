using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Melodex.Client;
using Melodex.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The web app is just another client of the public API, served from the same origin.
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<Theme>();
builder.Services.AddScoped(services =>
    new HttpClient(new SignInHandler(services.GetRequiredService<AuthState>())) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<MelodexClient>();
builder.Services.AddScoped<Preferences>();
builder.Services.AddScoped<ServerEvents>();
builder.Services.AddScoped<RequestTracker>();
builder.Services.AddScoped<Player>();
builder.Services.AddScoped<LibraryStore>();
builder.Services.AddScoped<SearchMemory>();
builder.Services.AddScoped<PageCache>();
builder.Services.AddScoped<Glimpses>();
builder.Services.AddScoped<NavSections>();
builder.Services.AddScoped<ViewTransitions>();

await builder.Build().RunAsync();
