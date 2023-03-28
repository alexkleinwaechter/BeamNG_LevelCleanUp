using BeamNG_LevelCleanUp.BlazorUI;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace BeamNG_LevelCleanUp
{
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
    }
}
