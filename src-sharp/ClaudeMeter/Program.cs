using System.Windows;

namespace ClaudeMeter;

static class Program
{
    [STAThread]
    static int Main()
    {
        // Single instance: a second launch bows out quietly.
        using var mutex = new Mutex(initiallyOwned: true, "ClaudeMeterSingleInstance", out var created);
        if (!created) return 0;

        System.Windows.Forms.Application.EnableVisualStyles();  // tray / context menus
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _ = new AppController(app);  // kept alive by its event subscriptions
        return app.Run();
    }
}
