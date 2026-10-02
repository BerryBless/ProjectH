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

        public static double StepTime(long stepIndex, int simHz) => simHz > 0 ? (double)stepIndex / simHz : 0.0;

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
