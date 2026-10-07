using System.Numerics;
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Client.Game.Map;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Tests
{
    // Phase 15 D6, D11: the middle-button tap (one context ping, or one Danger for a double press within 0.3 s), the context
    // chosen from what the aim ray hit, and the distance text that changes only with the whole metre.
    public class PingInputTests
    {
        // 기능: 맞은 점의 Location 맥락 Ping을 만든다.
        // 입력: x, z - 맞은 점.
        // 출력: Location Ping.
        private static MapMarker Location(float x, float z) => new MapMarker { Kind = MapMarkerKind.Location, Position = new Vector3(x, 1f, z) };

        // 기능: 아이템 하나를 가진 목록을 만든다.
        // 입력: id - 아이템 id, position - 위치.
        // 출력: 목록.
        private static WorldItemList Items(ushort id, Vector3 position)
        {
            var list = new WorldItemList();
            list.Upsert(new WorldItemData { ItemId = id, Kind = ItemKind.Ammo, DefId = 1, Amount = 10, Position = position }, out _, out _);
            return list;
        }

        [Test]
        public void SinglePress_SendsTheContextPingAfterTheWindow()
        {
            var tap = new PingTap();
            Assert.IsFalse(tap.Press(10f, true, Location(5f, 6f), out _));
            Assert.IsTrue(tap.Pending);
            Assert.IsFalse(tap.Update(10.29f, out _));   // not yet
            Assert.IsTrue(tap.Update(10.31f, out MapMarker sent));
            Assert.AreEqual(MapMarkerKind.Location, sent.Kind);
            Assert.AreEqual(5f, sent.Position.X);
            Assert.IsFalse(tap.Pending);
            Assert.IsFalse(tap.Update(11f, out _));   // only once
        }

        [Test]
        public void DoublePress_SendsOneDangerAtTheFirstPoint()
        {
            var tap = new PingTap();
            tap.Press(10f, true, new MapMarker { Kind = MapMarkerKind.Enemy, Position = new Vector3(3f, 2f, 4f), TargetId = 9 }, out _);
            Assert.IsTrue(tap.Press(10.25f, true, Location(50f, 50f), out MapMarker danger));
            Assert.AreEqual(MapMarkerKind.Danger, danger.Kind);
            Assert.AreEqual(new Vector3(3f, 2f, 4f), danger.Position);
            Assert.AreEqual(0, danger.TargetId);   // the server refuses a target on Danger
            Assert.IsFalse(tap.Pending);
            Assert.IsFalse(tap.Update(11f, out _));   // the first press's context ping never goes out
        }

        [Test]
        public void SecondPressWithoutAHit_StillMakesDanger()
        {
            var tap = new PingTap();
            tap.Press(1f, true, Location(1f, 1f), out _);
            Assert.IsTrue(tap.Press(1.2f, false, default, out MapMarker danger));
            Assert.AreEqual(MapMarkerKind.Danger, danger.Kind);
        }

        [Test]
        public void LatePress_IsANewTap()
        {
            var tap = new PingTap();
            tap.Press(1f, true, Location(1f, 1f), out _);
            Assert.IsTrue(tap.Update(1.35f, out MapMarker first));   // the frame's Update runs before its press
            Assert.AreEqual(MapMarkerKind.Location, first.Kind);
            Assert.IsFalse(tap.Press(1.35f, true, Location(2f, 2f), out _));
            Assert.IsTrue(tap.Pending);
        }

        [Test]
        public void PressWithoutAHit_WaitsForNothing_AndCancelDrops()
        {
            var tap = new PingTap();
            Assert.IsFalse(tap.Press(1f, false, default, out _));
            Assert.IsFalse(tap.Pending);
            tap.Press(2f, true, Location(1f, 1f), out _);
            tap.Cancel();
            Assert.IsFalse(tap.Update(3f, out _));
        }

        [Test]
        public void Context_EnemyCollider_IsEnemy()
        {
            Assert.IsTrue(PingContext.Choose(7, new Vector3(10f, 1f, 10f), Items(3, new Vector3(10f, 0f, 10f)), out MapMarker m));
            Assert.AreEqual(MapMarkerKind.Enemy, m.Kind);
            Assert.AreEqual(7, m.TargetId);
        }

        [Test]
        public void Context_ItemWithinTwoMetres_IsItem_AtTheItem()
        {
            var item = new Vector3(11.5f, 0f, 10f);
            Assert.IsTrue(PingContext.Choose(0, new Vector3(10f, 0.5f, 10f), Items(42, item), out MapMarker m));
            Assert.AreEqual(MapMarkerKind.Item, m.Kind);
            Assert.AreEqual(42, m.TargetId);
            Assert.AreEqual(item, m.Position);
        }

        [Test]
        public void Context_NearestItemWins()
        {
            var list = Items(1, new Vector3(11.8f, 0f, 10f));
            list.Upsert(new WorldItemData { ItemId = 2, Kind = ItemKind.Ammo, DefId = 1, Amount = 1, Position = new Vector3(10.5f, 0f, 10f) }, out _, out _);
            PingContext.Choose(0, new Vector3(10f, 0f, 10f), list, out MapMarker m);
            Assert.AreEqual(2, m.TargetId);
        }

        [Test]
        public void Context_FarItemOrNone_IsLocation_WithoutTarget()
        {
            Assert.IsTrue(PingContext.Choose(0, new Vector3(10f, 0f, 10f), Items(1, new Vector3(12.5f, 0f, 10f)), out MapMarker m));
            Assert.AreEqual(MapMarkerKind.Location, m.Kind);
            Assert.AreEqual(0, m.TargetId);
            Assert.AreEqual(new Vector3(10f, 0f, 10f), m.Position);
            Assert.IsTrue(PingContext.Choose(0, new Vector3(1f, 0f, 1f), null, out m));
            Assert.AreEqual(MapMarkerKind.Location, m.Kind);
        }

        [Test]
        public void Context_ItemIdZero_IsNeverAnItemPing()
        {
            Assert.IsTrue(PingContext.Choose(0, new Vector3(10f, 0f, 10f), Items(0, new Vector3(10f, 0f, 10f)), out MapMarker m));
            Assert.AreEqual(MapMarkerKind.Location, m.Kind);
        }

        [Test]
        public void Context_OutsideTheMap_IsNoPing()
        {
            Assert.IsFalse(PingContext.Choose(0, new Vector3(80.5f, 0f, 0f), null, out _));
            Assert.IsFalse(PingContext.Choose(5, new Vector3(0f, 0f, -81f), null, out _));
            Assert.IsFalse(PingContext.Choose(0, new Vector3(float.NaN, 0f, 0f), null, out _));
            Assert.IsTrue(PingContext.Choose(0, new Vector3(80f, 0f, -80f), null, out _));
        }

        [Test]
        public void Context_PingsPassTheServerReader()
        {
            // Enemy and Item carry a target, every other kind 0: what MapMarker.TryRead accepts.
            var buffer = new byte[MapMarker.Size];
            foreach (MapMarker m in new[]
                     {
                         Pick(7, new Vector3(1f, 0f, 1f), null),
                         Pick(0, new Vector3(1f, 0f, 1f), Items(4, new Vector3(1f, 0f, 1f))),
                         Pick(0, new Vector3(1f, 0f, 1f), null),
                     })
            {
                var writer = new PacketWriter(buffer);
                MapMarker.Write(ref writer, m);
                var reader = new PacketReader(writer.WrittenSpan);
                Assert.IsTrue(reader.TryReadPacketId(out PacketId id));
                Assert.AreEqual(PacketId.MapMarker, id);
                Assert.IsTrue(MapMarker.TryRead(ref reader, out MapMarker read), m.Kind.ToString());
                Assert.AreEqual(m.Kind, read.Kind);
            }
        }

        // 기능: 맥락 Ping을 고르고 성공을 확인한다.
        // 입력: enemy - 적 id, hit - 맞은 점, items - 아이템 목록.
        // 출력: 고른 Ping.
        private static MapMarker Pick(ushort enemy, Vector3 hit, WorldItemList items)
        {
            Assert.IsTrue(PingContext.Choose(enemy, hit, items, out MapMarker m));
            return m;
        }

        [Test]
        public void CanPing_DownedInPlay_IsAllowed()
        {
            // A downed player is not dead and still in play (TeamMemberState.Downed), but ActionsAllowed(Downed) is false:
            // the ping gate must not depend on it (D6).
            Assert.IsTrue(PingContext.CanPing(dead: false, hasMatch: true, inPlay: true, uiBlocked: false, spectating: false));
        }

        [Test]
        public void CanPing_Refusals()
        {
            Assert.IsFalse(PingContext.CanPing(dead: true, hasMatch: true, inPlay: true, uiBlocked: false, spectating: false));
            Assert.IsFalse(PingContext.CanPing(dead: false, hasMatch: true, inPlay: true, uiBlocked: false, spectating: true));
            Assert.IsFalse(PingContext.CanPing(dead: false, hasMatch: true, inPlay: true, uiBlocked: true, spectating: false));   // map, screen or free cursor
            Assert.IsFalse(PingContext.CanPing(dead: false, hasMatch: true, inPlay: false, uiBlocked: false, spectating: false)); // a newcomer without a team
        }

        [Test]
        public void CanPing_DevSandbox_AnyLivingPlayer()
        {
            Assert.IsTrue(PingContext.CanPing(dead: false, hasMatch: false, inPlay: false, uiBlocked: false, spectating: false));
        }

        [Test]
        public void DistanceText_ChangesOnlyWithTheWholeMetre()
        {
            var text = new DistanceText();
            Assert.IsTrue(text.Update(23.2f));
            Assert.AreEqual("23 m", text.Text);
            string first = text.Text;
            Assert.IsFalse(text.Update(23.9f));
            Assert.AreSame(first, text.Text);
            Assert.IsTrue(text.Update(24.01f));
            Assert.AreEqual("24 m", text.Text);
            Assert.IsTrue(text.Update(-3f));
            Assert.AreEqual("0 m", text.Text);
            Assert.IsFalse(text.Update(float.NaN));   // NaN counts as 0
            text.Reset();
            Assert.IsTrue(text.Update(0.5f));
        }
    }
}
