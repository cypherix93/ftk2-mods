# Crucible UI Driving — READ + WRITE the live UIToolkit tree

Objective: determine how a test harness could snapshot the visible UI (READ) and
press buttons / send keys into it (WRITE) generically — the way Playwright drives
a browser — instead of reverse-engineering each Director's private API. Trigger:
observed live boot into `MULTIPLAYER_LOBBY` with a stale-adventure rejoin
failure, showing "Online Error: Adventure Not Found (P0-16)" with a Close
button, where `RouterMono.Route(MAIN_MENU, 0, null, true, true)` changes
`GetCurrentRoute()` but the rendered screen does not change. `route` is not a
trustworthy state signal; a UI-driving approach detects and escapes this
uniformly.

See also [`crucible-ui-overlay-system.md`](./crucible-ui-overlay-system.md),
which found a *targeted* API fix for this same bug
(`MultiplayerViewHelper.HideMultiplayerJoinModal()` /
`PromptViewHelper.ClosePrompt()`). This document is the generic alternative:
drive the UIToolkit tree directly, which works on any screen without knowing
which Director/Helper owns it.

## Methodology note — how these members were captured

The task named the tool as `dotnet run --project
C:\Users\ben\repos\ftk2-wt-probe2\FTK2.DevKit\sandbox\TypeProbe -c Release --
<Type> --methods` and said it "can probe Unity types too... the Managed
folder holds `UnityEngine.UIElementsModule.dll`... Use that."

Checked the actual `TypeProbe` source
(`FTK2.DevKit/sandbox/TypeProbe/GameAssembly.cs`): it calls
`Assembly.LoadFrom` on `FTK2.dll` only and returns `asm.GetTypes()` — i.e. only
FTK2's own types. Sibling assemblies (`UnityEngine.*.dll`) are wired into an
`AssemblyResolve` handler so FTK2's *dependencies* load correctly, but their
own types are never enumerated. Confirmed empirically:

```
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- UnityEngine.UIElements.UIDocument --methods
## UnityEngine.UIElements.UIDocument — **NOT FOUND**
```

So a pure-Unity type (nothing in FTK2.dll references it by simple name lookup
across FTK2's own type list) cannot be probed with `TypeProbe` as shipped.

To satisfy the objective without touching the shared `TypeProbe` tool (other
agents share this worktree) I wrote a throwaway sibling tool, `UnityProbe`, in
my scratchpad, using **the identical reflection technique** as `TypeProbe`
(`Assembly.LoadFrom` + `AssemblyResolve` fallback + `BindingFlags.Public |
NonPublic | Instance | Static` + safe-signature formatting) but pointed
directly at the named module DLLs:

```
UnityEngine.CoreModule.dll, UnityEngine.UIElementsModule.dll,
UnityEngine.InputLegacyModule.dll, UnityEngine.IMGUIModule.dll, UnityEngine.dll
```

Every member name below was printed by this tool from the retail assembly at
`C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King
II_Data\Managed`. `InputController` (a game type) was probed with the real
`TypeProbe` as instructed. Nothing below is inferred syntax — acceptance rule
honored, just via an equivalent tool for the pure-Unity types since the named
tool cannot reach them.

---

## 1. Reading the tree

### `UIDocument` (`UnityEngine.UIElementsModule`)

```
prop | VisualElement | rootVisualElement
```

`UIDocument` is a `MonoBehaviour` (has `Awake()`, `GetComponent`, `gameObject`,
etc. — same base members `InputController` has). `rootVisualElement` is the
entry point into the tree for that document.

### `VisualElement` (`UnityEngine.UIElementsModule`) — walking + reading

```
method | IEnumerable`1 | Children | ()
prop   | Hierarchy      | hierarchy
prop   | Int32          | childCount
prop   | VisualElement  | Item          (indexer)
prop   | VisualElement  | parent
prop   | String         | name
prop   | Boolean        | visible
prop   | Boolean        | enabledSelf
prop   | Boolean        | enabledInHierarchy
prop   | IResolvedStyle | resolvedStyle      -> .display (DisplayStyle) via UnityEngine.UIElements.IResolvedStyle.display
prop   | IPanel         | panel
prop   | FocusController| focusController
method | Void           | Focus  ()
method | Void           | Blur   ()
method | Void           | SendEvent (EventBase e)
method | Void           | SendEvent (EventBase e, DispatchMode dispatchMode)
```

`Children()` returns direct children only (`IEnumerable<VisualElement>`); a
recursive walk (`foreach (var c in ve.Children()) recurse(c)`) builds the full
tree. `resolvedStyle.display` is reached by getting the `resolvedStyle`
property (returns an `IResolvedStyle`) then reading `display`
(`UnityEngine.UIElements.IResolvedStyle.display`, explicit interface member —
reflectively: `typeof(IResolvedStyle).GetProperty("display").GetValue(resolvedStyleInstance)`).

### `Button` / `Label` / `TextElement` — text

```
Button:      prop | String | text        (also: prop Clickable clickable)
Label:       (same TextElement-derived text member)
TextElement: prop | String | text
             prop | String | renderedText
             prop | String | originalText
```

All three read the same way: get the `text` property.

### `UQueryExtensions` (`UnityEngine.UIElementsModule`) — the `Q`/`Query` the game itself uses

```
method | T              | Q          (VisualElement e, String name, String className)
method | T              | Q          (VisualElement e, String name, String[] classes)
method | T              | MandatoryQ (VisualElement e, String name, String className)
method | UQueryBuilder`1| Query      (VisualElement e)
method | UQueryBuilder`1| Query      (VisualElement e, String name, String className)
method | UQueryBuilder`1| Query      (VisualElement e, String name, String[] classes)
method | VisualElement  | Q          (VisualElement e, String name, String className)
method | VisualElement  | Q          (VisualElement e, String name, String[] classes)
```

These are static extension methods, generic on `T : VisualElement`. Reflective
call: `typeof(UQueryExtensions).GetMethod("Q", ...).MakeGenericMethod(typeof(Button)).Invoke(null, new object[]{root, name, null})`.
Matches `CommandLineViewHelper.Initialize(CommandLineUIDocument.rootVisualElement)`
already-verified game pattern of passing `rootVisualElement` around.

---

## 2. Finding every live `UIDocument`

`UnityEngine.Object` (`UnityEngine.CoreModule`):

```
method | Object[] | FindObjectsOfType (Type type)
method | Object[] | FindObjectsOfType (Type type, Boolean includeInactive)
```

Confirmed present and simple — no generic-enum overload complexity like the
newer `FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)`
(also present, but `FindObjectsOfType(Type)` is the simplest reflective call
and is still live, non-deprecated-looking in this build). The harness enumerates
every active `UIDocument` each frame with:

```csharp
var docs = (UnityEngine.Object[])objectType
    .GetMethod("FindObjectsOfType", new[]{ typeof(Type) })
    .Invoke(null, new object[]{ uiDocumentType });
```

then reads `.rootVisualElement` off each to build a per-document snapshot.

---

## 3. Focus

Read:

```
VisualElement.panel               -> IPanel
IPanel.focusController            -> FocusController      (prop, confirmed on IPanel)
VisualElement.focusController     -> FocusController      (prop, also directly on VisualElement/Focusable)
FocusController.focusedElement    -> Focusable             (prop, confirmed)
```

So `element.panel.focusController.focusedElement` (or
`element.focusController.focusedElement` directly) gives the currently
focused `Focusable`. To mark an element focused in a dump, compare by
reference (`ReferenceEquals`) against each candidate element while walking
the tree.

Set:

```
Focusable.Focus()   — Void Focus ()   (confirmed on Focusable, and re-declared on VisualElement/Button/TextElement)
```

`element.Focus()` requests focus for that element (subject to
`focusable`/`canGrabFocus`/`excludeFromFocusRing` props, all confirmed present
on `Focusable`).

---

## 4. Sending input — options, with exact APIs

### Option A — `VisualElement.SendEvent(EventBase)` + navigation events

Confirmed on `VisualElement`:
```
method | Void | SendEvent (EventBase e)
method | Void | SendEvent (EventBase e, DispatchMode dispatchMode)
```

Event construction, all confirmed via `GetPooled` static factories:

- **`NavigationSubmitEvent`** / **`NavigationCancelEvent`** — neither declares
  its own `GetPooled` (BindingFlags without `FlattenHierarchy` didn't surface
  it at first; re-probed the shared generic base and found it there):

  `NavigationEventBase<T>` (`UnityEngine.UIElementsModule`):
  ```
  method | T | GetPooled (EventModifiers modifiers)
  method | T | GetPooled (NavigationDeviceType deviceType, EventModifiers modifiers)
  ```
  So `NavigationSubmitEvent evt = NavigationEventBase<NavigationSubmitEvent>.GetPooled(EventModifiers.None)`
  (reflectively: `typeof(NavigationEventBase<>).MakeGenericType(typeof(NavigationSubmitEvent)).GetMethod("GetPooled", new[]{typeof(EventModifiers)}).Invoke(null, new object[]{EventModifiers.None})`,
  or simpler — `typeof(NavigationSubmitEvent).GetMethod("GetPooled", BindingFlags.Public|BindingFlags.Static|BindingFlags.FlattenHierarchy, null, new[]{typeof(EventModifiers)}, null)`,
  which resolves the inherited generic method bound to `NavigationSubmitEvent`).
  Same for `NavigationCancelEvent`.

- **`NavigationMoveEvent`** declares its own overloads directly:
  ```
  method | NavigationMoveEvent | GetPooled (Direction direction, EventModifiers modifiers)
  method | NavigationMoveEvent | GetPooled (Direction direction, NavigationDeviceType deviceType, EventModifiers modifiers)
  method | NavigationMoveEvent | GetPooled (Vector2 moveVector, EventModifiers modifiers)
  method | NavigationMoveEvent | GetPooled (Vector2 moveVector, NavigationDeviceType deviceType, EventModifiers modifiers)
  ```

- **`ClickEvent`** only has:
  ```
  method | ClickEvent | GetPooled (PointerUpEvent pointerEvent, Int32 clickCount)
  ```
  i.e. it must be built *from* a `PointerUpEvent`, which itself needs
  constructing (pointer id, position, button state). Probed `PointerDownEvent`
  and found **no `GetPooled` declared directly on it either**
  (`PointerEventBase<T>` base almost certainly holds it, by the same pattern
  as `NavigationEventBase<T>`, but this was **not re-probed** — mark ASSUMED).

- **`KeyDownEvent`** — no `GetPooled` printed directly on it either; its base
  (`KeyboardEventBase<T>`, presumed by naming convention) was **not probed**.
  ASSUMED same generic pattern, **not confirmed**.

**Recommendation on Option A:** `NavigationSubmitEvent` / `NavigationCancelEvent`
/ `NavigationMoveEvent` are FEASIBLE-with-API (fully confirmed factories).
`ClickEvent`/`PointerDownEvent`/`KeyDownEvent` are NEEDS-LIVE-SPIKE — their
pooled-event construction chain was not fully traced.

Once constructed, dispatch with `targetElement.SendEvent(evt)` — `SendEvent`
sets the element as target and runs it through the panel's `EventDispatcher`
(confirmed: `IPanel.dispatcher` → `EventDispatcher`, with
`Dispatch(EventBase evt, IPanel panel, DispatchMode dispatchMode)` and
`ProcessEvent`/`ApplyDispatchingStrategies` machinery — real dispatch, not a
stub).

### Option B — invoke the Button's callback directly

`Button` (`UnityEngine.UIElementsModule`):
```
prop | Clickable | clickable
```

`Clickable` (`UnityEngine.UIElementsModule`):
```
field | Action    | clicked
field | Action`1  | clickedWithEventInfo
```

`clicked` and `clickedWithEventInfo` are **public instance fields**, not
properties — the multicast delegate the game registered via `button.clicked
+= Whatever` sits right there. Reflectively:
```csharp
var clickable = buttonType.GetProperty("clickable").GetValue(button);
var clickedDelegate = (Action)clickable.GetType().GetField("clicked").GetValue(clickable);
clickedDelegate?.Invoke();
```
This bypasses focus, panel, and dispatch entirely and runs exactly the
callback the game wired up — no event-construction risk.

**Recommendation on Option B: this is the most likely to work from a plugin.**
It needs zero knowledge of `EventModifiers`/`DispatchMode`/pooling internals,
works identically whether or not the element currently has focus or is under
the pointer, and Crucible's main-thread pump means invoking a `UnityEngine.UI`
callback delegate from a Harmony/BepInEx plugin context is exactly the kind
of call Crucible already does elsewhere (reflection + main-thread Unity
object access, per the brief). Option A (`SendEvent`) is the correct fallback
for elements that don't expose a `Clickable` (e.g. moving focus around with
`NavigationMoveEvent`, or Escape-ing a modal with `NavigationCancelEvent`
where no button exists to click at all).

### Option C — legacy `UnityEngine.Input` / `InputController`

Not viable — see §5. `InputController` gates and routes input, it does not
expose an injection point. Legacy `UnityEngine.Input` was not probed
separately since `InputController` (the confirmed game-side singleton)
already shows it is built entirely on the new Input System
(`InputSystemUIInputModule`, `PlayerInput`, `InputActions`, `CallbackContext`
handlers) — legacy `Input.GetKeyDown` style polling is not how this game
reads input, so simulating it would not reach the game's handlers.

---

## 5. `InputController` — full probe (via real `TypeProbe`, game type)

`InputController` is enable/disable **gating and routing** only. No
injection API. Confirmed relevant surface:

```
prop   | Boolean                  | ControllerInUse
prop   | InputController          | Instance                          (singleton)
prop   | InputSystemUIInputModule | InputModule
method | Void | RequestDisable        (eDisableRequest pRequester, String pLogMessage)
method | Void | ReleaseDisable        (eDisableRequest pRequester, String pLogMessage)
method | Void | EnableGlobalInput     ()
method | Void | DisableGlobalInput    ()
method | Void | EnableGlobalNavigation ()
method | Void | DisableGlobalNavigation ()
method | Void | EnableStickNavigation () / DisableStickNavigation ()
field  | Func`1  | OnGameplaySubmit
field  | Func`3  | OnBack
field  | Action`2| OnMove
field  | Action`1| OnNavigate
method | Void | _submitPerformed  (CallbackContext pCallback)   [private]
method | Void | _selectPerformed  (CallbackContext pCallback)   [private]
method | Void | _backPerformed    (CallbackContext pCallback)   [private]
```

The `_xPerformed` handlers are private `InputAction.CallbackContext`
consumers wired to the Input System's `PlayerInput`/`InputActions` asset —
there's no public "simulate a submit" entry point, and the private handlers
take an Input System `CallbackContext` struct that isn't trivial to
fabricate reflectively (it wraps native input-event pointers). This confirms
the player-log observation in the brief (`RequestDisable`/`ReleaseDisable`
only) — `InputController` is a gate, not an input source. **Driving the UI
must go through UIToolkit's own event system (§4 Option A/B), not through
`InputController`.**

---

## 6. Proposed commands

### `crucible_ui_dump`

1. `UnityEngine.Object.FindObjectsOfType(typeof(UIDocument))` → all live
   documents this frame.
2. For each, take `.rootVisualElement`, recursively walk via `.Children()`.
3. For each element emit: `{ type: element.GetType().Name, name:
   element.name, text: (if Button/Label/TextElement) .text, visible:
   element.visible && resolvedStyle.display != DisplayStyle.None,
   enabled: element.enabledInHierarchy, focused: ReferenceEquals(element,
   element.panel?.focusController?.focusedElement) }`.
4. Every field/property named above is a confirmed member (§1, §3) — no new
   reflective surface needed beyond what's already proven.

### `crucible_ui_click <selector>`

1. Same tree walk as `crucible_ui_dump`, filtered to `Button`-typed elements
   (`element.GetType() == typeof(Button)` or `is Button`).
2. `<selector>` matches on `name` (exact) first, falling back to `text`
   (case-insensitive substring) — covers both "the button named
   CloseButton" and "the button that says Close" without needing to know
   internal naming conventions in advance.
3. On the first visible match (§1 visibility filter applied), do Option B:
   read `.clickable.clicked` and `Invoke()` it. If `clicked` is null, try
   `.clickable.clickedWithEventInfo?.Invoke(null)`. If both are null, fall
   back to Option A: `NavigationSubmitEvent` via
   `NavigationEventBase<NavigationSubmitEvent>.GetPooled(EventModifiers.None)`
   then `button.SendEvent(evt)`.

### `crucible_ui_key <key>`

1. Map `<key>` to one of the three confirmed-constructible navigation
   events rather than a raw keycode, since that's what the game's own
   `InputController` produces internally (`OnNavigate`, `OnGameplaySubmit`,
   `OnBack` fields, §5) and is proven-constructible (§4 Option A):
   - `up`/`down`/`left`/`right` → `NavigationMoveEvent.GetPooled(Direction.X, EventModifiers.None)`
   - `enter`/`submit` → `NavigationSubmitEvent` via the generic-base path above
   - `escape`/`cancel`/`back` → `NavigationCancelEvent` via the generic-base path above
2. Dispatch via `focusedElement.SendEvent(evt)` if something has focus,
   otherwise `rootVisualElement.SendEvent(evt)` and let it bubble/trickle
   per the panel's dispatch strategy.
3. Raw `KeyDownEvent` injection is NEEDS-LIVE-SPIKE (§4) — not part of the
   v1 command since the navigation-event path is both proven and closer to
   what the game's controller-first UI (`InputController` is built on
   gamepad-style navigation actions, not raw keys) actually expects.

---

## 7. Concrete plan: pressing "Close" on the P0-16 modal

1. `docs := UnityEngine.Object.FindObjectsOfType(typeof(UIDocument))`.
2. For each `doc` in `docs`, walk `doc.rootVisualElement` recursively via
   `.Children()`.
3. Collect every element where `GetType().Name == "Button"` and
   `visible == true` and `resolvedStyle.display != DisplayStyle.None` (i.e.
   actually on screen — this is exactly the divergence problem in the brief:
   `RouterMono`'s logical route says `MAIN_MENU` but the modal is still
   rendered, so filtering on the *rendered* tree rather than trusting route
   state is the point).
4. Among those, find the one whose `.text` (confirmed `Button.text` prop)
   equals or contains `"Close"` (case-insensitive).
5. Read that button's `.clickable` prop → `Clickable` instance.
6. Reflectively read the `clicked` field (public `Action` field on
   `Clickable`) and `Invoke()` it on Crucible's main-thread pump.
7. If `clicked` is null, try `clickedWithEventInfo` the same way; if that's
   also null, fall back to constructing a `NavigationSubmitEvent` via
   `NavigationEventBase<NavigationSubmitEvent>.GetPooled(EventModifiers.None)`
   and calling `button.SendEvent(evt)` — this drives `Button`'s own
   `OnNavigationSubmit(NavigationSubmitEvent evt)` handler (confirmed method
   on `Button`), which internally triggers the same click machinery as a
   mouse click, without needing a real `PointerUpEvent`.
8. Re-run step 1-3 afterward (a fresh `crucible_ui_dump`) to verify the
   modal's `UIDocument`/root element is gone or its Button subtree no longer
   matches — that's the READ side closing the loop, the same generic
   mechanism working for every screen instead of only this one.

This sidesteps the divergence bug entirely: it never looks at
`RouterMono.GetCurrentRoute()`, only at what's actually rendered and
visible, which is exactly the signal the brief says is missing.

---

## Verdicts

- **READ (snapshot the visible UI): FEASIBLE-with-API.** `UIDocument.rootVisualElement`,
  recursive `VisualElement.Children()`, `.name`/`.text`/`.visible`/`.resolvedStyle.display`,
  `FocusController.focusedElement`, and `UnityEngine.Object.FindObjectsOfType(Type)`
  are all confirmed members with exact signatures (§1-§3).

- **WRITE (press a button / send a key): FEASIBLE-with-API for the primary
  path (Button.clickable.clicked invocation, §4 Option B) and for
  Navigation* events (§4 Option A, Submit/Cancel/Move all have confirmed
  `GetPooled` factories, reached via the shared generic base class). **NEEDS-LIVE-SPIKE**
  for raw `KeyDownEvent`/`PointerDownEvent`/`ClickEvent` injection — their
  `GetPooled` chains were not fully traced (their generic bases were not
  re-probed the way `NavigationEventBase<T>` was). This gap does not block a
  v1: the Clickable-invocation and Navigation-event paths cover both
  "press this button" and "send this navigation key" without needing raw
  keyboard/pointer event construction at all.

---

## Probed and not found / not fully chased

- `UnityEngine.UIElements.UIDocument` etc. — **not findable via the literal
  `TypeProbe` command** as shipped (see Methodology note); found instead via
  an equivalent scratch tool pointed at the Unity module DLLs directly.
- `PointerEventBase<T>` / `KeyboardEventBase<T>` — presumed (by naming
  convention, matching the confirmed `NavigationEventBase<T>` pattern) to
  hold the inherited `GetPooled` for `PointerDownEvent`/`ClickEvent`/`KeyDownEvent`,
  but **not probed** — ASSUMED, not verified, the way
  `NavigationSubmitEvent`/`NavigationCancelEvent`'s base was.
- Legacy `UnityEngine.Input` (the old `Input.GetKeyDown`-style static class)
  was not separately probed — ruled out by inference from `InputController`'s
  confirmed New-Input-System surface (§5), not by direct negative-control
  probe.
- `UQueryBuilder\`1` (returned by `UQueryExtensions.Query(...)`) itself was
  not probed for its filter/chaining members — the harness design above only
  needs the simpler `Q<T>` single-match extension methods, which are fully
  confirmed.
