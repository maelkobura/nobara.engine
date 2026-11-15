# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
#
# Writes the sound files of the tests from generated tones, with ffmpeg on
# the PATH: run it from this folder. The files are short and made for the
# repository; they carry no recording.
import math, random, struct, subprocess

def tone(rate, channels, seconds, base):
    random.seed(base)
    frames = int(rate * seconds)
    phase = [0.0] * channels
    data = []
    for i in range(frames):
        t = i / frames
        envelope = min(1.0, i / (rate * 0.01)) * (1 - t) ** 0.5
        for c in range(channels):
            f = base * (1 + 0.5 * t) + c * 40
            phase[c] += 2 * math.pi * f / rate
            data.append(envelope * (0.4 * math.sin(phase[c]) + 0.03 * (random.random() - 0.5)))
    return data

def make(name, rate, channels, seconds, base, *encoder):
    data = tone(rate, channels, seconds, base)
    open("tone.f32", "wb").write(struct.pack("<%df" % len(data), *data))
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "f32le", "-ar", str(rate), "-ac", str(channels),
                    "-i", "tone.f32", *encoder, f"Sounds/{name}"], check=True)

make("click.ogg", 44100, 1, 0.25, 880, "-c:a", "libvorbis", "-q:a", "4")
make("music.ogg", 48000, 2, 1.5, 220, "-c:a", "libvorbis", "-q:a", "4")
make("step.wav", 22050, 1, 0.3, 330, "-c:a", "pcm_s16le")
make("wind.wav", 44100, 2, 0.4, 150, "-c:a", "pcm_s24le")
make("bell.flac", 44100, 2, 0.5, 660, "-c:a", "flac", "-sample_fmt", "s16")

# The samples of click.ogg as a reference decoder (ffmpeg) gives them, 16-bit.
subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", "Sounds/click.ogg", "-f", "s16le", "-c:a", "pcm_s16le",
                "Sounds/click.s16"], check=True)
