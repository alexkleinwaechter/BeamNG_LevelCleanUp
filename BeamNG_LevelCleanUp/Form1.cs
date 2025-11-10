using BeamNG_LevelCleanUp.BlazorUI;
using BeamNG_LevelCleanUp.Logic;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace BeamNG_LevelCleanUp;

public partial class Form1 : Form
{
    public Form1()
    {
        InitializeComponent();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddWindowsFormsBlazorWebView();
        //serviceCollection.AddSingleton<WeatherForecastService>();

        // Code for MudBlazor
        serviceCollection.AddMudServices();

        blazorWebView1.HostPage = @"wwwroot\index.html";
#if DEBUG
        serviceCollection.AddBlazorWebViewDeveloperTools();
        serviceCollection.AddLogging();
#endif
        blazorWebView1.Services = serviceCollection.BuildServiceProvider();
        blazorWebView1.RootComponents.Add<App>("#app");
    }

    public Task InitializeAsync(CancellationToken token)
    {
        //Nur für splashscreen
        return Task.Delay(TimeSpan.FromSeconds(0));
    }

    public void Initialize()
    {
        //Nur für splashscreen
        //Thread.Sleep(TimeSpan.FromSeconds(5));
    }

    private void Form1_FormClosed(object sender, FormClosedEventArgs e)
    {
    }

    private void Form1_FormClosing(object sender, FormClosingEventArgs e)
    {
        if (!string.IsNullOrEmpty(ZipFileHandler.GetLastUnpackedPath()) ||
            !string.IsNullOrEmpty(ZipFileHandler.GetLastUnpackedCopyFromPath()))
        {
            if (MessageBox.Show("Should the Working Directory be cleaned from unpacked data?", "Cleanup",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                try
                {
                    ZipFileHandler.CleanUpWorkingDirectory();
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    if (MessageBox.Show(
                            $"Error while cleaning up working directory: {ex.Message}. Maybe you have an editor or terminal open in one of the directories. Do you want to close the tool anyway?",
                            "Error", MessageBoxButtons.YesNo, MessageBoxIcon.Error) ==
                        DialogResult.Yes) Environment.Exit(0);
                    e.Cancel = true;
                }
        }
        else
        {
            Environment.Exit(0);
        }
    }
}