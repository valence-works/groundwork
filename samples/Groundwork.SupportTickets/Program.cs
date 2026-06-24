using Groundwork.SupportTickets;

var builder = WebApplication.CreateBuilder(args);
var storageOptions = SupportTicketStorageOptions.FromConfiguration(builder.Configuration);
await using var supportTickets = await SupportTicketSampleHost.CreateAsync(storageOptions);

builder.Services.AddSingleton(supportTickets.Tickets);
builder.Services.AddSingleton(supportTickets.Operations);
builder.Services.AddSingleton(supportTickets.OperationalFit);
builder.Services.AddSingleton(supportTickets.Inbox);
builder.Services.AddSingleton(supportTickets.ExternalModuleFit);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapSupportTicketEndpoints(storageOptions);

await app.RunAsync();

public partial class Program;
