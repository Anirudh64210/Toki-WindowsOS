using System.Diagnostics;
using SherpaOnnx;

namespace Toki.Transcription;

/// <summary>One stretch of audio attributed to a speaker cluster.</summary>
public sealed record SpeakerSpan(int Speaker, float StartSec, float EndSec);

/// <summary>
/// Tells the remote participants apart. Only them.wav ever needs this: the mic is its own stream,
/// so the Toki user is already perfectly separated. Two routes with very different costs:
/// <see cref="Full"/> is the textbook pipeline (segmentation sweep + per-hop embeddings +
/// clustering); <see cref="ByUtterance"/> reuses the ASR timestamps - one embedding per utterance,
/// no segmentation pass. `--diartest` measures both on real audio.
/// </summary>
public static class SpeakerDiariser
{
    public const int SampleRate = 16000;

    /// <summary>Below this, an embedding is computed from too little speech to be worth trusting.</summary>
    public const float MinSpeechSec = 0.6f;

    public static bool Available => ModelManager.DiarisationInstalled;

    /// <summary>The textbook route: segmentation model + per-hop embeddings + clustering.</summary>
    public static (IReadOnlyList<SpeakerSpan> Spans, TimeSpan Took) Full(
        float[] samples, int expectedSpeakers = -1, int threads = 4)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = ModelManager.PathTo(ModelManager.Segmentation, "model.onnx");
        config.Segmentation.NumThreads = threads;      // the binding defaults to 1; this alone is worth ~4x
        config.Embedding.Model = ModelManager.SpeakerEmbedPath;
        config.Embedding.NumThreads = threads;
        config.Clustering.NumClusters = expectedSpeakers;   // -1 = estimate it
        config.Clustering.Threshold = 0.5f;                 // only consulted when NumClusters <= 0

        var sw = Stopwatch.StartNew();
        using var sd = new OfflineSpeakerDiarization(config);
        var segments = sd.Process(samples);
        sw.Stop();

        var spans = segments.Select(s => new SpeakerSpan(s.Speaker, s.Start, s.End)).ToList();
        return (spans, sw.Elapsed);
    }

    /// <summary>
    /// The ASR-guided route: embed once per utterance we already have boundaries for, then cluster.
    /// </summary>
    /// <param name="utterances">Start/end in seconds, from the ASR segments of the same audio.</param>
    public static (IReadOnlyList<SpeakerSpan> Spans, TimeSpan Took) ByUtterance(
        float[] samples, IReadOnlyList<(float Start, float End)> utterances,
        int expectedSpeakers = -1, int threads = 4, double? threshold = null)
    {
        var sw = Stopwatch.StartNew();

        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = ModelManager.SpeakerEmbedPath,
            NumThreads = threads,
        };
        using var extractor = new SpeakerEmbeddingExtractor(config);

        var vectors = new List<float[]>();
        var kept = new List<(float Start, float End)>();
        foreach (var (start, end) in utterances)
        {
            if (end - start < MinSpeechSec) continue;      // too little speech to fingerprint honestly
            var from = Math.Max(0, (int)(start * SampleRate));
            var to = Math.Min(samples.Length, (int)(end * SampleRate));
            if (to - from < SampleRate * MinSpeechSec) continue;

            var slice = new float[to - from];
            Array.Copy(samples, from, slice, 0, slice.Length);

            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(SampleRate, slice);
            stream.InputFinished();
            vectors.Add(extractor.Compute(stream));
            kept.Add((start, end));
        }

        var labels = Cluster(vectors, expectedSpeakers, threshold ?? Threshold);
        sw.Stop();

        var spans = kept.Select((u, i) => new SpeakerSpan(labels[i], u.Start, u.End)).ToList();
        return (spans, sw.Elapsed);
    }

    // ── clustering ──

    /// <summary>
    /// Agglomerative clustering on cosine distance. AVERAGE linkage, not complete: complete asks
    /// that EVERY pair be close, so one bad pair (network drop, off-mic sentence) permanently
    /// splits a person in two; average asks that a cluster be close ON THE WHOLE.
    /// </summary>
    internal static int[] Cluster(List<float[]> vectors, int expected, double threshold)
    {
        var n = vectors.Count;
        var labels = new int[n];
        if (n == 0) return labels;
        for (var i = 0; i < n; i++) labels[i] = i;
        if (n == 1) return labels;

        foreach (var v in vectors) Normalise(v);

        // Distance between two clusters = the AVERAGE over all pairs, so one outlier utterance cannot
        // veto a merge.
        var active = Enumerable.Range(0, n).ToList();
        var members = active.ToDictionary(i => i, i => new List<int> { i });

        while (active.Count > 1)
        {
            var (bestA, bestB, bestD) = (-1, -1, double.MaxValue);
            for (var x = 0; x < active.Count; x++)
                for (var y = x + 1; y < active.Count; y++)
                {
                    var d = Linkage(vectors, members[active[x]], members[active[y]]);
                    if (d < bestD) (bestA, bestB, bestD) = (active[x], active[y], d);
                }

            if (bestA < 0) break;
            // Two voices closer than the threshold ALWAYS merge - the attendee count may never
            // force a split the audio does not support. The count only works the other way:
            // merging may continue a little past the threshold, never stop early at the count.
            if (bestD > threshold && (expected <= 0 || active.Count <= expected)) break;

            members[bestA].AddRange(members[bestB]);
            members.Remove(bestB);
            active.Remove(bestB);
        }

        var next = 0;
        foreach (var cluster in members.Values.OrderBy(m => m.Min()))
        {
            foreach (var i in cluster) labels[i] = next;
            next++;
        }
        return labels;
    }

    /// <summary>
    /// Cosine distance above which two voices are different people, used only when the attendee
    /// count is unknown. Model-dependent and the single most important number here. Deliberately
    /// conservative: under-splitting reads as coarse, but over-splitting invents colleagues who
    /// were never in the room. The attendee count is the real answer when a calendar supplies one.
    /// </summary>
    public const double Threshold = 0.80;

    /// <summary>The exploratory value the threshold sweep started from. Kept only so `--diartest`
    /// can reproduce it; never used to label a real transcript.</summary>
    public const double ExploratoryThreshold = 0.55;

    static double Linkage(List<float[]> v, List<int> a, List<int> b)
    {
        var sum = 0.0;
        foreach (var i in a)
            foreach (var j in b)
                sum += 1.0 - Dot(v[i], v[j]);
        return sum / (a.Count * b.Count);
    }

    static void Normalise(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm <= 0) return;
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
    }

    static double Dot(float[] a, float[] b)
    {
        var sum = 0.0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) sum += (double)a[i] * b[i];
        return sum;
    }
}
