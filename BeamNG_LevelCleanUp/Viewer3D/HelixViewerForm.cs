using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using Pfim;
using Color = System.Drawing.Color;
using ImageFormat = Pfim.ImageFormat;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
///     Windows Forms dialog hosting the Helix Toolkit WPF viewport.
///     Provides 3D preview for DAE models and material textures.
/// </summary>
public class HelixViewerForm : Form
{
    /// <summary>
    ///     Stores all texture cards for filtering.
    /// </summary>
    private readonly List<(Panel Card, string MaterialName, MaterialFile File)> _allTextureCards = new();

    private readonly Button _clearSelectionButton;
    private readonly ElementHost _elementHost;
    private readonly Label _selectionLabel;
    private readonly FlowLayoutPanel _textureGallery;
    private readonly Panel _texturePanel;
    private readonly HelixViewportControl _viewportControl;
    private Viewer3DRequest? _pendingRequest;

    public HelixViewerForm()
    {
        InitializeForm();

        _viewportControl = new HelixViewportControl();
        _viewportControl.MeshSelected += OnMeshSelected;

        _elementHost = new ElementHost
        {
            Dock = DockStyle.Fill,
            Child = _viewportControl
        };

        _textureGallery = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Color.FromArgb(30, 30, 46)
        };

        // Create selection info panel
        var selectionPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 35,
            BackColor = Color.FromArgb(40, 40, 56),
            Padding = new Padding(8, 5, 8, 5)
        };

        _selectionLabel = new Label
        {
            Text = "Click on a mesh to see its material info",
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 9, FontStyle.Italic),
            AutoSize = true,
            Location = new Point(8, 8)
        };

        _clearSelectionButton = new Button
        {
            Text = "Show All",
            Size = new Size(80, 25),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(70, 70, 90),
            ForeColor = Color.White,
            Visible = false,
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        _clearSelectionButton.Click += (s, e) => ClearTextureFilter();

        selectionPanel.Controls.Add(_selectionLabel);
        selectionPanel.Controls.Add(_clearSelectionButton);
        selectionPanel.Resize += (s, e) =>
        {
            _clearSelectionButton.Location = new Point(selectionPanel.Width - 95, 5);
        };

        _texturePanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 235,
            BackColor = Color.FromArgb(30, 30, 46),
            AutoScroll = true
        };
        _texturePanel.Controls.Add(_textureGallery);
        _texturePanel.Controls.Add(selectionPanel);

        var buttonPanel = CreateButtonPanel();

        var mainPanel = new Panel { Dock = DockStyle.Fill };
        mainPanel.Controls.Add(_elementHost);

        Controls.Add(mainPanel);
        Controls.Add(_texturePanel);
        Controls.Add(buttonPanel);

        // Load content after the form is shown and WPF control is initialized
        Shown += OnFormShown;
    }

    private void InitializeForm()
    {
        Text = "3D Asset Viewer";
        Size = new Size(1200, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(30, 30, 46);
        MinimumSize = new Size(800, 600);
    }

    private Panel CreateButtonPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            BackColor = Color.FromArgb(45, 45, 61)
        };

        var closeButton = new Button
        {
            Text = "Close",
            Size = new Size(100, 35),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(80, 80, 100),
            ForeColor = Color.White
        };
        closeButton.Click += (s, e) => Close();

        var resetCameraButton = new Button
        {
            Text = "Reset Camera",
            Size = new Size(120, 35),
            Location = new Point(10, 8),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(80, 80, 100),
            ForeColor = Color.White
        };
        resetCameraButton.Click += (s, e) => _viewportControl?.ResetCamera();

        panel.Controls.Add(closeButton);
        panel.Controls.Add(resetCameraButton);

        // Position close button on right side
        panel.Resize += (s, e) => { closeButton.Location = new Point(panel.Width - 120, 8); };

        return panel;
    }

    /// <summary>
    ///     Sets the request to load when the form is shown.
    /// </summary>
    public void SetRequest(Viewer3DRequest request)
    {
        _pendingRequest = request;
        Text = $"3D Asset Viewer - {request.DisplayName}";
    }

    /// <summary>
    ///     Called when the form is shown - loads the content after WPF is initialized.
    /// </summary>
    private async void OnFormShown(object? sender, EventArgs e)
    {
        if (_pendingRequest == null)
            return;

        try
        {
            await _viewportControl.LoadAsync(_pendingRequest);
            PopulateTextureGallery(_pendingRequest.Materials);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to load content:\n\n{ex.Message}",
                "Load Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    ///     Handles mesh selection events from the viewport control.
    ///     Filters the texture gallery to show only textures for the selected material.
    /// </summary>
    private void OnMeshSelected(object? sender, MeshSelectionInfo? selectionInfo)
    {
        if (selectionInfo == null)
        {
            // Deselected - show all textures
            ClearTextureFilter();
            return;
        }

        // Update selection label
        _selectionLabel.Text = $"Material: {selectionInfo.MaterialName}";
        _selectionLabel.ForeColor = Color.FromArgb(255, 200, 100); // Highlight color
        _selectionLabel.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        _clearSelectionButton.Visible = true;

        // Filter texture gallery to show only textures for this material
        FilterTextureGallery(selectionInfo);
    }

    /// <summary>
    ///     Filters the texture gallery to show only textures for the selected material.
    /// </summary>
    private void FilterTextureGallery(MeshSelectionInfo selectionInfo)
    {
        foreach (var (card, materialName, file) in _allTextureCards)
        {
            // Check if this card belongs to the selected material
            var isMatch = materialName.Equals(selectionInfo.MaterialName, StringComparison.OrdinalIgnoreCase) ||
                          (selectionInfo.Material != null && (
                              materialName.Equals(selectionInfo.Material.Name, StringComparison.OrdinalIgnoreCase) ||
                              materialName.Equals(selectionInfo.Material.InternalName,
                                  StringComparison.OrdinalIgnoreCase) ||
                              materialName.Equals(selectionInfo.Material.MapTo, StringComparison.OrdinalIgnoreCase)));

            // Also check if the texture file is in the selection's texture files
            if (!isMatch && selectionInfo.TextureFiles.Count > 0)
                isMatch = selectionInfo.TextureFiles.Any(tf =>
                    tf.File?.FullName?.Equals(file.File?.FullName, StringComparison.OrdinalIgnoreCase) == true);

            card.Visible = isMatch;

            // Highlight matching cards
            if (isMatch)
                card.BackColor = Color.FromArgb(60, 60, 80); // Highlighted
            else
                card.BackColor = Color.FromArgb(45, 45, 61); // Normal
        }
    }

    /// <summary>
    ///     Clears the texture filter and shows all textures.
    /// </summary>
    private void ClearTextureFilter()
    {
        _selectionLabel.Text = "Click on a mesh to see its material info";
        _selectionLabel.ForeColor = Color.LightGray;
        _selectionLabel.Font = new Font("Segoe UI", 9, FontStyle.Italic);
        _clearSelectionButton.Visible = false;

        foreach (var (card, _, _) in _allTextureCards)
        {
            card.Visible = true;
            card.BackColor = Color.FromArgb(45, 45, 61);
        }
    }

    /// <summary>
    ///     Populates the texture gallery with material textures.
    ///     Uses Pfim for DDS conversion (2D preview only).
    ///     Supports .link file resolution from game asset ZIPs.
    /// </summary>
    private void PopulateTextureGallery(List<MaterialJson>? materials)
    {
        _textureGallery.Controls.Clear();
        _allTextureCards.Clear();

        if (materials == null || materials.Count == 0)
            return;

        foreach (var material in materials)
        {
            // MaterialFiles could be null
            if (material.MaterialFiles == null)
                continue;

            var materialName = material.Name ?? material.InternalName ?? "Unknown";

            foreach (var file in material.MaterialFiles)
            {
                // Check if file exists or can be resolved via .link
                if (file.File == null) continue;

                var filePath = file.File.FullName;
                var canResolve = file.File.Exists || LinkFileResolver.CanResolve(filePath);

                if (!canResolve) continue;

                try
                {
                    var card = CreateTextureCard(file, materialName);
                    _textureGallery.Controls.Add(card);
                    _allTextureCards.Add((card, materialName, file));
                }
                catch
                {
                    // Skip files that can't be loaded
                }
            }
        }
    }

    private Panel CreateTextureCard(MaterialFile file, string materialName)
    {
        var card = new Panel
        {
            Size = new Size(150, 195),
            BackColor = Color.FromArgb(45, 45, 61),
            Margin = new Padding(5),
            Cursor = Cursors.Hand
        };

        // Material name label at top
        var materialLabel = new Label
        {
            Text = materialName.Length > 20 ? materialName[..17] + "..." : materialName,
            Location = new Point(5, 3),
            Size = new Size(140, 16),
            ForeColor = Color.FromArgb(150, 180, 255),
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.TopCenter
        };

        var pictureBox = new PictureBox
        {
            Size = new Size(140, 120),
            Location = new Point(5, 20),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(60, 60, 80),
            Cursor = Cursors.Hand
        };

        try
        {
            var filePath = file.File!.FullName;
            var actualFileName = LinkFileResolver.GetActualFileName(filePath);

            // Use LinkFileResolver to get file stream (handles .link files automatically)
            using var stream = LinkFileResolver.GetFileStream(filePath);
            if (stream != null)
                // Detect actual content type from stream header, not file extension
                // (LinkFileResolver may have found .dds when material referenced .png)
                pictureBox.Image = LoadImageFromStream(stream);
            else
                pictureBox.BackColor = Color.DarkGray;
        }
        catch
        {
            pictureBox.BackColor = Color.DarkGray;
        }

        // Use actual file name (without .link) for display
        var displayFileName = LinkFileResolver.GetActualFileName(file.File!.Name);

        var label = new Label
        {
            Text = $"{file.MapType}\n{displayFileName}",
            Location = new Point(5, 143),
            Size = new Size(140, 45),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8),
            TextAlign = ContentAlignment.TopCenter
        };

        // Add tooltip with full info
        var tooltip = new ToolTip();
        var tooltipText = $"Material: {materialName}\nMap Type: {file.MapType}\nFile: {displayFileName}";
        tooltip.SetToolTip(card, tooltipText);
        tooltip.SetToolTip(pictureBox, tooltipText);
        tooltip.SetToolTip(label, tooltipText);
        tooltip.SetToolTip(materialLabel, tooltipText);

        card.Controls.Add(materialLabel);
        card.Controls.Add(pictureBox);
        card.Controls.Add(label);

        return card;
    }

    /// <summary>
    ///     Loads an image from a stream, detecting the format from the stream content.
    ///     Handles DDS files (via Pfim) and standard image formats (PNG, JPG, etc.).
    /// </summary>
    private static Bitmap? LoadImageFromStream(Stream stream)
    {
        // Check if stream is a DDS file by reading magic bytes
        if (IsDdsStream(stream)) return LoadDdsAsBitmap(stream);

        // Try loading as standard image format
        try
        {
            stream.Position = 0;
            return new Bitmap(stream);
        }
        catch
        {
            // If standard loading fails, try Pfim as last resort (handles more formats)
            try
            {
                stream.Position = 0;
                return LoadDdsAsBitmap(stream);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    ///     Checks if a stream contains DDS data by examining the magic bytes.
    ///     DDS files start with "DDS " (0x44 0x44 0x53 0x20).
    /// </summary>
    private static bool IsDdsStream(Stream stream)
    {
        if (stream.Length < 4)
            return false;

        var originalPosition = stream.Position;
        stream.Position = 0;

        Span<byte> magic = stackalloc byte[4];
        var bytesRead = stream.Read(magic);

        stream.Position = originalPosition;

        // DDS magic: "DDS " = 0x44, 0x44, 0x53, 0x20
        return bytesRead == 4 &&
               magic[0] == 0x44 &&
               magic[1] == 0x44 &&
               magic[2] == 0x53 &&
               magic[3] == 0x20;
    }

    /// <summary>
    ///     Loads a DDS file from a stream and converts it to a Bitmap for display.
    /// </summary>
    private static Bitmap? LoadDdsAsBitmap(Stream stream)
    {
        try
        {
            using var image = Pfimage.FromStream(stream);

            var format = image.Format switch
            {
                ImageFormat.Rgba32 => PixelFormats.Bgra32,
                ImageFormat.Rgb24 => PixelFormats.Bgr24,
                ImageFormat.Rgb8 => PixelFormats.Gray8,
                _ => PixelFormats.Bgra32
            };

            var handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
            try
            {
                var data = Marshal.UnsafeAddrOfPinnedArrayElement(image.Data, 0);
                var bitmapSource = BitmapSource.Create(
                    image.Width, image.Height,
                    96, 96,
                    format, null,
                    data, image.Data.Length, image.Stride);

                // Convert to PNG stream, then to GDI+ Bitmap
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                ms.Position = 0;

                return new Bitmap(ms);
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return null;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _viewportControl?.Dispose();
        base.OnFormClosing(e);
    }
}