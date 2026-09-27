using System;

namespace WinRealtimeWhisper
{
    internal sealed class PartialTextEventArgs : EventArgs
    {
        public PartialTextEventArgs(string text, long offsetTicks)
        {
            Text = text;
            OffsetTicks = offsetTicks;
        }

        public string Text { get; private set; }
        public long OffsetTicks { get; private set; }
    }

    internal sealed class FinalTextEventArgs : EventArgs
    {
        public FinalTextEventArgs(string text, long offsetTicks)
        {
            Text = text;
            OffsetTicks = offsetTicks;
        }

        public string Text { get; private set; }
        public long OffsetTicks { get; private set; }
    }

    internal sealed class StatusEventArgs : EventArgs
    {
        public StatusEventArgs(string text)
        {
            Text = text;
        }

        public string Text { get; private set; }
    }

    internal sealed class LevelEventArgs : EventArgs
    {
        public LevelEventArgs(float level)
        {
            Level = level;
        }

        public float Level { get; private set; }
    }

    internal sealed class SpeechActivityEventArgs : EventArgs
    {
        public SpeechActivityEventArgs(bool speaking)
        {
            Speaking = speaking;
        }

        public bool Speaking { get; private set; }
    }

    internal sealed class ErrorEventArgs : EventArgs
    {
        public ErrorEventArgs(string message, Exception exception)
        {
            Message = message;
            Exception = exception;
        }

        public string Message { get; private set; }
        public Exception Exception { get; private set; }
    }
}
