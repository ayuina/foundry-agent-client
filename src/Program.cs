using Azure.Core;
using Azure.Identity;
using image_search_demo.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

const string ArmScope = "https://management.azure.com/user_impersonation";

builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi([ArmScope])
    .AddInMemoryTokenCaches();
builder.Services.PostConfigure<OpenIdConnectOptions>(
    OpenIdConnectDefaults.AuthenticationScheme,
    options =>
    {
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        if (!options.Scope.Contains(ArmScope, StringComparer.OrdinalIgnoreCase))
        {
            options.Scope.Add(ArmScope);
        }
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = options.DefaultPolicy;
});

builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
builder.Services.AddDataProtection().SetApplicationName("FoundryChat");
builder.Services.AddHttpClient<IFoundryResourceDiscoveryService, FoundryResourceDiscoveryService>(client =>
{
    client.BaseAddress = new Uri("https://management.azure.com/");
});
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddSingleton<ProjectSelectionTokenService>();
builder.Services.AddSingleton<FoundryAgentCatalogService>();
builder.Services.AddSingleton<BlobCitationService>();
builder.Services.AddSingleton<IFoundryAgentGateway, FoundryAgentGateway>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

public partial class Program;
