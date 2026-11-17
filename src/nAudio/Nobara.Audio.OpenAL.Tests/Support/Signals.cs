// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Audio.OpenAL.Tests.Support;

/// <summary>Sounds the tests play, and measures of what the loopback device renders.</summary>
internal static class Signals
{
    public const int Rate = 48000;

    /// <summary>A mono sine of 16-bit samples.</summary>
    public static short[] Sine(double seconds, double frequency = 440, double amplitude = 0.5)
    {
        var samples = new short[(int)(seconds * Rate)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(Math.Sin(2 * Math.PI * frequency * i / Rate) * amplitude * short.MaxValue);
        }

        return samples;
    }

    /// <summary>Renders frames of the loopback device.</summary>
    public static float[] Render(ALDevice device, double seconds)
    {
        var frames = new float[(int)(seconds * Rate) * 2];
        device.Render(frames);
        return frames;
    }

    /// <summary>The mean square of a channel, 0 for the left one and 1 for the right one.</summary>
    public static double Energy(float[] frames, int channel)
    {
        int count = frames.Length / 2;
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            double sample = frames[(i * 2) + channel];
            sum += sample * sample;
        }

        return count > 0 ? sum / count : 0;
    }
}
