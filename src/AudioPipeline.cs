using System;
using System.Collections.Generic;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// 32bit float（任意のサンプルレート / チャンネル数）を、
    /// Whisper が要求する 16kHz / モノラル / float へ変換する。
    ///
    /// Whisper はファイルではなく float サンプル列を受け取るため、
    /// ここではバイト列ではなく float[] を出力する。
    /// </summary>
    internal sealed class SampleConverter
    {
        internal const int TargetSampleRate = 16000;
        internal const int TargetChannels = 1;

        private double _phase;

        /// <summary>
        /// float サンプル列（インターリーブ）を 16kHz モノラルへ変換する。
        /// チャンネル数が 2 以上のときは単純に平均してモノラル化する。
        /// </summary>
        public float[] Convert(float[] source, int count, int sourceRate, int sourceChannels)
        {
            if (source == null || count <= 0 || sourceRate <= 0 || sourceChannels <= 0)
            {
                return new float[0];
            }

            int frames = count / sourceChannels;
            if (frames <= 1)
            {
                return new float[0];
            }

            double step = (double)sourceRate / TargetSampleRate;
            double pos = _phase;

            int estimate = (int)(frames / step) + 4;
            var output = new List<float>(estimate > 0 ? estimate : 16);

            while (true)
            {
                int left = (int)Math.Floor(pos);
                if (left + 1 >= frames)
                {
                    break;
                }

                double frac = pos - left;

                float a = Mono(source, left, sourceChannels);
                float b = Mono(source, left + 1, sourceChannels);

                output.Add(a + (float)((b - a) * frac));
                pos += step;
            }

            _phase = pos - frames;
            if (_phase < 0)
            {
                _phase = 0;
            }

            return output.ToArray();
        }

        private static float Mono(float[] source, int frame, int channels)
        {
            if (channels == 1)
            {
                return source[frame];
            }

            float sum = 0;
            int baseIndex = frame * channels;
            for (int c = 0; c < channels; c++)
            {
                sum += source[baseIndex + c];
            }

            return sum / channels;
        }

        public void Reset()
        {
            _phase = 0;
        }
    }
}
