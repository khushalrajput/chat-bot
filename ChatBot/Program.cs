using ChatBot.Data;
using ChatBot.Services;
using ChatBot.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
builder.Services.Configure<GeminiSettings>(
    builder.Configuration.GetSection(GeminiSettings.SectionName));

// Read settings for SK registration
var geminiSettings = builder.Configuration
    .GetSection(GeminiSettings.SectionName)
    .Get<GeminiSettings>()!;

// --- Semantic Kernel Registration ---
//
// THIS IS THE KEY PART. Compare with old raw HTTP setup:
//
// OLD:
//   builder.Services.AddHttpClient<IGeminiService, GeminiService>(...);
//   builder.Services.AddHttpClient<IEmbeddingService, EmbeddingService>(...);
//   + GeminiRequest/Response DTOs
//   + Manual JSON serialization
//   + Manual SSE parsing
//
// NEW:
//   Two lines. SK handles HTTP, JSON, auth, SSE, retries internally.
//
// SWAP PROVIDER: Change these two lines to AddOpenAIChatCompletion / AddOpenAITextEmbeddingGeneration
// and the entire app works with OpenAI. Zero changes anywhere else.

builder.Services.AddGoogleAIGeminiChatCompletion(
    modelId: geminiSettings.Model,
    apiKey: geminiSettings.ApiKey);

builder.Services.AddGoogleAIEmbeddingGeneration(
    modelId: "gemini-embedding-001",
    apiKey: geminiSettings.ApiKey);

// --- Our Services (unchanged from before) ---
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddSingleton<IVectorStoreService, VectorStoreService>();
builder.Services.AddSingleton<IChatHistoryService, ChatHistoryService>();

// --- Database ---
builder.Services.AddDbContext<ChatBotDbContext>(options =>
    options.UseSqlite("Data Source=chatbot.db"));

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// --- Database Initialization ---
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
