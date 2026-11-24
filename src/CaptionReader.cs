using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Automation;

namespace CaptionCopy
{
    public enum CaptionConnection { Missing, WaitingForSpeech, Reading, Reconnecting, Unsupported }

    public sealed class CaptionObservation
    {
        public CaptionConnection Connection { get; private set; }
        public string Text { get; private set; }
        public long Session { get; private set; }
        public DateTime At { get; private set; }
        public string Detail { get; private set; }
        public CaptionObservation(CaptionConnection state, string text, long session, string detail)
        { Connection = state; Text = text; Session = session; Detail = detail; At = DateTime.UtcNow; }
    }

    public sealed class CaptionReader : IDisposable
    {
        private readonly Action<CaptionObservation> _publish;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly Thread _thread;
        private AutomationElement _window;
        private AutomationElement _caption;
        private long _session;
        private DateTime _attachedAt;
        private bool _everAttached;
        private bool _started;

        public CaptionReader(Action<CaptionObservation> publish)
        {
            _publish = publish;
            _thread = new Thread(Run) { IsBackground = true, Name = "Live Captions UI Automation" };
            _thread.SetApartmentState(ApartmentState.MTA);
        }

        public void Start() { if (!_started) { _started = true; _thread.Start(); } }

        private void Run()
        {
            while (!_stop.WaitOne(0))
            {
                try { ReadOnce(); }
                catch (ElementNotAvailableException) { _caption = null; LoseWindowIfStale(); Publish(CaptionConnection.Reconnecting, "", "字幕控件更新中，正在重新连接"); }
                catch (Exception ex)
                {
                    _caption = null;
                    LoseWindowIfStale();
                    Publish(CaptionConnection.Reconnecting, "", "暂时无法读取字幕：" + ex.GetType().Name);
                }
                _stop.WaitOne(_window == null ? 1000 : 300);
            }
        }

        private void ReadOnce()
        {
            if (_window == null)
            {
                _window = FindWindow();
                if (_window == null) { Publish(CaptionConnection.Missing, "", _everAttached ? "连接中断，等待实时字幕重新打开" : "等待实时字幕"); return; }
                _session++;
                _everAttached = true;
                _attachedAt = DateTime.UtcNow;
            }
            // Access root on every sample to detect windows being closed even if a stale child survives.
            int processId = _window.Current.ProcessId;
            if (_caption == null) _caption = FindById(_window, "CaptionsTextBlock");
            if (_caption != null)
            {
                string text = _caption.Current.Name ?? "";
                Publish(text.Length == 0 ? CaptionConnection.WaitingForSpeech : CaptionConnection.Reading, text,
                    text.Length == 0 ? "已连接，等待声音" : "正在读取");
                return;
            }
            bool ready = FindById(_window, "ReadyToCaptionTextBlock") != null;
            if (ready || DateTime.UtcNow - _attachedAt < TimeSpan.FromSeconds(8))
                Publish(CaptionConnection.WaitingForSpeech, "", "已连接，等待声音或完成系统字幕设置");
            else Publish(CaptionConnection.Unsupported, "", "未找到可读取的字幕控件，请确认系统字幕已完成设置；当前系统版本可能不兼容");
        }

        private void LoseWindowIfStale()
        {
            if (_window == null) return;
            try { int process = _window.Current.ProcessId; }
            catch { _window = null; }
        }

        private static AutomationElement FindWindow()
        {
            foreach (Process process in Process.GetProcessesByName("LiveCaptions"))
            {
                using (process)
                {
                    if (process.HasExited) continue;
                    var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id);
                    AutomationElement window = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
                    if (window != null) return window;
                }
            }
            return null;
        }

        private static AutomationElement FindById(AutomationElement root, string id)
        {
            return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        }

        private void Publish(CaptionConnection state, string text, string detail)
        {
            if (!_stop.WaitOne(0)) _publish(new CaptionObservation(state, text, _session, detail));
        }

        public void Dispose()
        {
            _stop.Set();
            // UIA providers can stall. Never block window close waiting for a foreign process.
            // The background thread exits after the pending call; the OS releases it at process exit.
        }
    }
}
