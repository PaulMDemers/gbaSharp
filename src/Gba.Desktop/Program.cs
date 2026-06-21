namespace Gba.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(ParseStartupOptions(args)));
    }

    private static DesktopStartupOptions ParseStartupOptions(string[] args)
    {
        string? startupRomPath = null;
        var controlServerEnabled = false;
        var controlPort = DesktopStartupOptions.DefaultControlPort;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--control-server", StringComparison.OrdinalIgnoreCase))
            {
                controlServerEnabled = true;
                continue;
            }

            if (string.Equals(arg, "--no-control-server", StringComparison.OrdinalIgnoreCase))
            {
                controlServerEnabled = false;
                continue;
            }

            if (string.Equals(arg, "--control-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                controlPort = ParsePort(args[++i]);
                continue;
            }

            const string portPrefix = "--control-port=";
            if (arg.StartsWith(portPrefix, StringComparison.OrdinalIgnoreCase))
            {
                controlPort = ParsePort(arg[portPrefix.Length..]);
                continue;
            }

            startupRomPath ??= arg;
        }

        return new DesktopStartupOptions(startupRomPath, controlServerEnabled, controlPort);
    }

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, out var port) || port is < 0 or > 65535)
        {
            throw new ArgumentException($"Invalid control server port '{value}'.");
        }

        return port;
    }
}
