using ChatBot.Services;
using ChatBot.Settings;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
builder.Services.Configure<GeminiSettings>(
    builder.Configuration.GetSection(GeminiSettings.SectionName));

// --- Service Registration ---

// Typed HttpClient for LLM generation (chat responses)
builder.Services.AddHttpClient<IGeminiService, GeminiService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Typed HttpClient for embedding API (separate client = separate pool)
// WHY separate HttpClient? Different endpoints may have different:
// - Timeout requirements (embedding is faster than generation)
// - Rate limits (embedding has higher throughput)
// - Retry policies (add Polly later per-client)
builder.Services.AddHttpClient<IEmbeddingService, EmbeddingService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Document processing — stateless, scoped is fine
builder.Services.AddScoped<IDocumentService, DocumentService>();

// Vector store — SINGLETON: must persist across requests.
// Scoped/transient = new empty store per request = data lost.
builder.Services.AddSingleton<IVectorStoreService, VectorStoreService>();

// Chat history — SINGLETON: same reason as vector store.
// Must persist across requests so conversations continue.
builder.Services.AddSingleton<IChatHistoryService, ChatHistoryService>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
