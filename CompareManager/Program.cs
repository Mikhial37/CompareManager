using System;
using System.Windows.Forms;

namespace CompareManager
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.ThreadException += (s, e) => ShowError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.Run(new MainForm());
        }

        private static void ShowError(Exception ex) =>
            MessageBox.Show(ex?.Message ?? "An unknown error occurred.", "Compare Manager",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
