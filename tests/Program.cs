using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CaptionCopy;

internal static class Tests
{
    private static int _passed, _failed;
    private static readonly DateTime Epoch = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--live") return Live(int.Parse(args[1]), args[2]);
        if (args.Length > 0 && args[0] == "--compare-clipboard") return CompareClipboard();
        if (args.Length > 0 && args[0] == "--hold-clipboard")
        {
            bool opened = OpenClipboard(IntPtr.Zero);
            Console.WriteLine(opened ? "LOCKED" : "FAILED");
            Console.Out.Flush();
            if (!opened) return 1;
            try { Thread.Sleep(int.Parse(args[1])); } finally { CloseClipboard(); }
            return 0;
        }
        Test("incremental Chinese captions", delegate
        {
            var e = Feed("你好", "你好世界", "你好世界。"); Equal("你好世界。", Text(e));
        });
        Test("tail recognition correction", delegate
        {
            var e = Feed("This is a cat", "This is a dog."); Equal("This is a dog.", Text(e));
        });
        Test("short English recognition correction", delegate
        {
            Equal("We are testing.", Text(Feed("We is testing", "We are testing.")));
        });
        Test("single complete English word overlap", delegate
        {
            Equal("This is a test.", Text(Feed("This is a", "a test.")));
        });
        Test("single Chinese character overlap", delegate
        {
            Equal("今天我们走。", Text(Feed("今天我", "我们走。")));
        });
        Test("multiple sentences in one snapshot", delegate
        {
            var e = Feed("第一句。第二句！Third sentence?"); Equal("第一句。|第二句！|Third sentence?", Text(e));
        });
        Test("exact rolling overlap", delegate
        {
            var e = Feed("第一句。第二句。", "第二句。第三句。", "第三句。第四句。"); Equal("第一句。|第二句。|第三句。|第四句。", Text(e));
        });
        Test("rolling overlap with revised tail", delegate
        {
            var e = Feed("First sentence. The answer is forty", "The answer is forty two. Next sentence.");
            Equal("First sentence.|The answer is forty two.|Next sentence.", Text(e));
        });
        Test("scrolled history cannot be revised", delegate
        {
            var e = Feed("旧句。正在识别旧词", "正在识别新词。后一句。"); Equal("旧句。|正在识别新词。|后一句。", Text(e));
        });
        Test("no overlap preserves both blocks", delegate
        {
            var e = Feed("第一段。", "另外一段。"); Equal("第一段。|另外一段。", Text(e));
        });
        Test("identical polls do not duplicate", delegate
        {
            var e = Feed("你好。", "你好。", "你好。"); Equal("你好。", Text(e));
        });
        Test("spoken repetition within growing window is retained", delegate
        {
            var e = Feed("你好。", "你好。你好。", "你好。你好。你好。"); Equal("你好。|你好。|你好。", Text(e));
        });
        Test("similar completed new sentences are retained", delegate
        {
            var e = Feed("That was great.", "It was great."); Equal("That was great.|It was great.", Text(e));
        });
        Test("new segment preserves genuinely repeated sentence", delegate
        {
            var e = Feed("重复一次。"); e.BeginSegment(); e.Observe("重复一次。", Epoch.AddSeconds(1)); Equal("重复一次。|重复一次。", Text(e));
        });
        Test("clear suppresses unchanged current window", delegate
        {
            var e = Feed("旧句。末句"); e.Clear(); e.Observe("旧句。末句", Epoch.AddSeconds(1)); Equal("", Text(e));
        });
        Test("clear captures only newly appended text", delegate
        {
            var e = Feed("旧句。"); e.Clear(); e.Observe("旧句。新句。", Epoch.AddSeconds(1)); Equal("新句。", Text(e));
        });
        Test("clear remains hidden after source correction", delegate
        {
            var e = Feed("这是旧词"); e.Clear(); e.Observe("这是新词", Epoch.AddSeconds(1)); Equal("", Text(e));
            e.Observe("这是新词。后来的一句。", Epoch.AddSeconds(2)); Equal("后来的一句。", Text(e));
        });
        Test("clear and source rolling", delegate
        {
            var e = Feed("旧句。保留的末句。"); e.Clear(); e.Observe("保留的末句。新内容。", Epoch.AddSeconds(1)); Equal("新内容。", Text(e));
        });
        Test("clear and reconnect", delegate
        {
            var e = Feed("旧句。"); e.Clear(); e.BeginSegment(); e.Observe("新会话。", Epoch.AddSeconds(1)); Equal("新会话。", Text(e));
        });
        Test("blank UIA transition does not erase content", delegate
        {
            var e = Feed("已有字幕。", "", "  "); Equal("已有字幕。", Text(e));
        });
        Test("draft and text pause", delegate
        {
            var e = Feed("没有标点"); True(e.GetSentences()[0].IsDraft, "should be a draft");
            e.Tick(Epoch.AddSeconds(1)); True(e.GetSentences()[0].IsDraft, "pause too early");
            e.Tick(Epoch.AddSeconds(2)); True(!e.GetSentences()[0].IsDraft, "should settle after 2 seconds");
            e.Observe("没有标点继续说话", Epoch.AddSeconds(3)); Equal("没有标点|继续说话", Text(e));
        });
        Test("slow English partial word does not split", delegate
        {
            var e = Feed("hel"); e.Tick(Epoch.AddSeconds(2)); e.Observe("hello world", Epoch.AddSeconds(3)); Equal("hello world", Text(e));
        });
        Test("late punctuation attaches to the settled sentence", delegate
        {
            var e = Feed("你好"); e.Tick(Epoch.AddSeconds(2));
            e.Observe("你好。下一句", Epoch.AddSeconds(3)); Equal("你好。|下一句", Text(e));
        });
        Test("English paused phrases split at a space", delegate
        {
            var e = Feed("hello world"); e.Tick(Epoch.AddSeconds(2)); e.Observe("hello world next phrase", Epoch.AddSeconds(3)); Equal("hello world|next phrase", Text(e));
        });
        Test("new independent block after pause is not a revision", delegate
        {
            var e = Feed("hello world"); e.Tick(Epoch.AddSeconds(2));
            e.Observe("hello there", Epoch.AddSeconds(3)); Equal("hello world|hello there", Text(e));
        });
        Test("clear after long scrolled history retains only live baseline", delegate
        {
            var e = Feed("第一句。第二句。", "第二句。第三句。"); e.Clear();
            e.Observe("第三句。第四句。", Epoch.AddSeconds(1)); Equal("第四句。", Text(e));
        });
        Test("punctuation and trailing quotes", delegate
        {
            Equal("他说：“你好！”|接下来。|She said, \"Hello!\"|Bye.", Text(Feed("他说：“你好！”接下来。 She said, \"Hello!\" Bye.")));
        });
        Test("decimal and English titles", delegate
        {
            Equal("Dr. Smith paid 3.14 dollars.|Next.", Text(Feed("Dr. Smith paid 3.14 dollars. Next.")));
        });
        Test("common dotted abbreviations", delegate
        {
            Equal("Use e.g. this sample.|Then finish.", Text(Feed("Use e.g. this sample. Then finish.")));
        });
        Test("English initials", delegate
        {
            Equal("J. Smith is here.|Go!", Text(Feed("J. Smith is here. Go!")));
        });
        Test("ellipsis and multiple terminators", delegate
        {
            Equal("Wait...|Really?!|真的……|继续。", Text(Feed("Wait... Really?! 真的……继续。")));
        });
        Test("mixed text and wrapped Chinese", delegate
        {
            Equal("中文测试。|Hello Windows 11!", Text(Feed("中文\r\n测试。\r\nHello  Windows 11!")));
        });
        Test("copy all includes live draft and newlines only", delegate
        {
            var e = Feed("已完成。未完成"); Equal("已完成。" + Environment.NewLine + "未完成", e.GetCopyText());
        });
        Test("shorter recognition revision", delegate
        {
            var e = Feed("This is too long", "This is"); Equal("This is", Text(e));
        });
        Test("first word revision with strong shared suffix", delegate
        {
            var e = Feed("Their cat is playing", "The cat is playing"); Equal("The cat is playing", Text(e));
        });
        Test("long rolling stream retains chronological history", delegate
        {
            var e = new TranscriptEngine();
            var expected = new List<string>();
            for (int i = 0; i < 2000; i++)
            {
                string sentence = "第" + i + "条测试字幕。";
                expected.Add(sentence);
                int start = Math.Max(0, expected.Count - 3);
                e.Observe(string.Join("", expected.Skip(start)), Epoch.AddMilliseconds(i * 300));
            }
            Equal(string.Join("|", expected), Text(e));
        });
        Test("continuous Chinese speech uses clauses without waiting for a pause", delegate
        {
            string speech = string.Concat(Enumerable.Repeat("他们从来不赚消费者的钱，相信这两年开满大街的零食店，不少人都光顾过吧，价格便宜品种丰富按需挑选，一买就是几十块上百块的人，我估计大有人在，", 4));
            var e = Feed(speech);
            True(e.GetSentences().Count > 8, "continuous speech should have several copyable chunks");
            Equal(Compact(speech), Compact(e.GetCopyText()));
            True(e.GetSentences().All(s => SentenceSplitter.Measure(s.Text) <= SentenceSplitter.MaximumChunkUnits), "a Chinese chunk is too long");
        });
        Test("unpunctuated live growth stays bounded and earlier chunks stay stable", delegate
        {
            string speech = string.Concat(Enumerable.Repeat("这是一段没有标点也没有任何停顿的连续讲话用于验证自动分段", 12));
            var e = new TranscriptEngine();
            string first = null;
            for (int i = 3; i <= speech.Length; i += 3)
            {
                e.Observe(speech.Substring(0, i), Epoch.AddMilliseconds(i * 20));
                var rows = e.GetSentences();
                if (rows.Count > 1 && first == null) first = rows[0].Text;
                if (first != null) Equal(first, rows[0].Text);
                True(rows.All(s => SentenceSplitter.Measure(s.Text) <= SentenceSplitter.MaximumChunkUnits), "a growing row exceeded the limit");
            }
            e.Observe(speech, Epoch.AddMilliseconds(speech.Length * 20));
            Equal(speech, Compact(e.GetCopyText()));
            True(e.GetSentences().Count > 6, "no-punctuation speech did not split");
        });
        Test("long English speech splits between whole words", delegate
        {
            string speech = string.Join(" ", Enumerable.Repeat("continuous English speech should remain easy to copy without breaking any individual words", 8));
            var e = Feed(speech);
            Equal(speech, string.Join(" ", e.GetSentences().Select(s => s.Text)));
            True(e.GetSentences().Count > 5, "English speech did not split");
            True(e.GetSentences().All(s => s.Text.Length <= SentenceSplitter.MaximumChunkUnits), "English chunk too long");
        });
        Test("long clauses do not split formatted numbers or decimal points", delegate
        {
            string speech = "The amount transferred this morning was 1,234.56 dollars, and the meeting starts at 12:30 with another 5,678.90 dollars remaining";
            string joined = string.Join(" ", Feed(speech).GetSentences().Select(s => s.Text));
            Equal(speech, joined);
            True(joined.Contains("1,234.56") && joined.Contains("12:30") && joined.Contains("5,678.90"), "number was split");
        });
        Test("meaningful short pauses settle before one second", delegate
        {
            const string first = "这是已经识别出来的一段完整内容";
            var e = Feed(first);
            True(!e.Tick(Epoch.AddMilliseconds(500)), "should not split at a tiny recognition gap");
            True(e.Tick(Epoch.AddMilliseconds(800)), "should accept a sub-second pause");
            True(!e.GetSentences().Last().IsDraft, "short pause should settle the row");
            e.Observe(first + "然后继续下一段话", Epoch.AddMilliseconds(1000));
            Equal(first + "|然后继续下一段话", Text(e));
        });
        Test("short pauses do not turn tiny fragments into separate rows", delegate
        {
            var e = Feed("这里"); e.Tick(Epoch.AddMilliseconds(900));
            True(e.GetSentences()[0].IsDraft, "tiny fragment should keep growing");
            e.Observe("这里继续识别", Epoch.AddMilliseconds(1200)); Equal("这里继续识别", Text(e));
        });
        Test("short-pause display does not disable recognition corrections", delegate
        {
            var e = Feed("这里已经识别出一段比较长的旧词内容");
            e.Tick(Epoch.AddMilliseconds(800));
            e.Observe("这里已经识别出一段比较长的新词内容", Epoch.AddMilliseconds(1100));
            Equal("这里已经识别出一段比较长的新词内容", Text(e));
        });
        Test("length splitting preserves surrogate pairs and combining marks", delegate
        {
            string speech = string.Concat(Enumerable.Repeat("这是中文测试内容🙂文字组合e\u0301继续讲话", 12));
            var rows = Feed(speech).GetSentences();
            Equal(speech, string.Concat(rows.Select(s => s.Text)));
            foreach (var row in rows)
            {
                True(!char.IsLowSurrogate(row.Text[0]) && !char.IsHighSurrogate(row.Text[row.Text.Length - 1]), "split a surrogate pair");
                True(row.Text[0] != '\u0301', "split a combining mark");
            }
        });
        Test("single long English token remains intact", delegate
        {
            string token = new string('a', 150);
            var rows = Feed(token + " next words follow").GetSentences();
            Equal(token, rows[0].Text);
            Equal(token + " next words follow", string.Join(" ", rows.Select(s => s.Text)));
        });
        Test("revision after length-based splitting preserves earlier chunks", delegate
        {
            string speech = string.Concat(Enumerable.Repeat("没有标点的连续内容", 14));
            var e = Feed(speech + "识别旧词");
            string first = e.GetSentences()[0].Text;
            e.Observe(speech + "识别新词", Epoch.AddMilliseconds(300));
            Equal(first, e.GetSentences()[0].Text);
            Equal(speech + "识别新词", Compact(e.GetCopyText()));
        });
        Test("embedded app icon includes small and large resolutions", delegate
        {
            using (Stream stream = typeof(TranscriptEngine).Assembly.GetManifestResourceStream("CaptionCopy.App.ico"))
            {
                True(stream != null, "missing icon resource");
                var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                True(decoder.Frames.Any(f => f.PixelWidth == 16) && decoder.Frames.Any(f => f.PixelWidth == 64) && decoder.Frames.Any(f => f.PixelWidth == 256), "missing icon sizes");
            }
        });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Test("forward drag range copies inclusive rows in reading order", delegate
        {
            Equal("二" + Environment.NewLine + "三" + Environment.NewLine + "四", SelectionText.Join(new[] { "一", "二", "三", "四" }, 1, 3));
        });
        Test("reverse drag range preserves reading order", delegate
        {
            Equal("一" + Environment.NewLine + "二" + Environment.NewLine + "三", SelectionText.Join(new[] { "一", "二", "三", "四" }, 2, 0));
        });
        Test("single click range copies exactly one row", delegate
        {
            Equal("中文 English", SelectionText.Join(new[] { "其他", "中文 English" }, 1, 1));
        });
        Test("range keeps repeated sentences", delegate
        {
            Equal("重复" + Environment.NewLine + "重复", SelectionText.Join(new[] { "重复", "重复" }, 0, 1));
        });
        Test("invalid or empty selection cannot copy unrelated rows", delegate
        {
            Equal("", SelectionText.Join(new string[0], 0, 0));
            Equal("", SelectionText.Join(new[] { "一" }, -1, 0));
            Equal("", SelectionText.Join(new[] { "一" }, 0, 1));
        });
        Test("protected transparency keeps content opacity at one", delegate
        {
            var p = new AppPreferences { Opacity = .45, ProtectText = true };
            True(p.WindowOpacity == 1 && p.BackgroundOpacity == .45, "text protection must not lower Window.Opacity");
        });
        Test("unprotected transparency fades the entire window", delegate
        {
            var p = new AppPreferences { Opacity = .45, ProtectText = false };
            True(p.WindowOpacity == .45 && p.BackgroundOpacity == 1, "must not apply opacity twice");
        });
        Test("opacity limits prevent invisible windows and invalid values", delegate
        {
            var p = new AppPreferences { Opacity = 0 }; True(p.Opacity == .3, "minimum");
            p.Opacity = 2; True(p.Opacity == 1, "maximum");
            p.Opacity = double.NaN; True(p.Opacity == 1, "NaN");
            p.Opacity = double.PositiveInfinity; True(p.Opacity == 1, "infinity");
        });
        Test("settings round trip saves preferences without transcripts", delegate
        {
            string path = Path.Combine(Path.GetTempPath(), "CaptionCopy-test-" + Guid.NewGuid() + ".xml");
            try
            {
                True(PreferencesStore.Save(path, new AppPreferences { Opacity = .65, ProtectText = false }), "save failed");
                var p = PreferencesStore.Load(path);
                True(p.Opacity == .65 && !p.ProtectText, "round trip failed");
                True(!File.ReadAllText(path).Contains("字幕"), "preferences should not contain captions");
            }
            finally { File.Delete(path); }
        });
        Test("corrupt settings recover to readable defaults", delegate
        {
            string path = Path.Combine(Path.GetTempPath(), "CaptionCopy-test-" + Guid.NewGuid() + ".xml");
            try
            {
                File.WriteAllText(path, "<broken>");
                var p = PreferencesStore.Load(path); True(p.Opacity == 1 && p.ProtectText, "bad defaults");
                File.WriteAllText(path, "<CaptionCopy opacity='NaN' protectText='true' />");
                True(PreferencesStore.Load(path).Opacity == 1, "unsafe opacity accepted");
            }
            finally { File.Delete(path); }
        });
        Test("missing settings use opaque protected defaults", delegate
        {
            var p = PreferencesStore.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml"));
            True(p.Opacity == 1 && p.ProtectText, "missing settings defaults");
        });
        Test("settings save failure is recoverable", delegate
        {
            True(!PreferencesStore.Save(Path.GetTempPath(), new AppPreferences()), "directory should not be writable as an XML file");
        });
        Test("clipboard preserves Unicode and exact newlines", delegate
        {
            string value = "中文。" + Environment.NewLine + "English draft";
            True(Await(ClipboardService.CopyAsync(value)), "copy failed"); Equal(value, Clipboard.GetText());
        });
        Test("empty copy leaves clipboard unchanged", delegate
        {
            Clipboard.SetText("test sentinel"); True(!Await(ClipboardService.CopyAsync("")), "empty should not copy"); Equal("test sentinel", Clipboard.GetText());
        });
        Test("clipboard retry succeeds after temporary contention", delegate
        {
            using (Process holder = LockClipboard(100))
            { True(Await(ClipboardService.CopyAsync("recovered")), "should recover"); holder.WaitForExit(); Equal("recovered", Clipboard.GetText()); }
        });
        Test("clipboard reports persistent contention without throwing", delegate
        {
            using (Process holder = LockClipboard(1000))
            { True(!Await(ClipboardService.CopyAsync("busy")), "should report failure"); holder.WaitForExit(); }
        });
        Console.WriteLine("RESULT: " + _passed + " passed, " + _failed + " failed");
        return _failed == 0 ? 0 : 1;
    }

    private static TranscriptEngine Feed(params string[] snapshots)
    {
        var engine = new TranscriptEngine();
        for (int i = 0; i < snapshots.Length; i++) engine.Observe(snapshots[i], Epoch.AddMilliseconds(i * 300));
        return engine;
    }
    private static string Text(TranscriptEngine engine) { return string.Join("|", engine.GetSentences().Select(s => s.Text)); }
    private static string Compact(string value) { return string.Concat(value.Where(c => !char.IsWhiteSpace(c))); }
    private static void Equal(string expected, string actual) { if (expected != actual) throw new Exception("Expected: " + expected + "\nActual: " + actual); }
    private static void True(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Test(string name, Action action)
    {
        try { action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { _failed++; Console.WriteLine("FAIL " + name + "\n" + e.Message); }
    }
    private static bool Await(Task<bool> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            task.ContinueWith(t => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }
    private static Process LockClipboard(int milliseconds)
    {
        var p = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--hold-clipboard " + milliseconds)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        if (p.StandardOutput.ReadLine() != "LOCKED") { p.Dispose(); throw new Exception("could not lock clipboard for test"); }
        return p;
    }

    private static int Live(int seconds, string logPath)
    {
        int samples = 0, reading = 0, changed = 0;
        string lastText = null, lastState = null;
        double peakMemory = 0;
        bool appGone = false;
        object gate = new object();
        Stopwatch timer = Stopwatch.StartNew();
        using (var writer = new StreamWriter(logPath, false, new UTF8Encoding(true)))
        using (var reader = new CaptionReader(delegate(CaptionObservation sample)
        {
            lock (gate)
            {
                samples++;
                if (sample.Connection == CaptionConnection.Reading) reading++;
                if (sample.Text != lastText || sample.Connection.ToString() != lastState)
                {
                    changed++;
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},sample,{1},{2},{3}", timer.ElapsedMilliseconds, sample.Connection, sample.Session, sample.Text.Length));
                    writer.Flush();
                    lastText = sample.Text; lastState = sample.Connection.ToString();
                }
            }
        }))
        {
            writer.WriteLine("elapsed_ms,event,state,session_or_memory_mib,text_length");
            reader.Start();
            for (int second = 0; second < seconds; second++)
            {
                Thread.Sleep(1000);
                Process[] apps = Process.GetProcessesByName("CaptionCopy");
                if (apps.Length == 0) appGone = true;
                foreach (Process app in apps)
                {
                    using (app)
                    {
                        double memory = app.WorkingSet64 / 1048576.0;
                        peakMemory = Math.Max(peakMemory, memory);
                        if (second % 30 == 0) lock (gate) { writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},health,{1},{2:F1},0", timer.ElapsedMilliseconds, app.Responding, memory)); writer.Flush(); }
                    }
                }
            }
            lock (gate)
            {
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "SUMMARY seconds={0} samples={1} reading={2} changes={3} peak_app_mib={4:F1} app_gone={5}", seconds, samples, reading, changed, peakMemory, appGone));
                writer.Flush();
            }
        }
        Console.WriteLine("LIVE RESULT: seconds=" + seconds + " samples=" + samples + " reading=" + reading + " changes=" + changed + " appGone=" + appGone);
        return reading > 0 && !appGone ? 0 : 1;
    }

    private static int CompareClipboard()
    {
        string copied = string.Concat(Clipboard.GetText().Where(c => !char.IsWhiteSpace(c)));
        string source = null;
        using (var received = new ManualResetEvent(false))
        using (var reader = new CaptionReader(delegate(CaptionObservation sample)
        {
            if (sample.Connection != CaptionConnection.Reading) return;
            source = string.Concat(sample.Text.Where(c => !char.IsWhiteSpace(c)));
            received.Set();
        }))
        {
            reader.Start();
            if (!received.WaitOne(5000)) { Console.WriteLine("FAIL: no readable live captions"); return 1; }
            bool matches = copied.Length > 0 && source.EndsWith(copied, StringComparison.Ordinal);
            Console.WriteLine("Live source characters=" + source.Length + ", copied characters=" + copied.Length + ", exact suffix match=" + matches);
            return matches ? 0 : 1;
        }
    }
}
