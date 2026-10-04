using System;

namespace ProjectH.Client.Bootstrap
{
    // Reads -host, -port, -devId and -autoConnect from the process command line (Standalone builds).
    public readonly struct LaunchArgs
    {
        // 기능: 접속 설정 값을 담은 LaunchArgs를 만든다.
        // 입력: host - 서버 주소, port - 서버 Port, devPlayerId - 개발용 플레이어 ID, autoConnect - 시작 시 자동 접속 여부.
        // 출력: 전달한 값으로 채워진 LaunchArgs.
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

        // 기능: 프로세스 Command Line에서 -host, -port, -devId, -autoConnect를 읽는다.
        // 입력: 없음.
        // 출력: 읽은 값의 LaunchArgs. 없는 값은 127.0.0.1, 7777, 자동 접속 끔이고, -devId가 없으면 임의의 "dev-xxxxxxxx" ID를 쓰며, 숫자가 아닌 -port는 무시한다.
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
