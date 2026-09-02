using ChatBot.Data;
using ChatBot.Services;
using ChatBot.Settings;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
builder.Services.Configure<GeminiSettings>(
    builder.Configuration.GetSection(GeminiSettings.SectionName));

// --- Database ---
// SQLite stores data in a single file. Path relative to app root.
// EnsureCreated() below will create the file + tables if missing.
builder.Services.AddDbContext<ChatBotDbContext>(options =>
    options.UseSqlite("Data Source=chatbot.db"));

// --- Service Registration ---
builder.Services.AddHttpClient<IGeminiService, GeminiService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<IEmbeddingService, EmbeddingService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddSingleton<IVectorStoreService, VectorStoreService>();
builder.Services.AddSingleton<IChatHistoryService, ChatHistoryService>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// --- Database Initialization ---
// EnsureCreated: creates DB + tables if they don't exist.
// For learning this is fine. Production uses migrations (dotnet ef migrations add).
//
// WHY at startup?
// App should be ready to serve requests immediately.
// No "first request creates the DB" surprise.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChatBotDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Load persisted vectors into memory cache
var vectorStore = app.Services.GetRequiredService<IVectorStoreService>();
await vectorStore.InitializeAsync();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
