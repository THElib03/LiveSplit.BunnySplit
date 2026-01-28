using System.Windows.Forms;
using System.Xml;

using LiveSplit.Model;
using System.IO.Pipes;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using LiveSplit.UI.Components;
using LiveSplit.UI;

namespace LiveSplit.BunnySplit
{
    class Component : IComponent
    {
        public string ComponentName => "BunnySplit";
        protected InfoTextComponent InternalComponent { get; set; }

        private const string PIPE_NAME = "BunnymodXT-BunnySplit";
        private enum MessageType : byte
        {
            Time = 0x00,
            Event = 0x04
        }
        private enum EventType : byte
        {
            GameEnd = 0x00,
            MapChange = 0x01,
            TimerReset = 0x02,
            TimerStart = 0x03,
            BS_ALeapOfFaith = 0x04
        }

        private interface IEvent
        {
            TimeSpan Time { get; }
        }
        private struct GameEndEvent : IEvent
        {
            public TimeSpan Time { get; set; }
        }
        private struct MapChangeEvent : IEvent
        {
            public TimeSpan Time { get; set; }
            public string Map { get; set; }
        }
        private struct TimerResetEvent : IEvent
        {
            public TimeSpan Time { get; set; }
        }
        private struct TimerStartEvent : IEvent
        {
            public TimeSpan Time { get; set; }
        }
        private struct BS_ALeapOfFaithEvent : IEvent
        {
            public TimeSpan Time { get; set; }
        }

        public float HorizontalWidth => InternalComponent.HorizontalWidth;
        public float MinimumWidth => InternalComponent.MinimumWidth;
        public float VerticalHeight => InternalComponent.VerticalHeight;
        public float MinimumHeight => InternalComponent.MinimumHeight;

        public float PaddingTop => InternalComponent.PaddingTop;
        public float PaddingLeft => InternalComponent.PaddingLeft;
        public float PaddingBottom => InternalComponent.PaddingBottom;
        public float PaddingRight => InternalComponent.PaddingRight;

        public IDictionary<string, Action> ContextMenuControls => null;
        private ComponentSettings settings = new ComponentSettings();
        private NamedPipeClientStream pipe = new NamedPipeClientStream(
            ".",
            PIPE_NAME,
            PipeAccessRights.ReadData | PipeAccessRights.WriteAttributes,
            PipeOptions.None,
            System.Security.Principal.TokenImpersonationLevel.None,
            System.IO.HandleInheritability.None);
        private CancellationTokenSource cts = new CancellationTokenSource();
        private Thread pipeThread;
        private TimerModel model;
        private TimeSpan currentTime = new TimeSpan();
        private TimeSpan? SumofBestValue { get; set; }
        private List<IEvent> events = new List<IEvent>();
        private object eventsLock = new object();
        private HashSet<string> visitedMaps = new HashSet<string>();
        private bool splitOnBSALeapOfFaith = false;

        private DateTime lastTime = DateTime.Now;

        public Component(LiveSplitState state)
        {
            state.OnStart += OnStart;
            model = new TimerModel() { CurrentState = state };
            model.InitializeGameTime();
            pipeThread = new Thread(PipeThreadFunc);
            pipeThread.Start();

            InternalComponent = new InfoTextComponent("Chapter Sum of Best", "0:00");
        }

        public void Dispose()
        {
            cts.Cancel();
            pipeThread.Join();
        }

        public XmlNode GetSettings(XmlDocument document)
        {
            return settings.GetSettings(document);
        }

        public Control GetSettingsControl(LayoutMode mode)
        {
            return settings;
        }

        public void SetSettings(XmlNode settings)
        {
            this.settings.SetSettings(settings);
        }

        public void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
        {
            state.IsGameTimePaused = true;
            
            bool start = false;
            lock (eventsLock)
            {
                foreach (var ev in events)
                {
                    if (ev is GameEndEvent)
                    {
                        if (state.CurrentPhase == TimerPhase.Running && settings.ShouldSplitOnGameEnd())
                        {
                            state.SetGameTime(ev.Time);
                            model.Split();
                        }
                    }
                    else if (ev is MapChangeEvent)
                    {
                        var e = (MapChangeEvent)ev;
                        if (visitedMaps.Add(e.Map) && settings.ShouldSplitOn(e.Map))
                        {
                            state.SetGameTime(e.Time);
                            model.Split();
                        }
                    }
                    else if (ev is TimerResetEvent)
                    {
                        if (settings.IsAutoResetEnabled())
                        {
                            state.SetGameTime(ev.Time);
                            model.Reset();
                        }
                    }
                    else if (ev is TimerStartEvent)
                    {
                        if (settings.IsAutoStartEnabled())
                        {
                            state.SetGameTime(ev.Time);
                            start = true;
                        }
                    }
                    else if (ev is BS_ALeapOfFaithEvent)
                    {
                        if (!splitOnBSALeapOfFaith)
                        {
                            state.SetGameTime(ev.Time);
                            model.Split();
                            splitOnBSALeapOfFaith = true;
                        }
                    }
                }
                events.Clear();
            }
            if (start)
                model.Start();

            TimeSpan curTime;
            lock (currentTimeLock)
            {
                curTime = currentTime;
            }
            state.SetGameTime(curTime);
        }

        private void OnStart(object sender, EventArgs e)
        {
            lock (eventsLock)
            {
                events.Clear();
            }

            visitedMaps.Clear();
            splitOnBSALeapOfFaith = false;
        }

        private TimeSpan ParseTime(byte[] buf, int offset)
        {
            var hours = BitConverter.ToUInt32(buf, offset);
            var minutes = buf[offset + 4];
            var seconds = buf[offset + 5];
            var milliseconds = BitConverter.ToUInt16(buf, offset + 6);
            return new TimeSpan((int)(hours / 24), (int)(hours % 24u), minutes, seconds, milliseconds);
        }

        private void ParseMessage(byte[] buf)
        {
            switch (buf[1])
            {
                case (byte)MessageType.Time:
                    {
                        var time = ParseTime(buf, 2);
                        //Debug.WriteLine("Received time: {0}:{1}:{2}.{3}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds);

                        lock (currentTimeLock)
                        {
                            currentTime = time;
                        }
                    }
                    break;

                case (byte)MessageType.Event:
                    {
                        var eventType = buf[2];
                        var time = ParseTime(buf, 3);

                        switch (eventType)
                        {
                            case (byte)EventType.GameEnd:
                                {
                                    var ev = new GameEndEvent() { Time = time };
                                    lock (eventsLock)
                                    {
                                        events.Add(ev);
                                    }
                                    Debug.WriteLine("Received a game end event: {0}:{1}:{2}.{3}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds);
                                }
                                break;

                            case (byte)EventType.MapChange:
                                {
                                    var len = BitConverter.ToInt32(buf, 11);
                                    string map = System.Text.Encoding.ASCII.GetString(buf, 15, len);

                                    var ev = new MapChangeEvent { Time = time, Map = map };
                                    lock (eventsLock)
                                    {
                                        events.Add(ev);
                                    }
                                    Debug.WriteLine("Received a map change event: {0}:{1}:{2}.{3}; {4}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds, map);
                                }
                                break;

                            case (byte)EventType.TimerReset:
                                {
                                    var ev = new TimerResetEvent() { Time = time };
                                    lock (eventsLock)
                                    {
                                        events.Add(ev);
                                    }
                                    Debug.WriteLine("Received a timer reset event: {0}:{1}:{2}.{3}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds);
                                }
                                break;

                            case (byte)EventType.TimerStart:
                                {
                                    var ev = new TimerStartEvent() { Time = time };
                                    lock (eventsLock)
                                    {
                                        events.Add(ev);
                                    }
                                    Debug.WriteLine("Received a timer start event: {0}:{1}:{2}.{3}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds);
                                }
                                break;

                            case (byte)EventType.BS_ALeapOfFaith:
                                {
                                    var ev = new BS_ALeapOfFaithEvent() { Time = time };
                                    lock (eventsLock)
                                    {
                                        events.Add(ev);
                                    }
                                    Debug.WriteLine("Received a BS A Leap of Faith event: {0}:{1}:{2}.{3}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds);
                                }
                                break;

                            default:
                                Debug.WriteLine("Received an unknown event type: " + buf[2]);
                                break;
                        }
                    }
                    break;

                default:
                    Debug.WriteLine("Received an unknown message type: " + buf[1]);
                    break;
            }
        }

        private void PipeThreadFunc()
        {
            while (!cts.IsCancellationRequested)
            {
                if (!pipe.IsConnected)
                {
                    Debug.WriteLine("Connecting to the pipe.");
                    try
                    {
                        pipe.Connect(0);
                        Debug.WriteLine("Connected to the pipe. Readmode: " + pipe.ReadMode);
                        pipe.ReadMode = PipeTransmissionMode.Message;
                        Debug.WriteLine("Set the message read mode.");
                    }
                    catch (Exception e) when (e is TimeoutException || e is IOException)
                    {
                        Debug.WriteLine("Idling for 1 second.");
                        cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                        continue;
                    }
                }

                // Connected to the pipe.
                try
                {
                    var buf = new byte[256];
                    var task = pipe.ReadAsync(buf, 0, 256, cts.Token);
                    task.Wait();

                    if (task.Result == 0)
                    {
                        // The pipe was closed.
                        Debug.WriteLine("Pipe end of stream reached.");
                        continue;
                    }

                    if (buf[0] != task.Result)
                    {
                        Debug.WriteLine("Received an incorrect number of bytes (" + task.Result + ", expected " + buf[0] + ").");
                        continue;
                    }

                    ParseMessage(buf);
                }
                catch (AggregateException e)
                {
                    foreach (var ex in e.InnerExceptions)
                    {
                        if (ex is TaskCanceledException)
                        {
                            return;
                        }
                    }

                    Debug.WriteLine("Error reading from the pipe:");
                    foreach (var ex in e.InnerExceptions)
                    {
                        Debug.WriteLine("- " + ex.GetType().Name + ": " + ex.Message);
                    }
                    Debug.WriteLine("Idling for 1 second.");
                    cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                    continue;
                }
                catch (Exception e)
                {
                    Debug.WriteLine("Error reading from the pipe: " + e.Message);
                    Debug.WriteLine("Idling for 1 second.");
                    cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                    continue;
                }
            }
        }

        // Component graphics, taken from https://github.com/TheSoundDefense/LiveSplit.ResetChance/blob/master/UI/Components/ResetChanceComponent.cs
        private void DrawBackground(Graphics g, LiveSplitState state, float width, float height)
        {
            if (settings.BackgroundColor.A > 0
                || settings.BackgroundGradient != GradientType.Plain
                && settings.BackgroundColor2.A > 0)
            {
                var gradientBrush = new LinearGradientBrush(
                            new PointF(0, 0),
                            settings.BackgroundGradient == GradientType.Horizontal
                            ? new PointF(width, 0)
                            : new PointF(0, height),
                            settings.BackgroundColor,
                            settings.BackgroundGradient == GradientType.Plain
                            ? settings.BackgroundColor
                            : settings.BackgroundColor2);
                g.FillRectangle(gradientBrush, 0, 0, width, height);
            }
        }

        public void DrawHorizontal(Graphics g, LiveSplitState state, float height, Region clipRegion)
        {
            DrawBackground(g, state, HorizontalWidth, height);

            InternalComponent.NameLabel.HasShadow
                = InternalComponent.ValueLabel.HasShadow
                = state.LayoutSettings.DropShadows;

            InternalComponent.NameLabel.ForeColor = settings.OverrideTextColor ? settings.TextColor : state.LayoutSettings.TextColor;
            InternalComponent.ValueLabel.ForeColor = settings.OverrideChanceColor ? settings.ChanceColor : state.LayoutSettings.TextColor;

            InternalComponent.DrawHorizontal(g, state, height, clipRegion);
        }

        public void DrawVertical(Graphics g, LiveSplitState state, float width, Region clipRegion)
        {
            DrawBackground(g, state, width, VerticalHeight);

            InternalComponent.DisplayTwoRows = settings.Display2Rows;

            InternalComponent.NameLabel.HasShadow
                = InternalComponent.ValueLabel.HasShadow
                = state.LayoutSettings.DropShadows;

            InternalComponent.NameLabel.ForeColor = settings.OverrideTextColor ? settings.TextColor : state.LayoutSettings.TextColor;
            InternalComponent.ValueLabel.ForeColor = settings.OverrideChanceColor ? settings.ChanceColor : state.LayoutSettings.TextColor;

            InternalComponent.DrawVertical(g, state, width, clipRegion);
        }
    }
}
