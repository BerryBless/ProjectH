# Client 성능 점검 기준 (Unity)

`client-hotpath`, `client-render-ui` 점검이 켜졌을 때만 읽는다.

## 22. Client 성능 점검 자동 활성화 조건

다음 코드를 수정할 때만 Client 성능 점검을 활성화한다.

- Update
- FixedUpdate
- LateUpdate
- 반복 Coroutine
- 대량 Object
- Instantiate / Destroy
- Object Pool
- Rendering
- UI
- Animation
- Physics
- Particle
- Asset Loading
- Addressables
- Frame Drop
- GC Allocation

## 23. Client 성능 목표

Client 성능 점검이 활성화된 경우 최우선 목표는 다음이다.

```text
저사양 기기에서도 안정적으로 동작
```

최고 FPS보다 안정적인 Frame Time을 우선한다. 평균 FPS가 높아도 GC나 스파이크로 프레임이 튀면 체감 품질이 떨어지기 때문이다.

확인 항목:

```text
Frame Time
Main Thread
Render Thread
GC.Alloc
GC Collection
CPU
Memory
UI Rebuild
Rendering
```

## 24. Client Hot Path 점검

반복 실행 코드일 때만 다음을 확인한다.

- LINQ
- boxing
- closure
- string 생성
- 임시 List
- 임시 Array
- delegate allocation
- 반복 GetComponent
- 불필요한 Component 탐색
- 반복 Instantiate / Destroy

Hot Path가 아니라면 이런 최적화를 강제하지 않는다.

## 25. Update 점검

Update 관련 작업에서만 확인한다.

매 프레임 실행할 이유가 없는 코드는 Update에서 제거할 수 있다.

필요하면 다음을 고려한다.

- Event 기반 처리
- 상태 변경 시 처리
- 낮은 빈도 Tick
- Scheduler

단순히 Update가 존재한다는 이유만으로 구조를 바꾸지 않는다.

## 26. Object Pool 점검

다음처럼 반복 생성/삭제가 실제 문제일 때만 Pool을 고려한다.

- Projectile
- Effect
- Damage Number
- 반복 UI
- 다량 Enemy

Object Pool을 기본 설계로 사용하지 않는다.

## 27. Rendering 점검

Rendering 관련 작업일 때만 확인한다.

- Draw Call
- SetPass Call
- Overdraw
- Transparent UI
- Particle Count
- Material Instance (`Renderer.material` 접근은 Material을 암묵적으로 복제하고, 복제본은 직접 Destroy하지 않으면 남는다)
- Shader Complexity
- Texture Memory
- Mesh Complexity
- Shadow
- Post Processing

## 28. UI 점검

UI 성능 문제가 발생할 가능성이 있는 작업일 때만 확인한다.

- Canvas Rebuild
- Layout Rebuild
- LayoutGroup
- ContentSizeFitter
- Mask
- Graphic 수
- Raycast Target
- Text 반복 갱신
- Scroll List

대량 리스트라면 Virtualization을 고려한다.

## 43. Unity Profiling이 필요한 경우

Client 성능 문제가 있을 때만 다음 도구를 사용한다.

- Unity Profiler
- Memory Profiler
- Frame Debugger
- Rendering Statistics

성능 판단은 Editor가 아니라 Development Build를 실제 Target Device에서 측정한 값으로 한다. Editor는 오버헤드가 커서 병목 위치가 달라 보일 수 있다.
