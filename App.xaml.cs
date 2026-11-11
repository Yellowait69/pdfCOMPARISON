using System;
using System.Linq;
using System.Windows;
using PDFComparison.ViewModels;

namespace PDFComparison;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args != null && e.Args.Any(a => string.Equals(a, "--source", StringComparison.OrdinalIgnoreCase)))
        {
            var vm = new MainViewModel();
            int exitCode = await vm.RunHeadlessCliAsync(e.Args);
            Shutdown(exitCode);
        }
    }
}
