using System.Threading.Channels;
using ProjectH.Server.Diagnostics;

namespace ProjectH.Server.Net;

// The only handoff from LiteNetLib's threads to the game loop. Both channels are bounded:
//
// | Channel | Producer               | Consumer  | Capacity               | When full                          |
// | Control | LiteNetLib event threads | GameLoop | ServerOptions.ControlChannelCapacity: 3 * (MaxPlayers + AcceptBurst + ceil(AcceptsPerSecond / SimHz)) (review fix A2) | TryWrite fails -> caller disconnects the peer |
// | Input   | LiteNetLib event threads | GameLoop | ServerOptions.InputChannelCapacity: MaxPlayers * InputBurst (review fix A5: every player's whole burst) | DropOldest (newest input matters most) |
// | Build   | LiteNetLib event threads | GameLoop | MaxPlayers * 2 * maxRequestsPerSecond (review fix A5: two back-to-back one-second windows per player) | DropOldest (Phase 13 D8; placements and, Phase 13.5, edits; the client's prediction of a dropped one times out) |
// | Marker  | LiteNetLib event threads | GameLoop | MaxPlayers * MarkersPerPlayer | DropOldest (Phase 15 D7: pings and waypoint changes; a dropped ping is only information lost) |
//
// SingleWriter is false: with UnsyncedEvents LiteNetLib may raise events from more than one thread.
public sealed class InboundChannels
{
    // Phase 15 D7: room per player in the Marker channel. The receive thread already lets at most pingBurst through at once
    // per connection, and the game loop drains the whole channel every tick.
    public const int MarkersPerPlayer = 4;

    // 기능: Network → Game Loop 유한 채널들을 만든다(Phase 15: Marker 채널 추가. 리뷰 수정 A5: Input은 모든 플레이어의 burst,
    //   Build는 플레이어마다 1초 창 두 개만큼의 요청을 담는다).
    // 입력: options - 서버 설정(MaxPlayers 등), stats - 입력 드롭을 셀 통계, buildDropped - 건설 요청이 밀려 버려질 때 부를 함수,
    //   markerDropped - 지도 표시 요청이 밀려 버려질 때 부를 함수(null = 세지 않음), buildRequestsPerSecond - building.json의
    //   maxRequestsPerSecond(연결당 1초 창 상한).
    // 출력: 네 채널이 준비된 InboundChannels.
    public InboundChannels(ServerOptions options, ServerStats stats, Action? buildDropped = null, Action? markerDropped = null,
        int buildRequestsPerSecond = 20)
    {
        BuildCapacity = options.MaxPlayers * 2 * Math.Max(1, buildRequestsPerSecond);
        Control = Channel.CreateBounded<ControlMessage>(new BoundedChannelOptions(options.ControlChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // TryWrite never blocks; it returns false when full
            SingleReader = true,
            SingleWriter = false,
        });

        Input = Channel.CreateBounded<InputMessage>(new BoundedChannelOptions(options.InputChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }, _ => stats.AddInputDrop());

        Build = Channel.CreateBounded<BuildMessage>(new BoundedChannelOptions(BuildCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }, _ => buildDropped?.Invoke());

        Marker = Channel.CreateBounded<MarkerMessage>(new BoundedChannelOptions(options.MaxPlayers * MarkersPerPlayer)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }, _ => markerDropped?.Invoke());
    }

    // Review fix A5: the Build channel's capacity (MaxPlayers * 2 * maxRequestsPerSecond).
    public int BuildCapacity { get; }
    public Channel<ControlMessage> Control { get; }
    public Channel<InputMessage> Input { get; }
    public Channel<BuildMessage> Build { get; }
    public Channel<MarkerMessage> Marker { get; }
}
