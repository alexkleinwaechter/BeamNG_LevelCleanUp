using System.Windows;
using BeamNG_LevelCleanUp.Objects;
using HelixToolkit.Wpf.SharpDX;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
///     Partial class for lighting control functionality.
/// </summary>
public partial class HelixViewportControl
{
    /// <summary>
    ///     Ambient light slider value changed.
    /// </summary>
    private void SliderAmbient_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var txtValue = FindName("TxtAmbientValue") as System.Windows.Controls.TextBlock;
        if (txtValue == null) return;
        
        var value = (float)e.NewValue;
        TextureMapConfig.AmbientLightIntensity = value;
        txtValue.Text = value.ToString("F2");
        
        // Update ambient light in viewport
        UpdateViewportLighting();
    }

    /// <summary>
    ///     Directional light slider value changed.
    /// </summary>
    private void SliderDirectional_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var txtValue = FindName("TxtDirectionalValue") as System.Windows.Controls.TextBlock;
        if (txtValue == null) return;
        
        var value = (float)e.NewValue;
        TextureMapConfig.DirectionalLightIntensity = value;
        txtValue.Text = value.ToString("F2");
        
        // Update directional lights in viewport
        UpdateViewportLighting();
    }

    /// <summary>
    ///     Material diffuse slider value changed.
    /// </summary>
    private void SliderDiffuse_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var txtValue = FindName("TxtDiffuseValue") as System.Windows.Controls.TextBlock;
        if (txtValue == null) return;
        
        var value = (float)e.NewValue;
        TextureMapConfig.MaterialDiffuseIntensity = value;
        txtValue.Text = value.ToString("F2");
        
        // Note: Material changes require reload to take effect
    }

    /// <summary>
    ///     Material ambient slider value changed.
    /// </summary>
    private void SliderMaterialAmbient_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var txtValue = FindName("TxtMaterialAmbientValue") as System.Windows.Controls.TextBlock;
        if (txtValue == null) return;
        
        var value = (float)e.NewValue;
        TextureMapConfig.MaterialAmbientIntensity = value;
        txtValue.Text = value.ToString("F2");
        
        // Note: Material changes require reload to take effect
    }

    /// <summary>
    ///     Updates the viewport lighting based on current TextureMapConfig settings.
    /// </summary>
    private void UpdateViewportLighting()
    {
        // Find and update ambient light
        var ambientIntensity = TextureMapConfig.AmbientLightIntensity;
        var ambientColor = (byte)(ambientIntensity * 255);
        
        // Find and update directional lights
        var dirIntensity = TextureMapConfig.DirectionalLightIntensity;
        
        foreach (var item in Viewport.Items)
        {
            if (item is AmbientLight3D ambientLight)
            {
                ambientLight.Color = System.Windows.Media.Color.FromRgb(ambientColor, ambientColor, ambientColor);
            }
            else if (item is DirectionalLight3D dirLight)
            {
                // Scale the original light color by the intensity factor
                var baseIntensity = 0.6f; // Base intensity for directional lights
                var scaledIntensity = (byte)Math.Min(255, baseIntensity * dirIntensity * 255);
                dirLight.Color = System.Windows.Media.Color.FromRgb(scaledIntensity, scaledIntensity, scaledIntensity);
            }
        }
    }

    /// <summary>
    ///     Set low lighting preset.
    /// </summary>
    private void BtnLowLight_Click(object sender, RoutedEventArgs e)
    {
        TextureMapConfig.SetLowLighting();
        UpdateSlidersFromConfig();
        UpdateViewportLighting();
    }

    /// <summary>
    ///     Set medium lighting preset.
    /// </summary>
    private void BtnMediumLight_Click(object sender, RoutedEventArgs e)
    {
        TextureMapConfig.SetMediumLighting();
        UpdateSlidersFromConfig();
        UpdateViewportLighting();
    }

    /// <summary>
    ///     Set high lighting preset.
    /// </summary>
    private void BtnHighLight_Click(object sender, RoutedEventArgs e)
    {
        TextureMapConfig.SetHighLighting();
        UpdateSlidersFromConfig();
        UpdateViewportLighting();
    }

    /// <summary>
    ///     Reload the model with current lighting/material settings.
    /// </summary>
    private async void BtnReloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRequest != null)
        {
            SetStatus("Reloading model with new settings...");
            await LoadAsync(_lastRequest);
        }
        else
        {
            SetStatus("No model to reload");
        }
    }

    /// <summary>
    ///     Updates slider positions from TextureMapConfig values.
    /// </summary>
    private void UpdateSlidersFromConfig()
    {
        var sliderAmbient = FindName("SliderAmbient") as System.Windows.Controls.Slider;
        var sliderDirectional = FindName("SliderDirectional") as System.Windows.Controls.Slider;
        var sliderDiffuse = FindName("SliderDiffuse") as System.Windows.Controls.Slider;
        var sliderMaterialAmbient = FindName("SliderMaterialAmbient") as System.Windows.Controls.Slider;
        var txtAmbient = FindName("TxtAmbientValue") as System.Windows.Controls.TextBlock;
        var txtDirectional = FindName("TxtDirectionalValue") as System.Windows.Controls.TextBlock;
        var txtDiffuse = FindName("TxtDiffuseValue") as System.Windows.Controls.TextBlock;
        var txtMaterialAmbient = FindName("TxtMaterialAmbientValue") as System.Windows.Controls.TextBlock;

        if (sliderAmbient != null)
        {
            sliderAmbient.Value = TextureMapConfig.AmbientLightIntensity;
            if (txtAmbient != null) txtAmbient.Text = TextureMapConfig.AmbientLightIntensity.ToString("F2");
        }
        if (sliderDirectional != null)
        {
            sliderDirectional.Value = TextureMapConfig.DirectionalLightIntensity;
            if (txtDirectional != null) txtDirectional.Text = TextureMapConfig.DirectionalLightIntensity.ToString("F2");
        }
        if (sliderDiffuse != null)
        {
            sliderDiffuse.Value = TextureMapConfig.MaterialDiffuseIntensity;
            if (txtDiffuse != null) txtDiffuse.Text = TextureMapConfig.MaterialDiffuseIntensity.ToString("F2");
        }
        if (sliderMaterialAmbient != null)
        {
            sliderMaterialAmbient.Value = TextureMapConfig.MaterialAmbientIntensity;
            if (txtMaterialAmbient != null) txtMaterialAmbient.Text = TextureMapConfig.MaterialAmbientIntensity.ToString("F2");
        }
    }
}
