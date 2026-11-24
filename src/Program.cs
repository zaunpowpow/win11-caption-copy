using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("字幕摘录")]
[assembly: AssemblyDescription("Windows 11 实时字幕复制工具")]
[assembly: AssemblyVersion("1.1.0.0")]

namespace CaptionCopy
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            try
            {
                var controller = new MainWindowController();
                app.MainWindow = controller.Window;
                app.Run(controller.Window);
            }
            catch (Exception ex)
            {
                MessageBox.Show("工具无法启动：" + ex.Message, "字幕摘录", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public sealed class SentenceRow : INotifyPropertyChanged
    {
        private string _text;
        private bool _draft;
        private bool _copied;
        public string Text { get { return _text; } set { if (_text != value) { _text = value; Notify("Text"); IsCopied = false; } } }
        public bool IsDraft { get { return _draft; } set { if (_draft != value) { _draft = value; Notify("IsDraft"); } } }
        public bool IsCopied { get { return _copied; } set { if (_copied != value) { _copied = value; Notify("IsCopied"); } } }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name)); }
    }

    public static class ClipboardService
    {
        // Clipboard access must stay on the WPF STA dispatcher, including retries.
        public static async Task<bool> CopyAsync(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var data = new System.Windows.Forms.DataObject(System.Windows.Forms.DataFormats.UnicodeText, text);
                    System.Windows.Forms.Clipboard.SetDataObject(data, true, 1, 0);
                    return true;
                }
                catch (COMException) { }
                catch (ExternalException) { }
                if (attempt < 4) await Task.Delay(60);
            }
            return false;
        }
    }

    public sealed class MainWindowController
    {
        public Window Window { get; private set; }
        private readonly TranscriptEngine _engine = new TranscriptEngine();
        private readonly ConcurrentQueue<CaptionObservation> _pending = new ConcurrentQueue<CaptionObservation>();
        private readonly ObservableCollection<SentenceRow> _rows = new ObservableCollection<SentenceRow>();
        private readonly CaptionReader _reader;
        private readonly DispatcherTimer _timer;
        private readonly ListBox _list;
        private readonly Button _copyAll;
        private readonly Button _clear;
        private readonly Button _launch;
        private readonly Button _reconnect;
        private readonly Button _latest;
        private readonly FrameworkElement _empty;
        private readonly TextBlock _count;
        private readonly TextBlock _status;
        private readonly TextBlock _toast;
        private readonly TextBlock _emptyTitle;
        private readonly TextBlock _emptyDescription;
        private readonly FrameworkElement _shortcut;
        private ScrollViewer _scroll;
        private CaptionObservation _observation;
        private long _session;
        private bool _follow = true;
        private bool _closed;
        private bool _copying;
        private int _toastVersion;
        private DateTime _feedbackUntil;

        public MainWindowController()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CaptionCopy.MainWindow.xaml"))
                Window = (Window)XamlReader.Load(stream);
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CaptionCopy.App.ico"))
            {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Window.Icon = decoder.Frames[0];
                foreach (BitmapFrame frame in decoder.Frames) if (frame.PixelWidth == 64) { Window.Icon = frame; break; }
            }
            Find<Image>("BrandIcon").Source = Window.Icon;
            _list = Find<ListBox>("SentenceList");
            _copyAll = Find<Button>("CopyAllButton");
            _clear = Find<Button>("ClearButton");
            _launch = Find<Button>("LaunchButton");
            _reconnect = Find<Button>("ReconnectButton");
            _latest = Find<Button>("LatestButton");
            _empty = Find<FrameworkElement>("EmptyPanel");
            _count = Find<TextBlock>("CountText");
            _status = Find<TextBlock>("StatusText");
            _toast = Find<TextBlock>("ToastText");
            _emptyTitle = Find<TextBlock>("EmptyTitle");
            _emptyDescription = Find<TextBlock>("EmptyDescription");
            _shortcut = Find<FrameworkElement>("ShortcutHint");
            _list.ItemsSource = _rows;
            _copyAll.IsEnabled = _clear.IsEnabled = false;
            _copyAll.Click += async delegate { await CopyAll(); };
            _clear.Click += delegate
            {
                Drain();
                _engine.Clear();
                Reconcile();
                ShowToast("已清空", false);
            };
            Find<ToggleButton>("PinToggle").Checked += delegate { Window.Topmost = true; };
            Find<ToggleButton>("PinToggle").Unchecked += delegate { Window.Topmost = false; };
            _launch.Click += LaunchCaptions;
            _reconnect.Click += LaunchCaptions;
            _latest.Click += delegate { _follow = true; ScrollToLatest(); };
            _list.AddHandler(Button.ClickEvent, new RoutedEventHandler(SentenceClicked));
            _list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ScrollChanged));
            Window.PreviewKeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                { e.Handled = true; await CopyAll(); }
            };
            _reader = new CaptionReader(o => _pending.Enqueue(o));
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += delegate
            {
                bool changed = Drain();
                changed |= _engine.Tick(DateTime.UtcNow);
                if (changed) Reconcile();
                if (_feedbackUntil != DateTime.MinValue && DateTime.UtcNow >= _feedbackUntil)
                {
                    foreach (SentenceRow row in _rows) row.IsCopied = false;
                    _feedbackUntil = DateTime.MinValue;
                }
            };
            Window.Loaded += delegate { Keyboard.Focus(Window); _reader.Start(); _timer.Start(); };
            Window.Closed += delegate { _closed = true; _timer.Stop(); _reader.Dispose(); };
        }

        private T Find<T>(string name) where T : class { return Window.FindName(name) as T; }

        private bool Drain()
        {
            bool changed = false;
            CaptionObservation next;
            while (_pending.TryDequeue(out next))
            {
                if (next.Session != 0 && next.Session != _session)
                {
                    if (_session != 0) _engine.BeginSegment();
                    _session = next.Session;
                    changed = true;
                }
                if (next.Connection == CaptionConnection.Reading) changed |= _engine.Observe(next.Text, next.At);
                _observation = next;
            }
            if (_observation != null) UpdateStatus();
            return changed;
        }

        private void UpdateStatus()
        {
            CaptionConnection connection = _observation.Connection;
            _status.Text = _observation.Detail;
            _status.ToolTip = _observation.Detail;
            Find<System.Windows.Shapes.Ellipse>("StatusDot").Fill = new SolidColorBrush(
                connection == CaptionConnection.Reading ? Color.FromRgb(44, 155, 105) :
                connection == CaptionConnection.Unsupported || connection == CaptionConnection.Reconnecting ? Color.FromRgb(217, 152, 50) : Color.FromRgb(144, 161, 187));
            bool missing = connection == CaptionConnection.Missing;
            _launch.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
            _reconnect.Visibility = missing && _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _shortcut.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
            _emptyTitle.Text = missing ? "等待实时字幕" : connection == CaptionConnection.Unsupported ? "暂时无法读取字幕" : "等待新的字幕";
            _emptyDescription.Text = missing ? "打开 Windows 实时字幕并播放声音，\n识别到的句子会出现在这里。" :
                connection == CaptionConnection.Unsupported ? _observation.Detail :
                connection == CaptionConnection.Reconnecting ? "正在重新连接系统字幕，已有记录会保留。" : "系统实时字幕已连接。\n播放声音后，新句子会自动出现在这里。";
        }

        private void Reconcile()
        {
            var sentences = _engine.GetSentences();
            for (int i = 0; i < sentences.Count; i++)
            {
                if (i == _rows.Count) _rows.Add(new SentenceRow());
                _rows[i].Text = sentences[i].Text;
                _rows[i].IsDraft = sentences[i].IsDraft;
            }
            while (_rows.Count > sentences.Count) _rows.RemoveAt(_rows.Count - 1);
            _count.Text = _rows.Count + " 段 · 点击复制";
            _copyAll.IsEnabled = _clear.IsEnabled = _rows.Count > 0;
            _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_rows.Count == 0) _follow = true;
            if (_observation != null) UpdateStatus();
            if (_follow) Window.Dispatcher.BeginInvoke(new Action(ScrollToLatest), DispatcherPriority.Loaded);
        }

        private void ScrollToLatest()
        {
            if (_closed || !_follow) return;
            if (_scroll == null) _scroll = FindVisual<ScrollViewer>(_list);
            if (_scroll != null) _scroll.ScrollToEnd();
            _latest.Visibility = Visibility.Collapsed;
        }

        private void ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            ScrollViewer viewer = e.OriginalSource as ScrollViewer;
            if (viewer == null) return;
            _scroll = viewer;
            // Extent changes come from new/wrapping text, not intentional user navigation.
            if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
                _follow = viewer.ScrollableHeight - viewer.VerticalOffset < 8;
            if (_follow && e.ExtentHeightChange != 0) Window.Dispatcher.BeginInvoke(new Action(ScrollToLatest), DispatcherPriority.Loaded);
            _latest.Visibility = !_follow && _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void SentenceClicked(object sender, RoutedEventArgs e)
        {
            Button button = e.OriginalSource as Button;
            SentenceRow row = button == null ? null : button.DataContext as SentenceRow;
            if (row == null || _copying) return;
            e.Handled = true;
            string snapshot = row.Text;
            _copying = true;
            bool copied = await ClipboardService.CopyAsync(snapshot);
            _copying = false;
            if (_closed) return;
            if (copied)
            {
                foreach (SentenceRow item in _rows) item.IsCopied = false;
                if (row.Text == snapshot) row.IsCopied = true;
                _feedbackUntil = DateTime.UtcNow.AddMilliseconds(1400);
            }
            ShowToast(copied ? "已复制此段" : "复制失败，请稍后重试", !copied);
        }

        private async Task CopyAll()
        {
            if (_copying) return;
            string snapshot = _engine.GetCopyText();
            if (snapshot.Length == 0) return;
            int count = _rows.Count;
            _copying = true;
            bool copied = await ClipboardService.CopyAsync(snapshot);
            _copying = false;
            if (!_closed) ShowToast(copied ? "已复制 " + count + " 段" : "复制失败，请稍后重试", !copied);
        }

        private void LaunchCaptions(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "LiveCaptions.exe")) { UseShellExecute = true });
                ShowToast("正在打开实时字幕", false);
            }
            catch { ShowToast("无法打开，请按 Win + Ctrl + L", true); }
        }

        private async void ShowToast(string text, bool error)
        {
            int version = ++_toastVersion;
            _toast.Text = text;
            _toast.Foreground = new SolidColorBrush(error ? Color.FromRgb(194, 80, 61) : Color.FromRgb(40, 132, 87));
            await Task.Delay(error ? 4000 : 1800);
            if (_closed || version != _toastVersion) return;
            _toast.Text = "退出后不保留记录";
            _toast.Foreground = new SolidColorBrush(Color.FromRgb(154, 165, 181));
        }

        private static T FindVisual<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                T match = child as T;
                if (match != null) return match;
                match = FindVisual<T>(child);
                if (match != null) return match;
            }
            return null;
        }
    }
}
