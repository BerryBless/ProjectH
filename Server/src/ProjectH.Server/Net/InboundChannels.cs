using System.Threading.Channels;
using ProjectH.Server.Diagnostics;

namespace ProjectH.Server.Net;

// The only handoff from LiteNetLib's threads to the game loop. All three channels are bounded:
//
// | Channel | Producer               | Consumer  | Capacity               | When full                          |
// | Control | LiteNetLib event threads | GameLoop | ServerOptions.ControlChannelCapacity: 3 * (MaxPlayers + ConnectBurstPerIp + ceil(ConnectsPerIpPerSecond / SimHz)), 3 * MaxPlayers with the per-IP limit off | TryWrite fails -> caller disconnects the peer |
// | Input   | LiteNetLib event threads | GameLoop | MaxPlayers * InputBuffer | DropOldest (newest input matters most) |
// | Build   | LiteNetLib event threads | GameLoop | MaxPlayers * BuildRequestQueue.Capacity | DropOldest (Phase 13 D8; the client's prediction of a dropped one times out) |
//
// SingleWriter is false: with UnsyncedEvents LiteNetLib may raise events from more than one thread.
public sealed class InboundChannels
{
    // 기능: Network Thread에서 Game Loop로 넘기는 Control·Input·Build Bounded Channel을 만든다.
    // 입력: options - Channel 용량을 정하는 서버 설정, stats - 버려진 입력 수를 셀 통계, buildDropped - 건설 요청이 버려질 때 호출할 동작(null이면 없음).
    // 출력: 세 Channel이 비어 있는 InboundChannels 객체.
    public InboundChannels(ServerOptions options, ServerStats stats, Action? buildDropped = null)
    {
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

        Build = Channel.CreateBounded<BuildMessage>(new BoundedChannelOptions(options.MaxPlayers * Game.Build.BuildRequestQueue.Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }, _ => buildDropped?.Invoke());
    }

    public Channel<ControlMessage> Control { get; }
    public Channel<InputMessage> Input { get; }
    public Channel<BuildMessage> Build { get; }
}
