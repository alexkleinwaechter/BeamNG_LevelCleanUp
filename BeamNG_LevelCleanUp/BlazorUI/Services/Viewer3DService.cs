using System.Windows.Forms;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Viewer3D;

namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
/// Service to launch the 3D viewer from Blazor pages.
/// No file copying needed - uses PathResolver for direct access.
/// </summary>
public class Viewer3DService
{
    /// <summary>
    /// Opens the 3D viewer for a CopyAsset.
    /// Convenience method for CopyAssets page compatibility.
    /// </summary>
    public Task OpenViewerAsync(CopyAsset asset)
    {
        var request = Viewer3DRequest.FromCopyAsset(asset);
        return OpenViewerAsync(request);
    }

    /// <summary>
    /// Opens the 3D viewer for a MaterialJson.
    /// Can be called from any page that has material data.
    /// </summary>
    public Task OpenViewerAsync(MaterialJson material, string? levelPath = null)
    {
        var request = Viewer3DRequest.FromMaterial(material, levelPath);
        return OpenViewerAsync(request);
    }

    /// <summary>
    /// Opens the 3D viewer for a single texture file.
    /// </summary>
    public Task OpenViewerAsync(string texturePath, string? displayName = null)
    {
        var request = Viewer3DRequest.FromTexture(texturePath, displayName);
        return OpenViewerAsync(request);
    }

    /// <summary>
    /// Opens the 3D viewer with a generic request.
    /// Uses a separate STA thread to avoid WPF/WinForms interop issues with Blazor WebView.
    /// </summary>
    public Task OpenViewerAsync(Viewer3DRequest request)
    {
        var tcs = new TaskCompletionSource();

        // Always use a separate STA thread for WPF content
        // This avoids conflicts with the Blazor WebView's message loop
        var thread = new Thread(() =>
        {
            try
            {
                // Create a new application context for this thread
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                
                var form = new HelixViewerForm();
                
                // Set the request - content will load after form is shown
                form.SetRequest(request);
                
                // Run the form with its own message loop
                Application.Run(form);
                
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                var errorMessage = ex.InnerException != null 
                    ? $"{ex.Message}\n\nInner: {ex.InnerException.Message}"
                    : ex.Message;
                    
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Viewer error: {errorMessage}");
                
                // Show error on this thread (it has its own message loop now)
                MessageBox.Show(
                    $"Failed to open 3D viewer:\n\n{errorMessage}",
                    "Viewer Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                    
                tcs.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }
}
