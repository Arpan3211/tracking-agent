using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace EmployeeAgent.Core;

/// <summary>
/// Captures every connected monitor on a timer and saves each as its own PNG.
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
        var screens = Screen.AllScreens;
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

        for (var i = 0; i < screens.Length; i++)
        {
            try
            {
                var bounds = screens[i].Bounds;
                using var bitmap = new Bitmap(bounds.Width, bounds.Height);
                using var g = Graphics.FromImage(bitmap);
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

                var fileName = $"screenshot_{timestamp}_monitor{i}.png";
                var fullPath = Path.Combine(_screenshotFolder, fileName);
                bitmap.Save(fullPath, ImageFormat.Png);

                _logger.Log("screenshot_captured", $"path={fullPath}; monitor={i}; primary={screens[i].Primary}");
            }
            catch (Exception ex)
            {
                _logger.Log("screenshot_failed", $"monitor={i}; error={ex.Message}");
            }
        }
    }
}
