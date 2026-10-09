// QA-5 D29: the JSON Lines format of the input recording. Pure (no UnityEngine) so the server test project can link it
// like the other ClientCopies; in Unity it exists only in the Editor and Development Builds (see QaProtocol.cs).
#if UNITY_EDITOR || DEVELOPMENT_BUILD || !UNITY_5_3_OR_NEWER
#nullable disable
using System.Text;

namespace ProjectH.Client.Qa
{
    // Line 1:  {"type":"header","version":1,"simHz":30,"devPlayerId":"p1"}
    // Then one line per simulation step, oldest first:
    //          {"t":0.0333,"moveX":0,"moveY":1,"yaw":90.5,"buttons":1,"aimYaw":90.5,"aimPitch":-2.25}
    // t: seconds of the step on the recording's own clock = (index of the step in the file, from 0) / simHz. Steps exist
    // only while the local player is spawned (prediction running), so time spent before the spawn or between a
    // disconnect and the next spawn is not in the file: t keeps counting from where it stopped. buttons: the
    // InputButtons bits as a number. Angles are degrees. Numbers are invariant fixed point (t 5 decimals, the rest 4).
    public static class QaInputRecordFormat
    {
        public const int Version = 1;
        // D29: at most 30 minutes at 30 Hz, counted in input lines (the header is not counted).
        public const int MaxInputLines = 54000;

        // 기능: 녹화 파일의 첫 줄(header: version, simHz, devPlayerId)을 JSON Lines 한 줄로 쓴다.
        // 입력: sb - 이어 쓸 StringBuilder, simHz - 시뮬레이션 Hz, devPlayerId - 녹화한 플레이어 이름.
        // 출력: 반환값 없음. sb 끝에 header 객체와 줄바꿈이 붙는다.
        public static void AppendHeader(StringBuilder sb, int simHz, string devPlayerId)
        {
            sb.Append("{\"type\":\"header\",\"version\":");
            QaJsonWriter.AppendLong(sb, Version);
            sb.Append(",\"simHz\":");
            QaJsonWriter.AppendLong(sb, simHz);
            sb.Append(",\"devPlayerId\":");
            QaJsonWriter.AppendString(sb, devPlayerId);
            sb.Append("}\n");
        }

        // 기능: 파일 안 몇 번째 단계인지로 녹화 시계의 초를 구한다.
        // 입력: stepIndex - 0부터 세는 단계 번호, simHz - 시뮬레이션 Hz.
        // 출력: stepIndex / simHz 초. simHz가 0 이하면 0.
        public static double StepTime(long stepIndex, int simHz) => simHz > 0 ? (double)stepIndex / simHz : 0.0;

        // 기능: 시뮬레이션 단계 하나의 입력을 JSON Lines 한 줄로 쓴다(t 5자리, 나머지 4자리 고정소수).
        // 입력: sb - 이어 쓸 StringBuilder, t - 녹화 시계 초, moveX·moveY - 이동 축, yaw - 이동 Yaw(도), buttons - InputButtons 비트,
        //   aimYaw·aimPitch - 조준 각(도).
        // 출력: 반환값 없음. sb 끝에 입력 객체와 줄바꿈이 붙는다.
        public static void AppendInput(StringBuilder sb, double t, float moveX, float moveY, float yaw, int buttons, float aimYaw,
            float aimPitch)
        {
            sb.Append("{\"t\":");
            QaJsonWriter.AppendFixed(sb, t, 5);
            sb.Append(",\"moveX\":");
            QaJsonWriter.AppendFixed(sb, moveX, 4);
            sb.Append(",\"moveY\":");
            QaJsonWriter.AppendFixed(sb, moveY, 4);
            sb.Append(",\"yaw\":");
            QaJsonWriter.AppendFixed(sb, yaw, 4);
            sb.Append(",\"buttons\":");
            QaJsonWriter.AppendLong(sb, buttons);
            sb.Append(",\"aimYaw\":");
            QaJsonWriter.AppendFixed(sb, aimYaw, 4);
            sb.Append(",\"aimPitch\":");
            QaJsonWriter.AppendFixed(sb, aimPitch, 4);
            sb.Append("}\n");
        }
    }
}
#endif
