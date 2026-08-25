using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection("Sync"));
builder.Services.Configure<SmartStandbyOptions>(builder.Configuration.GetSection("SmartStandby"));
builder.Services.AddSingleton<FileCatalog>();
builder.Services.AddSingleton<SmartStandbyStore>();
builder.Services.AddSingleton<SmartStandbyRenderer>();
builder.Services.AddSingleton<SmartStandbyPublisher>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SmartStandbyPublisher>());
builder.Services.AddHttpClient("smart-standby", client => client.Timeout = TimeSpan.FromSeconds(12));
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit =
  builder.Configuration.GetValue<long?>("Sync:MaxUploadBytes") ?? 100 * 1024 * 1024);

var app = builder.Build();
var options = app.Services.GetRequiredService<IOptions<SyncOptions>>().Value;
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, options.StoragePath));

app.Use(async (context, next) => {
  if (context.Request.Path.StartsWithSegments("/api/admin") &&
      !string.IsNullOrWhiteSpace(options.ApiKey) &&
      context.Request.Headers["X-Api-Key"] != options.ApiKey) {
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(new { error = "Invalid API key" });
    return;
  }
  await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));
app.MapGet("/standby", () => Results.Content(SmartStandbyPage.Html, "text/html; charset=utf-8"));
app.MapGet("/standby.html", () => Results.Content(SmartStandbyPage.Html, "text/html; charset=utf-8"));

app.MapGet("/api/v1/sync/manifest", async (HttpRequest request, FileCatalog catalog, CancellationToken ct) => {
  var page = Math.Max(1, ParsePositive(request.Query["page"], 1));
  var pageSize = Math.Clamp(ParsePositive(request.Query["pageSize"], 20), 1, 50);
  var type = request.Query["type"].ToString();
  var all = await catalog.ScanAsync(type, ct);
  var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
  var baseUrl = ResolveBaseUrl(request, options);
  return Results.Ok(new {
    version = 1,
    page,
    pageSize,
    total = all.Count,
    hasMore = page * pageSize < all.Count,
    items = items.Select(x => new {
      x.Id, type = x.Type, x.FileName, x.TargetPath, x.Size, x.Sha256, x.ModifiedUtc,
      downloadUrl = $"{baseUrl}/api/v1/sync/files/{x.Id}"
    })
  });
});

app.MapGet("/api/v1/sync/files/{id}", async (string id, FileCatalog catalog, CancellationToken ct) => {
  var file = await catalog.FindAsync(id, ct);
  return file is null ? Results.NotFound() : Results.File(file.FullPath, file.ContentType, file.FileName,
    lastModified: file.ModifiedUtc, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{file.Sha256}\""),
    enableRangeProcessing: true);
});

app.MapPost("/api/admin/files", async (HttpRequest request, FileCatalog catalog, CancellationToken ct) => {
  if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data required" });
  var form = await request.ReadFormAsync(ct);
  var upload = form.Files.GetFile("file");
  if (upload is null || upload.Length == 0) return Results.BadRequest(new { error = "file is required" });
  if (upload.Length > options.MaxUploadBytes) return Results.BadRequest(new { error = "file is too large" });
  try {
    var saved = await catalog.SaveAsync(upload, form["type"], ct);
    return Results.Created($"/api/v1/sync/files/{saved.Id}", saved.PublicView());
  } catch (ArgumentException ex) {
    return Results.BadRequest(new { error = ex.Message });
  }
}).DisableAntiforgery();

app.MapDelete("/api/admin/files/{id}", async (string id, FileCatalog catalog, CancellationToken ct) =>
  await catalog.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

app.MapGet("/api/admin/standby/templates", async (SmartStandbyStore store, CancellationToken ct) =>
  Results.Ok(await store.ListAsync(ct)));

app.MapPost("/api/admin/standby/templates", async (SmartStandbyTemplate template, SmartStandbyStore store,
  CancellationToken ct) => {
  try { return Results.Ok(await store.SaveAsync(template, ct)); }
  catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/admin/standby/templates/{id}/publish", async (string id, HttpRequest request,
  SmartStandbyStore store, SmartStandbyPublisher publisher, CancellationToken ct) => {
  var template = await store.FindAsync(id, ct);
  if (template is null) return Results.NotFound();
  return Results.Ok(await publisher.RenderAndPublishAsync(template, ResolveBaseUrl(request, options), ct));
});

app.MapGet("/api/v1/standby/{deviceId}/image.bmp", (string deviceId, SmartStandbyStore store) => {
  try {
    var path = store.ImagePath(deviceId);
    return path is null ? Results.NotFound() : Results.File(path, "image/bmp", enableRangeProcessing: true);
  } catch (ArgumentException) { return Results.BadRequest(); }
});

app.MapGet("/opds", async (HttpRequest request, FileCatalog catalog, CancellationToken ct) => {
  var books = await catalog.ScanAsync("book", ct);
  var baseUrl = ResolveBaseUrl(request, options);
  using var output = new MemoryStream();
  using (var xml = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true })) {
    xml.WriteStartDocument();
    xml.WriteStartElement("feed", "http://www.w3.org/2005/Atom");
    xml.WriteAttributeString("xmlns", "opds", null, "http://opds-spec.org/2010/catalog");
    xml.WriteElementString("id", $"{baseUrl}/opds");
    xml.WriteElementString("title", "CrossMux Sync Library");
    xml.WriteElementString("updated", DateTimeOffset.UtcNow.ToString("O"));
    foreach (var book in books) {
      xml.WriteStartElement("entry");
      xml.WriteElementString("id", book.Id);
      xml.WriteElementString("title", Path.GetFileNameWithoutExtension(book.FileName));
      xml.WriteElementString("updated", book.ModifiedUtc.ToString("O"));
      xml.WriteStartElement("link");
      xml.WriteAttributeString("rel", "http://opds-spec.org/acquisition");
      xml.WriteAttributeString("href", $"{baseUrl}/api/v1/sync/files/{book.Id}");
      xml.WriteAttributeString("type", book.ContentType);
      xml.WriteEndElement();
      xml.WriteEndElement();
    }
    xml.WriteEndElement();
    xml.WriteEndDocument();
  }
  return Results.Bytes(output.ToArray(), "application/atom+xml;profile=opds-catalog;kind=acquisition;charset=utf-8");
});

app.MapGet("/", () => Results.Content("""
<!doctype html><html><head><meta charset="utf-8"><title>CrossMux Sync</title>
<style>body{font:16px system-ui;max-width:720px;margin:48px auto;padding:0 20px}input,select,button{font:inherit;padding:8px;margin:4px}</style></head>
<body><h1>CrossMux Sync</h1><p>Upload an ebook or image for device synchronization.</p><p><a href="/standby">Open Smart Standby Studio</a></p>
<form id="f"><input id="key" type="password" placeholder="API key"><select id="type"><option value="auto">Auto detect</option><option value="book">Book</option><option value="image">Image</option></select><input id="file" type="file" required><button>Upload</button></form><pre id="out"></pre>
<script>f.onsubmit=async e=>{e.preventDefault();let d=new FormData();d.append('file',file.files[0]);d.append('type',type.value);let r=await fetch('/api/admin/files',{method:'POST',headers:{'X-Api-Key':key.value},body:d});out.textContent=await r.text()}</script></body></html>
""", "text/html; charset=utf-8"));

app.Run();

static int ParsePositive(string? value, int fallback) => int.TryParse(value, out var n) && n > 0 ? n : fallback;
static string ResolveBaseUrl(HttpRequest request, SyncOptions options) =>
  string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? $"{request.Scheme}://{request.Host}" : options.PublicBaseUrl.TrimEnd('/');

sealed class SyncOptions {
  public string StoragePath { get; set; } = "data";
  public string ApiKey { get; set; } = "change-me";
  public string PublicBaseUrl { get; set; } = "";
  public long MaxUploadBytes { get; set; } = 100 * 1024 * 1024;
}

sealed record CatalogFile(string Id, string Type, string FileName, string TargetPath, long Size, string Sha256,
  DateTimeOffset ModifiedUtc, string FullPath, string ContentType) {
  public object PublicView() => new { Id, Type, FileName, TargetPath, Size, Sha256, ModifiedUtc };
}

sealed class FileCatalog(IHostEnvironment environment, IOptions<SyncOptions> configured) {
  static readonly HashSet<string> BookExtensions = new(StringComparer.OrdinalIgnoreCase) { ".epub", ".txt", ".xtc" };
  static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".bmp", ".jpg", ".jpeg" };
  readonly ConcurrentDictionary<string, (long Size, long Ticks, string Hash)> hashCache = new();
  readonly string root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured.Value.StoragePath));

  public async Task<IReadOnlyList<CatalogFile>> ScanAsync(string? filter, CancellationToken ct) {
    Directory.CreateDirectory(root);
    var files = new List<CatalogFile>();
    foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
      ct.ThrowIfCancellationRequested();
      var info = new FileInfo(path);
      var type = Classify(info.Extension);
      if (type is null || (!string.IsNullOrWhiteSpace(filter) && filter != type)) continue;
      var hash = await HashAsync(path, info, ct);
      files.Add(Create(path, info, type, hash));
    }
    return files.OrderByDescending(x => x.ModifiedUtc).ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
  }

  public async Task<CatalogFile?> FindAsync(string id, CancellationToken ct) =>
    (await ScanAsync(null, ct)).FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));

  public async Task<CatalogFile> SaveAsync(IFormFile upload, string requestedType, CancellationToken ct) {
    var safeName = Path.GetFileName(upload.FileName);
    if (safeName.Length is 0 or > 180 || safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
      throw new ArgumentException("Invalid file name");
    var detected = Classify(Path.GetExtension(safeName)) ?? throw new ArgumentException("Unsupported file extension");
    var type = requestedType is "book" or "image" ? requestedType : detected;
    if (type != detected) throw new ArgumentException("The selected type does not match the file extension");
    var directory = Path.Combine(root, type == "book" ? "books" : "images");
    Directory.CreateDirectory(directory);
    var destination = Path.Combine(directory, safeName);
    var part = destination + ".part";
    await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
      await upload.CopyToAsync(output, ct);
    File.Move(part, destination, true);
    hashCache.TryRemove(destination, out _);
    var info = new FileInfo(destination);
    return Create(destination, info, type, await HashAsync(destination, info, ct));
  }

  public async Task<bool> DeleteAsync(string id, CancellationToken ct) {
    var file = await FindAsync(id, ct);
    if (file is null) return false;
    File.Delete(file.FullPath);
    hashCache.TryRemove(file.FullPath, out _);
    return true;
  }

  CatalogFile Create(string path, FileInfo info, string type, string hash) {
    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
    var target = type == "book" ? $"/Books/{info.Name}" : $"/Images/{info.Name}";
    return new(hash[..24], type, info.Name, target, info.Length, hash, info.LastWriteTimeUtc, path, ContentType(info.Extension));
  }

  async Task<string> HashAsync(string path, FileInfo info, CancellationToken ct) {
    if (hashCache.TryGetValue(path, out var cached) && cached.Size == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks)
      return cached.Hash;
    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
    hashCache[path] = (info.Length, info.LastWriteTimeUtc.Ticks, hash);
    return hash;
  }

  static string? Classify(string extension) => BookExtensions.Contains(extension) ? "book" : ImageExtensions.Contains(extension) ? "image" : null;
  static string ContentType(string extension) => extension.ToLowerInvariant() switch {
    ".epub" => "application/epub+zip", ".txt" => "text/plain", ".png" => "image/png", ".bmp" => "image/bmp",
    ".jpg" or ".jpeg" => "image/jpeg", _ => "application/octet-stream"
  };
}
