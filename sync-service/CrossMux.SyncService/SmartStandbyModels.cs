using System.Text.Json.Serialization;

sealed class SmartStandbyOptions {
  public string StoragePath { get; set; } = "smart-standby";
  public int RefreshSeconds { get; set; } = 300;
  public string MqttHost { get; set; } = "mqtt-cn.uipcat.com";
  public int MqttPort { get; set; } = 1883;
}

sealed record SmartStandbyTemplate(
  string Id,
  string Name,
  string DeviceId,
  int Width,
  int Height,
  string Background,
  List<SmartWidget> Widgets,
  List<SmartDataSource> DataSources,
  Dictionary<string, string> Values,
  bool Enabled = true,
  DateTimeOffset? UpdatedUtc = null);

sealed record SmartWidget(
  string Type,
  int X,
  int Y,
  int Width,
  int Height,
  string? Title = null,
  string? Value = null,
  float FontSize = 28,
  string Align = "left",
  bool Inverted = false,
  bool Border = false);

sealed record SmartDataSource(
  string Id,
  string Url,
  Dictionary<string, string> Bindings,
  Dictionary<string, string>? Headers = null);

sealed record SmartStandbyStatus(
  string DeviceId,
  string TemplateId,
  long Version,
  string Sha256,
  long Size,
  DateTimeOffset UpdatedUtc,
  string ImageUrl);

[JsonSerializable(typeof(SmartStandbyTemplate))]
[JsonSerializable(typeof(List<SmartStandbyTemplate>))]
[JsonSerializable(typeof(SmartStandbyStatus))]
partial class SmartStandbyJsonContext : JsonSerializerContext;
