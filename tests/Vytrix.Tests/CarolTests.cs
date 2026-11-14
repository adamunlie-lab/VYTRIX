using System.Diagnostics;
using System.Globalization;
using System.IO;
using Vytrix.Services;
using Xunit;

namespace Vytrix.Tests;

public class CarolTests
{
    [Theory]
    [InlineData("A4", 440.0)]
    [InlineData("C4", 261.63)]
    [InlineData("F#4", 369.99)]
    [InlineData("Bb3", 233.08)]
    [InlineData("G5", 783.99)]
    public void Note_names_map_to_correct_pitch(string note, double hz) =>
        Assert.Equal(hz, CarolSynth.Frequency(note), 1);

    public static IEnumerable<object[]> All() => CarolSynth.Carols.Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(All))]
    public void Every_bar_has_the_right_number_of_beats_and_a_chord(string id)
    {
        var c = CarolSynth.Carols.Single(x => x.Id == id);
        var bars = c.Melody.Split('|').Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
        double Sum(string bar) => bar.Split(' ', StringSplitOptions.RemoveEmptyEntries).Sum(t => double.Parse(t.Split(':')[1], CultureInfo.InvariantCulture));
        int i = 0;
        if (c.Pickup > 0) { Assert.Equal(c.Pickup, Sum(bars[0])); i = 1; }
        int fullBars = 0;
        for (; i < bars.Count; i++)
        {
            double beats = Sum(bars[i]);
            Assert.True(beats % c.BeatsPerBar == 0, $"{c.Title}: bar {i} has {beats} beats");
            fullBars += (int)(beats / c.BeatsPerBar);
        }
        Assert.Equal(fullBars, c.Chords.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Every_chord_and_note_parses()
    {
        foreach (var c in CarolSynth.Carols)
        {
            foreach (var chord in c.Chords.Split(' ', StringSplitOptions.RemoveEmptyEntries)) Assert.NotEmpty(CarolSynth.ChordTones(chord));
            foreach (var (note, _) in CarolSynth.ParseMelody(c.Melody)) if (note is not null) Assert.InRange(CarolSynth.Frequency(note), 200, 1000);
        }
    }

    [Fact]
    public void Credits_state_public_domain()
    {
        Assert.Equal(5, CarolSynth.Carols.Length);
        Assert.All(CarolSynth.Carols, c => Assert.Contains("public domain", c.Credit));
    }

    [Fact]
    public void Render_produces_clean_audio_quickly()
    {
        var c = CarolSynth.Carols[0];
        var sw = Stopwatch.StartNew();
        var pcm = CarolSynth.Render(c);
        sw.Stop();
        double seconds = pcm.Length / (double)CarolSynth.SampleRate;
        Assert.InRange(seconds, 30, 120);
        Assert.All(pcm, s => Assert.False(float.IsNaN(s) || float.IsInfinity(s)));
        float peak = pcm.Max(Math.Abs);
        Assert.InRange(peak, 0.8f, 0.9f);                                    // normalized, no clipping
        Assert.True(pcm.Skip(CarolSynth.SampleRate * 2).Take(CarolSynth.SampleRate).Max(Math.Abs) > 0.05f);   // not silent
        Assert.True(sw.ElapsedMilliseconds < 8000, $"render took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Wav_file_has_a_valid_header()
    {
        var path = Path.Combine(TestEnvironment.DataDir, "test.wav");
        CarolSynth.WriteWav(path, [0f, 0.5f, -0.5f, 0f]);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(44 + 8, bytes.Length);
        Assert.Equal(CarolSynth.SampleRate, BitConverter.ToInt32(bytes, 24));
    }
}
