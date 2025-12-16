using System.Text.Json;

namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
/// Persists window state (size, position, maximized) between application sessions.
/// Settings are stored in the user's AppData folder.
/// </summary>
public class WindowSettings
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeamNG_LevelCleanUp");

    private static readonly string SettingsFile = Path.Combine(SettingsFolder, "window-settings.json");

    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsMaximized { get; set; }

    /// <summary>
    /// Loads window settings from disk. Returns null if no settings exist or loading fails.
    /// </summary>
    public static WindowSettings? Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
                return null;

            var json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<WindowSettings>(json, BeamJsonOptions.GetJsonSerializerOptions());
        }
        catch
        {
            // If loading fails for any reason, return null to use defaults
            return null;
        }
    }

    /// <summary>
    /// Saves the current window settings to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            if (!Directory.Exists(SettingsFolder))
                Directory.CreateDirectory(SettingsFolder);

            var json = JsonSerializer.Serialize(this, BeamJsonOptions.GetJsonSerializerOptions());
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Silently fail - window settings are not critical
        }
    }

    /// <summary>
    /// Creates a WindowSettings instance from a Form's current state.
    /// If the form is maximized, stores the RestoreBounds (normal state bounds).
    /// </summary>
    public static WindowSettings FromForm(Form form)
    {
        // When maximized, use RestoreBounds to get the "normal" size/position
        var bounds = form.WindowState == FormWindowState.Maximized
            ? form.RestoreBounds
            : form.Bounds;

        return new WindowSettings
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = form.WindowState == FormWindowState.Maximized
        };
    }

    /// <summary>
    /// Applies the saved settings to a Form.
    /// Validates that the window is visible on at least one screen.
    /// </summary>
    public void ApplyTo(Form form)
    {
        // Ensure the window will be visible on at least one screen
        var proposedBounds = new Rectangle(Left, Top, Width, Height);

        if (IsVisibleOnAnyScreen(proposedBounds))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(Left, Top);
            form.Size = new Size(Width, Height);

            if (IsMaximized)
                form.WindowState = FormWindowState.Maximized;
        }
        // If not visible on any screen, let Windows use default positioning
    }

    /// <summary>
    /// Checks if at least a portion of the window is visible on any connected screen.
    /// This handles cases where the user disconnected a monitor.
    /// </summary>
    private static bool IsVisibleOnAnyScreen(Rectangle bounds)
    {
        // Check if at least 100x100 pixels of the window would be visible
        const int minVisibleSize = 100;

        foreach (var screen in Screen.AllScreens)
        {
            var intersection = Rectangle.Intersect(bounds, screen.WorkingArea);
            if (intersection.Width >= minVisibleSize && intersection.Height >= minVisibleSize)
                return true;
        }

        return false;
    }
}
