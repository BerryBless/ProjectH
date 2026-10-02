// QA-4 D26-D28: the pure parts of the QA command receiver (launch options, routes, the request body parser, the shot
// name check and the JSON writer). No UnityEngine, so the server test project can link this file like the other
// ClientCopies. In Unity the file exists only in the Editor and Development Builds (UNITY_5_3_OR_NEWER is defined by
// every Unity compile and by no .NET build, so the test project always sees it).
#if UNITY_EDITOR || DEVELOPMENT_BUILD || !UNITY_5_3_OR_NEWER
#nullable disable
using System;
using System.Text;

namespace ProjectH.Client.Qa
{
    // -qaPort N, -qaShotDir <dir>, -qaRecord <path>. Port 0 = the receiver is off; null strings = not given.
    public readonly struct QaLaunchOptions
    {
        public const string PortEnv = "PROJECTH_QA_PORT";
        public const string ShotDirEnv = "PROJECTH_QA_SHOT_DIR";
        public const string RecordEnv = "PROJECTH_QA_RECORD";

        public QaLaunchOptions(int port, string shotDir, string recordPath)
        {
            Port = port;
            ShotDir = shotDir;
            RecordPath = recordPath;
        }

        public int Port { get; }
        public string ShotDir { get; }
        public string RecordPath { get; }

        // A missing or invalid port (not 1..65535) leaves the receiver off. Empty paths count as not given.
        public static QaLaunchOptions Parse(string[] args)
        {
            int port = 0;
            string shotDir = null;
            string record = null;
            if (args == null) return default;
            for (int i = 0; i < args.Length; i++)
            {
                bool hasValue = i + 1 < args.Length;
                switch (args[i])
                {
                    case "-qaPort" when hasValue: port = ParsePort(args[++i]); break;
                    case "-qaShotDir" when hasValue: shotDir = NullIfEmpty(args[++i]); break;
                    case "-qaRecord" when hasValue: record = NullIfEmpty(args[++i]); break;
                }
            }
            return new QaLaunchOptions(port, shotDir, record);
        }

        // The process command line. Inside the Editor there is no command line per Play session, so the environment
        // variables above fill what the command line does not give (an Editor started with PROJECTH_QA_PORT set;
        // Multiplayer Play Mode clones inherit it, and only the first to bind the port gets the receiver).
        public static QaLaunchOptions FromEnvironment()
        {
            QaLaunchOptions options = Parse(Environment.GetCommandLineArgs());
#if UNITY_EDITOR
            int port = options.Port > 0 ? options.Port : ParsePort(Environment.GetEnvironmentVariable(PortEnv));
            string shotDir = options.ShotDir ?? NullIfEmpty(Environment.GetEnvironmentVariable(ShotDirEnv));
            string record = options.RecordPath ?? NullIfEmpty(Environment.GetEnvironmentVariable(RecordEnv));
            options = new QaLaunchOptions(port, shotDir, record);
#endif
            return options;
        }

        public static int ParsePort(string text)
        {
            return int.TryParse(text, out int port) && port >= 1 && port <= 65535 ? port : 0;
        }

        private static string NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public enum QaRoute : byte
    {
        NotFound,
        MethodNotAllowed,
        Status,       // GET  /qa/status
        Screenshot,   // POST /qa/screenshot {name}
        Ui,           // POST /qa/ui {command}
    }

    public enum QaUiCommand : byte
    {
        None,
        OpenMenu,
        CloseMenu,
        OpenStats,
        CloseStats,
        ToggleDebug,
    }

    public enum QaJsonResult : byte
    {
        Ok,
        Missing,   // valid object without that key
        Invalid,   // not a flat JSON object, or the key's value is not a string
    }

    public static class QaHttp
    {
        // D28: the shot file is <dir>/<name>.png, so the name can never leave the directory.
        public const int MaxShotNameLength = 64;

        // Exact, case-sensitive paths (one trailing slash tolerated).
        public static QaRoute Resolve(string method, string path)
        {
            if (path == null) return QaRoute.NotFound;
            if (path.Length > 1 && path[path.Length - 1] == '/') path = path.Substring(0, path.Length - 1);
            switch (path)
            {
                case "/qa/status": return method == "GET" ? QaRoute.Status : QaRoute.MethodNotAllowed;
                case "/qa/screenshot": return method == "POST" ? QaRoute.Screenshot : QaRoute.MethodNotAllowed;
                case "/qa/ui": return method == "POST" ? QaRoute.Ui : QaRoute.MethodNotAllowed;
                default: return QaRoute.NotFound;
            }
        }

        // [A-Za-z0-9_-]{1,64}
        public static bool IsValidShotName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxShotNameLength) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok) return false;
            }
            return !IsWindowsDeviceName(name);
        }

        // CON, PRN, AUX, NUL, COM1-9 and LPT1-9 (any case) open a device instead of a file on Windows, also with an
        // extension ("NUL.png"), so they are never shot names.
        public static bool IsWindowsDeviceName(string name)
        {
            if (name == null) return false;
            if (name.Length == 3)
            {
                return string.Equals(name, "CON", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "PRN", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "AUX", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "NUL", StringComparison.OrdinalIgnoreCase);
            }
            if (name.Length == 4 && name[3] >= '1' && name[3] <= '9')
            {
                return string.Compare(name, 0, "COM", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 ||
                       string.Compare(name, 0, "LPT", 0, 3, StringComparison.OrdinalIgnoreCase) == 0;
            }
            return false;
        }

        // POST bodies must be declared JSON. A browser page can send a "simple" cross-origin POST (text/plain, form) to
        // localhost without a preflight; requiring application/json makes such a request fail with 415 before it acts.
        public static bool IsJsonContentType(string contentType)
        {
            return contentType != null &&
                   contentType.TrimStart().StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
        }

        public static QaUiCommand ParseUiCommand(string command)
        {
            switch (command)
            {
                case "openMenu": return QaUiCommand.OpenMenu;
                case "closeMenu": return QaUiCommand.CloseMenu;
                case "openStats": return QaUiCommand.OpenStats;
                case "closeStats": return QaUiCommand.CloseStats;
                case "toggleDebug": return QaUiCommand.ToggleDebug;
                default: return QaUiCommand.None;
            }
        }

        public static string UiCommandName(QaUiCommand command)
        {
            switch (command)
            {
                case QaUiCommand.OpenMenu: return "openMenu";
                case QaUiCommand.CloseMenu: return "closeMenu";
                case QaUiCommand.OpenStats: return "openStats";
                case QaUiCommand.CloseStats: return "closeStats";
                case QaUiCommand.ToggleDebug: return "toggleDebug";
                default: return "none";
            }
        }
    }

    // A tiny reader for the request bodies: one flat JSON object whose values are strings, numbers, true, false or null.
    // Nested objects and arrays are rejected (no request needs them). The caller bounds the body size (16 KB), and the
    // reader only walks the string once, so a hostile body costs at most one linear pass.
    public static class QaJsonReader
    {
        public static QaJsonResult TryGetString(string json, string key, out string value)
        {
            value = null;
            if (json == null || key == null) return QaJsonResult.Invalid;
            int i = 0;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '{') return QaJsonResult.Invalid;
            i++;
            bool found = false;
            SkipWhitespace(json, ref i);
            if (i < json.Length && json[i] == '}')
            {
                i++;
            }
            else
            {
                while (true)
                {
                    SkipWhitespace(json, ref i);
                    if (!ReadString(json, ref i, out string name)) return QaJsonResult.Invalid;
                    SkipWhitespace(json, ref i);
                    if (i >= json.Length || json[i] != ':') return QaJsonResult.Invalid;
                    i++;
                    SkipWhitespace(json, ref i);
                    if (i >= json.Length) return QaJsonResult.Invalid;
                    bool match = name == key;
                    if (json[i] == '"')
                    {
                        if (!ReadString(json, ref i, out string text)) return QaJsonResult.Invalid;
                        if (match)
                        {
                            value = text;
                            found = true;
                        }
                    }
                    else
                    {
                        if (!SkipScalar(json, ref i)) return QaJsonResult.Invalid;
                        if (match) return QaJsonResult.Invalid;
                    }
                    SkipWhitespace(json, ref i);
                    if (i >= json.Length) return QaJsonResult.Invalid;
                    if (json[i] == ',')
                    {
                        i++;
                        continue;
                    }
                    if (json[i] != '}') return QaJsonResult.Invalid;
                    i++;
                    break;
                }
            }
            SkipWhitespace(json, ref i);
            if (i != json.Length) return QaJsonResult.Invalid;
            if (!found)
            {
                value = null;
                return QaJsonResult.Missing;
            }
            return QaJsonResult.Ok;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        // true, false, null or a number ([-+0-9.eE]+, not checked further: such values are never used).
        private static bool SkipScalar(string s, ref int i)
        {
            if (Literal(s, ref i, "true") || Literal(s, ref i, "false") || Literal(s, ref i, "null")) return true;
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            return i > start;
        }

        private static bool Literal(string s, ref int i, string literal)
        {
            if (string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0) return false;
            i += literal.Length;
            return true;
        }

        private static bool ReadString(string s, ref int i, out string text)
        {
            text = null;
            if (i >= s.Length || s[i] != '"') return false;
            i++;
            StringBuilder sb = null;
            int runStart = i;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"')
                {
                    if (sb == null)
                    {
                        text = s.Substring(runStart, i - runStart);
                    }
                    else
                    {
                        sb.Append(s, runStart, i - runStart);
                        text = sb.ToString();
                    }
                    i++;
                    return true;
                }
                if (c < 0x20) return false;
                if (c != '\\')
                {
                    i++;
                    continue;
                }
                if (sb == null) sb = new StringBuilder();
                sb.Append(s, runStart, i - runStart);
                i++;
                if (i >= s.Length) return false;
                char e = s[i];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 >= s.Length) return false;
                        int code = 0;
                        for (int k = 1; k <= 4; k++)
                        {
                            int h = HexValue(s[i + k]);
                            if (h < 0) return false;
                            code = code * 16 + h;
                        }
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: return false;
                }
                i++;
                runStart = i;
            }
            return false;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }

    // Appends JSON values to a reused StringBuilder without allocating (numbers are written digit by digit, in the
    // invariant format, whatever the machine's culture is).
    public static class QaJsonWriter
    {
        // Beyond this the fixed-point digits would not fit a long; such values are not meaningful game input anyway.
        private const double MaxMagnitude = 1e12;

        public static void AppendString(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u00");
                            sb.Append(HexDigit(c >> 4));
                            sb.Append(HexDigit(c & 0xF));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        public static void AppendBool(StringBuilder sb, bool value) => sb.Append(value ? "true" : "false");

        public static void AppendLong(StringBuilder sb, long value)
        {
            if (value < 0)
            {
                sb.Append('-');
                if (value == long.MinValue)
                {
                    sb.Append("9223372036854775808");
                    return;
                }
                value = -value;
            }
            long divisor = 1;
            while (value / divisor >= 10) divisor *= 10;
            for (; divisor > 0; divisor /= 10) sb.Append((char)('0' + (int)(value / divisor % 10)));
        }

        // Fixed point with at most `decimals` (0..9) digits after the point, trailing zeros dropped. NaN and infinities
        // are written as 0 so every line stays valid JSON.
        public static void AppendFixed(StringBuilder sb, double value, int decimals)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
            if (value > MaxMagnitude) value = MaxMagnitude;
            if (value < -MaxMagnitude) value = -MaxMagnitude;
            if (decimals < 0) decimals = 0;
            if (decimals > 9) decimals = 9;
            long scale = 1;
            for (int k = 0; k < decimals; k++) scale *= 10;
            long scaled = (long)Math.Round(Math.Abs(value) * scale, MidpointRounding.AwayFromZero);
            if (scaled == 0)
            {
                sb.Append('0');
                return;
            }
            if (value < 0) sb.Append('-');
            AppendLong(sb, scaled / scale);
            long fraction = scaled % scale;
            if (fraction == 0) return;
            int digits = decimals;
            while (fraction % 10 == 0)
            {
                fraction /= 10;
                digits--;
            }
            sb.Append('.');
            long divisor = 1;
            for (int k = 1; k < digits; k++) divisor *= 10;
            for (; divisor > 0; divisor /= 10) sb.Append((char)('0' + (int)(fraction / divisor % 10)));
        }

        private static char HexDigit(int v) => (char)(v < 10 ? '0' + v : 'a' + v - 10);
    }

    // D28 response bodies. Every body is one JSON object with "ok"; failures carry "error".
    public static class QaResponses
    {
        public static void AppendError(StringBuilder sb, string error)
        {
            sb.Append("{\"ok\":false,\"error\":");
            QaJsonWriter.AppendString(sb, error);
            sb.Append('}');
        }

        // statsOpen and debugVisible are additions to D28's field list (the QA tool can assert openStats/toggleDebug).
        public static void AppendStatus(StringBuilder sb, string devPlayerId, bool connected, bool joined, string screen,
            bool statsOpen, bool debugVisible, bool alive, int health, double fps, long frame)
        {
            sb.Append("{\"ok\":true,\"devPlayerId\":");
            QaJsonWriter.AppendString(sb, devPlayerId);
            sb.Append(",\"connected\":");
            QaJsonWriter.AppendBool(sb, connected);
            sb.Append(",\"joined\":");
            QaJsonWriter.AppendBool(sb, joined);
            sb.Append(",\"screen\":");
            QaJsonWriter.AppendString(sb, screen);
            sb.Append(",\"statsOpen\":");
            QaJsonWriter.AppendBool(sb, statsOpen);
            sb.Append(",\"debugVisible\":");
            QaJsonWriter.AppendBool(sb, debugVisible);
            sb.Append(",\"alive\":");
            QaJsonWriter.AppendBool(sb, alive);
            sb.Append(",\"health\":");
            QaJsonWriter.AppendLong(sb, health);
            sb.Append(",\"fps\":");
            QaJsonWriter.AppendFixed(sb, fps, 1);
            sb.Append(",\"frame\":");
            QaJsonWriter.AppendLong(sb, frame);
            sb.Append('}');
        }

        public static void AppendShot(StringBuilder sb, string path)
        {
            sb.Append("{\"ok\":true,\"path\":");
            QaJsonWriter.AppendString(sb, path);
            sb.Append('}');
        }

        // applied false (HTTP 409): the command does not apply to the current screen; nothing changed.
        public static void AppendUi(StringBuilder sb, bool applied, QaUiCommand command, string screen, bool statsOpen, bool debugVisible)
        {
            sb.Append("{\"ok\":");
            QaJsonWriter.AppendBool(sb, applied);
            if (!applied) sb.Append(",\"error\":\"command does not apply to the current screen\"");
            sb.Append(",\"command\":");
            QaJsonWriter.AppendString(sb, QaHttp.UiCommandName(command));
            sb.Append(",\"screen\":");
            QaJsonWriter.AppendString(sb, screen);
            sb.Append(",\"statsOpen\":");
            QaJsonWriter.AppendBool(sb, statsOpen);
            sb.Append(",\"debugVisible\":");
            QaJsonWriter.AppendBool(sb, debugVisible);
            sb.Append('}');
        }
    }
}
#endif
