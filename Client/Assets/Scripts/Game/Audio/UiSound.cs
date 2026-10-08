namespace ProjectH.Client.Game.Audio
{
    // Phase 18 D9: the one place a UI button's click sound comes from. UiFactory.CreateButton adds Click to every button; the
    // sink is read at click time, so buttons built before GameClient's Awake (UiRoot's screens) still sound once the audio
    // exists. GameClient sets Sink in Awake and clears it in OnDestroy. Main thread only (onClick runs from the EventSystem).
    public static class UiSound
    {
        public static GameAudio Sink { get; set; }

        // 기능: 버튼 클릭음을 요청한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Sink가 없으면(오디오 생성 전·해제 뒤) 아무것도 하지 않는다.
        public static void Click() => Sink?.Play2D(SoundKind.UiClick);
    }
}
