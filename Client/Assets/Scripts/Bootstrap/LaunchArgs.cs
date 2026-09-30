using System;

namespace ProjectH.Client.Bootstrap
{
    // Reads -host, -port, -devId and -autoConnect from the process command line (Standalone builds).
    public readonly struct LaunchArgs
    {
        public LaunchArgs(string host, int port, string devPlayerId, bool autoConnect)
        {
            Host = host;
            Port = port;
            DevPlayerId = devPlayerId;
            AutoConnect = autoConnect;
        }

        public string Host { get; }
        public int Port { get; }
        public string DevPlayerId { get; }
        public bool AutoConnect { get; }

        public static LaunchArgs FromCommandLine()
        {
            string host = "127.0.0.1";
            int port = 7777;
            string devId = null;
            bool autoConnect = false;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                bool hasValue = i + 1 < args.Length;
                switch (args[i])
                {
                    case "-host" when hasValue: host = args[++i]; break;
                    case "-port" when hasValue: if (int.TryParse(args[++i], out int p)) port = p; break;
                    case "-devId" when hasValue: devId = args[++i]; break;
                    case "-autoConnect": autoConnect = true; break;
                }
            }

            if (string.IsNullOrEmpty(devId)) devId = "dev-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            return new LaunchArgs(host, port, devId, autoConnect);
        }
    }
}
