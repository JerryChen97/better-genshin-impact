using System.Runtime.InteropServices;
using System.Text.Json;
using BetterGenshinImpact.Core.Recognition.OpenCv.FeatureMatch;
using OpenCvSharp;
using OpenCvSharp.Features2D;

var results = new List<object>();
var failed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
void Test(string name, Action body)
{
    try { body(); results.Add(new { name, passed = true }); }
    catch (Exception error) { failed++; results.Add(new { name, passed = false, error = error.ToString() }); }
}

// Fixed synthetic pixels, real SIFT and real descriptors. Exact descriptor rows below
// control the number of accepted correspondences; no matcher or native function is mocked.
using var feature = SIFT.Create();
using var query = new Mat(256, 256, MatType.CV_8UC1);
var pixels = new byte[query.Rows * query.Cols];
uint seed = 918273;
for (var i = 0; i < pixels.Length; i++)
{
    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
    pixels[i] = (byte)seed;
}
Marshal.Copy(pixels, 0, query.Data, pixels.Length);
using var queryDescriptors = new Mat();
feature.DetectAndCompute(query, null, out var queryPoints, queryDescriptors);
Check(queryPoints.Length >= 12, "Synthetic image must supply enough genuine SIFT features.");
var selected = new List<int>();
for (var i = 0; i < queryPoints.Length && selected.Count < 8; i++)
{
    if (selected.All(j => Math.Pow(queryPoints[i].Pt.X - queryPoints[j].Pt.X, 2) +
        Math.Pow(queryPoints[i].Pt.Y - queryPoints[j].Pt.Y, 2) > 225)) selected.Add(i);
}
Check(selected.Count == 8, "Fixture must have eight separate feature locations.");

(Mat Descriptors, KeyPoint[] Points) Training(int count, bool collinear = false)
{
    var descriptors = new Mat(Math.Max(count, 1), queryDescriptors.Cols, queryDescriptors.Type(), Scalar.All(2000));
    var points = new KeyPoint[descriptors.Rows];
    for (var i = 0; i < count; i++)
    {
        using var source = queryDescriptors.Row(selected[i]);
        using var target = descriptors.Row(i);
        source.CopyTo(target);
        var point = collinear ? new Point2f(100 + i * 10, 150) : queryPoints[selected[i]].Pt + new Point2f(120, 80);
        points[i] = new KeyPoint(point.X, point.Y, 1);
    }
    return (descriptors, points);
}

foreach (var matcherType in new[] { DescriptorMatcherType.BruteForce, DescriptorMatcherType.FlannBased })
{
    for (var count = 1; count <= 3; count++)
    {
        var accepted = count;
        Test($"{matcherType}: {accepted} accepted correspondences return no match", () =>
        {
            var training = Training(accepted);
            using var descriptors = training.Descriptors;
            using var matcher = DescriptorMatcher.Create(matcherType.ToString());
            var matches = matcher.Match(queryDescriptors, descriptors);
            var cutoff = Math.Max(matches.Min(match => match.Distance) * 2, 0.02);
            Check(matches.Count(match => match.Distance < cutoff) == accepted, "Verify exact sparse fixture before production call.");
            Check(feature.Match(training.Points, descriptors, query, matcherType: matcherType) == default,
                "Sparse image must report no match without calling invalid homography.");
        });
    }
    Test($"{matcherType}: blank query reports no match", () =>
    {
        using var blank = new Mat(query.Size(), MatType.CV_8UC1, Scalar.All(0));
        var training = Training(8);
        using var descriptors = training.Descriptors;
        Check(feature.Match(training.Points, descriptors, blank, matcherType: matcherType) == default,
            "Empty query descriptors are an ordinary recognition miss.");
    });
    Test($"{matcherType}: degenerate homography reports no match", () =>
    {
        var training = Training(8, collinear: true);
        using var descriptors = training.Descriptors;
        Check(feature.Match(training.Points, descriptors, query, matcherType: matcherType) == default,
            "An empty homography cannot be passed to PerspectiveTransform.");
    });
    foreach (var count in new[] { 4, 8 })
    {
        Test($"{matcherType}: {count} valid correspondences preserve center coordinates", () =>
        {
            var training = Training(count);
            using var descriptors = training.Descriptors;
            var actual = feature.Match(training.Points, descriptors, query, matcherType: matcherType);
            Check(Math.Abs(actual.X - 248) < 0.1 && Math.Abs(actual.Y - 208) < 0.1,
                $"Expected translated query center (248,208), got {actual}.");
        });
    }
    Test($"{matcherType}: incompatible training descriptors remain fatal", () =>
    {
        using var invalid = new Mat(8, queryDescriptors.Cols, MatType.CV_8UC1, Scalar.All(1));
        Exception? error = null;
        try { feature.Match(new KeyPoint[8], invalid, query, matcherType: matcherType); }
        catch (Exception caught) { error = caught; }
        Check(error is OpenCVException, "Unknown asset/engine errors must propagate, not become no match.");
    });
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = results.Count - failed, failed, results,
    scope = "Linked production Match; real OpenCvSharp 4.11/SIFT/descriptor matching/homography on controlled synthetic images. No desktop, game or BGI process." },
    new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;
