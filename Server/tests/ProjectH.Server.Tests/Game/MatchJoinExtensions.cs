using System.Security.Cryptography;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;

// In the ProjectH.Server namespace, which encloses every test namespace, so the tests' match.TryJoin(peer, name) calls
// (written before review fix B4) find it without a using.
namespace ProjectH.Server;

// Review fix B4: Match.TryJoin now takes the connection's keys and resume claim. These tests join the way an honest client
// does: a fresh session key per connection, and, when the name has a graced character, the resume proof made with that
// character's resume key (the key the client kept from its last join). Tests about proofs call the full TryJoin.
public static class MatchJoinExtensions
{
    // 기능: 정직한 Client처럼 Join한다(새 세션 키, 같은 이름의 유예 캐릭터가 있으면 그 Resume 키로 증명).
    // 입력: match - 경기, peerId - 연결 id, devPlayerId - 이름.
    // 출력: Match.TryJoin의 결과.
    public static JoinResult TryJoin(this Match match, int peerId, string devPlayerId)
    {
        byte[] session = RandomNumberGenerator.GetBytes(ProtocolLimits.SessionKeyBytes);
        byte[] resumeKey = SessionAuth.DeriveResumeKey(session);
        PlayerEntity? graced = match.GracedNamed(devPlayerId);
        byte[]? proof = null;
        uint nonce = 0;
        if (graced?.ResumeKey != null)
        {
            nonce = graced.LastResumeNonce + 1;
            proof = new byte[ProtocolLimits.ResumeProofBytes];
            SessionAuth.ComputeResumeProof(graced.ResumeKey, nonce, session, devPlayerId, proof);
        }
        return match.TryJoin(peerId, devPlayerId, resumeKey, session, nonce, proof, out _);
    }

    // 기능: Resume 증명 없이 Join한다(같은 이름을 쓰는 다른 사람).
    // 입력: match - 경기, peerId - 연결 id, devPlayerId - 이름.
    // 출력: Match.TryJoin의 결과.
    public static JoinResult JoinWithoutProof(this Match match, int peerId, string devPlayerId)
    {
        byte[] session = RandomNumberGenerator.GetBytes(ProtocolLimits.SessionKeyBytes);
        return match.TryJoin(peerId, devPlayerId, SessionAuth.DeriveResumeKey(session), session, 0, null, out _);
    }
}
