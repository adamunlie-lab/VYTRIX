using System.IO;
using System.Windows.Media;
using Vytrix.Core;

namespace Vytrix.Services;

/// <summary>
/// A public-domain carol, written as melody notes ("G4:1.5" = G4 for 1.5 beats, "R:1" = rest)
/// plus one chord per bar for the accompaniment. Arrangements by VYTRIX.
/// </summary>
public sealed record Carol(string Id, string Title, string Credit, double Bpm, int BeatsPerBar, double Pickup, string Melody, string Chords, bool SleighBells = false);

/// <summary>
/// Synthesizes music-box arrangements of public-domain carols. Everything is generated
/// on the user's PC: no downloads, no recordings, no licensing questions. Each carol is
/// rendered once (in the background) to a small mono WAV in the VYTRIX data folder.
/// </summary>
public static class CarolSynth
{
    public const int SampleRate = 32000;
    private const string RenderVersion = "v1";   // bump to re-render after changing the synth

    public static readonly Carol[] Carols =
    [
        new("jingle-bells", "Jingle Bells", "James Lord Pierpont, 1857 · public domain", 150, 4, 0,
            "B4:1 B4:1 B4:2 | B4:1 B4:1 B4:2 | B4:1 D5:1 G4:1.5 A4:.5 | B4:4 | C5:1 C5:1 C5:1.5 C5:.5 | C5:1 B4:1 B4:1 B4:.5 B4:.5 | B4:1 A4:1 A4:1 B4:1 | A4:2 D5:2 |" +
            "B4:1 B4:1 B4:2 | B4:1 B4:1 B4:2 | B4:1 D5:1 G4:1.5 A4:.5 | B4:4 | C5:1 C5:1 C5:1.5 C5:.5 | C5:1 B4:1 B4:1 B4:.5 B4:.5 | D5:1 D5:1 C5:1 A4:1 | G4:4",
            "G G G G C G A7 D7 G G G G C G D7 G", SleighBells: true),
        new("we-wish", "We Wish You a Merry Christmas", "Traditional English carol · public domain", 160, 3, 1,
            "D4:1 | G4:1 G4:.5 A4:.5 G4:.5 F#4:.5 | E4:1 E4:1 E4:1 | A4:1 A4:.5 B4:.5 A4:.5 G4:.5 | F#4:1 D4:1 D4:1 | B4:1 B4:.5 C5:.5 B4:.5 A4:.5 | G4:1 E4:1 D4:.5 D4:.5 | E4:1 A4:1 F#4:1 | G4:2 D4:1 |" +
            "G4:1 G4:1 G4:1 | F#4:2 F#4:1 | G4:1 F#4:1 E4:1 | D4:2 A4:1 | B4:1 A4:1 G4:1 | D5:1 D4:1 D4:.5 D4:.5 | E4:1 A4:1 F#4:1 | G4:3",
            "G C A7 D B7 Em C G G D C D G Em C G"),
        new("deck-the-halls", "Deck the Halls", "Traditional Welsh melody · public domain", 132, 4, 0,
            "D5:1.5 C5:.5 B4:1 A4:1 | G4:1 A4:1 B4:1 G4:1 | A4:.5 B4:.5 C5:.5 A4:.5 B4:1.5 A4:.5 | G4:1 F#4:1 G4:2 |" +
            "D5:1.5 C5:.5 B4:1 A4:1 | G4:1 A4:1 B4:1 G4:1 | A4:.5 B4:.5 C5:.5 A4:.5 B4:1.5 A4:.5 | G4:1 F#4:1 G4:2 |" +
            "A4:1.5 B4:.5 C5:1 A4:1 | B4:1.5 C5:.5 D5:1 A4:1 | B4:.5 C#5:.5 D5:1 E5:.5 F#5:.5 G5:1 | F#5:1 E5:1 D5:2 |" +
            "D5:1.5 C5:.5 B4:1 A4:1 | G4:1 A4:1 B4:1 G4:1 | E5:.5 E5:.5 E5:.5 E5:.5 D5:1.5 C5:.5 | B4:1 A4:1 G4:2",
            "G G D G G G D G D G D D G G C G"),
        new("silent-night", "Silent Night", "Franz Xaver Gruber, 1818 · public domain", 150, 6, 0,
            "G4:3 A4:1 G4:2 | E4:6 | G4:3 A4:1 G4:2 | E4:6 | D5:4 D5:2 | B4:6 | C5:4 C5:2 | G4:6 |" +
            "A4:4 A4:2 | C5:3 B4:1 A4:2 | G4:3 A4:1 G4:2 | E4:6 | A4:4 A4:2 | C5:3 B4:1 A4:2 | G4:3 A4:1 G4:2 | E4:6 |" +
            "D5:4 D5:2 | F5:3 D5:1 B4:2 | C5:6 | E5:6 | C5:3 G4:1 E4:2 | G4:3 F4:1 D4:2 | C4:12",
            "C C C C G G C C F F C C F F C C G G C C C G7 C C"),
        new("joy-to-the-world", "Joy to the World", "Lowell Mason, 1839 (after Handel) · public domain", 120, 4, 0,
            "C5:2 B4:1.5 A4:.5 | G4:3 F4:1 | E4:2 D4:2 | C4:3 G4:1 | A4:3 A4:1 | B4:3 B4:1 | C5:4 |" +
            "C5:1 C5:1 B4:1 A4:1 | G4:1 G4:1.5 F4:.5 E4:1 | C5:1 C5:1 B4:1 A4:1 | G4:1 G4:1.5 F4:.5 E4:1 | E4:1 E4:1 E4:1 E4:.5 F4:.5 | G4:3 F4:.5 E4:.5 |" +
            "D4:1 D4:1 D4:1 D4:.5 E4:.5 | F4:3 E4:.5 D4:.5 | C4:1 C5:2 A4:1 | G4:1.5 F4:.5 E4:1 F4:1 | E4:2 D4:2 | C4:4",
            "C G7 C C F G C C C C C C C G G7 F C G7 C"),
    ];

    public static string MusicFolder => Directory.CreateDirectory(Path.Combine(AppPaths.Root, "music")).FullName;
    public static string FileFor(Carol c) => Path.Combine(MusicFolder, $"{c.Id}-{RenderVersion}.wav");

    // ───────────────────────── parsing

    internal static double Frequency(string note)
    {
        // "C4", "F#4", "Bb3" → Hz (A4 = 440).
        int i = 1;
        int semis = "C D EF G A B".IndexOf(char.ToUpperInvariant(note[0]));
        if (semis < 0) throw new FormatException(note);
        if (note.Length > 2 && note[1] == '#') { semis++; i++; }
        else if (note.Length > 2 && note[1] == 'b') { semis--; i++; }
        int octave = int.Parse(note[i..]);
        int midi = 12 * (octave + 1) + semis;
        return 440.0 * Math.Pow(2, (midi - 69) / 12.0);
    }

    internal static List<(string? Note, double Beats)> ParseMelody(string melody) =>
        melody.Split([' ', '|'], StringSplitOptions.RemoveEmptyEntries).Select(tok =>
        {
            var parts = tok.Split(':');
            double beats = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            return (parts[0] == "R" ? null : parts[0], beats);
        }).ToList<(string?, double)>();

    internal static double[] ChordTones(string chord)
    {
        // "G", "Em", "A7", "C#m" → root frequency (octave 3) and triad/seventh intervals.
        string root = chord.Length > 1 && (chord[1] == '#' || chord[1] == 'b') ? chord[..2] : chord[..1];
        string kind = chord[root.Length..];
        double f = Frequency(root + "3");
        int[] iv = kind switch { "m" => [0, 3, 7], "7" => [0, 4, 7, 10], "m7" => [0, 3, 7, 10], _ => [0, 4, 7] };
        return iv.Select(s => f * Math.Pow(2, s / 12.0)).ToArray();
    }

    // ───────────────────────── synthesis

    /// <summary>
    /// A struck metallic voice (music box / celesta): a few slightly inharmonic partials,
    /// each decaying at its own rate. Uses a rotating phasor instead of Math.Sin (fast).
    /// </summary>
    private static void Strike(float[] buf, int start, double freq, double amp, double ringSeconds, (double Ratio, double Gain, double Decay)[] partials)
    {
        int len = Math.Min(buf.Length - start, (int)(ringSeconds * SampleRate));
        if (len <= 0) return;
        int attack = (int)(0.003 * SampleRate);
        foreach (var (ratio, gain, decay) in partials)
        {
            double f = freq * ratio;
            if (f >= SampleRate / 2.2) continue;   // avoid aliasing
            double w = 2 * Math.PI * f / SampleRate, cw = Math.Cos(w), sw = Math.Sin(w);
            double re = 1, im = 0, env = amp * gain, fall = Math.Exp(-decay / SampleRate);
            for (int n = 0; n < len; n++)
            {
                double a = n < attack ? env * n / attack : env;
                buf[start + n] += (float)(im * a);
                double nr = re * cw - im * sw; im = re * sw + im * cw; re = nr;
                env *= fall;
                if (env < 1e-5) break;
            }
        }
    }

    private static readonly (double, double, double)[] MusicBox = [(1, 1.0, 2.6), (2, 0.42, 4.0), (3, 0.2, 5.5), (4.16, 0.12, 7.5), (5.43, 0.05, 10)];
    private static readonly (double, double, double)[] Bass = [(1, 1.0, 1.7), (2, 0.35, 2.8), (3, 0.08, 4)];

    private static void SleighBell(float[] buf, int start, double amp, Random rng)
    {
        int len = Math.Min(buf.Length - start, (int)(0.12 * SampleRate));
        double env = amp, fall = Math.Exp(-38.0 / SampleRate), prev = 0;
        for (int n = 0; n < len; n++)
        {
            double noise = rng.NextDouble() * 2 - 1;
            buf[start + n] += (float)((noise - prev) * env);   // first difference = bright, jingly
            prev = noise;
            env *= fall;
        }
    }

    public static float[] Render(Carol c)
    {
        var notes = ParseMelody(c.Melody);
        var chords = c.Chords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double spb = 60.0 / c.Bpm;
        double totalBeats = notes.Sum(n => n.Beats);
        double maxFreq = notes.Where(n => n.Note is not null).Max(n => Frequency(n.Note!));
        int passes = 2;
        double passSeconds = totalBeats * spb;
        var buf = new float[(int)((passSeconds * passes + 1.0 + 3.5) * SampleRate)];
        var rng = new Random(c.Id.GetHashCode() & 0x7fffffff);

        for (int pass = 0; pass < passes; pass++)
        {
            double t0 = 1.0 + pass * passSeconds;   // one second of quiet lead-in
            // Second pass: melody an octave up (music-box sparkle), if it stays in range.
            double shift = pass == 1 && maxFreq * 2 < 1600 ? 2 : 1;
            double beat = 0;
            foreach (var (note, beats) in notes)
            {
                if (note is not null)
                {
                    double ring = Math.Min(3.2, beats * spb + 1.4);
                    Strike(buf, (int)((t0 + beat * spb) * SampleRate), Frequency(note) * shift, 0.30, ring, MusicBox);
                }
                beat += beats;
            }
            // Accompaniment: bass on strong beats, gentle arpeggio on every beat.
            int bars = (int)Math.Ceiling((totalBeats - c.Pickup) / c.BeatsPerBar);
            int step = c.Bpm > 150 && c.BeatsPerBar == 6 ? 2 : 1;
            for (int bar = 0; bar < bars && bar < chords.Length; bar++)
            {
                var tones = ChordTones(chords[bar]);
                double barStart = t0 + (c.Pickup + bar * c.BeatsPerBar) * spb;
                int[] strong = c.BeatsPerBar switch { 4 => [0, 2], 6 => [0, 3], _ => [0] };
                foreach (var s in strong) Strike(buf, (int)((barStart + s * spb) * SampleRate), tones[0] / 2, 0.22, 1.6, Bass);
                int[] order = [0, 2, 1, 2];
                for (int b = 0, k = 0; b < c.BeatsPerBar; b += step, k++)
                {
                    double f = tones[order[k % order.Length] % tones.Length] * 2;
                    Strike(buf, (int)((barStart + b * spb) * SampleRate), f, 0.075, 1.2, MusicBox);
                }
                if (c.SleighBells)
                    for (double b = 0; b < c.BeatsPerBar; b += 0.5)
                        SleighBell(buf, (int)((barStart + b * spb) * SampleRate), b % 1 == 0 && ((int)b % 2 == 1) ? 0.09 : 0.045, rng);
            }
        }
        Reverb(buf);
        Normalize(buf, 0.89f);
        FadeOut(buf, (int)(2.5 * SampleRate));
        return buf;
    }

    /// <summary>A small Schroeder reverb (4 combs + 2 all-passes) for a soft hall.</summary>
    private static void Reverb(float[] x)
    {
        int[] combs = [1116, 1188, 1277, 1356];
        int[] passes = [556, 441];
        double scale = SampleRate / 44100.0;
        var wet = new float[x.Length];
        foreach (var d0 in combs)
        {
            int d = (int)(d0 * scale);
            var line = new float[d];
            float store = 0;
            for (int n = 0, i = 0; n < x.Length; n++, i = (i + 1) % d)
            {
                float y = line[i];
                store = y * 0.75f + store * 0.25f;   // damping
                line[i] = x[n] + store * 0.80f;
                wet[n] += y;
            }
        }
        foreach (var d0 in passes)
        {
            int d = (int)(d0 * scale);
            var line = new float[d];
            for (int n = 0, i = 0; n < wet.Length; n++, i = (i + 1) % d)
            {
                float b = line[i], y = -wet[n] + b;
                line[i] = wet[n] + b * 0.5f;
                wet[n] = y;
            }
        }
        for (int n = 0; n < x.Length; n++) x[n] = x[n] * 0.82f + wet[n] * 0.06f;
    }

    private static void Normalize(float[] x, float peak)
    {
        float max = 1e-6f;
        foreach (var v in x) max = Math.Max(max, Math.Abs(v));
        float g = peak / max;
        for (int i = 0; i < x.Length; i++) x[i] *= g;
    }

    private static void FadeOut(float[] x, int len)
    {
        len = Math.Min(len, x.Length);
        for (int i = 0; i < len; i++) x[x.Length - len + i] *= 1f - (float)i / len;
    }

    public static void WriteWav(string path, float[] samples)
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var w = new BinaryWriter(fs))
        {
            int bytes = samples.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            foreach (var s in samples) w.Write((short)Math.Clamp(s * 32767f, -32768f, 32767f));
        }
        File.Move(tmp, path, true);
    }

    /// <summary>Renders any carols that aren't cached yet. Safe to call repeatedly.</summary>
    public static Task EnsureRenderedAsync() => Task.Run(() =>
    {
        foreach (var c in Carols)
        {
            var file = FileFor(c);
            if (File.Exists(file)) continue;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            WriteWav(file, Render(c));
            Log.Info("music", $"Rendered \"{c.Title}\" in {sw.ElapsedMilliseconds} ms.");
        }
        // Remove renders from older synth versions.
        foreach (var old in Directory.GetFiles(MusicFolder, "*.wav").Where(f => !f.EndsWith($"-{RenderVersion}.wav")))
            try { File.Delete(old); } catch { }
    });
}

/// <summary>Plays the carols as a looping playlist. Never starts on its own.</summary>
public sealed class CarolPlayer
{
    public static CarolPlayer Instance { get; } = new();

    private MediaPlayer? _player;
    private int _index;
    private bool _autoPaused;

    public bool IsPlaying { get; private set; }
    public bool IsPreparing { get; private set; }
    public string CurrentTitle => CarolSynth.Carols[_index].Title;
    public string CurrentCredit => CarolSynth.Carols[_index].Credit;
    public event Action? StateChanged;

    public async Task ToggleAsync()
    {
        if (IsPlaying) { Pause(); return; }
        await PlayAsync();
    }

    public async Task PlayAsync()
    {
        if (IsPreparing) return;
        if (_player is null)
        {
            IsPreparing = true;
            StateChanged?.Invoke();
            try { await CarolSynth.EnsureRenderedAsync(); }
            catch (Exception ex)
            {
                Log.Error("music", "Could not prepare carols", ex);
                IsPreparing = false;
                StateChanged?.Invoke();
                return;
            }
            IsPreparing = false;
            _player = new MediaPlayer();
            _player.MediaEnded += (_, _) => Next();
            _player.MediaFailed += (_, e) => { Log.Warn("music", $"Playback failed: {e.ErrorException?.Message}"); Next(); };
            Open(_index);
        }
        _player.Volume = Settings.Current.MusicVolume;
        _player.Play();
        IsPlaying = true;
        _autoPaused = false;
        Log.Info("music", $"Playing \"{CurrentTitle}\".");
        StateChanged?.Invoke();
    }

    private void Open(int index)
    {
        _index = (index % CarolSynth.Carols.Length + CarolSynth.Carols.Length) % CarolSynth.Carols.Length;
        _player!.Open(new Uri(CarolSynth.FileFor(CarolSynth.Carols[_index])));
        _player.Volume = Settings.Current.MusicVolume;
    }

    public void Next()
    {
        if (_player is null) return;
        Open(_index + 1);
        if (IsPlaying) _player.Play();
        StateChanged?.Invoke();
    }

    public void Pause()
    {
        _player?.Pause();
        IsPlaying = false;
        StateChanged?.Invoke();
    }

    /// <summary>Pauses while VYTRIX is minimised (e.g. while gaming) and resumes on restore.</summary>
    public void SetWindowVisible(bool visible)
    {
        if (!visible && IsPlaying) { Pause(); _autoPaused = true; }
        else if (visible && _autoPaused) { _autoPaused = false; _ = PlayAsync(); }
    }

    public void SetVolume(double v)
    {
        Settings.Current.MusicVolume = Math.Clamp(v, 0, 1);
        if (_player is not null) _player.Volume = Settings.Current.MusicVolume;
    }

    public void Stop()
    {
        _player?.Stop();
        _player?.Close();
        _player = null;
        IsPlaying = false;
        StateChanged?.Invoke();
    }
}
