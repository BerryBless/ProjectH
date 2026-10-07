using System.Text;
using NUnit.Framework;
using ProjectH.Client.Qa;

namespace ProjectH.Client.Tests
{
    // QA gameplay input: the pure half of POST /qa/input (route, body parser, number reader, texts) and the status fields.
    public class QaInputProtocolTests
    {
        // 기능: Body를 파싱하고 성공을 확인한다.
        // 입력: body - 요청 Body.
        // 출력: 파싱된 입력. 실패하면 테스트가 실패한다.
        private static QaInputRequest Parse(string body)
        {
            bool ok = QaInput.TryParse(body, out QaInputRequest request, out string error);
            Assert.IsTrue(ok, error);
            Assert.IsNull(error);
            return request;
        }

        // 기능: Body가 거절되는지 확인한다.
        // 입력: body - 요청 Body.
        // 출력: 반환값 없음. 거절되지 않거나 오류 문구가 없으면 테스트가 실패한다.
        private static void AssertRejected(string body)
        {
            bool ok = QaInput.TryParse(body, out _, out string error);
            Assert.IsFalse(ok, body);
            Assert.IsFalse(string.IsNullOrEmpty(error), body);
        }

        // 기능: 입력의 applied 문구를 만든다.
        // 입력: body - 올바른 요청 Body.
        // 출력: AppendDescription이 쓴 문구.
        private static string Describe(string body)
        {
            var sb = new StringBuilder();
            QaInput.AppendDescription(sb, Parse(body));
            return sb.ToString();
        }

        [Test]
        public void Resolve_Input_IsPostOnly()
        {
            Assert.AreEqual(QaRoute.Input, QaHttp.Resolve("POST", "/qa/input"));
            Assert.AreEqual(QaRoute.Input, QaHttp.Resolve("POST", "/qa/input/"));
            Assert.AreEqual(QaRoute.MethodNotAllowed, QaHttp.Resolve("GET", "/qa/input"));
            Assert.AreEqual(QaRoute.NotFound, QaHttp.Resolve("POST", "/qa/Input"));
            Assert.AreEqual(QaRoute.Status, QaHttp.Resolve("GET", "/qa/status"));
        }

        [Test]
        public void Key_WithoutHoldOrAction_IsPress()
        {
            QaInputRequest r = Parse("{\"key\":\"q\"}");
            Assert.AreEqual(QaInputKind.Key, r.Kind);
            Assert.AreEqual(QaInputAction.Press, r.Action);
            Assert.AreEqual("q", QaInput.KeyNames[r.Code]);
        }

        [Test]
        public void Key_HoldMs_IsHold()
        {
            QaInputRequest r = Parse("{\"key\":\"w\",\"holdMs\":1500}");
            Assert.AreEqual(QaInputAction.Hold, r.Action);
            Assert.AreEqual(1500, r.HoldMs);
        }

        [Test]
        public void Key_DownAndUp()
        {
            Assert.AreEqual(QaInputAction.Down, Parse("{\"key\":\"leftShift\",\"action\":\"down\"}").Action);
            Assert.AreEqual(QaInputAction.Up, Parse("{\"key\":\"leftShift\",\"action\":\"up\"}").Action);
            AssertRejected("{\"key\":\"w\",\"action\":\"toggle\"}");
        }

        [Test]
        public void HoldMs_Limits()
        {
            Assert.AreEqual(1, Parse("{\"key\":\"w\",\"holdMs\":1}").HoldMs);
            Assert.AreEqual(10000, Parse("{\"key\":\"w\",\"holdMs\":10000}").HoldMs);
            AssertRejected("{\"key\":\"w\",\"holdMs\":0}");
            AssertRejected("{\"key\":\"w\",\"holdMs\":10001}");
            AssertRejected("{\"key\":\"w\",\"holdMs\":-5}");
            AssertRejected("{\"key\":\"w\",\"holdMs\":1.5}");
            AssertRejected("{\"key\":\"w\",\"holdMs\":\"100\"}");
            Assert.AreEqual(1000, Parse("{\"key\":\"w\",\"holdMs\":1e3}").HoldMs);
        }

        [Test]
        public void HoldMsWithAction_IsRejected()
        {
            AssertRejected("{\"key\":\"w\",\"holdMs\":100,\"action\":\"down\"}");
            AssertRejected("{\"button\":\"left\",\"holdMs\":100,\"action\":\"up\"}");
        }

        [Test]
        public void KeyWhitelist_AcceptsEveryListedName_AndRejectsOthers()
        {
            foreach (string name in QaInput.KeyNames)
                Assert.AreEqual(name, QaInput.KeyNames[Parse("{\"key\":\"" + name + "\"}").Code]);
            Assert.AreEqual(26, QaInput.KeyNames.Length);
            AssertRejected("{\"key\":\"W\"}");
            AssertRejected("{\"key\":\"leftAlt\"}");
            AssertRejected("{\"key\":\"6\"}");
            AssertRejected("{\"key\":\"\"}");
            AssertRejected("{\"key\":5}");
            Assert.AreEqual(-1, QaInput.KeyIndex(null));
        }

        [Test]
        public void Button_LeftRight()
        {
            QaInputRequest left = Parse("{\"button\":\"left\"}");
            Assert.AreEqual(QaInputKind.Button, left.Kind);
            Assert.AreEqual(0, left.Code);
            QaInputRequest right = Parse("{\"button\":\"right\",\"holdMs\":800}");
            Assert.AreEqual(1, right.Code);
            Assert.AreEqual(QaInputAction.Hold, right.Action);
            Assert.AreEqual(QaInputAction.Down, Parse("{\"button\":\"left\",\"action\":\"down\"}").Action);
            AssertRejected("{\"button\":\"middle\"}");
        }

        [Test]
        public void Look_ValuesAndSpread()
        {
            QaInputRequest r = Parse("{\"lookX\":120,\"lookY\":-30.5,\"ms\":300}");
            Assert.AreEqual(QaInputKind.Look, r.Kind);
            Assert.AreEqual(120.0, r.LookX);
            Assert.AreEqual(-30.5, r.LookY);
            Assert.AreEqual(300, r.Ms);
            QaInputRequest onlyX = Parse("{\"lookX\":5}");
            Assert.AreEqual(0.0, onlyX.LookY);
            Assert.AreEqual(0, onlyX.Ms);
            Assert.AreEqual(-7.0, Parse("{\"lookY\":-7}").LookY);
        }

        [Test]
        public void Look_Limits()
        {
            Parse("{\"lookX\":20000,\"lookY\":-20000,\"ms\":5000}");
            AssertRejected("{\"lookX\":20000.5}");
            AssertRejected("{\"lookY\":-20001}");
            AssertRejected("{\"lookX\":1,\"ms\":5001}");
            AssertRejected("{\"lookX\":1,\"ms\":-1}");
            AssertRejected("{\"lookX\":1,\"ms\":2.5}");
            AssertRejected("{\"lookX\":1e999}");
            AssertRejected("{\"lookX\":true}");
            AssertRejected("{\"lookX\":null}");
        }

        [Test]
        public void ExactlyOneKind()
        {
            AssertRejected("{}");
            AssertRejected("{\"key\":\"w\",\"button\":\"left\"}");
            AssertRejected("{\"key\":\"w\",\"lookX\":1}");
            AssertRejected("{\"button\":\"left\",\"lookY\":1}");
            AssertRejected("{\"holdMs\":100}");
        }

        [Test]
        public void FieldsThatDoNotApply_AreRejected()
        {
            AssertRejected("{\"key\":\"w\",\"ms\":100}");
            AssertRejected("{\"lookX\":1,\"holdMs\":100}");
            AssertRejected("{\"lookX\":1,\"action\":\"down\"}");
            AssertRejected("{\"key\":\"w\",\"holdms\":100}");
        }

        [Test]
        public void BadJson_IsRejected()
        {
            AssertRejected(null);
            AssertRejected("");
            AssertRejected("[\"key\"]");
            AssertRejected("{\"key\":\"w\"");
            AssertRejected("{\"key\":\"w\"} x");
            AssertRejected("{\"key\":{\"a\":1}}");
            AssertRejected("{\"lookX\":1,\"lookX\":2}");
        }

        [Test]
        public void ReleaseAll_MustBeTrueAndAlone()
        {
            Assert.AreEqual(QaInputKind.ReleaseAll, Parse("{\"releaseAll\":true}").Kind);
            AssertRejected("{\"releaseAll\":false}");
            AssertRejected("{\"releaseAll\":\"true\"}");
            AssertRejected("{\"releaseAll\":1}");
            AssertRejected("{\"releaseAll\":true,\"key\":\"w\"}");
        }

        [Test]
        public void TryGetNumber_Results()
        {
            Assert.AreEqual(QaJsonResult.Ok, QaJsonReader.TryGetNumber("{\"a\":\"x\",\"n\":-1.25e2}", "n", out double n));
            Assert.AreEqual(-125.0, n);
            Assert.AreEqual(QaJsonResult.Missing, QaJsonReader.TryGetNumber("{\"a\":1}", "n", out _));
            Assert.AreEqual(QaJsonResult.Invalid, QaJsonReader.TryGetNumber("{\"n\":\"1\"}", "n", out _));
            Assert.AreEqual(QaJsonResult.Invalid, QaJsonReader.TryGetNumber("{\"n\":false}", "n", out _));
            Assert.AreEqual(QaJsonResult.Invalid, QaJsonReader.TryGetNumber("{\"n\":1-2}", "n", out _));
            Assert.AreEqual(QaJsonResult.Invalid, QaJsonReader.TryGetNumber("{\"n\":1", "n", out _));
        }

        [Test]
        public void TryGetBool_Results()
        {
            Assert.AreEqual(QaJsonResult.Ok, QaJsonReader.TryGetBool("{\"b\":true}", "b", out bool b));
            Assert.IsTrue(b);
            Assert.AreEqual(QaJsonResult.Ok, QaJsonReader.TryGetBool("{\"b\":false}", "b", out b));
            Assert.IsFalse(b);
            Assert.AreEqual(QaJsonResult.Missing, QaJsonReader.TryGetBool("{}", "b", out _));
            Assert.AreEqual(QaJsonResult.Invalid, QaJsonReader.TryGetBool("{\"b\":null}", "b", out _));
        }

        [Test]
        public void HasOnlyKeys_Checks()
        {
            string[] allowed = { "a", "b" };
            Assert.IsTrue(QaJsonReader.HasOnlyKeys("{}", allowed));
            Assert.IsTrue(QaJsonReader.HasOnlyKeys("{\"a\":1,\"b\":\"x\"}", allowed));
            Assert.IsFalse(QaJsonReader.HasOnlyKeys("{\"c\":1}", allowed));
            Assert.IsFalse(QaJsonReader.HasOnlyKeys("not json", allowed));
        }

        [Test]
        public void AppliedText()
        {
            Assert.AreEqual("press q", Describe("{\"key\":\"q\"}"));
            Assert.AreEqual("hold w 1500ms", Describe("{\"key\":\"w\",\"holdMs\":1500}"));
            Assert.AreEqual("down left", Describe("{\"button\":\"left\",\"action\":\"down\"}"));
            Assert.AreEqual("up right", Describe("{\"button\":\"right\",\"action\":\"up\"}"));
            Assert.AreEqual("look 120,-30.5 300ms", Describe("{\"lookX\":120,\"lookY\":-30.5,\"ms\":300}"));
            Assert.AreEqual("releaseAll", Describe("{\"releaseAll\":true}"));
            var sb = new StringBuilder();
            QaResponses.AppendInput(sb, Parse("{\"key\":\"f1\"}"));
            Assert.AreEqual("{\"ok\":true,\"applied\":\"press f1\"}", sb.ToString());
        }

        [Test]
        public void Status_HasToolPreviewAndCursorLocked()
        {
            var sb = new StringBuilder();
            QaResponses.AppendStatus(sb, "qa1", true, true, "InGame", false, false, true, 100, 60, 42, "Build", "NoResource", true);
            Assert.AreEqual("{\"ok\":true,\"devPlayerId\":\"qa1\",\"connected\":true,\"joined\":true,\"screen\":\"InGame\"," +
                            "\"statsOpen\":false,\"debugVisible\":false,\"alive\":true,\"health\":100,\"fps\":60,\"frame\":42," +
                            "\"tool\":\"Build\",\"preview\":\"NoResource\",\"cursorLocked\":true}", sb.ToString());
        }
    }
}
