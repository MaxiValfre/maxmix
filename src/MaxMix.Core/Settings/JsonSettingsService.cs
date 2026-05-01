namespace MaxMix.Core.Settings;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// JSON-backed implementation of <see cref="ISettingsService"/>. Persists
/// <see cref="AppSettings"/> to a file on disk (default location:
/// %LOCALAPPDATA%\MaxMix\settings.json) using <see cref="System.Text.Json"/>
/// with pretty-printing for human readability.
///
/// Spec section 5:
///   - First launch: file missing → create from <see cref="AppSettings.Defaults"/>.
///   - Corrupted JSON: rename to .bak, write a fresh defaults file.
///   - Schema migration: SchemaVersion &lt; CurrentSchemaVersion → migrate, save.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,                 // pretty-print
        PropertyNameCaseInsensitive = true,   // tolerate hand-edited files
    };

    private readonly string _path;
    private readonly ILogger<JsonSettingsService> _log;
    private AppSettings _current = AppSettings.Defaults;

    public AppSettings Current => _current;
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>
    /// Default location: <c>%LOCALAPPDATA%\MaxMix\settings.json</c>.
    /// </summary>
    public JsonSettingsService()
        : this(DefaultPath(), null) { }

    /// <summary>
    /// DI-friendly constructor: default path + injected logger.
    /// </summary>
    public JsonSettingsService(ILogger<JsonSettingsService>? logger)
        : this(DefaultPath(), logger) { }

    /// <summary>
    /// Constructor for tests / non-default storage.
    /// </summary>
    public JsonSettingsService(string path)
        : this(path, null) { }

    public JsonSettingsService(string path, ILogger<JsonSettingsService>? logger)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Path must be non-empty.", nameof(path));
        _path = path;
        _log = logger ?? NullLogger<JsonSettingsService>.Instance;
    }

    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MaxMix",
            "settings.json");

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsureDirectory();

        // First launch: no file → write defaults.
        if (!File.Exists(_path))
        {
            _log.LogInformation("Settings file not found at {Path}; creating defaults.", _path);
            _current = AppSettings.Defaults;
            await WriteAsync(_current, cancellationToken).ConfigureAwait(false);
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // File exists but couldn't be read — fall back to in-memory defaults
            // without overwriting whatever is on disk.
            _current = AppSettings.Defaults;
            return;
        }

        AppSettingsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AppSettingsDto>(text, SerializerOptions);
        }
        catch (JsonException ex)
        {
            // Corrupted file: back up and rewrite with defaults.
            _log.LogWarning(ex, "Settings file at {Path} is corrupted; backing up and writing defaults.", _path);
            BackupCorruptedFile();
            _current = AppSettings.Defaults;
            await WriteAsync(_current, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (dto is null)
        {
            BackupCorruptedFile();
            _current = AppSettings.Defaults;
            await WriteAsync(_current, cancellationToken).ConfigureAwait(false);
            return;
        }

        var settings = dto.ToAppSettings();

        // Sequential migration: each version handler raises the version one step.
        if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
        {
            _log.LogInformation(
                "Migrating settings schema from v{From} to v{To}.",
                settings.SchemaVersion, AppSettings.CurrentSchemaVersion);
            settings = Migrate(settings);
            _current = settings;
            await WriteAsync(_current, cancellationToken).ConfigureAwait(false);
            return;
        }

        _current = settings;
        _log.LogInformation("Settings loaded from {Path} (schema v{Version}).", _path, settings.SchemaVersion);
    }

    public async Task SaveAsync(AppSettings next, CancellationToken cancellationToken = default)
    {
        if (next is null) throw new ArgumentNullException(nameof(next));
        EnsureDirectory();
        await WriteAsync(next, cancellationToken).ConfigureAwait(false);
        _current = next;
        _log.LogInformation("Settings saved to {Path}.", _path);
        SettingsChanged?.Invoke(this, next);
    }

    // -------- helpers --------

    private void EnsureDirectory()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private async Task WriteAsync(AppSettings s, CancellationToken ct)
    {
        var dto = AppSettingsDto.From(s);
        var json = JsonSerializer.Serialize(dto, SerializerOptions);
        await File.WriteAllTextAsync(_path, json, ct).ConfigureAwait(false);
    }

    private void BackupCorruptedFile()
    {
        var backupPath = _path + ".bak";
        try
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(_path, backupPath);
        }
        catch (IOException)
        {
            // Best-effort: if we can't move, try to copy + delete.
            try
            {
                File.Copy(_path, backupPath, overwrite: true);
                File.Delete(_path);
            }
            catch (IOException)
            {
                // Give up on backup — the new defaults write below will overwrite.
            }
        }
    }

    /// <summary>
    /// Sequential migration. Add a new branch per schema version bump.
    /// </summary>
    private static AppSettings Migrate(AppSettings input)
    {
        var s = input;

        // v0 → v1: introduced StartMinimized. Per the start-with-windows
        // spec (§Defaults applied on missing flags), an omitted flag falls
        // back to AppSettings.Defaults.StartMinimized (= false), which the
        // DTO already supplies. We therefore only bump the schema version
        // here and leave the value as deserialised (defaulting to false when
        // missing from the file).
        if (s.SchemaVersion < 1)
        {
            s = s with { SchemaVersion = 1 };
        }

        // v1 → v2: introduced desktop-side idle lighting settings.
        if (s.SchemaVersion < 2)
        {
            s = s with
            {
                SchemaVersion = 2,
                IdleLightingEnabled = false,
                IdleLightingAfterSeconds = 30,
                IdleLightingPresetName = "Aurora",
            };
        }

        // v2 → v3: added idle-lighting animation mode + speed.
        if (s.SchemaVersion < 3)
        {
            s = s with
            {
                SchemaVersion = 3,
                IdleLightingMode = IdleLightingMode.Static,
                IdleLightingSpeed = 5,
            };
        }

        // v3 -> v4: persist explicit idle colors so animations are independent
        // from the preset registry once saved.
        if (s.SchemaVersion < 4)
        {
            var preset = ColorPresets.GetByName(s.IdleLightingPresetName ?? string.Empty)
                ?? ColorPresets.Aurora;
            s = s with
            {
                SchemaVersion = 4,
                IdleLightingColorA = preset.VolumeMinColor,
                IdleLightingColorB = preset.VolumeMaxColor,
            };
        }

        return s;
    }

    // -------- DTO --------
    //
    // We use a mutable DTO because AppSettings is an immutable record with an
    // IReadOnlyDictionary; System.Text.Json can't construct that directly.
    // The DTO normalises null/missing fields into defaults.

    private sealed class AppSettingsDto
    {
        public int SchemaVersion { get; set; } = AppSettings.CurrentSchemaVersion;
        public DisplayMode StartupMode { get; set; } = DisplayMode.Application;
        public byte AccelerationPercentage { get; set; } = 50;
        public bool ContinuousScroll { get; set; } = true;
        public byte SleepAfterSeconds { get; set; } = 0;
        public bool AutoStartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public bool IdleLightingEnabled { get; set; } = false;
        public byte IdleLightingAfterSeconds { get; set; } = 30;
        public string? IdleLightingPresetName { get; set; } = "Aurora";
        public IdleLightingMode IdleLightingMode { get; set; } = IdleLightingMode.Static;
        public byte IdleLightingSpeed { get; set; } = 5;
        public RgbColor IdleLightingColorA { get; set; } = new(20, 220, 120);
        public RgbColor IdleLightingColorB { get; set; } = new(40, 200, 255);
        public string? LastConnectedPort { get; set; }
        public Dictionary<DisplayMode, ColorProfile> ColorProfiles { get; set; } = new();

        public static AppSettingsDto From(AppSettings s)
        {
            var dto = new AppSettingsDto
            {
                SchemaVersion = s.SchemaVersion,
                StartupMode = s.StartupMode,
                AccelerationPercentage = s.AccelerationPercentage,
                ContinuousScroll = s.ContinuousScroll,
                SleepAfterSeconds = s.SleepAfterSeconds,
                AutoStartWithWindows = s.AutoStartWithWindows,
                StartMinimized = s.StartMinimized,
                MinimizeToTray = s.MinimizeToTray,
                IdleLightingEnabled = s.IdleLightingEnabled,
                IdleLightingAfterSeconds = s.IdleLightingAfterSeconds,
                IdleLightingPresetName = s.IdleLightingPresetName,
                IdleLightingMode = s.IdleLightingMode,
                IdleLightingSpeed = s.IdleLightingSpeed,
                IdleLightingColorA = s.IdleLightingColorA,
                IdleLightingColorB = s.IdleLightingColorB,
                LastConnectedPort = s.LastConnectedPort,
            };
            foreach (var kv in s.ColorProfiles)
            {
                dto.ColorProfiles[kv.Key] = kv.Value;
            }
            return dto;
        }

        public AppSettings ToAppSettings() => new(
            SchemaVersion:          SchemaVersion,
            StartupMode:            StartupMode,
            AccelerationPercentage: AccelerationPercentage,
            ContinuousScroll:       ContinuousScroll,
            SleepAfterSeconds:      SleepAfterSeconds,
            AutoStartWithWindows:   AutoStartWithWindows,
            StartMinimized:         StartMinimized,
            MinimizeToTray:         MinimizeToTray,
            LastConnectedPort:      LastConnectedPort,
            ColorProfiles:          new Dictionary<DisplayMode, ColorProfile>(ColorProfiles))
        {
            IdleLightingEnabled = IdleLightingEnabled,
            IdleLightingAfterSeconds = IdleLightingAfterSeconds,
            IdleLightingPresetName = IdleLightingPresetName,
            IdleLightingMode = IdleLightingMode,
            IdleLightingSpeed = IdleLightingSpeed,
            IdleLightingColorA = IdleLightingColorA,
            IdleLightingColorB = IdleLightingColorB,
        };
    }
}
