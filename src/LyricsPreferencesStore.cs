using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AMLyrics {
 public enum LyricsPreferencesLoadStatus {
  Loaded,
  DefaultsUsed,
  CorruptConfiguration,
  LoadFailed
 }

 public enum LyricsPreferencesSaveStatus {
  Saved,
  SaveFailed
 }

 public sealed class LyricsPreferencesLoadResult {
  public LyricsWindowPreferences Preferences { get; private set; }
  public LyricsPreferencesLoadStatus Status { get; private set; }
  public string Error { get; private set; }

  internal LyricsPreferencesLoadResult(LyricsWindowPreferences preferences,
   LyricsPreferencesLoadStatus status, string error) {
   Preferences = preferences;
   Status = status;
   Error = error;
  }

  public bool UsedDefaults {
   get { return Status != LyricsPreferencesLoadStatus.Loaded; }
  }
 }

 public sealed class LyricsPreferencesSaveResult {
  public LyricsPreferencesSaveStatus Status { get; private set; }
  public string Error { get; private set; }

  internal LyricsPreferencesSaveResult(LyricsPreferencesSaveStatus status, string error) {
   Status = status;
   Error = error;
  }

  public bool Succeeded {
   get { return Status == LyricsPreferencesSaveStatus.Saved; }
  }
 }

 public sealed class LyricsPreferencesStore {
  public const int CurrentVersion = 1;
  public const string DefaultRelativePath = "work\\state\\settings.json";

  readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
  string loadError;

  public string FilePath { get; private set; }
  public LyricsWindowPreferences Current { get; private set; }
  public LyricsPreferencesLoadStatus LastLoadStatus { get; private set; }
  public LyricsPreferencesSaveStatus LastSaveStatus { get; private set; }
  public string LastError { get; private set; }

  public LyricsPreferencesStore(string filePath) {
   if (String.IsNullOrWhiteSpace(filePath)) {
    string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
    string projectRoot = FindProjectRoot(baseDirectory);
    filePath = Path.Combine(projectRoot ?? baseDirectory, DefaultRelativePath);
   }
   FilePath = Path.GetFullPath(filePath);
   Current = CreateDefaultPreferences();
   LastLoadStatus = LyricsPreferencesLoadStatus.DefaultsUsed;
   LastSaveStatus = LyricsPreferencesSaveStatus.SaveFailed;
  }

  public LyricsPreferencesStore() : this(null) { }

  public LyricsPreferencesLoadResult Load() {
   LyricsWindowPreferences defaults = CreateDefaultPreferences();
   LastError = loadError = null;

   try {
    PersistedPreferences persisted = ReadIfPresent(FilePath);
    Current = persisted == null ? defaults : FromPersisted(persisted);
    LastLoadStatus = persisted == null ? LyricsPreferencesLoadStatus.DefaultsUsed : LyricsPreferencesLoadStatus.Loaded;
    return new LyricsPreferencesLoadResult(Current.Clone(), LastLoadStatus, null);
   } catch (Exception ex) {
    // Keep the original file untouched. The caller can decide whether to retry or replace it.
    Current = defaults;
    LastError = loadError = FormatError(ex);
    LastLoadStatus = IsConfigurationError(ex)
     ? LyricsPreferencesLoadStatus.CorruptConfiguration
     : LyricsPreferencesLoadStatus.LoadFailed;
    return new LyricsPreferencesLoadResult(Current.Clone(), LastLoadStatus, LastError);
   }
  }

  public LyricsPreferencesSaveResult Save(LyricsWindowPreferences preferences) {
   LastError = null;
   if (preferences == null) {
    LastError = "不能保存空的歌词偏好。";
    LastSaveStatus = LyricsPreferencesSaveStatus.SaveFailed;
    return new LyricsPreferencesSaveResult(LastSaveStatus, LastError);
   }

   string temporaryPath = null;
   try {
    if (loadError != null) throw new InvalidOperationException("原配置无法加载，已保留且未保存：" + loadError);
    bool replaceExisting = ReadIfPresent(FilePath) != null;
    LyricsWindowPreferences normalized = preferences.Clone();
    if (!IsFinite(normalized.Width)) normalized.Width = LyricsWindowPreferences.DefaultWidth;
    if (normalized.Display == null) normalized.Display = new LyricsDisplaySettings();
    if (!IsFinite(normalized.Display.FontSize)) normalized.Display.FontSize = 30;
    if (!IsFinite(normalized.Display.BackgroundOpacity)) normalized.Display.BackgroundOpacity = 0;
    normalized.Normalize();
    PersistedPreferences persisted = ToPersisted(normalized);
    string directory = Path.GetDirectoryName(FilePath);
    if (String.IsNullOrEmpty(directory)) throw new InvalidOperationException("配置路径没有有效目录。");
    Directory.CreateDirectory(directory);

    temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    string json = serializer.Serialize(persisted);
    using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew,
     FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
     byte[] bytes = new UTF8Encoding(false).GetBytes(json);
     stream.Write(bytes, 0, bytes.Length);
     stream.Flush(true);
    }

    if (replaceExisting) File.Replace(temporaryPath, FilePath, null);
    else File.Move(temporaryPath, FilePath);
    temporaryPath = null;

    Current = normalized;
    LastSaveStatus = LyricsPreferencesSaveStatus.Saved;
    return new LyricsPreferencesSaveResult(LastSaveStatus, null);
   } catch (Exception ex) {
    LastError = FormatError(ex);
    LastSaveStatus = LyricsPreferencesSaveStatus.SaveFailed;
    return new LyricsPreferencesSaveResult(LastSaveStatus, LastError);
   } finally {
    if (!String.IsNullOrEmpty(temporaryPath)) {
     try { File.Delete(temporaryPath); } catch { }
    }
   }
  }

  public static LyricsWindowPreferences CreateDefaultPreferences() {
   LyricsWindowPreferences preferences = new LyricsWindowPreferences {
    Enabled = false,
    Locked = false,
    Width = LyricsWindowPreferences.DefaultWidth,
    Left = Double.NaN,
    Top = Double.NaN,
    ScreenDeviceName = "",
    Display = new LyricsDisplaySettings {
     FontSize = 30,
     ForegroundHex = "#FFF7FA",
     BackgroundOpacity = 0,
     StrokeEnabled = true,
     ShadowEnabled = true,
     AnimateLongLines = true,
     ShowTranslation = false,
     ShowRomanization = false,
     CustomColors = new int[0]
    }
   };
   preferences.Normalize();
   return preferences;
  }

  static string FindProjectRoot(string baseDirectory) {
   for (var directory = new DirectoryInfo(baseDirectory); directory != null; directory = directory.Parent) {
    string candidate = directory.FullName;
    if (Directory.Exists(Path.Combine(candidate, "src")) &&
     Directory.Exists(Path.Combine(candidate, "scripts")) && Directory.Exists(Path.Combine(candidate, "outputs"))) return candidate;
   }
   return null;
  }

  PersistedPreferences ReadIfPresent(string path) {
   string json;
   try { json = File.ReadAllText(path, new UTF8Encoding(false, true)); }
   catch (FileNotFoundException) { return null; }
   catch (DirectoryNotFoundException) { return null; }
   var values = serializer.DeserializeObject(json) as Dictionary<string,object>;
   if (values == null) throw new InvalidDataException("配置文件为空或不是 JSON 对象。");
   object version;
   if (!values.TryGetValue("Version", out version) || !(version is int)) throw new InvalidDataException("配置文件缺少有效版本号。");
   if ((int)version != CurrentVersion) throw new NotSupportedException("不支持此配置版本。");
   return serializer.ConvertToType<PersistedPreferences>(values);
  }

  static bool IsConfigurationError(Exception ex) {
   return ex is InvalidDataException || ex is ArgumentException ||
    ex is FormatException || ex is InvalidOperationException;
  }

  static string FormatError(Exception ex) {
   return ex.GetType().Name + ": " + ex.Message;
  }

  static PersistedPreferences ToPersisted(LyricsWindowPreferences preferences) {
   LyricsDisplaySettings display = preferences.Display ?? new LyricsDisplaySettings();
   int[] custom = LyricsDisplaySettings.NormalizeCustomColors(display.CustomColors);
   return new PersistedPreferences {
    Version = CurrentVersion,
    Enabled = preferences.Enabled,
    Locked = preferences.Locked,
    Left = FiniteOrNull(preferences.Left),
    Top = FiniteOrNull(preferences.Top),
    ScreenDeviceName = preferences.ScreenDeviceName ?? "",
    Width = FiniteOrDefault(preferences.Width, LyricsWindowPreferences.DefaultWidth),
    TimingOffsets = preferences.TimingOffsets.Count == 0 ? null : preferences.TimingOffsets,
    Display = new PersistedDisplaySettings {
     FontSize = FiniteOrDefault(display.FontSize, 30),
     ForegroundHex = display.ForegroundHex ?? "#FFF7FA",
     FontFamilyName = display.FontFamilyName ?? "Segoe UI",
     FontWeightName = display.FontWeightName ?? "常规",
     StrokeHex = display.StrokeHex ?? "#0F0F12",
     BackgroundOpacity = FiniteOrDefault(display.BackgroundOpacity, 0),
     StrokeEnabled = display.StrokeEnabled,
     ShadowEnabled = display.ShadowEnabled,
     AnimateLongLines = display.AnimateLongLines,
     ShowTranslation = display.ShowTranslation,
     ShowRomanization = display.ShowRomanization,
     CustomColors = custom.Length > 0 ? custom : null
    }
   };
  }

  static LyricsWindowPreferences FromPersisted(PersistedPreferences persisted) {
   PersistedDisplaySettings display = persisted.Display ?? new PersistedDisplaySettings();
   LyricsWindowPreferences preferences = new LyricsWindowPreferences {
    Enabled = persisted.Enabled ?? false,
    Locked = persisted.Locked ?? false,
    Left = persisted.Left.HasValue && IsFinite(persisted.Left.Value) ? persisted.Left.Value : Double.NaN,
    Top = persisted.Top.HasValue && IsFinite(persisted.Top.Value) ? persisted.Top.Value : Double.NaN,
    ScreenDeviceName = persisted.ScreenDeviceName ?? "",
    Width = FiniteOrDefault(persisted.Width, LyricsWindowPreferences.DefaultWidth),
    TimingOffsets = persisted.TimingOffsets,
    Display = new LyricsDisplaySettings {
     FontSize = FiniteOrDefault(display.FontSize, 30),
     ForegroundHex = String.IsNullOrWhiteSpace(display.ForegroundHex) ? "#FFF7FA" : display.ForegroundHex,
     FontFamilyName = String.IsNullOrWhiteSpace(display.FontFamilyName) ? "Segoe UI" : display.FontFamilyName,
     FontWeightName = String.IsNullOrWhiteSpace(display.FontWeightName) ? "常规" : display.FontWeightName,
     StrokeHex = String.IsNullOrWhiteSpace(display.StrokeHex) ? "#0F0F12" : display.StrokeHex,
     BackgroundOpacity = FiniteOrDefault(display.BackgroundOpacity, 0),
     StrokeEnabled = display.StrokeEnabled ?? true,
     ShadowEnabled = display.ShadowEnabled ?? true,
     AnimateLongLines = display.AnimateLongLines ?? true,
     ShowTranslation = display.ShowTranslation ?? false,
     ShowRomanization = display.ShowRomanization ?? false,
     CustomColors = LyricsDisplaySettings.NormalizeCustomColors(display.CustomColors)
    }
   };
   preferences.Normalize();
   return preferences;
  }

  static double? FiniteOrNull(double value) {
   return IsFinite(value) ? (double?)value : null;
  }

  static double FiniteOrDefault(double? value, double fallback) {
   return value.HasValue && IsFinite(value.Value) ? value.Value : fallback;
  }

  static bool IsFinite(double value) {
   return !Double.IsNaN(value) && !Double.IsInfinity(value);
  }

  public sealed class PersistedPreferences {
   public int Version { get; set; }
   public bool? Enabled { get; set; }
   public bool? Locked { get; set; }
   public double? Left { get; set; }
   public double? Top { get; set; }
   public string ScreenDeviceName { get; set; }
   public double? Width { get; set; }
   public PersistedDisplaySettings Display { get; set; }
   public Dictionary<string,double> TimingOffsets { get; set; }
  }

  public sealed class PersistedDisplaySettings {
   public double? FontSize { get; set; }
   public string ForegroundHex { get; set; }
   public string FontFamilyName { get; set; }
   public string FontWeightName { get; set; }
   public string StrokeHex { get; set; }
   public double? BackgroundOpacity { get; set; }
   public bool? StrokeEnabled { get; set; }
   public bool? ShadowEnabled { get; set; }
   public bool? AnimateLongLines { get; set; }
   public bool? ShowTranslation { get; set; }
   public bool? ShowRomanization { get; set; }
   public int[] CustomColors { get; set; }
  }
 }
}
