// QA-4 D26-D28: the pure parts of the QA command receiver (launch options, routes, the request body parser, the shot
// name check and the JSON writer). No UnityEngine, so the server test project can link this file like the other
// ClientCopies. In Unity the file exists only in the Editor and Development Builds (UNITY_5_3_OR_NEWER is defined by
// every Unity compile and by no .NET build, so the test project always sees it).
#if UNITY_EDITOR || DEVELOPMENT_BUILD || !UNITY_5_3_OR_NEWER
#nullable disable
using System;
using System.Globalization;
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
        Input,        // POST /qa/input {key|button|lookX,lookY, ...}
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

        // 기능: HTTP 메서드와 경로를 QA 명령 경로로 바꾼다. 경로는 대소문자를 구분하고 끝의 '/' 하나는 허용한다.
        // 입력: method - HTTP 메서드, path - URL의 절대 경로.
        // 출력: 알려진 경로와 맞는 메서드면 그 QaRoute, 경로는 맞고 메서드가 다르면 MethodNotAllowed, 그 밖에는 NotFound.
        public static QaRoute Resolve(string method, string path)
        {
            if (path == null) return QaRoute.NotFound;
            if (path.Length > 1 && path[path.Length - 1] == '/') path = path.Substring(0, path.Length - 1);
            switch (path)
            {
                case "/qa/status": return method == "GET" ? QaRoute.Status : QaRoute.MethodNotAllowed;
                case "/qa/screenshot": return method == "POST" ? QaRoute.Screenshot : QaRoute.MethodNotAllowed;
                case "/qa/ui": return method == "POST" ? QaRoute.Ui : QaRoute.MethodNotAllowed;
                case "/qa/input": return method == "POST" ? QaRoute.Input : QaRoute.MethodNotAllowed;
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

    public enum QaInputKind : byte
    {
        None,
        Key,      // {"key"}: a keyboard key from QaInput.KeyNames
        Button,   // {"button"}: left or right mouse button
        Look,     // {"lookX","lookY"}: mouse delta spread over "ms"
        ReleaseAll,   // {"releaseAll":true}: let go of every held key and button and stop every look
    }

    public enum QaInputAction : byte
    {
        Press,   // no holdMs and no action: down now, up on the next frame
        Hold,    // holdMs: down now, up after holdMs
        Down,    // action "down": down until "up" (or MaxHoldMs)
        Up,      // action "up": up now
    }

    // One parsed POST /qa/input body. Code is the index into QaInput.KeyNames (Key) or 0 = left, 1 = right (Button).
    public readonly struct QaInputRequest
    {
        public QaInputRequest(QaInputKind kind, QaInputAction action, int code, int holdMs, double lookX, double lookY, int ms)
        {
            Kind = kind;
            Action = action;
            Code = code;
            HoldMs = holdMs;
            LookX = lookX;
            LookY = lookY;
            Ms = ms;
        }

        public QaInputKind Kind { get; }
        public QaInputAction Action { get; }
        public int Code { get; }
        public int HoldMs { get; }
        public double LookX { get; }
        public double LookY { get; }
        public int Ms { get; }
    }

    // The pure half of POST /qa/input: which keys exist, the limits, the body parser and the "applied" text. The receiver
    // maps a key index to an Input System Key in the same order as KeyNames.
    public static class QaInput
    {
        public const int MaxHoldMs = 10000;   // also how long an explicit "down" stays down without an "up"
        public const int MaxLookMs = 5000;
        public const double MaxLook = 20000;

        // Input System key names, exactly as the request writes them. The receiver's key table follows this order.
        public static readonly string[] KeyNames =
        {
            "w", "a", "s", "d", "space", "leftShift", "leftCtrl", "c", "q", "f", "z", "x", "v", "b", "t", "r", "e", "g",
            "1", "2", "3", "4", "5", "escape", "f1",
        };

        // Every field a body may carry; any other field is a 400, so a typo ("holdms") is not silently a press.
        private static readonly string[] Fields = { "key", "button", "action", "holdMs", "lookX", "lookY", "ms", "releaseAll" };
        private static readonly string[] ReleaseAllFields = { "releaseAll" };

        // 기능: 키 이름을 KeyNames의 번호로 바꾼다(대소문자 구분).
        // 입력: name - 요청의 키 이름.
        // 출력: 허용된 키면 KeyNames의 번호, 아니면 -1.
        public static int KeyIndex(string name)
        {
            if (name == null) return -1;
            for (int i = 0; i < KeyNames.Length; i++)
            {
                if (KeyNames[i] == name) return i;
            }
            return -1;
        }

        // 기능: POST /qa/input Body를 검사해 입력 하나로 바꾼다. {"releaseAll":true}는 다른 필드 없이 혼자 온다. 그 밖에는
        //       key·button·look 중 정확히 하나, holdMs와 action은 함께 쓸 수 없고 key·button에만, ms는 look에만 쓴다.
        //       holdMs(1–10000)와 ms(0–5000)는 정수, lookX·lookY는 절댓값 20000 이하.
        // 입력: body - 요청 Body(크기는 호출자가 16 KB로 제한한다).
        // 출력: 올바르면 true와 입력, 아니면 false와 400 응답에 넣을 오류 문구.
        public static bool TryParse(string body, out QaInputRequest request, out string error)
        {
            request = default;
            if (!QaJsonReader.HasOnlyKeys(body, Fields))
            {
                error = "expected one flat JSON object with only key, button, action, holdMs, lookX, lookY, ms, releaseAll";
                return false;
            }
            QaJsonResult releaseResult = QaJsonReader.TryGetBool(body, "releaseAll", out bool releaseAll);
            if (releaseResult != QaJsonResult.Missing)
            {
                if (releaseResult != QaJsonResult.Ok || !releaseAll || !QaJsonReader.HasOnlyKeys(body, ReleaseAllFields))
                {
                    error = "releaseAll must be true and come alone";
                    return false;
                }
                request = new QaInputRequest(QaInputKind.ReleaseAll, QaInputAction.Press, 0, 0, 0, 0, 0);
                error = null;
                return true;
            }
            QaJsonResult keyResult = QaJsonReader.TryGetString(body, "key", out string key);
            QaJsonResult buttonResult = QaJsonReader.TryGetString(body, "button", out string button);
            QaJsonResult actionResult = QaJsonReader.TryGetString(body, "action", out string actionText);
            QaJsonResult holdResult = QaJsonReader.TryGetNumber(body, "holdMs", out double hold);
            QaJsonResult xResult = QaJsonReader.TryGetNumber(body, "lookX", out double lookX);
            QaJsonResult yResult = QaJsonReader.TryGetNumber(body, "lookY", out double lookY);
            QaJsonResult msResult = QaJsonReader.TryGetNumber(body, "ms", out double ms);
            if (keyResult == QaJsonResult.Invalid || buttonResult == QaJsonResult.Invalid || actionResult == QaJsonResult.Invalid ||
                holdResult == QaJsonResult.Invalid || xResult == QaJsonResult.Invalid || yResult == QaJsonResult.Invalid ||
                msResult == QaJsonResult.Invalid)
            {
                error = "key, button and action must be strings; holdMs, lookX, lookY and ms must be numbers";
                return false;
            }
            bool hasKey = keyResult == QaJsonResult.Ok;
            bool hasButton = buttonResult == QaJsonResult.Ok;
            bool hasLook = xResult == QaJsonResult.Ok || yResult == QaJsonResult.Ok;
            if ((hasKey ? 1 : 0) + (hasButton ? 1 : 0) + (hasLook ? 1 : 0) != 1)
            {
                error = "expected exactly one of key, button or lookX/lookY";
                return false;
            }
            bool hasHold = holdResult == QaJsonResult.Ok;
            bool hasAction = actionResult == QaJsonResult.Ok;
            bool hasMs = msResult == QaJsonResult.Ok;

            if (hasLook)
            {
                if (hasHold || hasAction)
                {
                    error = "holdMs and action do not apply to lookX/lookY";
                    return false;
                }
                if (xResult != QaJsonResult.Ok) lookX = 0;
                if (yResult != QaJsonResult.Ok) lookY = 0;
                if (Math.Abs(lookX) > MaxLook || Math.Abs(lookY) > MaxLook)
                {
                    error = "lookX and lookY must be within -20000..20000";
                    return false;
                }
                int spread = 0;
                if (hasMs && !TryWholeNumber(ms, 0, MaxLookMs, out spread))
                {
                    error = "ms must be a whole number 0..5000";
                    return false;
                }
                request = new QaInputRequest(QaInputKind.Look, QaInputAction.Press, 0, 0, lookX, lookY, spread);
                error = null;
                return true;
            }

            if (hasMs)
            {
                error = "ms applies to lookX/lookY only (use holdMs)";
                return false;
            }
            if (hasHold && hasAction)
            {
                error = "use holdMs or action, not both";
                return false;
            }
            int code;
            if (hasKey)
            {
                code = KeyIndex(key);
                if (code < 0)
                {
                    error = "unknown key (allowed: w a s d space leftShift leftCtrl c q f z x v b t r e g 1 2 3 4 5 escape f1)";
                    return false;
                }
            }
            else
            {
                code = button == "left" ? 0 : button == "right" ? 1 : -1;
                if (code < 0)
                {
                    error = "button must be left or right";
                    return false;
                }
            }
            QaInputAction action = QaInputAction.Press;
            int holdMs = 0;
            if (hasHold)
            {
                if (!TryWholeNumber(hold, 1, MaxHoldMs, out holdMs))
                {
                    error = "holdMs must be a whole number 1..10000";
                    return false;
                }
                action = QaInputAction.Hold;
            }
            else if (hasAction)
            {
                if (actionText == "down") action = QaInputAction.Down;
                else if (actionText == "up") action = QaInputAction.Up;
                else
                {
                    error = "action must be down or up";
                    return false;
                }
            }
            request = new QaInputRequest(hasKey ? QaInputKind.Key : QaInputKind.Button, action, code, holdMs, 0, 0, 0);
            error = null;
            return true;
        }

        // 기능: 숫자가 min..max 범위의 정수인지 확인한다.
        // 입력: value - JSON 숫자, min·max - 허용 범위(양 끝 포함).
        // 출력: 범위 안의 정수면 true와 int 값, 아니면 false.
        private static bool TryWholeNumber(double value, int min, int max, out int whole)
        {
            whole = 0;
            if (value < min || value > max || Math.Floor(value) != value) return false;
            whole = (int)value;
            return true;
        }

        // 기능: 적용한 입력을 사람이 읽는 짧은 문구로 쓴다(예: "press q", "hold w 1500ms", "down left", "look 120,-30 300ms",
        //       "releaseAll").
        //       키 이름은 영문·숫자뿐이라 JSON 문자열 안에 그대로 써도 된다.
        // 입력: sb - 이어 쓸 StringBuilder, request - TryParse가 만든 입력.
        // 출력: 반환값 없음. sb 끝에 문구가 붙는다.
        public static void AppendDescription(StringBuilder sb, in QaInputRequest request)
        {
            if (request.Kind == QaInputKind.ReleaseAll)
            {
                sb.Append("releaseAll");
                return;
            }
            if (request.Kind == QaInputKind.Look)
            {
                sb.Append("look ");
                QaJsonWriter.AppendFixed(sb, request.LookX, 3);
                sb.Append(',');
                QaJsonWriter.AppendFixed(sb, request.LookY, 3);
                sb.Append(' ');
                QaJsonWriter.AppendLong(sb, request.Ms);
                sb.Append("ms");
                return;
            }
            switch (request.Action)
            {
                case QaInputAction.Hold: sb.Append("hold "); break;
                case QaInputAction.Down: sb.Append("down "); break;
                case QaInputAction.Up: sb.Append("up "); break;
                default: sb.Append("press "); break;
            }
            sb.Append(request.Kind == QaInputKind.Key ? KeyNames[request.Code] : request.Code == 0 ? "left" : "right");
            if (request.Action != QaInputAction.Hold) return;
            sb.Append(' ');
            QaJsonWriter.AppendLong(sb, request.HoldMs);
            sb.Append("ms");
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

        // 기능: 평평한 JSON 객체에서 key의 숫자 값을 읽는다(InvariantCulture). 무한대·NaN이 되는 값은 받지 않는다.
        // 입력: json - 요청 Body, key - 찾을 이름.
        // 출력: 숫자면 Ok와 값, key가 없으면 Missing, 객체가 아니거나 값이 숫자가 아니거나 key가 두 번 나오면 Invalid.
        public static QaJsonResult TryGetNumber(string json, string key, out double value)
        {
            value = 0;
            if (key == null) return QaJsonResult.Invalid;
            QaJsonResult result = Find(json, key, null, out int start, out int end, out bool isString);
            if (result != QaJsonResult.Ok) return result;
            if (isString) return QaJsonResult.Invalid;
            // Unity's Mono and .NET disagree on overflow (false vs. infinity); both end as Invalid here.
            if (!double.TryParse(json.Substring(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
                double.IsNaN(parsed) || double.IsInfinity(parsed))
                return QaJsonResult.Invalid;
            value = parsed;
            return QaJsonResult.Ok;
        }

        // 기능: 평평한 JSON 객체에서 key의 true/false 값을 읽는다.
        // 입력: json - 요청 Body, key - 찾을 이름.
        // 출력: true나 false면 Ok와 값, key가 없으면 Missing, 객체가 아니거나 값이 true/false가 아니거나 key가 두 번 나오면 Invalid.
        public static QaJsonResult TryGetBool(string json, string key, out bool value)
        {
            value = false;
            if (key == null) return QaJsonResult.Invalid;
            QaJsonResult result = Find(json, key, null, out int start, out int end, out bool isString);
            if (result != QaJsonResult.Ok) return result;
            if (isString) return QaJsonResult.Invalid;
            if (end - start == 4 && string.CompareOrdinal(json, start, "true", 0, 4) == 0)
            {
                value = true;
                return QaJsonResult.Ok;
            }
            if (end - start == 5 && string.CompareOrdinal(json, start, "false", 0, 5) == 0) return QaJsonResult.Ok;
            return QaJsonResult.Invalid;
        }

        // 기능: Body가 평평한 JSON 객체이고 허용된 이름만 담고 있는지 확인한다.
        // 입력: json - 요청 Body, allowed - 허용된 이름 목록(대소문자 구분).
        // 출력: 올바른 객체이고 모든 이름이 allowed 안에 있으면 true, 아니면 false.
        public static bool HasOnlyKeys(string json, string[] allowed)
        {
            return allowed != null && Find(json, null, allowed, out _, out _, out _) != QaJsonResult.Invalid;
        }

        // 기능: 평평한 JSON 객체를 한 번 훑어 key의 값이 있는 범위를 찾는다(TryGetString과 같은 문법).
        // 입력: json - 요청 Body, key - 찾을 이름(null이면 찾지 않고 문법만 본다), allowed - null이 아니면 허용된 이름 목록.
        // 출력: 찾으면 Ok와 값의 [start, end) 범위·문자열 여부, 없으면 Missing, 문법 오류·허용되지 않은 이름·key 중복이면 Invalid.
        private static QaJsonResult Find(string json, string key, string[] allowed, out int start, out int end, out bool isString)
        {
            start = -1;
            end = -1;
            isString = false;
            if (json == null) return QaJsonResult.Invalid;
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
                    if (allowed != null && Array.IndexOf(allowed, name) < 0) return QaJsonResult.Invalid;
                    SkipWhitespace(json, ref i);
                    if (i >= json.Length || json[i] != ':') return QaJsonResult.Invalid;
                    i++;
                    SkipWhitespace(json, ref i);
                    if (i >= json.Length) return QaJsonResult.Invalid;
                    bool match = key != null && name == key;
                    if (match && found) return QaJsonResult.Invalid;
                    int valueStart = i;
                    bool text = json[i] == '"';
                    if (text)
                    {
                        if (!ReadString(json, ref i, out _)) return QaJsonResult.Invalid;
                    }
                    else if (!SkipScalar(json, ref i))
                    {
                        return QaJsonResult.Invalid;
                    }
                    if (match)
                    {
                        found = true;
                        start = valueStart;
                        end = i;
                        isString = text;
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
            return found ? QaJsonResult.Ok : QaJsonResult.Missing;
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

        // 기능: GET /qa/status 응답 JSON을 쓴다.
        // 입력: sb - 이어 쓸 StringBuilder, devPlayerId - 마지막 접속의 id(null 가능), connected·joined - 접속·Join 여부,
        //       screen - 화면 이름, statsOpen·debugVisible - 통계 창·F1 줄, alive·health - 내 생존·체력, fps·frame - 프레임 값,
        //       tool - Weapon|Harvest|Build|none, preview - Valid|Invalid|NoResource|none, cursorLocked - 게임이 보는 커서 잠금
        //       (QA 가정 포함).
        // 출력: 반환값 없음. sb 끝에 상태 객체 하나가 붙는다.
        // statsOpen and debugVisible are additions to D28's field list (the QA tool can assert openStats/toggleDebug);
        // tool, preview and cursorLocked come with POST /qa/input (the QA tool waits on them after gameplay input).
        public static void AppendStatus(StringBuilder sb, string devPlayerId, bool connected, bool joined, string screen,
            bool statsOpen, bool debugVisible, bool alive, int health, double fps, long frame, string tool, string preview,
            bool cursorLocked)
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
            sb.Append(",\"tool\":");
            QaJsonWriter.AppendString(sb, tool);
            sb.Append(",\"preview\":");
            QaJsonWriter.AppendString(sb, preview);
            sb.Append(",\"cursorLocked\":");
            QaJsonWriter.AppendBool(sb, cursorLocked);
            sb.Append('}');
        }

        // 기능: POST /qa/input 성공 응답 JSON을 쓴다.
        // 입력: sb - 이어 쓸 StringBuilder, request - 적용한 입력.
        // 출력: 반환값 없음. sb 끝에 {"ok":true,"applied":"..."}가 붙는다.
        public static void AppendInput(StringBuilder sb, in QaInputRequest request)
        {
            sb.Append("{\"ok\":true,\"applied\":\"");
            QaInput.AppendDescription(sb, request);
            sb.Append("\"}");
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
