using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D4: the development line that used to be in the IMGUI panel (connection state, RTT, entity id), toggled with
    // F1 and hidden at start. One Text on its own canvas (no GraphicRaycaster), so the RTT changing redraws only this
    // canvas. The string is rebuilt only when a shown value changes, and nothing is built while it is hidden.
    public sealed class DebugOverlay : System.IDisposable
    {
        private readonly GameObject _root;
        private readonly Text _text;
        private readonly Text _movement;
        private readonly Text _route;
        private readonly Text _build;
        private bool _visible;
        private ClientState _state = (ClientState)(-1);
        private int _rtt = -1;
        private ushort _entity;
        private long _authDrops = -1;
        private int _tickRejects = -1;
        private int _spawnRejects = -1;
        // Phase 12 D14: the shown movement digits; the line is rebuilt when one changes, at most 10 times a second. Two
        // values outside MovementMode stand for "nothing shown yet" (rebuild on the next tick) and "dead" (the line empty).
        private const MovementMode ModeUnknown = (MovementMode)255;
        private const MovementMode ModeDead = (MovementMode)254;
        private MovementMode _mode = ModeUnknown;
        private int _horizontal;
        private int _vertical;
        private int _energy;
        private int _correction;
        private float _nextMovementAt;
        private bool _hasRoute;
        private DropRoute _shownRoute;
        // Phase 13 D16: the build line, rebuilt at most 4 times a second when a shown value changed; the request rate is
        // counted over whole seconds.
        private float _nextBuildAt;
        private readonly int[] _buildShown = new int[10];
        private bool _buildDirty = true;
        private int _rateSent;
        private float _rateFrom = -1f;
        private int _rate;

        // 기능: F1 줄 네 개(연결·이동·수송기 경로·건설)를 가진 Canvas를 만들고 숨긴다.
        // 입력: 없음.
        // 출력: 숨겨진 DebugOverlay(Dispose가 Canvas를 파괴한다).
        public DebugOverlay()
        {
            _root = UiFactory.CreateCanvas("DebugOverlay", 120, interactive: false);
            // The lines start below the POI label (PoiLabel, top left from y -20, 40 high), so F1 never covers it.
            _text = UiFactory.CreateText("Line", _root.transform, string.Empty, 20, TextAnchor.UpperLeft, new Vector2(0f, 1f),
                new Vector2(24f, -100f), new Vector2(900f, 30f));
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _movement = UiFactory.CreateText("Movement", _root.transform, string.Empty, 20, TextAnchor.UpperLeft, new Vector2(0f, 1f),
                new Vector2(24f, -130f), new Vector2(900f, 30f));
            _movement.horizontalOverflow = HorizontalWrapMode.Overflow;
            _route = UiFactory.CreateText("Route", _root.transform, string.Empty, 20, TextAnchor.UpperLeft, new Vector2(0f, 1f),
                new Vector2(24f, -160f), new Vector2(900f, 30f));
            _route.horizontalOverflow = HorizontalWrapMode.Overflow;
            _build = UiFactory.CreateText("Build", _root.transform, string.Empty, 20, TextAnchor.UpperLeft, new Vector2(0f, 1f),
                new Vector2(24f, -190f), new Vector2(900f, 30f));
            _build.horizontalOverflow = HorizontalWrapMode.Overflow;
            _root.SetActive(false);
        }

        public bool Visible => _visible;

        // 기능: F1 줄을 보이거나 숨긴다. 다시 보일 때 모든 줄이 다음 Tick에 새로 만들어지도록 보인 값을 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음. Visible이 뒤집히고 Canvas가 켜지거나 꺼진다(Canvas가 이미 파괴됐으면 아무것도 하지 않는다).
        public void Toggle()
        {
            if (_root == null) return;
            _visible = !_visible;
            _root.SetActive(_visible);
            _rtt = -1;   // rebuild on the next Tick
            _mode = ModeUnknown;
            _hasRoute = false;
            _route.text = string.Empty;
            _buildDirty = true;
            _rateFrom = -1f;
        }

        // 기능: 연결 상태 줄을 갱신한다. 보이는 값(상태·RTT·Entity·인증 버림·Tick 거절·Spawn 거절 수)이 바뀐 때만 문자열을 다시 만든다.
        // 입력: state - 연결 상태, roundTripMs - RTT, entityId - 내 Entity, authDrops - 인증 계층이 버린 데이터그램 수(리뷰 B3),
        //   tickRejects·spawnRejects - 너무 앞선 Tick·상한을 넘은 원격 Spawn을 버린 수(리뷰 D3).
        // 출력: 반환값 없음. 숨겨져 있으면 아무것도 하지 않는다.
        public void Tick(ClientState state, int roundTripMs, ushort entityId, long authDrops, int tickRejects, int spawnRejects)
        {
            if (_root == null || !_visible) return;
            if (state == _state && roundTripMs == _rtt && entityId == _entity && authDrops == _authDrops && tickRejects == _tickRejects &&
                spawnRejects == _spawnRejects) return;
            _state = state;
            _rtt = roundTripMs;
            _entity = entityId;
            _authDrops = authDrops;
            _tickRejects = tickRejects;
            _spawnRejects = spawnRejects;
            _text.text = UiText.DebugLine(state.ToString(), roundTripMs, entityId, authDrops, tickRejects, spawnRejects);
        }

        // 기능: 이동 줄과 수송기 경로 줄을 갱신한다(Phase 12 D14). 경로는 바뀔 때, 이동 줄은 보이는 자릿수가 바뀔 때 0.1초에 한 번만 다시 만든다.
        // 입력: now - 현재 시각(초), alive - 살아 있는지(아니면 이동 줄을 비운다), mode - 이동 모드, horizontal·vertical - 수평·수직 속도(m/s),
        //   energy - 기력, correction - 마지막 예측 보정 거리(m), hasRoute·route - 수송기 경로가 있는지와 그 경로.
        // 출력: 반환값 없음. 숨겨져 있으면 아무것도 하지 않는다.
        // Phase 12 D14: the local player's mode, speeds, energy and the last prediction correction, and the transport route.
        public void TickMovement(float now, bool alive, MovementMode mode, float horizontal, float vertical, float energy, float correction,
            bool hasRoute, in DropRoute route)
        {
            if (_root == null || !_visible) return;
            if (hasRoute != _hasRoute || (hasRoute && !SameRoute(route, _shownRoute)))
            {
                _hasRoute = hasRoute;
                _shownRoute = route;
                _route.text = hasRoute ? UiText.RouteLine(route.StartX, route.StartZ, route.EndX, route.EndZ) : string.Empty;
            }
            if (!alive)
            {
                if (_mode != ModeDead) _movement.text = string.Empty;
                _mode = ModeDead;
                return;
            }
            if (now < _nextMovementAt) return;
            int h = Mathf.RoundToInt(horizontal * 10f);
            int v = Mathf.RoundToInt(vertical * 10f);
            int e = Mathf.RoundToInt(energy);
            int c = Mathf.RoundToInt(correction * 100f);
            if (mode == _mode && h == _horizontal && v == _vertical && e == _energy && c == _correction) return;
            _mode = mode;
            _horizontal = h;
            _vertical = v;
            _energy = e;
            _correction = c;
            _nextMovementAt = now + 0.1f;
            _movement.text = UiText.MovementLine(ModeName(mode), h, v, e, c);
        }

        // 기능: 건설 줄을 갱신한다(Phase 13 D16). 요청 속도는 1초 단위로 세고, 보이는 값이 바뀌었을 때 0.25초에 한 번만 다시 만든다.
        // 입력: now - 현재 시각(초), tool - 도구, piece·material - 고른 조각·재료, stored·drawn - 확정 조각의 저장·표시 수,
        //   ignored - 무시한 사건 수, sent·refused - 보낸·거절된 요청 수, lastRefusal - 마지막 거절 코드.
        // 출력: 반환값 없음. 숨겨져 있으면 아무것도 하지 않는다(요청 속도 계산도 멈춘다).
        // Phase 13 D16: the tool, the selection, the confirmed pieces stored and drawn, events ignored, requests (and per
        // second) and refusals.
        public void TickBuild(float now, ToolKind tool, BuildPieceType piece, BuildMaterialType material, int stored, int drawn, int ignored,
            int sent, int refused, BuildResultCode lastRefusal)
        {
            if (_root == null || !_visible) return;
            if (_rateFrom < 0f || sent < _rateSent)
            {
                _rateFrom = now;
                _rateSent = sent;
            }
            else if (now - _rateFrom >= 1f)
            {
                _rate = Mathf.RoundToInt((sent - _rateSent) / (now - _rateFrom));
                _rateFrom = now;
                _rateSent = sent;
            }
            if (now < _nextBuildAt) return;
            int[] v = _buildShown;
            if (!_buildDirty && v[0] == (int)tool && v[1] == (int)piece && v[2] == (int)material && v[3] == stored && v[4] == drawn &&
                v[5] == ignored && v[6] == sent && v[7] == refused && v[8] == (int)lastRefusal && v[9] == _rate)
                return;
            _buildDirty = false;
            v[0] = (int)tool;
            v[1] = (int)piece;
            v[2] = (int)material;
            v[3] = stored;
            v[4] = drawn;
            v[5] = ignored;
            v[6] = sent;
            v[7] = refused;
            v[8] = (int)lastRefusal;
            v[9] = _rate;
            _nextBuildAt = now + 0.25f;
            _build.text = UiText.BuildDebugLine(tool, piece, material, stored, drawn, ignored, sent, refused, lastRefusal, _rate);
        }

        // 기능: 두 수송기 경로가 같은지 비교한다(시작·끝 좌표와 시작 Tick).
        // 입력: a·b - 비교할 경로.
        // 출력: 모두 같으면 true, 아니면 false.
        private static bool SameRoute(in DropRoute a, in DropRoute b) =>
            a.StartX == b.StartX && a.StartZ == b.StartZ && a.EndX == b.EndX && a.EndZ == b.EndZ && a.StartTick == b.StartTick;

        // 기능: F1 이동 줄의 모드 이름을 돌려준다(Phase 14: Downed 포함).
        // 입력: mode - 이동 모드.
        // 출력: 상수 문자열(할당 없음).
        private static string ModeName(MovementMode mode)
        {
            switch (mode)
            {
                case MovementMode.Crouch: return "Crouch";
                case MovementMode.Slide: return "Slide";
                case MovementMode.Vault: return "Vault";
                case MovementMode.Freefall: return "Freefall";
                case MovementMode.Glide: return "Glide";
                case MovementMode.Transport: return "Transport";
                case MovementMode.Downed: return "Downed";
                default: return "Ground";
            }
        }

        // 기능: F1 Canvas를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 이미 파괴됐으면 아무것도 하지 않는다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
