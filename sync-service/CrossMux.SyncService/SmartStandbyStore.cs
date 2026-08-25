using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;

sealed class SmartStandbyStore(IHostEnvironment environment, IOptions<SmartStandbyOptions> configured) {
  readonly SemaphoreSlim gate = new(1, 1);
  readonly string root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured.Value.StoragePath));

  public async Task<IReadOnlyList<SmartStandbyTemplate>> ListAsync(CancellationToken ct) {
    Directory.CreateDirectory(root);
    var templates = new List<SmartStandbyTemplate>();
    foreach (var path in Directory.EnumerateFiles(root, "*.template.json", SearchOption.TopDirectoryOnly)) {
      await using var input = File.OpenRead(path);
      var item = await JsonSerializer.DeserializeAsync(input, SmartStandbyJsonContext.Default.SmartStandbyTemplate, ct);
      if (item is not null) templates.Add(item);
    }
    return templates.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
  }

  public async Task<SmartStandbyTemplate?> FindAsync(string id, CancellationToken ct) =>
    (await ListAsync(ct)).FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

  public async Task<SmartStandbyTemplate?> FindByDeviceAsync(string deviceId, CancellationToken ct) =>
    (await ListAsync(ct)).FirstOrDefault(x => x.Enabled && string.Equals(x.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

  public async Task<SmartStandbyTemplate> SaveAsync(SmartStandbyTemplate input, CancellationToken ct) {
    var id = SafeId(input.Id, "template");
    var deviceId = SafeId(input.DeviceId, "deviceId");
    var normalized = input with {
      Id = id, DeviceId = deviceId, Width = Math.Clamp(input.Width, 320, 1200), Height = Math.Clamp(input.Height, 240, 1200),
      Widgets = input.Widgets.Take(64).ToList(), DataSources = input.DataSources.Take(8).ToList(), UpdatedUtc = DateTimeOffset.UtcNow
    };
    Directory.CreateDirectory(root);
    await gate.WaitAsync(ct);
    try {
      var target = Path.Combine(root, id + ".template.json");
      var part = target + ".part";
      await using (var output = File.Create(part))
        await JsonSerializer.SerializeAsync(output, normalized, SmartStandbyJsonContext.Default.SmartStandbyTemplate, ct);
      File.Move(part, target, true);
    } finally { gate.Release(); }
    return normalized;
  }

  public async Task<SmartStandbyStatus> SaveImageAsync(SmartStandbyTemplate template, byte[] image, string baseUrl, CancellationToken ct) {
    Directory.CreateDirectory(root);
    var imagePath = Path.Combine(root, template.DeviceId + ".bmp");
    var part = imagePath + ".part";
    await File.WriteAllBytesAsync(part, image, ct);
    File.Move(part, imagePath, true);
    var hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
    var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var status = new SmartStandbyStatus(template.DeviceId, template.Id, version, hash, image.Length,
      DateTimeOffset.UtcNow, $"{baseUrl}/api/v1/standby/{template.DeviceId}/image.bmp?v={version}");
    await File.WriteAllTextAsync(Path.Combine(root, template.DeviceId + ".status.json"), JsonSerializer.Serialize(status), ct);
    return status;
  }

  public string? ImagePath(string deviceId) {
    var safe = SafeId(deviceId, "deviceId");
    var path = Path.Combine(root, safe + ".bmp");
    return File.Exists(path) ? path : null;
  }

  static string SafeId(string value, string name) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
      throw new ArgumentException($"Invalid {name}");
    return value;
  }
}

sealed class SmartStandbyPublisher(
  SmartStandbyStore store,
  SmartStandbyRenderer renderer,
  IHttpClientFactory httpFactory,
  IOptions<SmartStandbyOptions> configured,
  IOptions<SyncOptions> syncConfigured,
  ILogger<SmartStandbyPublisher> logger) : BackgroundService {
  readonly SmartStandbyOptions options = configured.Value;
  readonly ConcurrentDictionary<string, SemaphoreSlim> deviceLocks = new(StringComparer.OrdinalIgnoreCase);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.RefreshSeconds, 30, 86400)));
    do {
      try {
        foreach (var template in await store.ListAsync(stoppingToken))
          if (template.Enabled) await RenderAndPublishAsync(template, null, stoppingToken);
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
      catch (Exception ex) { logger.LogError(ex, "Smart standby refresh cycle failed"); }
    } while (await timer.WaitForNextTickAsync(stoppingToken));
  }

  public async Task<SmartStandbyStatus> RenderAndPublishAsync(SmartStandbyTemplate template, string? requestBaseUrl, CancellationToken ct) {
    var deviceLock = deviceLocks.GetOrAdd(template.DeviceId, _ => new SemaphoreSlim(1, 1));
    await deviceLock.WaitAsync(ct);
    try {
      var http = httpFactory.CreateClient("smart-standby");
      var values = await renderer.ResolveValuesAsync(template, http, ct);
      var images = await renderer.ResolveImagesAsync(template, values, http, ct);
      var image = renderer.Render(template, values, images);
      var baseUrl = (requestBaseUrl ?? syncConfigured.Value.PublicBaseUrl).TrimEnd('/');
      if (string.IsNullOrEmpty(baseUrl)) baseUrl = "http://localhost:8080";
      var status = await store.SaveImageAsync(template, image, baseUrl, ct);
      await PublishMqttAsync(status, ct);
      return status;
    } finally { deviceLock.Release(); }
  }

  async Task PublishMqttAsync(SmartStandbyStatus status, CancellationToken ct) {
    var factory = new MqttFactory();
    using var client = factory.CreateMqttClient();
    var builder = new MqttClientOptionsBuilder().WithTcpServer(options.MqttHost, options.MqttPort)
      .WithClientId("crossmux-sync-" + Guid.NewGuid().ToString("N")[..12]).WithCleanSession();
    await client.ConnectAsync(builder.Build(), ct);
    var payload = JsonSerializer.SerializeToUtf8Bytes(status);
    var message = new MqttApplicationMessageBuilder().WithTopic($"crossmux/device/{status.DeviceId}/standby/v1")
      .WithPayload(payload).WithRetainFlag().WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).Build();
    await client.PublishAsync(message, ct);
    await client.DisconnectAsync(new MqttClientDisconnectOptions(), ct);
  }
}
