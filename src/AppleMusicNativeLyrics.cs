using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace AMLyrics {
 public enum NativeLyricsAvailability {
  Unavailable,
  CloakedOtherDesktop,
  PanelClosed,
  Empty,
  Available
 }

 public sealed class NativeLyricEntry {
  public string Text = "";
  public string Translation = "";
  public string Romanization = "";
  public bool IsInstrumental;
  public bool IsOffscreen;
  public double Top;
  public double Height;
 }

 public sealed class NativeChildNode {
  public string AutomationId = "";
  public string Text = "";
 }

 public sealed class NativeLyricsSnapshot {
  public NativeLyricsAvailability Availability;
  public List<NativeLyricEntry> Entries = new List<NativeLyricEntry>();
  public int ActiveIndex = -1;
  public double PaneTop;
  public double PaneHeight;
  public double VerticalScrollPercent = -1;
  public bool HasTranslation;
  public bool HasRomanization;
  public DateTime CapturedAtUtc = DateTime.UtcNow;
  public string Fingerprint = "";
 }

 public static class NativeLyricsParser {
  public static string CleanNativeText(string text) {
   if (string.IsNullOrEmpty(text)) return "";
   var sb = new StringBuilder(text.Length);
   bool prevSpace = true;
   for (int i = 0; i < text.Length; i++) {
    char c = text[i];
    if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
    if (c == '\u00A0' || c == '\t' || c == '\r' || c == '\n') c = ' ';
    if (c == ' ') {
     if (!prevSpace) {
      sb.Append(' ');
      prevSpace = true;
     }
    } else {
     sb.Append(c);
     prevSpace = false;
    }
   }
   return sb.ToString().Trim();
  }

  public static NativeLyricEntry ClassifyButton(
   string buttonName,
   bool isEnabled,
   IEnumerable<NativeChildNode> children,
   bool isOffscreen,
   double top,
   double height) {

   var childList = new List<NativeChildNode>();
   if (children != null) {
    foreach (var child in children) {
     if (child != null) childList.Add(child);
    }
   }

   bool hasInstrumentalChild = childList.Any(c =>
    (c.AutomationId ?? "").StartsWith("InstrumentalTextBlock", StringComparison.OrdinalIgnoreCase));

   string orig = CleanNativeText(buttonName);

   if (hasInstrumentalChild || (orig.Length == 0 && !isEnabled)) {
    return new NativeLyricEntry {
     Text = "",
     IsInstrumental = true,
     IsOffscreen = isOffscreen,
     Top = top,
     Height = height
    };
   }

   string translation = "";
   string romanization = "";
   var unclassified = new List<string>();
   var seenUnclassified = new HashSet<string>(StringComparer.Ordinal);

   for (int i = 0; i < childList.Count; i++) {
    string childId = (childList[i].AutomationId ?? "").Trim();
    string childText = CleanNativeText(childList[i].Text);
    if (childText.Length == 0) continue;
    if (string.Equals(childId, "WritersTextBlock", StringComparison.OrdinalIgnoreCase)) continue;
    if (childId.StartsWith("InstrumentalTextBlock", StringComparison.OrdinalIgnoreCase)) continue;
    if (string.Equals(childText, orig, StringComparison.Ordinal)) continue;

    if (childId.IndexOf("Translation", StringComparison.OrdinalIgnoreCase) >= 0) {
     if (translation.Length == 0) translation = childText;
    } else if (IsRomanizationId(childId)) {
     if (romanization.Length == 0) romanization = childText;
    } else {
     if (seenUnclassified.Add(childText)) {
      unclassified.Add(childText);
     }
    }
   }

   if (unclassified.Count > 0) {
    if (translation.Length > 0 && romanization.Length == 0) {
     string candidate = unclassified.FirstOrDefault(u => !string.Equals(u, translation, StringComparison.Ordinal));
     if (!string.IsNullOrEmpty(candidate)) romanization = candidate;
    } else if (romanization.Length > 0 && translation.Length == 0) {
     string candidate = unclassified.FirstOrDefault(u => !string.Equals(u, romanization, StringComparison.Ordinal));
     if (!string.IsNullOrEmpty(candidate)) translation = candidate;
    } else if (translation.Length == 0 && romanization.Length == 0) {
     if (unclassified.Count >= 2) {
      romanization = unclassified[0];
      translation = unclassified[1];
     } else if (unclassified.Count == 1) {
      if (HasCjkKanaHangul(orig) && LooksMostlyLatinPhonetic(unclassified[0])) {
       romanization = unclassified[0];
      } else {
       translation = unclassified[0];
      }
     }
    }
   }

   return new NativeLyricEntry {
    Text = orig,
    Translation = translation,
    Romanization = romanization,
    IsInstrumental = false,
    IsOffscreen = isOffscreen,
    Top = top,
    Height = height
   };
  }

  public static int ResolveActiveIndex(IList<NativeLyricEntry> entries, double paneTop, double paneHeight, double vPercent) {
   if (entries == null || entries.Count == 0 || double.IsNaN(paneTop) || double.IsNaN(paneHeight) || paneHeight <= 0) {
    return -1;
   }

   var visible = new List<int>();
   for (int i = 0; i < entries.Count; i++) {
    NativeLyricEntry e = entries[i];
    if (e == null || e.IsOffscreen) continue;
    if (double.IsNaN(e.Top) || double.IsNaN(e.Height) || e.Height <= 0) continue;
    if (e.Top >= paneTop - 2.0 && e.Top < paneTop + paneHeight) {
     visible.Add(i);
    }
   }

   if (visible.Count == 0) return -1;
   if (vPercent >= 0 && vPercent <= 0.5) return visible[0];

   double typicalFullHeight;
   if (visible.Count >= 3) {
    var middleHeights = new List<double>();
    for (int i = 1; i <= visible.Count - 2; i++) {
     middleHeights.Add(entries[visible[i]].Height);
    }
    middleHeights.Sort();
    int mid = middleHeights.Count / 2;
    typicalFullHeight = (middleHeights.Count % 2 == 1)
     ? middleHeights[mid]
     : (middleHeights[mid - 1] + middleHeights[mid]) / 2.0;
   } else if (visible.Count == 2) {
    typicalFullHeight = Math.Max(entries[visible[0]].Height, entries[visible[1]].Height);
   } else {
    typicalFullHeight = entries[visible[0]].Height;
   }

   if (visible.Count >= 2 &&
       entries[visible[0]].Top <= paneTop + 4.0 &&
       (entries[visible[0]].Height < typicalFullHeight * 0.85 ||
        entries[visible[1]].Top - paneTop <= Math.Max(140.0, typicalFullHeight * 0.9))) {
    return visible[1];
   }

   return visible[0];
  }

  static bool IsRomanizationId(string childId) {
   return childId.IndexOf("Pronunciation", StringComparison.OrdinalIgnoreCase) >= 0 ||
          childId.IndexOf("Transliteration", StringComparison.OrdinalIgnoreCase) >= 0 ||
          childId.IndexOf("Romanization", StringComparison.OrdinalIgnoreCase) >= 0 ||
          childId.IndexOf("Phonetic", StringComparison.OrdinalIgnoreCase) >= 0 ||
          childId.IndexOf("Romaji", StringComparison.OrdinalIgnoreCase) >= 0 ||
          childId.IndexOf("Pinyin", StringComparison.OrdinalIgnoreCase) >= 0;
  }

  static bool IsCjkKanaHangulChar(char c) {
   return (c >= '\u3040' && c <= '\u30FF') ||
          (c >= '\u31F0' && c <= '\u31FF') ||
          (c >= '\u4E00' && c <= '\u9FFF') ||
          (c >= '\uAC00' && c <= '\uD7AF');
  }

  static bool HasCjkKanaHangul(string text) {
   if (string.IsNullOrEmpty(text)) return false;
   for (int i = 0; i < text.Length; i++) {
    if (IsCjkKanaHangulChar(text[i])) return true;
   }
   return false;
  }

  static bool LooksMostlyLatinPhonetic(string text) {
   if (string.IsNullOrEmpty(text)) return false;
   int latinLetters = 0;
   int totalLetters = 0;
   int cjkCount = 0;
   for (int i = 0; i < text.Length; i++) {
    char c = text[i];
    if (char.IsLetter(c)) {
     totalLetters++;
     if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) {
      latinLetters++;
     }
     if (IsCjkKanaHangulChar(c)) {
      cjkCount++;
     }
    }
   }
   if (totalLetters == 0) return false;
   return ((double)latinLetters / totalLetters >= 0.40) &&
          (cjkCount == 0 || (double)cjkCount / totalLetters <= 0.15);
  }
 }

 public static class NativeLyricsAligner {
  struct IndexedNormLine {
   public int Index;
   public string Norm;
  }

  struct IndexedNativeLine {
   public int Index;
   public string NormOrig;
   public string NormRom;
  }

  struct MatchedNative {
   public int NativeIndex;
   public bool IsRomMatch;
  }

  public static LyricDocument Enrich(LyricDocument baseDoc, NativeLyricsSnapshot native) {
   if (baseDoc == null || baseDoc.Lines == null || baseDoc.Lines.Count == 0) return null;
   if (native == null || native.Availability != NativeLyricsAvailability.Available) return null;
   if (native.Entries == null || native.Entries.Count == 0) return null;
   if (!native.HasTranslation &&
       !native.HasRomanization &&
       !native.Entries.Any(e => e != null && !e.IsInstrumental && KoreanRomanizer.ContainsHangul(e.Text))) {
    return null;
   }

   int lineCount = baseDoc.Lines.Count;
   var baseNonEmpty = new List<IndexedNormLine>();
   var baseParts = new string[lineCount][];
   bool baseHadAny = false;

   for (int i = 0; i < lineCount; i++) {
    LyricLine line = baseDoc.Lines[i];
    string orig, rom, trans;
    LyricsTrackResolver.ExtractParts(line, out orig, out rom, out trans);
    baseParts[i] = new[] { orig, rom, trans };
    if (rom.Length > 0 || trans.Length > 0) baseHadAny = true;

    string norm = LyricsMetadata.Normalize(orig);
    if (norm.Length > 0) {
     baseNonEmpty.Add(new IndexedNormLine { Index = i, Norm = norm });
    }
   }

   var nativeNonEmpty = new List<IndexedNativeLine>();
   for (int i = 0; i < native.Entries.Count; i++) {
    NativeLyricEntry entry = native.Entries[i];
    if (entry == null || entry.IsInstrumental) continue;
    string cleanText = NativeLyricsParser.CleanNativeText(entry.Text);
    string normOrig = LyricsMetadata.Normalize(cleanText);
    string normRom = !string.IsNullOrEmpty(entry.Romanization)
     ? LyricsMetadata.Normalize(entry.Romanization)
     : (KoreanRomanizer.ContainsHangul(cleanText) ? LyricsMetadata.Normalize(KoreanRomanizer.Romanize(cleanText)) : "");
    if (normOrig.Length > 0 || normRom.Length > 0) {
     nativeNonEmpty.Add(new IndexedNativeLine {
      Index = i,
      NormOrig = normOrig,
      NormRom = normRom
     });
    }
   }

   if (baseNonEmpty.Count == 0 || nativeNonEmpty.Count == 0) return null;

   const int nativeWindow = 6;
   const int maxBaseSkipLookahead = 2;
   var baseToNative = new Dictionary<int, MatchedNative>();
   int nIdx = 0;
   int directMatches = 0;
   int romMatches = 0;

   for (int bi = 0; bi < baseNonEmpty.Count && nIdx < nativeNonEmpty.Count; bi++) {
    int nLimit = Math.Min(nativeNonEmpty.Count - 1, nIdx + nativeWindow);
    int foundNi = -1;
    bool foundIsRom = false;
    for (int ni = nIdx; ni <= nLimit; ni++) {
     bool isRom;
     if (MatchesNative(baseNonEmpty[bi].Norm, nativeNonEmpty[ni], out isRom)) {
      foundNi = ni;
      foundIsRom = isRom;
      break;
     }
    }

    if (foundNi < 0) continue;

    if (foundNi > nIdx) {
     bool skipForEarlierNative = false;
     int bLimit = Math.Min(baseNonEmpty.Count - 1, bi + maxBaseSkipLookahead);
     for (int bNext = bi + 1; bNext <= bLimit; bNext++) {
      for (int ni = nIdx; ni < foundNi; ni++) {
       bool unusedIsRom;
       if (MatchesNative(baseNonEmpty[bNext].Norm, nativeNonEmpty[ni], out unusedIsRom)) {
        skipForEarlierNative = true;
        break;
       }
      }
      if (skipForEarlierNative) break;
     }
     if (skipForEarlierNative) continue;
    }

    baseToNative[baseNonEmpty[bi].Index] = new MatchedNative {
     NativeIndex = nativeNonEmpty[foundNi].Index,
     IsRomMatch = foundIsRom
    };
    if (foundIsRom) romMatches++;
    else directMatches++;
    nIdx = foundNi + 1;
   }

   int matched = directMatches + romMatches;
   int denom = Math.Max(1, Math.Min(baseNonEmpty.Count, nativeNonEmpty.Count));
   bool identityPassed = (matched >= 3 && (double)matched / denom >= 0.45) ||
                         (denom < 3 && matched == denom && matched > 0);
   if (!identityPassed) return null;

   bool baseWasRomanized = romMatches > directMatches && romMatches >= 2;
   var enrichedLines = new List<LyricLine>(lineCount);
   bool receivedNew = false;
   bool generatedKoreanRom = false;

   for (int i = 0; i < lineCount; i++) {
    LyricLine origLine = baseDoc.Lines[i];
    string origText = origLine == null ? "" : (origLine.Text ?? "");
    double ts = origLine == null ? 0 : origLine.TimestampSeconds;
    string origPrimary = baseParts[i][0];
    string rom = baseParts[i][1];
    string trans = baseParts[i][2];
    string lineText = origText;

    MatchedNative match;
    if (baseToNative.TryGetValue(i, out match)) {
     NativeLyricEntry ne = native.Entries[match.NativeIndex];
     string cleanNative = NativeLyricsParser.CleanNativeText(ne.Text);
     string nTrans = NativeLyricsParser.CleanNativeText(ne.Translation);
     string nRom = NativeLyricsParser.CleanNativeText(ne.Romanization);

     if (baseWasRomanized && KoreanRomanizer.ContainsHangul(cleanNative)) {
      lineText = cleanNative;
      receivedNew = true;
      rom = !string.IsNullOrEmpty(nRom)
       ? nRom
       : (KoreanRomanizer.LooksLikeRomanizedLatin(origPrimary) ? origPrimary : KoreanRomanizer.Romanize(cleanNative));
      if (nTrans.Length > 0) trans = nTrans;
     } else {
      if (nTrans.Length > 0) {
       if (!string.Equals(trans, nTrans, StringComparison.Ordinal)) receivedNew = true;
       trans = nTrans;
      }
      if (nRom.Length > 0) {
       if (!string.Equals(rom, nRom, StringComparison.Ordinal)) receivedNew = true;
       rom = nRom;
      }
      if (rom.Length == 0 && KoreanRomanizer.ContainsHangul(origPrimary)) {
       rom = KoreanRomanizer.Romanize(origPrimary);
       if (rom.Length > 0) {
        receivedNew = true;
        generatedKoreanRom = true;
       }
      }
     }
    } else {
     if (rom.Length == 0 && KoreanRomanizer.ContainsHangul(origPrimary)) {
      rom = KoreanRomanizer.Romanize(origPrimary);
      if (rom.Length > 0) {
       receivedNew = true;
       generatedKoreanRom = true;
      }
     }
    }

    enrichedLines.Add(new LyricLine(ts, lineText, trans, rom));
   }

   if (!receivedNew && !baseHadAny) return null;

   string source = baseWasRomanized
    ? "Apple Music 原生原文与翻译 + LRCLIB 音译时间轴"
    : (generatedKoreanRom
     ? "Apple Music 原生融合 · " + (baseDoc.Source ?? "") + "（含韩语音译）"
     : "Apple Music 原生融合 · " + (baseDoc.Source ?? ""));

   return new LyricDocument {
    Source = source,
    IsSynchronized = baseDoc.IsSynchronized,
    Lines = enrichedLines
   };
  }

  static bool MatchesNative(string baseNorm, IndexedNativeLine nativeItem, out bool isRomMatch) {
   if (LinesMatch(baseNorm, nativeItem.NormOrig)) {
    isRomMatch = false;
    return true;
   }
   if (nativeItem.NormRom.Length > 0 && LinesMatch(baseNorm, nativeItem.NormRom)) {
    isRomMatch = true;
    return true;
   }
   isRomMatch = false;
   return false;
  }

  public static List<LyricLine> ToLiveLines(NativeLyricsSnapshot native) {
   var result = new List<LyricLine>();
   if (native == null || native.Entries == null) return result;
   for (int i = 0; i < native.Entries.Count; i++) {
    NativeLyricEntry e = native.Entries[i];
    if (e == null) continue;
    if (e.IsInstrumental) {
     result.Add(new LyricLine(i, "", e.Translation ?? "", e.Romanization ?? ""));
    } else {
     string clean = NativeLyricsParser.CleanNativeText(e.Text);
     string rom = !string.IsNullOrEmpty(e.Romanization)
      ? e.Romanization
      : (KoreanRomanizer.ContainsHangul(clean) ? KoreanRomanizer.Romanize(clean) : "");
     result.Add(new LyricLine(i, clean, e.Translation ?? "", rom));
    }
   }
   return result;
  }

  static bool LinesMatch(string normA, string normB) {
   if (string.IsNullOrEmpty(normA) || string.IsNullOrEmpty(normB)) return false;
   if (string.Equals(normA, normB, StringComparison.Ordinal)) return true;
   if (normA.Length >= 4 && normB.Length >= 4) {
    int minLen = Math.Min(normA.Length, normB.Length);
    int maxLen = Math.Max(normA.Length, normB.Length);
    if ((double)minLen / maxLen >= 0.75 &&
        (normA.IndexOf(normB, StringComparison.Ordinal) >= 0 ||
         normB.IndexOf(normA, StringComparison.Ordinal) >= 0)) {
     return true;
    }
    if (BigramDiceSimilarity(normA, normB) >= 0.72) return true;
   }
   return false;
  }

  static double BigramDiceSimilarity(string a, string b) {
   int lenA = a.Length - 1;
   int lenB = b.Length - 1;
   if (lenA <= 0 || lenB <= 0) return 0;

   var counts = new Dictionary<string, int>(StringComparer.Ordinal);
   for (int i = 0; i < lenA; i++) {
    string bg = a.Substring(i, 2);
    int c;
    counts.TryGetValue(bg, out c);
    counts[bg] = c + 1;
   }

   int intersection = 0;
   for (int i = 0; i < lenB; i++) {
    string bg = b.Substring(i, 2);
    int c;
    if (counts.TryGetValue(bg, out c) && c > 0) {
     intersection++;
     counts[bg] = c - 1;
    }
   }

   return (2.0 * intersection) / (lenA + lenB);
  }
 }

 public sealed class AppleMusicNativeLyricsService : IDisposable {
  const uint DWMWA_CLOAKED = 14;
  const int MaxTimeBasedChildren = 400;
  const int MaxButtonTextChildren = 8;
  const int MaxCacheEntries = 64;
  const double TrackSwitchStaleGuardSeconds = 2.5;

  delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

  [DllImport("user32.dll")]
  static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

  [DllImport("user32.dll")]
  static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

  [DllImport("user32.dll")]
  static extern bool IsWindowVisible(IntPtr hWnd);

  [DllImport("dwmapi.dll")]
  static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

  sealed class WindowCandidate {
   public IntPtr Hwnd;
   public int Cloaked;
  }

  readonly object gate = new object();
  readonly AutoResetEvent wakeEvent = new AutoResetEvent(false);
  readonly Thread worker;
  readonly Dictionary<string, LyricDocument> enrichedCache = new Dictionary<string, LyricDocument>(StringComparer.Ordinal);
  readonly Queue<string> cacheOrder = new Queue<string>();

  NativeLyricsSnapshot latestSnapshot = new NativeLyricsSnapshot { Availability = NativeLyricsAvailability.Unavailable };
  string currentTrackKey = "";
  string previousTrackFingerprint = "";
  string lastKnownAvailableFingerprint = "";
  DateTime trackChangedAtUtc = DateTime.MinValue;
  bool isActive;
  bool needLiveFallback;
  volatile bool stopRequested;
  int disposed;

  public AppleMusicNativeLyricsService() {
   worker = new Thread(WorkerLoop) {
    IsBackground = true,
    Name = "AMNativeLyricsReader"
   };
   worker.SetApartmentState(ApartmentState.MTA);
   worker.Start();
  }

  public void SetActive(bool active) {
   bool shouldWake = false;
   lock (gate) {
    if (isActive != active) {
     isActive = active;
     if (active) shouldWake = true;
    }
   }
   if (shouldWake) SafeWake();
  }

  public void OnTrackChanged(string trackKey) {
   string normalizedKey = trackKey ?? "";
   lock (gate) {
    if (string.Equals(currentTrackKey, normalizedKey, StringComparison.Ordinal)) return;
    if (latestSnapshot != null &&
        latestSnapshot.Availability == NativeLyricsAvailability.Available &&
        !string.IsNullOrEmpty(latestSnapshot.Fingerprint)) {
     previousTrackFingerprint = latestSnapshot.Fingerprint;
    } else if (!string.IsNullOrEmpty(lastKnownAvailableFingerprint)) {
     previousTrackFingerprint = lastKnownAvailableFingerprint;
    }
    currentTrackKey = normalizedKey;
    trackChangedAtUtc = DateTime.UtcNow;
   }
   SafeWake();
  }

  public void SetNeedLiveFallback(bool needed) {
   bool shouldWake = false;
   lock (gate) {
    if (needLiveFallback != needed) {
     needLiveFallback = needed;
     if (needed) shouldWake = true;
    }
   }
   if (shouldWake) SafeWake();
  }

  public NativeLyricsSnapshot ReadSnapshot() {
   lock (gate) return latestSnapshot;
  }

  public LyricDocument TryGetCachedEnriched(string trackKey) {
   if (string.IsNullOrEmpty(trackKey)) return null;
   lock (gate) {
    LyricDocument doc;
    return enrichedCache.TryGetValue(trackKey, out doc) ? doc : null;
   }
  }

  public void InvalidateEnrichedCache(string trackKey) {
   if (string.IsNullOrEmpty(trackKey)) return;
   lock (gate) {
    enrichedCache.Remove(trackKey);
   }
  }

  static bool CachedHasTranslation(LyricDocument doc) {
   if (doc == null || doc.Lines == null) return false;
   for (int i = 0; i < doc.Lines.Count; i++) {
    LyricLine line = doc.Lines[i];
    if (line == null) continue;
    string orig, rom, trans;
    LyricsTrackResolver.ExtractParts(line, out orig, out rom, out trans);
    if (trans.Length > 0) return true;
   }
   return false;
  }

  public LyricDocument TryEnrich(string trackKey, LyricDocument baseDoc) {
   if (string.IsNullOrEmpty(trackKey) || baseDoc == null || baseDoc.Lines == null || baseDoc.Lines.Count == 0) {
    return null;
   }

   NativeLyricsSnapshot snap = ReadSnapshot();
   LyricDocument cached = TryGetCachedEnriched(trackKey);
   if (cached != null) {
    bool shouldUpgrade = !CachedHasTranslation(cached) &&
                         snap != null &&
                         snap.Availability == NativeLyricsAvailability.Available &&
                         snap.HasTranslation &&
                         IsFreshForCurrentTrack(snap);
    if (!shouldUpgrade) return cached;
   }

   if (!IsFreshForCurrentTrack(snap)) return cached;

   LyricDocument enriched = NativeLyricsAligner.Enrich(baseDoc, snap);
   if (enriched == null) return cached;

   lock (gate) {
    if (!enrichedCache.ContainsKey(trackKey)) {
     while (enrichedCache.Count >= MaxCacheEntries && cacheOrder.Count > 0) {
      string oldest = cacheOrder.Dequeue();
      enrichedCache.Remove(oldest);
     }
     cacheOrder.Enqueue(trackKey);
    }
    enrichedCache[trackKey] = enriched;
   }

   return enriched;
  }

  public bool IsFreshForCurrentTrack(NativeLyricsSnapshot snap) {
   if (snap == null || snap.Availability != NativeLyricsAvailability.Available) return false;
   lock (gate) {
    if (string.IsNullOrEmpty(previousTrackFingerprint)) return true;
    if ((DateTime.UtcNow - trackChangedAtUtc).TotalSeconds > TrackSwitchStaleGuardSeconds) return true;
    if (string.IsNullOrEmpty(snap.Fingerprint)) return false;
    return !string.Equals(snap.Fingerprint, previousTrackFingerprint, StringComparison.Ordinal);
   }
  }

  public void Dispose() {
   if (Interlocked.Exchange(ref disposed, 1) != 0) return;
   stopRequested = true;
   SafeWake();
   if (worker != null && worker.IsAlive && Thread.CurrentThread != worker) {
    worker.Join(500);
   }
   wakeEvent.Dispose();
  }

  void SafeWake() {
   if (disposed != 0) return;
   try {
    wakeEvent.Set();
   } catch (ObjectDisposedException) {
   }
  }

  void WorkerLoop() {
   while (!stopRequested) {
    bool active;
    bool liveFallback;
    lock (gate) {
     active = isActive;
     liveFallback = needLiveFallback;
    }

    int delayMs;
    if (!active) {
     PublishSnapshot(new NativeLyricsSnapshot { Availability = NativeLyricsAvailability.Unavailable });
     delayMs = 1500;
    } else {
     NativeLyricsSnapshot snap = PollOnce();
     PublishSnapshot(snap);
     if (snap.Availability == NativeLyricsAvailability.Available) {
      delayMs = liveFallback ? 150 : 500;
     } else {
      delayMs = 1200;
     }
    }

    if (stopRequested) break;
    try {
     wakeEvent.WaitOne(delayMs);
    } catch (ObjectDisposedException) {
     break;
    }
   }
  }

  void PublishSnapshot(NativeLyricsSnapshot snap) {
   lock (gate) {
    latestSnapshot = snap;
    if (snap.Availability == NativeLyricsAvailability.Available && !string.IsNullOrEmpty(snap.Fingerprint)) {
     lastKnownAvailableFingerprint = snap.Fingerprint;
    }
   }
  }

  static HashSet<uint> CollectAppleMusicPids() {
   var pids = new HashSet<uint>();
   Process[] procs = Process.GetProcessesByName("AppleMusic");
   try {
    for (int i = 0; i < procs.Length; i++) {
     pids.Add((uint)procs[i].Id);
    }
   } finally {
    for (int i = 0; i < procs.Length; i++) procs[i].Dispose();
   }

   if (pids.Count == 0) {
    Process[] all = Process.GetProcesses();
    try {
     for (int i = 0; i < all.Length; i++) {
      try {
       string name = all[i].ProcessName ?? "";
       if (string.Equals(name, "AppleMusic", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "AppleMusic.exe", StringComparison.OrdinalIgnoreCase)) {
        pids.Add((uint)all[i].Id);
       }
      } catch {
      }
     }
    } finally {
     for (int i = 0; i < all.Length; i++) all[i].Dispose();
    }
   }
   return pids;
  }

  static List<WindowCandidate> FindVisibleMainWindows(HashSet<uint> pids) {
   var results = new List<WindowCandidate>();
   if (pids.Count == 0) return results;

   EnumWindows((hwnd, lp) => {
    uint pid;
    GetWindowThreadProcessId(hwnd, out pid);
    if (!pids.Contains(pid)) return true;
    if (!IsWindowVisible(hwnd)) return true;

    var cls = new StringBuilder(256);
    if (GetClassName(hwnd, cls, cls.Capacity) <= 0) return true;
    if (!string.Equals(cls.ToString(), "WinUIDesktopWin32WindowClass", StringComparison.Ordinal)) return true;

    int cloaked = 0;
    DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, Marshal.SizeOf(typeof(int)));
    results.Add(new WindowCandidate { Hwnd = hwnd, Cloaked = cloaked });
    return true;
   }, IntPtr.Zero);

   return results;
  }

  NativeLyricsSnapshot PollOnce() {
   HashSet<uint> pids = CollectAppleMusicPids();
   if (pids.Count == 0) {
    return new NativeLyricsSnapshot { Availability = NativeLyricsAvailability.Unavailable };
   }

   List<WindowCandidate> windows = FindVisibleMainWindows(pids);
   if (windows.Count == 0) {
    return new NativeLyricsSnapshot { Availability = NativeLyricsAvailability.Unavailable };
   }

   var uncloaked = new List<WindowCandidate>();
   for (int i = 0; i < windows.Count; i++) {
    if (windows[i].Cloaked == 0) uncloaked.Add(windows[i]);
   }

   if (uncloaked.Count == 0) {
    return new NativeLyricsSnapshot { Availability = NativeLyricsAvailability.CloakedOtherDesktop };
   }

   NativeLyricsSnapshot fallbackStatus = null;
   for (int i = 0; i < uncloaked.Count; i++) {
    NativeLyricsSnapshot snap = ReadWindowUia(uncloaked[i].Hwnd);
    if (snap.Availability == NativeLyricsAvailability.Available ||
        snap.Availability == NativeLyricsAvailability.Empty) {
     return snap;
    }
    if (fallbackStatus == null || snap.Availability == NativeLyricsAvailability.PanelClosed) {
     fallbackStatus = snap;
    }
   }

   return fallbackStatus ?? new NativeLyricsSnapshot { Availability = NativeLyricsAvailability.Unavailable };
  }

  static NativeLyricsSnapshot ReadWindowUia(IntPtr hwnd) {
   try {
    AutomationElement root = AutomationElement.FromHandle(hwnd);
    if (root == null) {
     return new NativeLyricsSnapshot { Availability = NativeLyricsAvailability.Unavailable };
    }

    AutomationElement timePane = root.FindFirst(
     TreeScope.Descendants,
     new PropertyCondition(AutomationElement.AutomationIdProperty, "TimeBasedLyrics"));

    if (timePane == null) {
     AutomationElement toggleBtn = root.FindFirst(
      TreeScope.Descendants,
      new PropertyCondition(AutomationElement.AutomationIdProperty, "LyricsToggleButton"));
     if (toggleBtn != null) {
      object togObj;
      if (toggleBtn.TryGetCurrentPattern(TogglePattern.Pattern, out togObj)) {
       ToggleState unusedState = ((TogglePattern)togObj).Current.ToggleState;
      }
      return new NativeLyricsSnapshot { Availability = NativeLyricsAvailability.PanelClosed };
     }
     return new NativeLyricsSnapshot { Availability = NativeLyricsAvailability.Unavailable };
    }

    double vPercent = -1;
    try {
     object spObj;
     if (timePane.TryGetCurrentPattern(ScrollPattern.Pattern, out spObj)) {
      vPercent = ((ScrollPattern)spObj).Current.VerticalScrollPercent;
     }
    } catch {
    }

    Rect paneRect = Rect.Empty;
    try {
     paneRect = timePane.Current.BoundingRectangle;
    } catch {
    }

    double paneTop = paneRect.IsEmpty ? 0 : paneRect.Top;
    double paneHeight = paneRect.IsEmpty ? 0 : paneRect.Height;

    var entries = new List<NativeLyricEntry>();
    var walker = TreeWalker.ControlViewWalker;
    AutomationElement child = null;
    try {
     child = walker.GetFirstChild(timePane);
    } catch {
    }

    int childCount = 0;
    while (child != null && childCount < MaxTimeBasedChildren) {
     childCount++;
     try {
      var cur = child.Current;
      string childId = cur.AutomationId ?? "";
      if (!string.Equals(childId, "WritersTextBlock", StringComparison.OrdinalIgnoreCase) &&
          cur.ControlType == ControlType.Button) {
       Rect btnRect = cur.BoundingRectangle;
       double top = btnRect.IsEmpty ? 0 : btnRect.Top;
       double height = btnRect.IsEmpty ? 0 : btnRect.Height;

       var childNodes = ReadDirectTextChildren(walker, child);
       NativeLyricEntry entry = NativeLyricsParser.ClassifyButton(
        cur.Name,
        cur.IsEnabled,
        childNodes,
        cur.IsOffscreen,
        top,
        height);
       entries.Add(entry);
      }
     } catch {
     }

     try {
      child = walker.GetNextSibling(child);
     } catch {
      child = null;
     }
    }

    if (entries.Count == 0) {
     return new NativeLyricsSnapshot {
      Availability = NativeLyricsAvailability.Empty,
      PaneTop = paneTop,
      PaneHeight = paneHeight,
      VerticalScrollPercent = vPercent
     };
    }

    int activeIndex = NativeLyricsParser.ResolveActiveIndex(entries, paneTop, paneHeight, vPercent);
    bool hasTranslation = false;
    bool hasRomanization = false;
    var fpParts = new List<string>(3);

    for (int i = 0; i < entries.Count; i++) {
     NativeLyricEntry e = entries[i];
     if (!string.IsNullOrEmpty(e.Translation)) hasTranslation = true;
     if (!string.IsNullOrEmpty(e.Romanization)) hasRomanization = true;
     if (!e.IsInstrumental && fpParts.Count < 3) {
      string norm = LyricsMetadata.Normalize(e.Text);
      if (norm.Length > 0) fpParts.Add(norm);
     }
    }

    return new NativeLyricsSnapshot {
     Availability = NativeLyricsAvailability.Available,
     Entries = entries,
     ActiveIndex = activeIndex,
     PaneTop = paneTop,
     PaneHeight = paneHeight,
     VerticalScrollPercent = vPercent,
     HasTranslation = hasTranslation,
     HasRomanization = hasRomanization,
     CapturedAtUtc = DateTime.UtcNow,
     Fingerprint = string.Join("|", fpParts)
    };
   } catch {
    return new NativeLyricsSnapshot { Availability = NativeLyricsAvailability.Unavailable };
   }
  }

  static List<NativeChildNode> ReadDirectTextChildren(TreeWalker walker, AutomationElement button) {
   var nodes = new List<NativeChildNode>();
   AutomationElement sub = null;
   try {
    sub = walker.GetFirstChild(button);
   } catch {
    return nodes;
   }

   int count = 0;
   while (sub != null && count < MaxButtonTextChildren) {
    count++;
    try {
     var subCur = sub.Current;
     if (subCur.ControlType == ControlType.Text) {
      string id = subCur.AutomationId ?? "";
      string txt = subCur.Name ?? "";
      if (string.IsNullOrWhiteSpace(txt)) {
       object txtObj;
       if (sub.TryGetCurrentPattern(TextPattern.Pattern, out txtObj)) {
        try {
         txt = ((TextPattern)txtObj).DocumentRange.GetText(512) ?? "";
        } catch {
        }
       }
      }
      nodes.Add(new NativeChildNode {
       AutomationId = id,
       Text = txt
      });
     }
    } catch {
    }

    try {
     sub = walker.GetNextSibling(sub);
    } catch {
     sub = null;
    }
   }

   return nodes;
  }
 }
}
