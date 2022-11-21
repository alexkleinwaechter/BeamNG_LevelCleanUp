using AutoUpdaterDotNET;
namespace BeamNG_LevelCleanUp
{
    /// <summary>
    /// Asynchronous with splash screen
    /// https://msdn.microsoft.com/en-us/magazine/mt620013
    /// </summary>
    class Program6
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            var cancelSource = new CancellationTokenSource();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            Program6 p = new Program6();
            p.ExitRequested += p_ExitRequested;

            Task programStart = p.StartAsync(cancelSource.Token);
            HandleExceptions(programStart);

            Application.Run();
        }

        private static async void HandleExceptions(Task task)
        {
            try
            {
                await Task.Yield(); //ensure this runs as a continuation
                await task;
            }
            catch (Exception ex)
            {
                //deal with exception, either with message box
                //or delegating to general exception handling logic you may have wired up 
                //e.g. to Application.ThreadException and AppDomain.UnhandledException
                MessageBox.Show(ex.ToString());

                Application.Exit();
            }
        }

        static void p_ExitRequested(object sender, EventArgs e)
        {
            Application.ExitThread();
        }

        private readonly Form1 m_mainForm;
        private Program6()
        {
            m_mainForm = new Form1();
            m_mainForm.FormClosed += m_mainForm_FormClosed;
        }

        public async Task StartAsync(CancellationToken token)
        {
            using (SplashScreen splashScreen = new SplashScreen())
            {
                //if user closes splash screen, quit
                //that would also be a good opportunity to set a cancellation token
                splashScreen.FormClosed += m_mainForm_FormClosed;
                splashScreen.Show();

                await m_mainForm.InitializeAsync(token);

                //this ensures the activation works,
                //so when the splash screen goes away, the main form is activated
                //setting the owner removes from alt-tab (depending on properties of the splash screen)...
                //...do that only once we are ready for the main form to show and take its place
                splashScreen.Owner = m_mainForm;
                m_mainForm.Show();

                splashScreen.FormClosed -= m_mainForm_FormClosed;
                splashScreen.Close();
                
                AutoUpdater.Start("https://raw.githubusercontent.com/alexkleinwaechter/BeamNG_LevelCleanUp/master/BeamNG_LevelCleanUp/AutoUpdater.xml");
            }
        }

        public event EventHandler<EventArgs> ExitRequested;
        void m_mainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            OnExitRequested(EventArgs.Empty);
        }

        protected virtual void OnExitRequested(EventArgs e)
        {
            if (ExitRequested != null)
                ExitRequested(this, e);
        }
    }
}