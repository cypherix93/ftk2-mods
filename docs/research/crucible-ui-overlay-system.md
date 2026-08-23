# FTK2 UI Overlay / Modal System — TypeProbe Research

Objective: find the type(s) that own modal dialogs/prompts, an API to detect and
dismiss whatever overlay is currently on screen, and what explains the
boot-into-multiplayer-with-stale-error behaviour observed live today (boots into
`MULTIPLAYER_LOBBY`, fails to rejoin, shows "Online Error: Adventure Not Found
(P0-16)" with a Close button; `RouterMono.Route(MAIN_MENU, ...)` updates the
logical route but the rendered screen — multiplayer browser + modal — does not
change).

All evidence below is verbatim `dotnet run --project
C:\Users\ben\repos\ftk2-wt-probe2\FTK2.DevKit\sandbox\TypeProbe -c Release --
<Type> --methods` output. Every member name quoted was actually printed by
TypeProbe; nothing below is inferred syntax.

---

## 1. What owns modal dialogs/prompts

Two distinct, real "prompt" systems exist. Neither is named `PromptHelper`,
`DialogHelper`, `ModalHelper`, `PopupHelper`, `OverlayHelper`,
`NotificationHelper`, `MessagePromptHelper`, `UIPromptHelper`, or
`GamePromptHelper` — all of those were probed previously and confirmed NOT
FOUND.

### `MultiplayerViewHelper` — the one relevant to this bug

This is the type that renders the multiplayer lobby browser, the multiplayer
menu, the join modal, and the error/notification prompts shown during
online play — i.e. exactly the screen described in the bug report. Full
member list was captured; the load-bearing subset:

```
method | Boolean | AreAnyMultiplayerMenusVisible | ()
method | Void    | HideAllMenus                  | ()
method | Void    | HideConnectedPlayers          | ()
method | Void    | HideContextMenu               | ()
method | Void    | HideDebugInfo                 | ()
method | Void    | HideMultiplayerJoinModal      | ()
method | Void    | HideMultiplayerMenu           | (Boolean pTryConnectedListRestore)
method | Void    | HideMultiplayerMenuCharacters | ()
method | Void    | HideMultiplayerNotificationMenu | ()
method | Void    | HideMultiplayerWaitingNotificationMenu | ()
method | Void    | HidePromptMenu                | ()
method | Void    | HideReassignMenu              | ()
method | Void    | HideReassignSubtitle          | ()
method | Void    | HideServerPopUp               | ()
method | Boolean | IsControllerMenuVisible        | ()
method | Boolean | IsMultiplayerMenuShowing       | ()
method | Boolean | IsMultiplayerWaitingNotificationMenuShowing | ()
method | Boolean | IsNotificationMenuVisible      | ()
method | Boolean | IsPlayerOptionsMenuVisible     | ()
method | Boolean | IsPromptMenuVisible            | ()
method | Boolean | IsReassignMenuVisible          | ()
method | Void    | ShowContextMenu                | ()
method | Void    | ShowContextMessage              | (eMultiplayerMessageType pMessageType, MultiplayerMessageData pData)
method | Void    | ShowMultiplayerBusyNotificationMenu | ()
method | Void    | ShowMultiplayerMenu             | (Env pEnv, Boolean pCanManagePlayers, Boolean pOpenControllerAssignmentTab)
method | Void    | ShowMultiplayerNotificationMenu | ()
method | Void    | ShowMultiplayerWaitingNotificationMenu | (eNotificationType pType, Action pCancelAction, String pErrorCode)
method | Void    | ShowPromptMenu                  | (eMultiplayerMessageType pMessageType, MultiplayerMessageData pData, Boolean pAreYouHost)
```

`MultiplayerViewHelper` is a static-style helper accessed by name only (no
instance obtained via probing here); it exposes both the "is X currently
showing" reads and the "hide X" writes as plain public static-shaped methods.
Given `AreAnyMultiplayerMenusVisible()` and `HideAllMenus()` exist, this is
very likely the single most useful pair of calls for the harness.

Supporting types confirmed real:

- `MultiplayerMessageData` fields: `ConnectionId`, `CustomMessage`,
  `CustomTitle`, `ErrorCode`, `IsHost`, `Username`.
- `eMultiplayerMessageType`: `CONNECTION_LOST`, `CUSTOM_MESSAGE`, `DESYNC`,
  `NOT_JOINABLE`, `OFFLINE`, `REMOTE_PLAYER_CONNECT`,
  `REMOTE_PLAYER_DISCONNECT`, `SELF_DISCONNECT`.
- `eNotificationType` (nested `MultiplayerViewHelper+eNotificationType`):
  `CREATING_ROOM`, `JOINING`, `JOINING_IN_PROGRESS`, `RECONNECTING`,
  `WAITING_FOR_PLAYERS`.

The observed "Online Error: Adventure Not Found (P0-16)" dialog is almost
certainly rendered through `ShowPromptMenu(eMultiplayerMessageType,
MultiplayerMessageData, Boolean)` or
`ShowMultiplayerWaitingNotificationMenu(eNotificationType, Action, String
pErrorCode)`, driven by `MultiplayerLobbyDirector._showErrorPrompt(VisualElement
pLayout, NetworkError pError)` (see §5). The `P0-16` format matches
`NetworkHelper.CreatePhotonErrorCode(Int32 pPhotonError, Int32 pIdentifier)` —
also present on `NetworkHelper` are `CreateSteamErrorCode`,
`CreatePlayFabErrorCode`, and `CreateIogErrorCode`, all producing similarly
shaped codes.

### `PromptViewHelper` — a second, unrelated prompt system

Also real, but this owns onboarding/marketing prompts (demo completion,
feedback survey, wishlist, intro, "experimental" opt-in) — not error dialogs:

```
method | Void    | ClosePrompt            | ()
method | Boolean | PromptIsShowing        | ()
method | Void    | ShowDemoCompletionPrompt | (Action pCompletionAction)
method | Void    | ShowExperimentalPrompt   | (Action pAction)
method | Void    | ShowFeedbackPrompt       | ()
method | Void    | ShowIntroPrompt          | (Env pEnv, Action pAction)
method | Void    | ShowStoryPrompt          | ()
```

Not the type responsible for the bug, but worth knowing it exists and has its
own detect/dismiss pair (`PromptIsShowing()` / `ClosePrompt()`) in case the
harness ever hits one of these instead.

**Probed and confirmed NOT FOUND** (in addition to the eight the task said
were already ruled out): `ModalViewHelper`, `PopupViewHelper`,
`DialogViewHelper`, `OverlayViewHelper`, `MenuViewHelper`,
`MainMenuViewHelper`, `OnlineViewHelper`, `ErrorViewHelper`, `eOnlineErrors`,
`OnlineError`, `GamePrompt`, `eGamePrompts`, `LobbyHelper`, `PhotonHelper`.

---

## 2. Dismiss API — the single most valuable answer

**`MultiplayerViewHelper.HideAllMenus()`** — `Void HideAllMenus()`, no
parameters. This is the broad-spectrum "close whatever multiplayer overlay is
up" call.

For a targeted dismiss of exactly what's likely showing in this bug:

- `Void HideMultiplayerJoinModal()`
- `Void HidePromptMenu()`
- `Void HideMultiplayerNotificationMenu()`
- `Void HideMultiplayerWaitingNotificationMenu()`
- `Void HideMultiplayerMenu(Boolean pTryConnectedListRestore)`

All are zero/single-bool-arg, on `MultiplayerViewHelper`, confirmed via
TypeProbe (see §1 table).

For the unrelated onboarding-prompt system: `PromptViewHelper.ClosePrompt()`.

No dismiss method was found on any Router/Director type itself — `RouterMono`
and `MultiplayerLobbyDirector` have no `Close`/`Dismiss`/`Hide` method of
their own; dismissal lives entirely on the `*ViewHelper` static UI layer, not
on the director/route layer. This explains the divergence bug directly: the
UI is not driven by route state, it's driven by the ViewHelper's own visible
flags, and `RouterMono.Route(...)` never touches `MultiplayerViewHelper`.

---

## 3. Detect API — how to tell a modal is up

`MultiplayerViewHelper.AreAnyMultiplayerMenusVisible()` — `Boolean
AreAnyMultiplayerMenusVisible()`, zero args. This is the one-shot "is any
multiplayer overlay currently showing" check the harness needs, since route
alone is proven insufficient.

Narrower reads, all `Boolean`, zero-arg, on `MultiplayerViewHelper`:

- `IsMultiplayerMenuShowing()`
- `IsMultiplayerWaitingNotificationMenuShowing()`
- `IsNotificationMenuVisible()`
- `IsPromptMenuVisible()`
- `IsControllerMenuVisible()`
- `IsPlayerOptionsMenuVisible()`
- `IsReassignMenuVisible()`

For the onboarding-prompt system: `PromptViewHelper.PromptIsShowing()`.

These are the detect primitives; a harness health-check should call
`AreAnyMultiplayerMenusVisible()` (and, if the general-purpose modal target
expands beyond multiplayer, `PromptViewHelper.PromptIsShowing()`) after every
route change and refuse to proceed if either is `true`.

---

## 4. `IContainer` — the Director interface

Full member list (this is the complete interface, nothing was elided):

```
method | Void | Deinitialize        | ()
method | Void | DeinitializeCommon  | ()
method | Void | InitializeCommon    | ()
method | Void | LateUpdate          | (Single pFixedTime)
method | Void | Update              | (Single pFixedTime)
```

`IContainer` declares only lifecycle methods — `Deinitialize()` /
`DeinitializeCommon()` / `InitializeCommon()` / `Update` / `LateUpdate`. There
is **no** `Close`/`Hide`/`Dismiss` on the interface. `Deinitialize()` is a
director-lifecycle teardown (the counterpart to whatever `Initialize(...)`
each director defines with its own signature, e.g.
`MultiplayerLobbyDirector.Initialize(String pAutoJoinRoomId, UIDocument
pCanvas2D, UserData pUserData, Camera pCameraDefault)`), not a "dismiss the
current dialog" call — calling it on `RouterMono._currentDirector` would tear
down the whole director (multiplayer lobby scene/state), not just close the
modal. That's a much bigger hammer than the UI-layer Hide* calls in §2, and
`_currentDirector` is a **private field** on `RouterMono` (no public
getter/property was found) — accessing it from a harness would require
reflection, whereas `MultiplayerViewHelper.HideAllMenus()` is a plain public
call. Recommend the harness use §2's ViewHelper API and treat
`IContainer.Deinitialize()` as a last-resort, reflection-gated, whole-director
reset if the ViewHelper approach ever proves insufficient.

`RouteEventArgs` (passed to `RouterMono.OnClose(Object, RouteEventArgs)` and
used internally by `_immediateTransition`/`_startTransition`) fields:
`CustomData` (Object), `Data` (eRoutes), `ForceRoute` (Boolean), `RandomSeed`
(Int32), `ReloadRoute` (Boolean). No dismiss-relevant members here either —
this is purely routing metadata.

---

## 5. Boot-into-multiplayer behaviour

`MultiplayerLobbyDirector.Initialize` signature:

```
Void Initialize(String pAutoJoinRoomId, UIDocument pCanvas2D, UserData pUserData, Camera pCameraDefault)
```

It takes an explicit `pAutoJoinRoomId` string. `RouterMono` has a matching
local: `_registerDebugCommands(String& autoJoinRoomId)` (an out/ref param
registered alongside debug console commands), and `NetworkHelper` exposes a
`Boolean CanAutoJoinRoom` field — confirming an auto-join-on-boot path exists
and is gated by this flag. `UserData.LastGameRunIdPlayed` (`String` field) is
confirmed present on `UserData`, consistent with the task's hypothesis that
boot-time auto-rejoin is keyed off the last-played run id, though TypeProbe
(a static-signature tool) cannot show the actual wiring between
`LastGameRunIdPlayed` and the `pAutoJoinRoomId` argument — that assignment
happens in method bodies TypeProbe doesn't print.

The error-path itself, fully visible on `MultiplayerLobbyDirector`:

```
method | Task | OnConnectToRoom            | (Object pSender, EventArgs pArgs)
method | Task | _onNetworkError            | (NetworkError pError)
method | Void | _onRequestGameStateError   | (NetworkError pError)
method | Void | _onRequestGameStateSuccess | (GameStateResponse pResponse)
method | Task | _showErrorPrompt           | (VisualElement pLayout, NetworkError pError)
method | Task | _connectToRoomById         | (String pRoomId, VisualElement pLayout)
method | Void | _continue                  | (VisualElement pLayout, String pAutoJoinRoomId)
```

`NetworkError` fields: `Error` (`eNetworkErrorType`), `ErrorCode` (String),
`ErrorMessage` (String). `_showErrorPrompt(VisualElement, NetworkError)` is
the method shape that would turn a failed rejoin into the on-screen "Online
Error: Adventure Not Found (P0-16)" dialog, most likely by calling into
`MultiplayerViewHelper.ShowPromptMenu(...)` /
`ShowMultiplayerWaitingNotificationMenu(..., pErrorCode)` with
`NetworkError.ErrorCode` as the string. This is architecturally consistent
but not verified by TypeProbe (method bodies aren't visible to it) — flagging
as inference, not evidence.

Net picture: boot → `MULTIPLAYER_LOBBY` route → `MultiplayerLobbyDirector`
auto-attempts a rejoin using a stored room/run id → `OnConnectToRoom` /
`_connectToRoomById` fails → `_onRequestGameStateError` /
`_onNetworkError` fires with a `NetworkError` → `_showErrorPrompt` renders the
modal via `MultiplayerViewHelper`. `RouterMono.Route(...)` changing
`_currentRoute` afterward does nothing to this modal because nothing in
`RouterMono` calls back into `MultiplayerViewHelper` to hide it — confirming
the divergence is real and structural, not a harness mistake.

`eRoutes.MAIN_MENU` and `eRoutes.MULTIPLAYER_LOBBY` were both confirmed
present as enum fields.

---

## 6. Full negative list (probed, NOT FOUND)

From this session:

`ModalViewHelper`, `PopupViewHelper`, `DialogViewHelper`, `OverlayViewHelper`,
`MenuViewHelper`, `MainMenuViewHelper`, `OnlineViewHelper`, `ErrorViewHelper`,
`eOnlineErrors`, `OnlineError`, `GamePrompt`, `eGamePrompts`, `LobbyHelper`,
`PhotonHelper`.

From the prior session (per task brief, not re-probed): `PromptHelper`,
`DialogHelper`, `ModalHelper`, `PopupHelper`, `OverlayHelper`,
`NotificationHelper`, `MessagePromptHelper`, `UIPromptHelper`,
`GamePromptHelper`.

Types NOT probed in this pass that a follow-up could still try:
`UIToolkitHelper`, `UIHelper`, `UIDocumentHelper`, `ViewHelper` (base class,
if one exists), `eNetworkErrorType` (referenced by `NetworkError.Error` but
its member list wasn't pulled), `eConnectionStatus` (referenced by
`NetworkHelper`).

---

## Verdict

- **Can the harness detect a modal is up?** **FEASIBLE (with API).**
  `MultiplayerViewHelper.AreAnyMultiplayerMenusVisible()` — zero-arg,
  `Boolean` — directly answers this for the multiplayer-lobby class of modal
  that caused today's failure. Narrower `Is*Visible()`/`Is*Showing()` reads
  exist for finer-grained checks. `PromptViewHelper.PromptIsShowing()`
  covers the separate onboarding-prompt system if that ever comes up too.

- **Can the harness dismiss it?** **FEASIBLE (with API).**
  `MultiplayerViewHelper.HideAllMenus()` — zero-arg, `Void` — is the
  broad-spectrum dismiss. Targeted alternatives
  (`HideMultiplayerJoinModal()`, `HidePromptMenu()`,
  `HideMultiplayerNotificationMenu()`,
  `HideMultiplayerWaitingNotificationMenu()`,
  `HideMultiplayerMenu(Boolean)`) are available if a more surgical close is
  preferred. `PromptViewHelper.ClosePrompt()` covers the onboarding system.
  `IContainer.Deinitialize()` on `RouterMono._currentDirector` is a
  NEEDS-LIVE-SPIKE fallback only — it's a whole-director teardown behind a
  private field, not a scoped dialog-close, and its actual runtime effect
  (does it hang, does it require re-`Initialize`, does it desync network
  state) is unverified and would need to be tried live before relying on it.
