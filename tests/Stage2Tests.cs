using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Media;
using AMLyrics;

class Stage2Tests {
 static int total;
 static int failures;

 static void Check(bool ok, string name) {
  total++;
  Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
  if (!ok) failures++;
 }

 static void Near(double actual, double expected, string name) {
  Check(Math.Abs(actual - expected) < 0.0001, name + " (actual=" + actual + ", expected=" + expected + ")");
 }

 static int Main(string[] args) {
  string tempRoot = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
   ? Path.GetFullPath(args[0])
   : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "stage2-unit-" + Guid.NewGuid().ToString("N"));
  string watchExePath = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
   ? Path.GetFullPath(args[1])
   : null;

  bool createdTempRoot = !Directory.Exists(tempRoot);
  Directory.CreateDirectory(tempRoot);

   try {
    TestLayoutMethod1();
    TestLyricsTrackResolver();
    TestNativeLyricsParser();
    TestNativeLyricsAligner();
    TestKoreanRomanizerAndCleaner();
    TestHangulAndCrossScriptFusion();
    TestPreferencesStoreRoundtripAndLegacy(tempRoot);
    TestCustomColorPaletteState();
    TestStandbyWatcherStopIncludeHost(watchExePath);
   } catch (Exception ex) {
   Console.WriteLine("FAIL Unhandled exception: " + ex);
   failures++;
  } finally {
   if (createdTempRoot) {
    try { Directory.Delete(tempRoot, true); } catch { }
   }
  }

  Console.WriteLine(total + " checks; " + failures + " failed.");
  return failures == 0 ? 0 : 1;
 }

 static void TestLayoutMethod1() {
  Func<string, double, double> measure = (text, size) => (text == null ? 0 : text.Length) * size * 0.5;
  string longLine = new string('A', 40); // at size 35 -> 40 * 35 * 0.5 = 700 > 400

  var marquee = LyricsLineLayoutEngine.Layout(longLine, 400, 35, true, measure);
  Near(marquee.FontSize, 35, "Layout Method 1: long line keeps requested FontSize 35 with animate=true");
  Check(marquee.Mode == LyricsLongLineMode.Marquee, "Layout Method 1: long line selects Marquee when animate=true");
  Near(marquee.Overflow, 300, "Layout Method 1: overflow equals measured - availableWidth (700 - 400)");
  Check(marquee.MotionDuration >= TimeSpan.FromSeconds(2.5), "Layout Method 1: MotionDuration >= 2.5s");

  var ellipsis = LyricsLineLayoutEngine.Layout(longLine, 400, 35, false, measure);
  Near(ellipsis.FontSize, 35, "Layout Method 1: long line keeps requested FontSize 35 with animate=false");
  Check(ellipsis.Mode == LyricsLongLineMode.Ellipsis, "Layout Method 1: long line selects Ellipsis when animate=false");
  Near(ellipsis.Overflow, 300, "Layout Method 1: ellipsis overflow equals measured - availableWidth");

  var fit = LyricsLineLayoutEngine.Layout("Hello", 400, 35, true, measure);
  Check(fit.Mode == LyricsLongLineMode.Fit, "Layout Method 1: short line selects Fit");
  Near(fit.FontSize, 35, "Layout Method 1: short line keeps requested FontSize 35");
  Near(fit.Overflow, 0, "Layout Method 1: short line has zero overflow");

  var clampedFit = LyricsLineLayoutEngine.Layout("Hi", 400, 72, true, measure);
  Near(clampedFit.FontSize, 48, "Layout Method 1: requested font size clamps at 48 max for fit");

  var clampedMarquee = LyricsLineLayoutEngine.Layout(longLine, 400, 72, true, measure);
  Near(clampedMarquee.FontSize, 48, "Layout Method 1: requested font size clamps at 48 max for marquee");

  double secondaryThree = LyricsLayoutMetrics.SecondaryThreeTrackFontSize(35);
  Check(secondaryThree < 35 && secondaryThree >= LyricsLayoutMetrics.MinimumFontSize,
   "Layout Method 1: SecondaryThreeTrackFontSize(35) < 35 and >= MinimumFontSize (" + secondaryThree + ")");
 }

 static void TestLyricsTrackResolver() {
  var triDoc = new LyricDocument {
   IsSynchronized = true,
   Source = "unit",
   Lines = new List<LyricLine> {
    new LyricLine(10, "夢灯籠", "梦灯笼", "yumetourou"),
    new LyricLine(15, "この声が届く", "这声音能传达到", "kono koe ga todoku")
   }
  };

  var rOff = LyricsTrackResolver.Resolve(triDoc, 0, false, false);
  Check(!rOff.ShowTertiary && rOff.Primary == "夢灯籠" && rOff.Secondary == "この声が届く" && rOff.Tertiary == "",
   "TrackResolver: trilingual (false, false) -> 2-track (orig, nextOrig, ShowTertiary=false)");
  Check(rOff.HasTranslation && rOff.HasRomanization,
   "TrackResolver: trilingual document reports HasTranslation=true and HasRomanization=true");

  var rTrans = LyricsTrackResolver.Resolve(triDoc, 0, true, false);
  Check(!rTrans.ShowTertiary && rTrans.Primary == "夢灯籠" && rTrans.Secondary == "梦灯笼" && rTrans.Tertiary == "",
   "TrackResolver: trilingual (true, false) -> 2-track (orig, trans, ShowTertiary=false)");

  var rRom = LyricsTrackResolver.Resolve(triDoc, 0, false, true);
  Check(!rRom.ShowTertiary && rRom.Primary == "夢灯籠" && rRom.Secondary == "yumetourou" && rRom.Tertiary == "",
   "TrackResolver: trilingual (false, true) -> 2-track (orig, rom, ShowTertiary=false)");

  var rBoth = LyricsTrackResolver.Resolve(triDoc, 0, true, true);
  Check(rBoth.ShowTertiary && rBoth.Primary == "夢灯籠" && rBoth.Secondary == "yumetourou" && rBoth.Tertiary == "梦灯笼",
   "TrackResolver: trilingual (true, true) -> 3-track (orig, rom, trans, ShowTertiary=true)");

  var transOnlyDoc = new LyricDocument {
   IsSynchronized = true,
   Source = "unit",
   Lines = new List<LyricLine> {
    new LyricLine(10, "Hello world", "你好世界", ""),
    new LyricLine(15, "Next line", "下一句", "")
   }
  };
  var rTransOnly = LyricsTrackResolver.Resolve(transOnlyDoc, 0, true, true);
  Check(!rTransOnly.ShowTertiary && rTransOnly.Primary == "Hello world" && rTransOnly.Secondary == "你好世界" &&
        rTransOnly.HasTranslation && !rTransOnly.HasRomanization,
   "TrackResolver: translation-only + (true, true) stays 2-track (orig, trans, ShowTertiary=false)");

  var romOnlyDoc = new LyricDocument {
   IsSynchronized = true,
   Source = "unit",
   Lines = new List<LyricLine> {
    new LyricLine(10, "사랑해", "", "saranghae"),
    new LyricLine(15, "보고싶어", "", "bogosipeo")
   }
  };
  var rRomOnly = LyricsTrackResolver.Resolve(romOnlyDoc, 0, true, true);
  Check(!rRomOnly.ShowTertiary && rRomOnly.Primary == "사랑해" && rRomOnly.Secondary == "saranghae" &&
        !rRomOnly.HasTranslation && rRomOnly.HasRomanization,
   "TrackResolver: romanization-only + (true, true) stays 2-track (orig, rom, ShowTertiary=false)");

  var bilingualLrc = LrcParser.Parse(
   "[00:05.00]First original\n[00:05.00]第一句翻译\n[00:10.00]Second original\n[00:10.00]第二句翻译",
   "local-bi");
  var lrcBiOff = LyricsTrackResolver.Resolve(bilingualLrc, 0, false, false);
  Check(lrcBiOff.HasTranslation && !lrcBiOff.HasRomanization &&
        lrcBiOff.Primary == "First original" && lrcBiOff.Secondary == "Second original" &&
        lrcBiOff.Secondary.IndexOf('\n') < 0,
   "TrackResolver: bilingual LRC resolves clean first-line nextOrig when translation off");
  var lrcBiOn = LyricsTrackResolver.Resolve(bilingualLrc, 0, true, false);
  Check(lrcBiOn.Primary == "First original" && lrcBiOn.Secondary == "第一句翻译" && !lrcBiOn.ShowTertiary,
   "TrackResolver: bilingual LRC resolves translation track when enabled");

  var trilingualLrc = LrcParser.Parse(
   "[00:05.00]夢灯籠\n[00:05.00]yumetourou\n[00:05.00]梦灯笼\n[00:10.00]次の行\n[00:10.00]tsugi no gyou\n[00:10.00]下一句",
   "local-tri");
  var lrcTriOff = LyricsTrackResolver.Resolve(trilingualLrc, 0, false, false);
  Check(lrcTriOff.HasTranslation && lrcTriOff.HasRomanization &&
        lrcTriOff.Primary == "夢灯籠" && lrcTriOff.Secondary == "次の行" && !lrcTriOff.ShowTertiary,
   "TrackResolver: trilingual LRC resolves clean first-line nextOrig when toggles off");
  var lrcTriOn = LyricsTrackResolver.Resolve(trilingualLrc, 0, true, true);
  Check(lrcTriOn.ShowTertiary && lrcTriOn.Primary == "夢灯籠" && lrcTriOn.Secondary == "yumetourou" && lrcTriOn.Tertiary == "梦灯笼",
   "TrackResolver: trilingual LRC resolves 3-track (orig, rom, trans) when both enabled");

  var prelude = LyricsTrackResolver.Resolve(triDoc, -1, false, false);
  Check(prelude.Primary == "前奏" && prelude.Secondary == "夢灯籠" && !prelude.ShowTertiary,
   "TrackResolver: prelude (index=-1) returns 前奏 + line 0 in nextOrig");

  var instDoc = new LyricDocument {
   IsSynchronized = true,
   Source = "inst",
   Lines = new List<LyricLine> {
    new LyricLine(10, "", "", ""),
    new LyricLine(20, "After instrumental", "", "")
   }
  };
  var inst = LyricsTrackResolver.Resolve(instDoc, 0, false, false);
  Check(inst.Primary == "间奏" && inst.Secondary == "After instrumental",
   "TrackResolver: instrumental empty line returns 间奏");

  var monoWithEchoLrc = LrcParser.Parse(
   "[00:05.00]Line one\n[00:10.00]Line two\n[00:15.00]Line three\n[00:20.00]Line four\n" +
   "[00:25.00]Line five\n[00:25.00](echo)\n[00:30.00]Line six\n[00:35.00]Line seven\n" +
   "[00:40.00]Line eight\n[00:45.00]Line nine\n[00:50.00]Line ten",
   "mono-echo");
  var monoRes = LyricsTrackResolver.Resolve(monoWithEchoLrc, 4, true, true);
  Check(!monoRes.HasTranslation && !monoRes.HasRomanization &&
        monoRes.Primary == "Line five / (echo)" && monoRes.Secondary == "Line six" && !monoRes.ShowTertiary,
   "TrackResolver: 10-line monolingual LRC with 1 duplicate timestamp reports !HasTranslation && !HasRomanization");
 }

 static void TestNativeLyricsParser() {
  var explicitEntry = NativeLyricsParser.ClassifyButton(
   "夢灯籠",
   true,
   new[] {
    new NativeChildNode { AutomationId = "PronunciationContainer", Text = "yumetourou" },
    new NativeChildNode { AutomationId = "TranslationContainer", Text = "梦灯笼" },
    new NativeChildNode { AutomationId = "WritersTextBlock", Text = "Yojiro Noda" }
   },
   false,
   282,
   185);
  Check(!explicitEntry.IsInstrumental && explicitEntry.Text == "夢灯籠" &&
        explicitEntry.Romanization == "yumetourou" && explicitEntry.Translation == "梦灯笼",
   "NativeLyricsParser: classifies PronunciationContainer + TranslationContainer and ignores WritersTextBlock");

  var translitEntry = NativeLyricsParser.ClassifyButton(
   "사랑해",
   true,
   new[] {
    new NativeChildNode { AutomationId = "TransliterationContainer", Text = "saranghae" },
    new NativeChildNode { AutomationId = "TranslationContainer", Text = "我爱你" }
   },
   false,
   467,
   185);
  Check(!translitEntry.IsInstrumental && translitEntry.Romanization == "saranghae" && translitEntry.Translation == "我爱你",
   "NativeLyricsParser: classifies TransliterationContainer as Romanization");

  var inst1 = NativeLyricsParser.ClassifyButton(
   "",
   false,
   new[] { new NativeChildNode { AutomationId = "InstrumentalTextBlock1", Text = "..." } },
   false,
   100,
   80);
  var inst2 = NativeLyricsParser.ClassifyButton(
   "",
   false,
   new[] { new NativeChildNode { AutomationId = "InstrumentalTextBlock2", Text = "..." } },
   false,
   180,
   80);
  var inst3 = NativeLyricsParser.ClassifyButton(
   "ignored",
   true,
   new[] { new NativeChildNode { AutomationId = "InstrumentalTextBlock3", Text = "..." } },
   false,
   260,
   80);
  Check(inst1.IsInstrumental && inst1.Text == "" && inst2.IsInstrumental && inst3.IsInstrumental,
   "NativeLyricsParser: recognizes InstrumentalTextBlock1..3");

  var heuristicRom = NativeLyricsParser.ClassifyButton(
   "さくら",
   true,
   new[] { new NativeChildNode { AutomationId = "TextBlock_0", Text = "sakura" } },
   false,
   300,
   140);
  Check(heuristicRom.Romanization == "sakura" && heuristicRom.Translation == "",
   "NativeLyricsParser: unclassified Latin phonetic child under CJK/Kana orig classifies as Romanization");

  var heuristicTrans = NativeLyricsParser.ClassifyButton(
   "Hello my friend",
   true,
   new[] { new NativeChildNode { AutomationId = "TextBlock_0", Text = "你好我的朋友" } },
   false,
   300,
   140);
  Check(heuristicTrans.Translation == "你好我的朋友" && heuristicTrans.Romanization == "",
   "NativeLyricsParser: unclassified non-phonetic child classifies as Translation");

  var heuristicTwo = NativeLyricsParser.ClassifyButton(
   "さくら",
   true,
   new[] {
    new NativeChildNode { AutomationId = "ChildA", Text = "sakura" },
    new NativeChildNode { AutomationId = "ChildB", Text = "樱花" }
   },
   false,
   300,
   140);
  Check(heuristicTwo.Romanization == "sakura" && heuristicTwo.Translation == "樱花",
   "NativeLyricsParser: two unclassified children map to Romanization then Translation");

  const double paneTop = 282.0;
  const double paneHeight = 1091.0;

  var entriesTop = new List<NativeLyricEntry> {
   new NativeLyricEntry { Text = "Line 0", Top = 282, Height = 185 },
   new NativeLyricEntry { Text = "Line 1", Top = 467, Height = 185 },
   new NativeLyricEntry { Text = "Line 2", Top = 652, Height = 185 }
  };
  Check(NativeLyricsParser.ResolveActiveIndex(entriesTop, paneTop, paneHeight, 0.0) == 0,
   "NativeLyricsParser: VPercent=0 selects index 0");

  var entriesClipped = new List<NativeLyricEntry> {
   new NativeLyricEntry { Text = "Line A clipped", Top = 282, Height = 90 },
   new NativeLyricEntry { Text = "Line B active", Top = 372, Height = 185 },
   new NativeLyricEntry { Text = "Line C", Top = 557, Height = 185 },
   new NativeLyricEntry { Text = "Line D", Top = 742, Height = 185 }
  };
  Check(NativeLyricsParser.ResolveActiveIndex(entriesClipped, paneTop, paneHeight, 20.11) == 1,
   "NativeLyricsParser: VPercent=20.11 with clipped Line A (Top=282,H=90) and Line B (Top=372,H=185) selects B");

  var entriesT1 = new List<NativeLyricEntry> {
   new NativeLyricEntry { Text = "Offscreen line", IsOffscreen = true, Top = 100, Height = 185 },
   new NativeLyricEntry { Text = "T1 clipped", Top = 282, Height = 85 },
   new NativeLyricEntry { Text = "Next active", Top = 367, Height = 185 },
   new NativeLyricEntry { Text = "Following", Top = 552, Height = 185 }
  };
  Check(NativeLyricsParser.ResolveActiveIndex(entriesT1, paneTop, paneHeight, 20.11) == 2,
   "NativeLyricsParser: T1 (Top=282,H=85 -> next Top=367,H=185) selects next line");
 }

 static void TestNativeLyricsAligner() {
  var baseDoc = new LyricDocument {
   Source = "LRCLIB",
   IsSynchronized = true,
   Lines = new List<LyricLine> {
    new LyricLine(10.0, "晴天，刮风这天!"),
    new LyricLine(15.5, "我想起你的脸"),
    new LyricLine(20.25, "故事的小黄花"),
    new LyricLine(25.75, "从出生那年就飘着")
   }
  };

  var native = new NativeLyricsSnapshot {
   Availability = NativeLyricsAvailability.Available,
   HasTranslation = true,
   HasRomanization = true,
   Entries = new List<NativeLyricEntry> {
    new NativeLyricEntry { Text = "晴天, 刮風這天？", Romanization = "qing tian, gua feng zhe tian", Translation = "Sunny day, windy day" },
    new NativeLyricEntry { Text = "我想起你的臉", Romanization = "wo xiang qi ni de lian", Translation = "I remember your face" },
    new NativeLyricEntry { Text = "故事的小黃花", Romanization = "gu shi de xiao huang hua", Translation = "The little yellow flower in the story" },
    new NativeLyricEntry { Text = "從出生那年就飄著", Romanization = "cong chu sheng na nian jiu piao zhe", Translation = "Floating since the year of birth" }
   }
  };

  var enriched = NativeLyricsAligner.Enrich(baseDoc, native);
  Check(enriched != null && enriched != baseDoc, "NativeLyricsAligner: Enrich returns new non-null LyricDocument");
  if (enriched != null) {
   Check(enriched.IsSynchronized && enriched.Lines.Count == 4, "NativeLyricsAligner: Enrich preserves line count and sync flag");
   bool timestampsPreserved = true;
   for (int i = 0; i < 4; i++) {
    if (Math.Abs(enriched.Lines[i].TimestampSeconds - baseDoc.Lines[i].TimestampSeconds) > 0.0001) {
     timestampsPreserved = false;
    }
   }
   Check(timestampsPreserved, "NativeLyricsAligner: Enrich preserves every baseDoc timestamp");
   Check(enriched.Lines[0].Translation == "Sunny day, windy day" &&
         enriched.Lines[0].Romanization == "qing tian, gua feng zhe tian" &&
         enriched.Lines[3].Translation == "Floating since the year of birth" &&
         enriched.Lines[3].Romanization == "cong chu sheng na nian jiu piao zhe",
    "NativeLyricsAligner: Enrich fuses translation and romanization despite punctuation/Traditional differences");
  }

  Check(baseDoc.Lines[0].Translation == "" && baseDoc.Lines[0].Romanization == "" &&
        baseDoc.Lines[3].Translation == "" && baseDoc.Lines[3].Romanization == "",
   "NativeLyricsAligner: Enrich does not mutate baseDoc");

  var mismatched = new NativeLyricsSnapshot {
   Availability = NativeLyricsAvailability.Available,
   HasTranslation = true,
   HasRomanization = false,
   Entries = new List<NativeLyricEntry> {
    new NativeLyricEntry { Text = "Completely different song line one", Translation = "完全不同的歌一" },
    new NativeLyricEntry { Text = "Completely different song line two", Translation = "完全不同的歌二" },
    new NativeLyricEntry { Text = "Completely different song line three", Translation = "完全不同的歌三" },
    new NativeLyricEntry { Text = "Completely different song line four", Translation = "完全不同的歌四" }
   }
  };
  Check(NativeLyricsAligner.Enrich(baseDoc, mismatched) == null,
   "NativeLyricsAligner: Enrich returns null for mismatched song");
 }

 static double BigramDice(string a, string b) {
  if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
  if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;
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

 static void TestKoreanRomanizerAndCleaner() {
  Check(!KoreanRomanizer.ContainsHangul("Hello"),
   "KoreanRomanizer: ContainsHangul(\"Hello\") is false");
  Check(KoreanRomanizer.TryRomanizeIfKorean("RUDE!") == "",
   "KoreanRomanizer: TryRomanizeIfKorean(\"RUDE!\") returns empty string");

  string r1 = KoreanRomanizer.Romanize("그 때 아이스 티는 내 맛 최애였잖아");
  string normR1 = LyricsMetadata.Normalize(r1);
  string normExpected = LyricsMetadata.Normalize("Geu ttae aiseutineun nae mat choeaeyeotjana");
  double sim = BigramDice(normR1, normExpected);
  Check(!string.IsNullOrWhiteSpace(r1) && !KoreanRomanizer.ContainsHangul(r1) && sim >= 0.72,
   "KoreanRomanizer: Romanize Hangul produces pure Latin matching expected romanization (sim=" + sim.ToString("F2") + ", r1=" + r1 + ")");

  Check(NativeLyricsParser.CleanNativeText("스\u2060치\u2060는\u2060\u00A0\u2060순간 모\u2060두가\u2060\u00A0My friend") == "스치는 순간 모두가 My friend",
   "NativeLyricsParser: CleanNativeText strips U+2060 and normalizes NBSP");
  Check(LyricsMetadata.Normalize("스\u2060치\u2060는") == LyricsMetadata.Normalize("스치는"),
   "LyricsMetadata: Normalize removes U+2060 word joiners");

  var cleanedEntry = NativeLyricsParser.ClassifyButton(
   "스\u2060치\u2060는\u2060\u00A0\u2060순간 모\u2060두가\u2060\u00A0My friend",
   true,
   new[] { new NativeChildNode { AutomationId = "TranslationContainer", Text = "擦肩而过的瞬间 所有人都是我的朋友" } },
   false,
   400,
   177);
  Check(cleanedEntry.Text == "스치는 순간 모두가 My friend" &&
        cleanedEntry.Translation == "擦肩而过的瞬间 所有人都是我的朋友",
   "NativeLyricsParser: ClassifyButton produces clean Hangul Text and Translation");
 }

 static void TestHangulAndCrossScriptFusion() {
  var hangulBase = new LyricDocument {
   Source = "LRCLIB",
   IsSynchronized = true,
   Lines = new List<LyricLine> {
    new LyricLine(10.0, "나답게 굴 때 난 누구보다 빛나"),
    new LyricLine(15.0, "스치는 순간 모두가 My friend"),
    new LyricLine(20.0, "I've been actin' too"),
    new LyricLine(25.0, "RUDE!")
   }
  };
  var hangulNative = new NativeLyricsSnapshot {
   Availability = NativeLyricsAvailability.Available,
   HasTranslation = true,
   HasRomanization = false,
   Entries = new List<NativeLyricEntry> {
    new NativeLyricEntry { Text = "나\u2060답게 굴 때 난 누구\u2060보다 빛나", Translation = "做自己时 我比谁都耀眼" },
    new NativeLyricEntry { Text = "스\u2060치\u2060는\u2060\u00A0\u2060순간 모\u2060두가\u2060\u00A0My friend", Translation = "擦肩而过的瞬间 所有人都是我的朋友" },
    new NativeLyricEntry { Text = "I've been actin' too", Translation = "我一直都表现得太" },
    new NativeLyricEntry { Text = "RUDE!", Translation = "无礼" }
   }
  };

  var hangulEnriched = NativeLyricsAligner.Enrich(hangulBase, hangulNative);
  Check(hangulEnriched != null && hangulEnriched.Lines.Count == 4,
   "HangulFusion: NativeLyricsAligner.Enrich succeeds for Hangul + English track");
  if (hangulEnriched != null) {
   Check(!string.IsNullOrEmpty(hangulEnriched.Lines[0].Romanization) &&
         !KoreanRomanizer.ContainsHangul(hangulEnriched.Lines[0].Romanization) &&
         !string.IsNullOrEmpty(hangulEnriched.Lines[1].Romanization) &&
         hangulEnriched.Lines[3].Romanization == "",
    "HangulFusion: generates Romanization for Hangul lines while leaving English line RUDE! empty");

   var koTrans = LyricsTrackResolver.Resolve(hangulEnriched, 0, true, false);
   Check(!koTrans.ShowTertiary && koTrans.Primary == hangulEnriched.Lines[0].Text && koTrans.Secondary == "做自己时 我比谁都耀眼",
    "HangulFusion: line 0 (true, false) -> 2-track (Hangul, Chinese)");

   var koRom = LyricsTrackResolver.Resolve(hangulEnriched, 0, false, true);
   Check(!koRom.ShowTertiary && koRom.Primary == hangulEnriched.Lines[0].Text && koRom.Secondary == hangulEnriched.Lines[0].Romanization,
    "HangulFusion: line 0 (false, true) -> 2-track (Hangul, Romanization)");

   var koBoth = LyricsTrackResolver.Resolve(hangulEnriched, 0, true, true);
   Check(koBoth.ShowTertiary && koBoth.Primary == hangulEnriched.Lines[0].Text &&
         koBoth.Secondary == hangulEnriched.Lines[0].Romanization && koBoth.Tertiary == "做自己时 我比谁都耀眼",
    "HangulFusion: line 0 (true, true) -> 3-track (Hangul, Romanization, Chinese)");

   var enBoth = LyricsTrackResolver.Resolve(hangulEnriched, 3, true, true);
   Check(!enBoth.ShowTertiary && enBoth.Primary == "RUDE!" && enBoth.Secondary == "无礼",
    "HangulFusion: English line RUDE! (true, true) -> 2-track (RUDE!, 无礼, ShowTertiary=false)");
  }

  var crossBase = new LyricDocument {
   Source = "LRCLIB",
   IsSynchronized = true,
   Lines = new List<LyricLine> {
    new LyricLine(10.0, "Geu ttae aiseutineun nae mat choeaeyeotjana"),
    new LyricLine(15.0, "Matjana crime signs"),
    new LyricLine(20.0, "Saekom dalkom jelly"),
    new LyricLine(25.0, "Algo boni smelly")
   }
  };
  var crossNative = new NativeLyricsSnapshot {
   Availability = NativeLyricsAvailability.Available,
   HasTranslation = true,
   HasRomanization = false,
   Entries = new List<NativeLyricEntry> {
    new NativeLyricEntry { Text = "그\u2060때 아이스\u2060티는 내 맛 최애였잖아", Translation = "那时冰红茶正好是我最爱的口味" },
    new NativeLyricEntry { Text = "맞\u2060잖아 Crime signs", Translation = "没错吧 犯罪信号" },
    new NativeLyricEntry { Text = "새\u2060콤 달\u2060콤 Jelly", Translation = "酸酸甜甜的果冻" },
    new NativeLyricEntry { Text = "알\u2060고 보니 Smelly", Translation = "原来有股怪味" }
   }
  };

  var crossEnriched = NativeLyricsAligner.Enrich(crossBase, crossNative);
  Check(crossEnriched != null && crossEnriched.Lines.Count == 4,
   "CrossScriptFusion: NativeLyricsAligner.Enrich succeeds for romanized LRCLIB + Hangul Apple Music");
  if (crossEnriched != null) {
   bool tsOk = Math.Abs(crossEnriched.Lines[0].TimestampSeconds - 10.0) < 0.0001 &&
               Math.Abs(crossEnriched.Lines[1].TimestampSeconds - 15.0) < 0.0001 &&
               Math.Abs(crossEnriched.Lines[2].TimestampSeconds - 20.0) < 0.0001 &&
               Math.Abs(crossEnriched.Lines[3].TimestampSeconds - 25.0) < 0.0001;
   Check(tsOk, "CrossScriptFusion: preserves baseDoc timestamps [10, 15, 20, 25]");
   Check(crossEnriched.Lines[0].Text == "그때 아이스티는 내 맛 최애였잖아" &&
         crossEnriched.Lines[0].Romanization == "Geu ttae aiseutineun nae mat choeaeyeotjana" &&
         crossEnriched.Lines[0].Translation == "那时冰红茶正好是我最爱的口味",
    "CrossScriptFusion: replaces line 0 Text with clean Hangul while preserving LRCLIB romanization and native translation");

   var cTrans = LyricsTrackResolver.Resolve(crossEnriched, 0, true, false);
   Check(!cTrans.ShowTertiary && cTrans.Primary == "그때 아이스티는 내 맛 최애였잖아" && cTrans.Secondary == "那时冰红茶正好是我最爱的口味",
    "CrossScriptFusion: (true, false) -> 2-track (Hangul, Chinese)");

   var cRom = LyricsTrackResolver.Resolve(crossEnriched, 0, false, true);
   Check(!cRom.ShowTertiary && cRom.Primary == "그때 아이스티는 내 맛 최애였잖아" && cRom.Secondary == "Geu ttae aiseutineun nae mat choeaeyeotjana",
    "CrossScriptFusion: (false, true) -> 2-track (Hangul, Romanization)");

   var cBoth = LyricsTrackResolver.Resolve(crossEnriched, 0, true, true);
   Check(cBoth.ShowTertiary && cBoth.Primary == "그때 아이스티는 내 맛 최애였잖아" &&
         cBoth.Secondary == "Geu ttae aiseutineun nae mat choeaeyeotjana" &&
         cBoth.Tertiary == "那时冰红茶正好是我最爱的口味",
    "CrossScriptFusion: (true, true) -> 3-track (Hangul, Romanization, Chinese)");
  }
 }

 static void TestPreferencesStoreRoundtripAndLegacy(string tempRoot) {
  int[] normNull = LyricsDisplaySettings.NormalizeCustomColors(null);
  int[] normWhite = LyricsDisplaySettings.NormalizeCustomColors(new[] { 0x00FFFFFF, unchecked((int)0xFFFFFFFF) });
  Check(normNull != null && normNull.Length == 0 && normWhite != null && normWhite.Length == 0,
   "LyricsDisplaySettings.NormalizeCustomColors: null and all-white return Length == 0");

  int[] normPartial = LyricsDisplaySettings.NormalizeCustomColors(new[] { unchecked((int)0xFFAA55FF), 0x00112233 });
  bool partialRestWhite = normPartial != null && normPartial.Length == 16 &&
                          normPartial[0] == 0x00AA55FF && normPartial[1] == 0x00112233;
  if (partialRestWhite) {
   for (int i = 2; i < 16; i++) {
    if (normPartial[i] != 0x00FFFFFF) {
     partialRestWhite = false;
     break;
    }
   }
  }
  Check(partialRestWhite,
   "LyricsDisplaySettings.NormalizeCustomColors: partial array masks high byte and pads 2..15 with 0x00FFFFFF");

  Check(LyricsDisplaySettings.CustomColorsEqual(null, new[] { 0x00FFFFFF, unchecked((int)0xFFFFFFFF) }) &&
        LyricsDisplaySettings.CustomColorsEqual(new[] { unchecked((int)0xFFAA55FF), 0x00112233 }, new[] { 0x00AA55FF, 0x00112233 }),
   "LyricsDisplaySettings.CustomColorsEqual: treats normalized equivalent arrays as equal");

  string prefPath = Path.Combine(tempRoot, "stage2-prefs.json");
  var store = new LyricsPreferencesStore(prefPath);
  var prefs = LyricsPreferencesStore.CreateDefaultPreferences();
  prefs.Display.ShowTranslation = true;
  prefs.Display.ShowRomanization = true;
  prefs.Display.FontSize = 35;
  prefs.Display.CustomColors = new[] { 0x00AA55FF, 0x00112233 };

  var saveRes = store.Save(prefs);
  Check(saveRes.Succeeded, "PreferencesStore: saves ShowTranslation=true, ShowRomanization=true, and CustomColors");

  var reloadStore = new LyricsPreferencesStore(prefPath);
  var loadRes = reloadStore.Load();
  Check(loadRes.Status == LyricsPreferencesLoadStatus.Loaded &&
        loadRes.Preferences.Display.ShowTranslation &&
        loadRes.Preferences.Display.ShowRomanization &&
        Math.Abs(loadRes.Preferences.Display.FontSize - 35) < 0.0001 &&
        loadRes.Preferences.Display.CustomColors != null &&
        loadRes.Preferences.Display.CustomColors.Length == 16 &&
        loadRes.Preferences.Display.CustomColors[0] == 0x00AA55FF &&
        loadRes.Preferences.Display.CustomColors[1] == 0x00112233 &&
        loadRes.Preferences.Display.CustomColors[15] == 0x00FFFFFF,
   "PreferencesStore: round-trip preserves ShowTranslation=true, ShowRomanization=true, FontSize=35, CustomColors");

  string legacyPath = Path.Combine(tempRoot, "legacy-v1-prefs.json");
  string legacyJson = "{\"Version\":1,\"Enabled\":true,\"Locked\":false,\"Width\":800,\"Display\":{\"FontSize\":32,\"ForegroundHex\":\"#FFFFFF\",\"FontFamilyName\":\"Segoe UI\",\"FontWeightName\":\"常规\",\"StrokeHex\":\"#0F0F12\",\"BackgroundOpacity\":0.2,\"StrokeEnabled\":true,\"ShadowEnabled\":true,\"AnimateLongLines\":true}}";
  File.WriteAllText(legacyPath, legacyJson, new UTF8Encoding(false));

  var legacyStore = new LyricsPreferencesStore(legacyPath);
  var legacyRes = legacyStore.Load();
  Check(legacyRes.Status == LyricsPreferencesLoadStatus.Loaded &&
        legacyRes.Preferences.Enabled &&
        Math.Abs(legacyRes.Preferences.Width - 800) < 0.0001 &&
        Math.Abs(legacyRes.Preferences.Display.FontSize - 32) < 0.0001 &&
        !legacyRes.Preferences.Display.ShowTranslation &&
        !legacyRes.Preferences.Display.ShowRomanization &&
        legacyRes.Preferences.Display.CustomColors != null &&
        legacyRes.Preferences.Display.CustomColors.Length == 0,
   "PreferencesStore: loads legacy Version 1 JSON omitting ShowTranslation/ShowRomanization/CustomColors");
 }

 static void TestCustomColorPaletteState() {
  Check(CustomColorPaletteState.SlotCount == 16 &&
        CustomColorPaletteState.EmptyColorRef == 0x00FFFFFF &&
        CustomColorPaletteState.IsEmptySlot(CustomColorPaletteState.EmptyColorRef) &&
        !CustomColorPaletteState.IsEmptySlot(0x000000FF) &&
        !CustomColorPaletteState.IsEmptySlot(0x00FF0000),
   "CustomColorPaletteState: constants and IsEmptySlot match contract");

  // 1. Null and all-white initialization default to slot 0
  var emptyNull = new CustomColorPaletteState(null);
  bool allSlotsEmpty = emptyNull.SelectedSlotIndex == 0;
  for (int i = 0; i < CustomColorPaletteState.SlotCount; i++) {
   if (!emptyNull.IsSlotEmpty(i) || emptyNull.GetSlotColorRef(i) != CustomColorPaletteState.EmptyColorRef) {
    allSlotsEmpty = false;
    break;
   }
  }
  int[] emptyGetSlots = emptyNull.GetSlots();
  int[] emptyNorm = emptyNull.GetNormalizedCustomColors();
  Check(allSlotsEmpty &&
        emptyGetSlots != null && emptyGetSlots.Length == 16 &&
        emptyNorm != null && emptyNorm.Length == 0,
   "CustomColorPaletteState: null init selects slot 0 with 16 empty slots and empty normalized array");

  int[] allWhite = new int[16];
  for (int i = 0; i < allWhite.Length; i++) {
   allWhite[i] = CustomColorPaletteState.EmptyColorRef;
  }
  var emptyWhite = new CustomColorPaletteState(allWhite);
  Check(CustomColorPaletteState.FindFirstEmptySlot(allWhite) == 0 &&
        emptyWhite.SelectedSlotIndex == 0 &&
        emptyWhite.GetNormalizedCustomColors().Length == 0,
   "CustomColorPaletteState: all-white init selects slot 0 and normalizes to length 0");

  // 2. Red at slot 0 -> init selects first empty slot (1)
  var state = new CustomColorPaletteState(new int[] { 0x000000FF });
  int[] normAfterRed = state.GetNormalizedCustomColors();
  Check(state.SelectedSlotIndex == 1 &&
        !state.IsSlotEmpty(0) &&
        state.GetSlotColorRef(0) == 0x000000FF &&
        state.IsSlotEmpty(1) &&
        normAfterRed != null && normAfterRed.Length == 16 &&
        normAfterRed[0] == 0x000000FF &&
        normAfterRed[1] == CustomColorPaletteState.EmptyColorRef,
   "CustomColorPaletteState: init with red in slot 0 selects first empty slot 1");

  // 3. Save blue into selected slot 1 -> slot 0 stays red, slot 1 becomes 0x00FF0000, advances to slot 2
  int savedBlueIndex;
  bool savedBlue = state.SaveColorToSelectedSlot(Color.FromRgb(0, 0, 255), out savedBlueIndex);
  Check(savedBlue &&
        savedBlueIndex == 1 &&
        state.GetSlotColorRef(0) == 0x000000FF &&
        state.GetSlotColorRef(1) == 0x00FF0000 &&
        !state.IsSlotEmpty(1) &&
        state.SelectedSlotIndex == 2,
   "CustomColorPaletteState: saving blue to slot 1 writes 0x00FF0000, preserves slot 0, advances to slot 2");

  // 4. Select empty slot 5 -> returns false, moves selection to 5; save green -> writes slot 5, advances to 6
  Color loadedEmpty;
  bool selectedEmpty5 = state.SelectSlot(5, out loadedEmpty);
  Check(!selectedEmpty5 && state.SelectedSlotIndex == 5,
   "CustomColorPaletteState: SelectSlot(5) on empty slot returns false and updates SelectedSlotIndex to 5");

  int savedGreenIndex;
  bool savedGreen = state.SaveColorToSelectedSlot(Color.FromRgb(0, 204, 68), out savedGreenIndex);
  Check(savedGreen &&
        savedGreenIndex == 5 &&
        state.GetSlotColorRef(5) == 0x0044CC00 &&
        !state.IsSlotEmpty(5) &&
        state.SelectedSlotIndex == 6,
   "CustomColorPaletteState: saving green to slot 5 writes 0x0044CC00 and advances to slot 6");

  // 5. Select filled slot 0 -> returns true with red (255, 0, 0) and SelectedSlotIndex == 0
  Color loadedRed;
  bool selectedRed0 = state.SelectSlot(0, out loadedRed);
  Check(selectedRed0 &&
        loadedRed == Color.FromRgb(255, 0, 0) &&
        state.SelectedSlotIndex == 0,
   "CustomColorPaletteState: SelectSlot(0) on red slot returns true with Color(255,0,0) and SelectedSlotIndex 0");

  // 6. Clear slot 0 -> returns true, becomes empty, stays on slot 0; second clear returns false
  bool clearedFirst = state.ClearSelectedSlot();
  Check(clearedFirst &&
        state.IsSlotEmpty(0) &&
        state.GetSlotColorRef(0) == CustomColorPaletteState.EmptyColorRef &&
        state.SelectedSlotIndex == 0 &&
        state.GetSlotColorRef(1) == 0x00FF0000 &&
        state.GetSlotColorRef(5) == 0x0044CC00,
   "CustomColorPaletteState: ClearSelectedSlot clears slot 0, keeps selection at 0, preserves other slots");

  bool clearedSecond = state.ClearSelectedSlot();
  Check(!clearedSecond && state.SelectedSlotIndex == 0,
   "CustomColorPaletteState: second ClearSelectedSlot on empty slot 0 returns false");

  // 7. Full 16 slots, slot 15 empty, and wrap-around advancement
  int[] fullSlots = new int[16];
  for (int i = 0; i < 16; i++) {
   fullSlots[i] = CustomColorPaletteState.ToColorRef((byte)(16 + i * 12), (byte)(32 + i * 8), (byte)(48 + i * 10));
  }
  Check(CustomColorPaletteState.FindFirstEmptySlot(fullSlots) == 0 &&
        CustomColorPaletteState.FindNextEmptySlot(fullSlots, 0) == 1 &&
        CustomColorPaletteState.FindNextEmptySlot(fullSlots, 14) == 15 &&
        CustomColorPaletteState.FindNextEmptySlot(fullSlots, 15) == 0,
   "CustomColorPaletteState: full 16 slots FindFirstEmptySlot returns 0 and FindNextEmptySlot wraps (idx+1)%16");

  var fullState = new CustomColorPaletteState(fullSlots);
  int fullSavedIdx;
  bool fullSaved0 = fullState.SaveColorToSelectedSlot(Color.FromRgb(12, 34, 56), out fullSavedIdx);
  Check(fullSaved0 && fullSavedIdx == 0 &&
        fullState.GetSlotColorRef(0) == 0x0038220C &&
        fullState.SelectedSlotIndex == 1,
   "CustomColorPaletteState: full palette init selects 0 and saving advances to 1");

  Color loadedFull15;
  bool selectedFull15 = fullState.SelectSlot(15, out loadedFull15);
  bool fullSaved15 = fullState.SaveColorToSelectedSlot(Color.FromRgb(90, 80, 70), out fullSavedIdx);
  Check(selectedFull15 &&
        loadedFull15 == CustomColorPaletteState.FromColorRef(fullSlots[15]) &&
        fullSaved15 && fullSavedIdx == 15 &&
        fullState.GetSlotColorRef(15) == 0x0046505A &&
        fullState.SelectedSlotIndex == 0,
   "CustomColorPaletteState: full palette saving slot 15 wraps selection to 0");

  int[] almostFull15 = new int[16];
  for (int i = 0; i < 15; i++) {
   almostFull15[i] = fullSlots[i];
  }
  almostFull15[15] = CustomColorPaletteState.EmptyColorRef;
  Check(CustomColorPaletteState.FindFirstEmptySlot(almostFull15) == 15,
   "CustomColorPaletteState: FindFirstEmptySlot returns 15 when 0..14 are full");

  var state15 = new CustomColorPaletteState(almostFull15);
  Check(state15.SelectedSlotIndex == 15,
   "CustomColorPaletteState: init selects 15 when only slot 15 is empty");

  Color loaded14;
  bool selected14 = state15.SelectSlot(14, out loaded14);
  int saved14Idx;
  bool saved14 = state15.SaveColorToSelectedSlot(Color.FromRgb(10, 20, 30), out saved14Idx);
  Check(selected14 &&
        loaded14 == CustomColorPaletteState.FromColorRef(fullSlots[14]) &&
        saved14 && saved14Idx == 14 && state15.SelectedSlotIndex == 15,
   "CustomColorPaletteState: saving non-white in slot 14 advances to empty slot 15");

  int[] slot0Empty = new int[16];
  slot0Empty[0] = CustomColorPaletteState.EmptyColorRef;
  for (int i = 1; i < 16; i++) {
   slot0Empty[i] = fullSlots[i];
  }
  var stateWrap0 = new CustomColorPaletteState(slot0Empty);
  Color loadedWrap15;
  bool selectedWrap15 = stateWrap0.SelectSlot(15, out loadedWrap15);
  int savedWrap15Idx;
  bool savedWrap15 = stateWrap0.SaveColorToSelectedSlot(Color.FromRgb(200, 100, 50), out savedWrap15Idx);
  Check(selectedWrap15 &&
        loadedWrap15 == CustomColorPaletteState.FromColorRef(fullSlots[15]) &&
        savedWrap15 && savedWrap15Idx == 15 &&
        stateWrap0.GetSlotColorRef(15) == 0x003264C8 &&
        stateWrap0.SelectedSlotIndex == 0,
   "CustomColorPaletteState: saving non-white in slot 15 wraps to empty slot 0");

  // 8. Pure white (255, 255, 255) cannot be saved and leaves slot and selection unchanged
  int whiteIdx;
  bool whiteOnEmpty = emptyNull.SaveColorToSelectedSlot(Color.FromRgb(255, 255, 255), out whiteIdx);
  Check(!whiteOnEmpty && emptyNull.IsSlotEmpty(0) && emptyNull.SelectedSlotIndex == 0,
   "CustomColorPaletteState: saving pure white on empty slot returns false without mutating slot or selection");

  var whiteGuard = new CustomColorPaletteState(new int[] { 0x000000FF });
  Color dummyLoaded;
  bool whiteGuardSelected0 = whiteGuard.SelectSlot(0, out dummyLoaded);
  bool whiteOnFilled = whiteGuard.SaveColorToSelectedSlot(Color.FromRgb(255, 255, 255), out whiteIdx);
  Check(whiteGuardSelected0 &&
        dummyLoaded == Color.FromRgb(255, 0, 0) &&
        !whiteOnFilled &&
        whiteGuard.GetSlotColorRef(0) == 0x000000FF &&
        whiteGuard.SelectedSlotIndex == 0,
   "CustomColorPaletteState: saving pure white on filled slot returns false and preserves existing color and selection");

  // 9. ToColorRef / FromColorRef (no R/B swap) and RgbToHsv / HsvToRgb roundtrip accuracy (<= 1)
  Check(CustomColorPaletteState.ToColorRef(255, 0, 0) == 0x000000FF &&
        CustomColorPaletteState.ToColorRef(0, 255, 0) == 0x0000FF00 &&
        CustomColorPaletteState.ToColorRef(0, 0, 255) == 0x00FF0000 &&
        CustomColorPaletteState.ToColorRef(0, 204, 68) == 0x0044CC00 &&
        CustomColorPaletteState.FromColorRef(0x000000FF) == Color.FromRgb(255, 0, 0) &&
        CustomColorPaletteState.FromColorRef(0x0000FF00) == Color.FromRgb(0, 255, 0) &&
        CustomColorPaletteState.FromColorRef(0x00FF0000) == Color.FromRgb(0, 0, 255) &&
        CustomColorPaletteState.FromColorRef(0x0044CC00) == Color.FromRgb(0, 204, 68),
   "CustomColorPaletteState: ToColorRef and FromColorRef preserve R/G/B byte order without R/B reversal");

  Color[] hsvSamples = new Color[] {
   Color.FromRgb(255, 0, 0),
   Color.FromRgb(0, 255, 0),
   Color.FromRgb(0, 0, 255),
   Color.FromRgb(0, 0, 0),
   Color.FromRgb(128, 128, 128),
   Color.FromRgb(255, 255, 255),
   Color.FromRgb(0, 204, 68),
   Color.FromRgb(170, 85, 255),
   Color.FromRgb(213, 147, 62),
   Color.FromRgb(45, 110, 195)
  };
  bool allRoundtripOk = true;
  string roundtripDetail = "";
  for (int i = 0; i < hsvSamples.Length; i++) {
   Color c = hsvSamples[i];
   double h;
   double s;
   double v;
   CustomColorPaletteState.RgbToHsv(c.R, c.G, c.B, out h, out s, out v);
   Color rt = CustomColorPaletteState.HsvToRgb(h, s, v);
   if (double.IsNaN(h) || double.IsNaN(s) || double.IsNaN(v) ||
       rt.A != 255 ||
       Math.Abs(rt.R - c.R) > 1 ||
       Math.Abs(rt.G - c.G) > 1 ||
       Math.Abs(rt.B - c.B) > 1) {
    allRoundtripOk = false;
    roundtripDetail = "mismatch at (" + c.R + "," + c.G + "," + c.B + ") -> (" + rt.R + "," + rt.G + "," + rt.B + ", A=" + rt.A + ")";
    break;
   }
  }
  Check(allRoundtripOk,
   "CustomColorPaletteState: RgbToHsv/HsvToRgb roundtrip accuracy <= 1 for primary, grayscale, and mid-tone colors" +
   (allRoundtripOk ? "" : " (" + roundtripDetail + ")"));
 }

 static void TestStandbyWatcherStopIncludeHost(string watchExePath) {
  if (string.IsNullOrEmpty(watchExePath) || !File.Exists(watchExePath)) {
   Check(false, "StandbyWatcher --stop --include-host: AppleMusicLyricsWatch.exe path is required");
   return;
  }

  string scope = "stage2qa-" + Guid.NewGuid().ToString("N");
  string pipeName = HostIpc.BuildPipeName(scope);
  string hostMutexName = HostIpc.BuildMutexName("AMDesktopLyrics.SingleInstance", scope);

  bool shutdownReceived = false;
  using (var mutexReady = new ManualResetEvent(false))
  using (var shutdownSignal = new ManualResetEvent(false))
  using (var server = new HostIpcServer(pipeName, cmd => {
   if (cmd.Kind == HostCommandKind.Shutdown) {
    shutdownReceived = true;
    shutdownSignal.Set();
   }
   return new HostStateSnapshot {
    Enabled = true,
    Locked = false,
    Visible = true,
    Polling = true,
    ClientAvailable = true
   };
  })) {
   server.Start();

   bool createdNew = false;
   Thread mutexOwnerThread = new Thread(() => {
    using (var hostMutex = new Mutex(true, hostMutexName, out createdNew)) {
     mutexReady.Set();
     shutdownSignal.WaitOne(5000);
     if (createdNew) {
      try { hostMutex.ReleaseMutex(); } catch (ApplicationException) { }
     }
    }
   });
   mutexOwnerThread.IsBackground = true;
   mutexOwnerThread.Start();

   Check(mutexReady.WaitOne(3000) && createdNew,
    "StandbyWatcher --stop --include-host: isolated host mutex acquired by mock host");

   Process watchStopProc = null;
   try {
    var sw = Stopwatch.StartNew();
    watchStopProc = Process.Start(new ProcessStartInfo {
     FileName = watchExePath,
     Arguments = "--stop --include-host --ipc-scope " + scope,
     UseShellExecute = false,
     CreateNoWindow = true
    });
    bool exited = watchStopProc != null && watchStopProc.WaitForExit(5000);
    sw.Stop();
    Check(exited && watchStopProc.ExitCode == 0,
     "StandbyWatcher --stop --include-host: exited cleanly with code 0 (elapsed=" + sw.ElapsedMilliseconds + "ms)");
    Check(shutdownReceived,
     "StandbyWatcher --stop --include-host: sent HostCommandKind.Shutdown to scoped HostIpcServer");
    Check(mutexOwnerThread.Join(3000),
     "StandbyWatcher --stop --include-host: mock host mutex thread exited cleanly");
   } finally {
    shutdownSignal.Set();
    if (watchStopProc != null) {
     try { if (!watchStopProc.HasExited) watchStopProc.Kill(); } catch { }
     watchStopProc.Dispose();
    }
   }
  }
 }
}
