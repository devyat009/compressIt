using System.IO;
using System.Media;

namespace CompressIt.Services;

public static class CompletionSoundService
{
    private static readonly byte[] CachedChimeWav = BuildSuccessChimeWav();

    public static void PlaySuccessChime()
    {
        try
        {
            // SoundPlayer.Play() runs asynchronously on its own internal thread;
            // keep the stream alive only for playback or clone per call.
            Task.Run(() =>
            {
                try
                {
                    using var ms = new MemoryStream(CachedChimeWav, writable: false);
                    using var player = new SoundPlayer(ms);
                    player.PlaySync();
                }
                catch
                {
                    SystemSounds.Asterisk.Play();
                }
            });
        }
        catch
        {
            try { SystemSounds.Asterisk.Play(); } catch { }
        }
    }

    private static byte[] BuildSuccessChimeWav()
    {
        const int sampleRate = 44100;
        const double durationSec = 0.48;
        int sampleCount = (int)(sampleRate * durationSec);
        short[] pcm = new short[sampleCount];

        // Gentle two-note ascending chime: C5 (523.25 Hz) -> G5 (783.99 Hz)
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double sample = 0;

            // Note 1: 0.00s .. 0.30s
            if (t < 0.30)
            {
                double env1 = Math.Sin(Math.Min(1.0, t / 0.015) * Math.PI / 2.0) * Math.Exp(-t * 10.0);
                sample += 0.35 * env1 * Math.Sin(2.0 * Math.PI * 523.25 * t);
            }

            // Note 2: 0.11s .. 0.48s
            if (t >= 0.11)
            {
                double t2 = t - 0.11;
                double env2 = Math.Sin(Math.Min(1.0, t2 / 0.015) * Math.PI / 2.0) * Math.Exp(-t2 * 8.5);
                sample += 0.40 * env2 * Math.Sin(2.0 * Math.PI * 783.99 * t2);
            }

            pcm[i] = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int dataBytes = pcm.Length * 2;
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16); // PCM chunk size
        bw.Write((short)1); // PCM format
        bw.Write((short)1); // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2); // Byte rate
        bw.Write((short)2); // Block align
        bw.Write((short)16); // Bits per sample
        bw.Write("data"u8);
        bw.Write(dataBytes);

        foreach (short s in pcm)
        {
            bw.Write(s);
        }

        bw.Flush();
        return ms.ToArray();
    }
}
