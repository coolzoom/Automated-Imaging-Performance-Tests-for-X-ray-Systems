using OpenCvSharp;

namespace WireInspect.Core;

public static class ImageIO
{
    public static readonly string[] Exts = [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"];

    /// <summary>Read an image as 8-bit grayscale; bytes are read by .NET so non-ASCII paths work.</summary>
    public static Mat ReadGray(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length == 0)
            throw new IOException("图片文件为空（0 字节），可能是解压或拷贝不完整，请重新获取原图");
        var img = Cv2.ImDecode(data, ImreadModes.Unchanged);
        if (img.Empty())
            throw new IOException($"无法解码图片（文件损坏或不是图片）: {path}");
        if (img.Depth() != MatType.CV_8U)
        {
            var n = new Mat();
            Cv2.Normalize(img, n, 0, 255, NormTypes.MinMax, MatType.CV_8U);
            img.Dispose();
            img = n;
        }
        if (img.Channels() > 1)
        {
            var g = new Mat();
            Cv2.CvtColor(img, g, img.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
            img.Dispose();
            img = g;
        }
        return img;
    }

    public static void WritePng(string path, Mat img)
    {
        Cv2.ImEncode(".png", img, out var buf);
        File.WriteAllBytes(path, buf);
    }

    public static Mat Clahe(Mat gray, double clip = 2.0)
    {
        using var blur = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(0, 0), 0.8);
        using var c = Cv2.CreateCLAHE(clip, new Size(8, 8));
        var dst = new Mat();
        c.Apply(blur, dst);
        return dst;
    }
}
