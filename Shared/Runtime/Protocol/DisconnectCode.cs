using System;

namespace ProjectH.Shared.Protocol
{
    // Phase 10 D1: why the server closed a connection. Sent as the single byte of LiteNetLib's disconnect data, so it
    // arrives with the disconnect itself (a separate packet could arrive after it or be lost). Keep values stable.
    public enum DisconnectCode : byte
    {
        None = 0,
        ServerShutdown = 1,
        Kicked = 2,         // too many invalid packets
        JoinTimeout = 3,    // connected but never sent a JoinMatchRequest
        InputTimeout = 4,   // joined but sent no PlayerInput for too long
        ServerError = 5,    // the match was reset after repeated tick failures (D6)
        Congested = 6,      // Phase 13 final review A4: its reliable queue stayed too long for too long (a link too slow)
    }

    // Pure helpers shared by the Unity client and the bots. No LiteNetLib types here: each caller maps its own
    // DisconnectReason to the two flags below, so neither has to link the other's sources.
    public static class DisconnectCodes
    {
        // Phase 10 D10, D11: attempts per disconnect. Attempt n starts ReconnectOffsetSeconds(n) after the drop was
        // detected (1, 3, 7 s: 1, 2 and 4 s apart), and each attempt gives up within the connect budget below, so the
        // last one has failed or connected by about 8.5 s, inside the server's default 10 s grace.
        public const int MaxReconnectAttempts = 3;

        // The connect budget of an automatic attempt (LiteNetLib NetManager.ReconnectDelay and MaxConnectAttempts set
        // by the caller before it connects). LiteNetLib resends the request every ReconnectDelay and gives up after
        // MaxConnectAttempts resends: (5 + 1) * 250 ms = 1.5 s, measured 1.53 s. Its defaults (500 ms x 10, about
        // 5.5 s) would make an attempt outlive its slot. A manual connect keeps the defaults.
        public const int ReconnectRequestIntervalMs = 250;
        public const int ReconnectRequestAttempts = 5;

        // Seconds from the drop to the start of attempt n (1-based): 2^n - 1. Callers measure from the drop, not from
        // the previous failure, so a slow failure cannot push the later attempts past the grace.
        public static float ReconnectOffsetSeconds(int attempt)
        {
            if (attempt < 1) attempt = 1;
            if (attempt > MaxReconnectAttempts) attempt = MaxReconnectAttempts;
            return (1 << attempt) - 1;
        }

        // The disconnect data of a remote close. Empty data or a value this build does not know is None, so an older
        // client reading a newer server's code never breaks.
        public static DisconnectCode Read(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return DisconnectCode.None;
            byte value = data[0];
            return value <= (byte)DisconnectCode.Congested ? (DisconnectCode)value : DisconnectCode.None;
        }

        // D10: may the client connect again on its own?
        //   remoteClose: the server closed the connection (LiteNetLib RemoteConnectionClose); code is what it sent.
        //   networkLoss: the connection was lost or could not be made (Timeout, ConnectionFailed, Host/NetworkUnreachable).
        // Only ServerError among the server's codes is worth a retry: a shutdown, a kick, a timeout or a congested link
        // would happen again. A local Disconnect() or a rejected connect is neither flag, so it never retries.
        // The caller decides when a cycle may start at all: a first, manual connect that fails is not retried.
        public static bool ShouldReconnect(bool remoteClose, DisconnectCode code, bool networkLoss)
        {
            if (remoteClose) return code == DisconnectCode.ServerError;
            return networkLoss;
        }
    }
}
