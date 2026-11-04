namespace SQLExtended.Settings;

/// <summary>
/// Stand-in for the extension's settings object, which is the only thing the linked
/// <c>ModuleDecryptionService</c> reads (<c>SQLExtendedSettings.Current.DecryptEncryptedModules</c>). The real one
/// loads %APPDATA%\SQLExtended\SSMS\sqlextended-settings.json — the wrong source for a scheduled task running as a
/// service account. Here the value comes from the exporter's own config, set per server before each export.
/// </summary>
internal sealed class SQLExtendedSettings
{
    public static SQLExtendedSettings Current { get; } = new();

    public bool DecryptEncryptedModules { get; set; }
}
