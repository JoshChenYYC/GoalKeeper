using GoalKeeper.Application;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure;
using GoalKeeper.Web.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var dataRoot = builder.Configuration["GoalKeeper:DataRoot"];
if (string.IsNullOrWhiteSpace(dataRoot))
{
    dataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GoalKeeper");
}
Directory.CreateDirectory(dataRoot);
var databasePath = Path.Combine(dataRoot, "goalkeeper.db");

builder.Services.AddDbContextFactory<GoalKeeperDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath};Pooling=False"));
builder.Services.AddSingleton(new SessionArtifactStore(dataRoot));
builder.Services.AddScoped<IGoalKeeperRepository, EfGoalKeeperRepository>();
builder.Services.AddScoped<SetupWorkflow>();
builder.Services.AddSingleton<IClock, SystemClock>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<IGoalKeeperRepository>().InitializeAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
