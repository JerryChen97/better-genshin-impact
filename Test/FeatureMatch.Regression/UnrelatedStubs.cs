using OpenCvSharp;

// The linked Match implementation and OpenCV are real. These unused storage/UI helpers
// only allow compiling the whole production extensions class without starting BGI.
namespace BetterGenshinImpact.GameTask.Common.BgiVision
{
    public static class Bv
    {
        public static Mat ImRead(string path, ImreadModes mode) => throw new NotSupportedException();
    }
}

namespace BetterGenshinImpact.Core.Recognition.OpenCv.FeatureMatch
{
    public static class FeatureStorageHelper
    {
        public static void SaveKeyPointArray(KeyPoint[] points, string path) => throw new NotSupportedException();
        public static void SaveDescMat(Mat descriptors, string path) => throw new NotSupportedException();
    }
}

namespace BetterGenshinImpact.Core.Recognition.OpenCv
{
    public static class PointConversion
    {
        public static List<Point2d> ToPoint2d(this List<Point2f> points) =>
            points.ConvertAll(point => new Point2d(point.X, point.Y));
    }
}
