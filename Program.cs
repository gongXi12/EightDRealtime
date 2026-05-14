using System.Windows.Forms;

namespace EightDRealtime;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--list-devices", StringComparison.OrdinalIgnoreCase))
        {
            DeviceDiagnostics.WriteDeviceReport();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
