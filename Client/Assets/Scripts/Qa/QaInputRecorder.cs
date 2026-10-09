// QA-5 D29: -qaRecord <path> writes every simulation step's InputCommand as JSON Lines (QaInputRecordFormat). The whole
// file exists only in the Editor and Development Builds.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Text;
using ProjectH.Client.Game;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Qa
{
    // Owned by GameClient: created in its Awake (only with -qaRecord), fed from its LateUpdate on the main thread, disposed
    // in its OnDestroy (which also runs on quit). Records the inputs exactly as they are sent: after SetAim gave the
    // frame's steps their aim, before the packet is built. Build placement requests (SendBuild) are not InputCommands
    // and are not in the timeline.
    // Writes go through one buffered FileStream (synchronous, no Task); the file is flushed every FlushEveryLines lines
    // so a crash loses little, and closed on Dispose. The line text is built in one reused StringBuilder and copied into
    // one reused char buffer, so a step allocates nothing.
    public sealed class QaInputRecorder : IDisposable
    {
        private const int FlushEveryLines = 300;
        private const int FileBufferBytes = 64 * 1024;

        private readonly string _path;
        private readonly StreamWriter _writer;
        private readonly StringBuilder _line = new StringBuilder(256);
        private char[] _chars = new char[256];
        private bool _headerWritten;
        private int _simHz;
        private long _lines;
        private int _sinceFlush;
        private bool _capReported;

        // 기능: 열린 Writer로 녹화기를 만든다(FromLaunch만 부른다).
        // 입력: path - 녹화 파일의 절대 경로(로그용), writer - 그 파일에 연 StreamWriter(소유권이 넘어온다).
        // 출력: header를 아직 쓰지 않은 QaInputRecorder.
        private QaInputRecorder(string path, StreamWriter writer)
        {
            _path = path;
            _writer = writer;
        }

        // 기능: -qaRecord(또는 Editor 환경 변수) 경로의 파일을 새로 만들어(폴더 포함, BOM 없는 UTF-8) 녹화기를 연다.
        // 입력: 없음(QaLaunchOptions.FromEnvironment를 읽는다).
        // 출력: 녹화기. -qaRecord가 없으면 null, 파일을 만들지 못하면 경고 한 줄을 남기고 null.
        // Null when -qaRecord is not given or the file cannot be created (one warning; recording stays off).
        public static QaInputRecorder FromLaunch()
        {
            string path = QaLaunchOptions.FromEnvironment().RecordPath;
            if (path == null) return null;
            FileStream stream = null;
            try
            {
                path = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, FileBufferBytes);
                // UTF-8 without BOM: the header line must parse as JSON on its own.
                var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024);
                Debug.Log($"[QA] Recording inputs to {path}");
                return new QaInputRecorder(path, writer);
            }
            catch (Exception e)
            {
                stream?.Dispose();
                Debug.LogWarning($"[QA] Input recording disabled: cannot open '{path}': {e.Message}");
                return null;
            }
        }

        // 기능: 이번 프레임의 최신 steps개 입력을 오래된 것부터 한 줄씩 쓴다. 첫 호출에 header를 먼저 쓰고, FlushEveryLines마다 Flush한다.
        // 입력: predictor - 입력 이력을 가진 예측기(null이면 무시), steps - 이번 프레임에 진행한 단계 수(HistorySize·LastSeq로 잘린다),
        //   simHz - 시뮬레이션 Hz(header용), devPlayerId - 플레이어 이름(header용).
        // 출력: 반환값 없음. 줄 수가 MaxInputLines에 닿으면 경고 한 번 뒤 더 쓰지 않고, IOException이 나면 녹화를 멈춘다.
        // The newest `steps` inputs of this frame, oldest first. simHz and devPlayerId go into the header, written with
        // the first step (they are known only after the join).
        public void Record(LocalPlayerPredictor predictor, int steps, int simHz, string devPlayerId)
        {
            if (predictor == null || steps <= 0 || _writer == null) return;
            if (steps > LocalPlayerPredictor.HistorySize) steps = LocalPlayerPredictor.HistorySize;
            if (steps > predictor.LastSeq) steps = (int)predictor.LastSeq;
            try
            {
                if (!_headerWritten)
                {
                    _simHz = simHz;
                    _line.Clear();
                    QaInputRecordFormat.AppendHeader(_line, simHz, devPlayerId);
                    WriteLine();
                    _headerWritten = true;
                }
                for (int i = steps - 1; i >= 0; i--)
                {
                    if (_lines >= QaInputRecordFormat.MaxInputLines)
                    {
                        if (!_capReported)
                        {
                            _capReported = true;
                            _writer.Flush();
                            Debug.LogWarning($"[QA] Input recording stopped at {QaInputRecordFormat.MaxInputLines} lines: {_path}");
                        }
                        return;
                    }
                    InputCommand command = predictor.InputAt(predictor.LastSeq - (uint)i);
                    _line.Clear();
                    QaInputRecordFormat.AppendInput(_line, QaInputRecordFormat.StepTime(_lines, _simHz), command.MoveX, command.MoveY,
                        command.Yaw, (int)command.Buttons, command.AimYaw, command.AimPitch);
                    WriteLine();
                    _lines++;
                    if (++_sinceFlush >= FlushEveryLines)
                    {
                        _sinceFlush = 0;
                        _writer.Flush();
                    }
                }
            }
            catch (IOException e)
            {
                // A full disk or a removed drive: stop at once instead of throwing every frame.
                Debug.LogWarning($"[QA] Input recording stopped: {e.Message}");
                _lines = QaInputRecordFormat.MaxInputLines;
                _capReported = true;
            }
        }

        // 기능: _line의 내용을 재사용 char 버퍼로 복사해 Writer에 쓴다(단계마다 할당 없음).
        // 입력: 없음(_line을 읽는다).
        // 출력: 반환값 없음. 버퍼가 모자라면 두 배로 키운다.
        private void WriteLine()
        {
            int length = _line.Length;
            if (_chars.Length < length) _chars = new char[Math.Max(length, _chars.Length * 2)];
            _line.CopyTo(0, _chars, 0, length);
            _writer.Write(_chars, 0, length);
        }

        // 기능: 녹화 파일을 Flush하고 닫는다.
        // 입력: 없음.
        // 출력: 반환값 없음. 닫기에 실패하면 경고 한 줄만 남긴다.
        public void Dispose()
        {
            try
            {
                _writer?.Dispose();   // flushes and closes the file
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[QA] Input recording close failed: {e.Message}");
            }
        }
    }
}
#endif
