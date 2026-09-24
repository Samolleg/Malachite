using System.IO;
using System.Windows;

namespace QuestStudio;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--verify-editing") return EditingVerification.Run(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "--verify") return Verification.Run(args.Skip(1).ToArray());
            var app = new Application();
            var window = new MainWindow();
            if (args.Length == 1) window.LoadWorkspace(Workspace.Open(args[0]), false);
            return app.Run(window);
        }
        catch (Exception ex)
        {
            if (args.Length > 0 && args[0].StartsWith("--verify")) { File.WriteAllText(Path.Combine(args.Length > 1 ? args[1] : ".", "verification-error.txt"), ex.ToString()); return 1; }
            MessageBox.Show(ex.Message, "Малахит — ошибка открытия", MessageBoxButton.OK, MessageBoxImage.Error); return 1;
        }
    }
}
