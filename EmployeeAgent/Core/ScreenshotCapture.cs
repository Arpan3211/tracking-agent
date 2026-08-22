using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace EmployeeAgent.Core;

/// <summary>
/// Captures the full primary screen on a timer and saves it as a PNG.
/// This is the most storage- and privacy-heavy feature in the whole list -
/// keep the interval long (default 10 minutes) and treat the output
/// folder as sensitive data requiring the same access controls as the
/// activity log itself.
/// </summary>
public sealed class ScreenshotCapture
{
    private readonly ActivityLogger _logger;
    private readonly string _screenshotFolder;

    public ScreenshotCapture(ActivityLogger logger)
    {
        _logger = logger;

        _screenshotFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EmployeeAgent", "Screenshots");

        Directory.CreateDirectory(_screenshotFolder);
    }

    public void CaptureNow()
    {
        try
        {
            var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

            var fileName = $"screenshot_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png";
            var fullPath = Path.Combine(_screenshotFolder, fileName);
            bitmap.Save(fullPath, ImageFormat.Png);

            _logger.Log("screenshot_captured", fullPath);
        }
        catch (Exception ex)
        {
            _logger.Log("screenshot_failed", ex.Message);
        }
    }
}
