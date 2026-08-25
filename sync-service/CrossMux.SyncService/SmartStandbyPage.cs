using System.Reflection;

static class SmartStandbyPage {
  static readonly Lazy<string> Content = new(() => {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CrossMux.SyncService.standby.html")
      ?? throw new InvalidOperationException("Embedded Smart Standby page is missing");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  });

  public static string Html => Content.Value;
}
