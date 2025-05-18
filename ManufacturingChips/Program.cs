using ManufacturingChips.Hubs;
using ManufacturingChips.Interfaces;
using ManufacturingChips.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddSignalR();

builder.Services.AddSingleton<ISimulationService, SimulationService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Simulation}/{action=Index}/{id?}"
);
app.MapHub<SimulationHub>("/simulationHub");

app.Run();