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

        private QaInputRecorder(string path, StreamWriter writer)
        {
            _path = path;
            _writer = writer;
        }

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

        private void WriteLine()
        {
            int length = _line.Length;
            if (_chars.Length < length) _chars = new char[Math.Max(length, _chars.Length * 2)];
            _line.CopyTo(0, _chars, 0, length);
            _writer.Write(_chars, 0, length);
        }

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
