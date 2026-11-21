using System;
using System.Linq;
using System.Threading.Tasks;
using VoK.Sdk;
using VoK.Sdk.Ddo;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Events;
using VoK.Sdk.Properties;
using VoK.Sdk.Windows;

namespace TrailFollow
{
    /// <summary>
    /// Awesome AF가 하던 부가 기능 (1차):
    ///  - 리더 쪽: 탈것 탑승/리콜 시작을 공유 메모리에 알림
    ///  - 서브 쪽: 파티 초대, 퀘스트 공유, 부활, 비공정 초대, 그레이터 텔레포트 자동 수락
    /// 게임 대화창 버튼 위치/번호는 Awesome AF 분석값을 그대로 사용한다.
    /// </summary>
    internal static class Features
    {
        // 대화창 UI 번호 (Awesome AF 분석값)
        private const uint UiQuestShare = 26;
        private const uint UiPartyInvite = 268438136;
        private const uint UiDialogAccept = 268441489;   // 비공정 초대 등 일반 확인창
        private const uint UiResurrect = 268438350;
        private const uint UiEffectDialog = 268442527;   // 그레이터 텔레포트
        private const uint UiRecall = 268436911;
        private const uint PropRecallState = 268439052;  // 값이 4일 때 리콜 버튼 사용 가능
        private const uint UiQuestEntry = 268438489;     // 던전 입장창 (Enter 버튼)
        private const uint UiNpcDialog = 268436433;      // NPC 대화창
        private const uint UiExplorerJournal = 268438083;
        private const uint PropUsableAction = 268442345; // 아이템 사용 효과
        private const int TeleportAction = 1879254780;   // "순간이동" 효과
        private const uint ExcludedTeleportWeenie = 1879244951;
        private const uint PropObjectType = 287;         // 대상 종류 (하위16비트: 종류, 상위16비트: 세부)

        private const uint UiLoot = 268438265;           // 상자 루팅 창
        private const uint UiLootAllButton = 268443758;  // 상자 창의 [모두 가져오기] 버튼 (Chest_Loot_All_Button)
        private const uint InputLootAll = 268436024;     // 게임 입력 "LOOTALL"
        private const uint PropLootAssigned = 268449038; // 아이템이 지금 배정된 사람
        private const uint PropLootEligible = 268449037; // 배정 가능한 사람 목록
        private const uint PropCharId = 268443373;

        public static void Register(IDdoGameDataProvider ddo, Follower f)
        {
            IGameDataProvider g = (IGameDataProvider)ddo;
            var ev = ddo.EventProvider;
            if (ev == null) { Plugin.Log.Warn("[TrailFollow] 이벤트 제공자가 없어 자동 수락 기능을 끕니다"); return; }

            // ---- 리더 쪽: 탈것/리콜 시작 알림 ----
            ev.OnProgressStart.AddHandler(text =>
            {
                try
                {
                    f.OnProgress(true, text);   // 진행 막대 중에는 자동 행동을 미룸 (리콜 취소 방지)
                    if (text == "Mounting up!") f.Shared.BumpMount(f.Slot);
                    else if (text == "Recalling...") f.Shared.BumpRecall(f.Slot);
                }
                catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 진행 표시 처리 실패", ex); }
                return Task.CompletedTask;
            });
            ev.OnProgressStop.AddHandler(text =>
            {
                try { f.OnProgress(false, text); } catch { }
                return Task.CompletedTask;
            });

            // ---- 리더 점프 감지 (게임 동작 신호) ----
            ev.OnRunScript.AddHandler(args =>
            {
                try { if (args != null) f.OnScript(args.ScriptSourceIid, args.ScriptTargetIid, args.FxId); } catch { }
                return Task.CompletedTask;
            });

            // ---- 리더 쪽: Ctrl+사용키 (Awesome AF와 같은 규칙) ----
            // 키 설정에서 Ctrl+사용키가 "DoNothing"으로 묶여 있으면 게임이 무시하므로, 대신 사용 동작을 보낸다.
            // 그러면 리더가 대상을 사용하고, 서브들은 그 "사용" 동작 신호를 보고 따라 한다.
            ev.KeyDown.AddHandler(hk =>
            {
                try
                {
                    if (hk == null || !hk.Control || hk.Key == 0 || hk.Key == 162 || hk.Key == 163) return Task.CompletedTask;
                    var km = g.Keymap;
                    if (km == null) return Task.CompletedTask;
                    var useKey = km.GetHotkey(InputActions.Use.ToString()) ?? km.GetHotkey(InputActions.Interact.ToString());
                    if (useKey == null || useKey.DirectInputKey != hk.DirectInputKey || useKey.Alt != hk.Alt || useKey.Shift != hk.Shift) return Task.CompletedTask;
                    if (km.GetAction(hk) == AltInputActions.DoNothing.ToString())
                    {
                        ddo.SendInput((uint)InputActions.Interact);
                        Log(f, "Ctrl+사용 - 리더가 대상 사용");
                    }
                }
                catch { }
                return Task.CompletedTask;
            });

            // ---- 리더 쪽: 입장창 Enter 클릭 알림 ----
            ev.OnRewardOpened.AddHandler(id => { try { f.OnRewardOpened(id); } catch { } return Task.CompletedTask; });
            ev.OnRewardClosed.AddHandler(id => { try { f.OnRewardClosed(id); } catch { } return Task.CompletedTask; });

            ev.OnUiEvent.AddHandler(e =>
            {
                try
                {
                                        if (e != null && e.UiElementId == UiQuestEntry && e.MouseEventType.HasValue && (int)e.MouseEventType.Value == 4 && e.Event == "Enter")
                    {
                        f.LastEnterBroadcastAt = f.NowSeconds;
                        f.Shared.BumpPortal(f.Slot);
                        Log(f, "입장 (Enter) - 서브들에게 알림");
                    }
                }
                catch { }
                return Task.CompletedTask;
            });

            // ---- 리더 쪽: 순간이동 아이템 사용 알림 ----
            ev.OnHotbarUsage.AddHandler(slot =>
            {
                try
                {
                    if (slot == null || slot.ItemIid == 0) return Task.CompletedTask;
                    var item = g.GetEntity(slot.ItemIid);
                    if (item == null || item.WeenieId == ExcludedTeleportWeenie) return Task.CompletedTask;
                    int? action = item.PropertyCollection?.GetInt32PropertyValue(PropUsableAction);
                    if (action == TeleportAction)
                    {
                        f.Shared.BumpTeleport(f.Slot, slot.ReferenceDid);
                        Log(f, "순간이동 아이템 사용 - 서브들에게 알림");
                    }
                }
                catch { }
                return Task.CompletedTask;
            });

            // ---- 리더 쪽: Ctrl을 누른 채 고른 대화 선택지 알림 ----
            ev.OnDialogAction.AddHandler(option =>
            {
                try
                {
                    // 지금 조작 중인 창(리더)만 알림. Ctrl을 누르고 있거나 Ctrl로 시작한 대화가 이어지는 중(마지막 동작 후 60초)일 때
                    if (option.HasValue && f.IsFocused && (Native.CtrlDown || f.InConversation))
                    {
                        f.TouchConversation();
                        f.LastDialogBroadcastAt = f.NowSeconds;
                        f.Shared.BumpDialog(f.Slot, option.Value);
                        Log(f, $"대화 선택지 {option.Value + 1}번 - 서브들에게 알림");
                    }
                }
                catch { }
                return Task.CompletedTask;
            });

            // ---- 리더 쪽: Ctrl을 누른 채 상자를 열면 서브들에게 알림 (v0.8.17) ----
            ev.OnChestOpen.AddHandler(chestId =>
            {
                try { f.OnChestOpened(chestId); } catch { }
                return Task.CompletedTask;
            });

            // ---- 파티 초대: 이 PC의 내 캐릭터가 보낸 것만 ----
            ev.OnPartyInvitation.AddHandler((inviterId, inviterName) =>
            {
                var s = f.Settings;
                if (s == null || !s.AcceptPartyInvites) return Task.CompletedTask;
                if (!f.Shared.IsKnownChar(inviterId) && !f.Shared.IsKnownName(inviterName)) return Task.CompletedTask;
                Log(f, $"파티 초대 수락 ({inviterName})");
                return ClickAsync(ddo, g, UiPartyInvite, r => (112, Height(r) - 37), 300);
            });

            // ---- 퀘스트 공유 ----
            ev.OnQuestShare.AddHandler(questId =>
            {
                var s = f.Settings;
                if (s == null || !s.AcceptQuestShares) return Task.CompletedTask;
                Log(f, "퀘스트 공유 수락");
                return ClickAsync(ddo, g, UiQuestShare, r => (168, Height(r) - 37), 300);
            });

            // ---- 비공정 초대: 내 캐릭터가 보낸 것만 ----
            ev.OnAirshipInvitation.AddHandler(name =>
            {
                var s = f.Settings;
                if (s == null || !s.AcceptAirshipAndTeleports) return Task.CompletedTask;
                if (!f.Shared.IsKnownName(name)) return Task.CompletedTask;
                Log(f, $"비공정 초대 수락 ({name})");
                return ClickAsync(ddo, g, UiDialogAccept, r => (Width(r) / 2 - 46, Height(r) - 36), 300);
            });

            // ---- 부활 ----
            ev.OnResurrectAttempt.AddHandler(msg =>
            {
                var s = f.Settings;
                if (s == null || !s.AcceptResurrections) return Task.CompletedTask;
                Log(f, $"부활 수락 ({msg?.Caster})");
                return ClickAsync(ddo, g, UiResurrect, r => (204, Height(r) - 40), 200);
            });

            // ---- 그레이터 텔레포트: 내 캐릭터가 건 "Teleport to ..." 효과만 ----
            ev.OnEffectDialog.AddHandler(dlg =>
            {
                var s = f.Settings;
                if (s == null || !s.AcceptAirshipAndTeleports || dlg == null) return Task.CompletedTask;
                string effectName = null;
                try { effectName = g.PropertyMaster?.GetPropertyCollection((uint)dlg.EffectDid)?.Name; } catch { }
                if (effectName == null || !effectName.StartsWith("Teleport to ")) return Task.CompletedTask;
                if (!f.Shared.IsKnownChar(dlg.EffectOriginator)) return Task.CompletedTask;
                Log(f, $"텔레포트 수락 ({effectName})");
                return ClickAsync(ddo, g, UiEffectDialog, r => (Width(r) / 2 - 38, Height(r) - 32), 500);
            });
        }

        /// <summary>리더를 따라 리콜 (Awesome AF와 같은 방식: 리콜 버튼 클릭)</summary>
        /// <summary>리콜 버튼을 누를 수 있는 상태면 누르고 true. (시전·전투 중이라 안 되면 false - 호출한 쪽에서 다시 시도)</summary>
        public static bool TryRecall(IDdoGameDataProvider ddo, string charName, bool quiet = false)
        {
            try
            {
                IGameDataProvider g = (IGameDataProvider)ddo;
                uint? state = g.GetCurrentCharacter()?.PropertyCollection?.GetEnumProperty(PropRecallState)?.UInt32Value;
                if (state != 4) { if (!quiet) Plugin.Log.Info($"[TrailFollow] {charName}: 지금은 리콜할 수 없는 상태 ({state}) - 될 때까지 다시 시도"); return false; }
                Plugin.Log.Info($"[TrailFollow] {charName}: 리더를 따라 리콜");
                _ = RecallAsync(ddo, g, charName);
                return true;
            }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 리콜 실패", ex); return false; }
        }

        /// <summary>리콜 버튼 클릭 → 확인창이 뜨면 확인 버튼 클릭 (Awesome AF와 같은 순서)</summary>
        private static async Task RecallAsync(IDdoGameDataProvider ddo, IGameDataProvider g, string charName)
        {
            bool clicked = await ClickAsync(ddo, g, UiRecall, r => (Width(r) - 40, Height(r) - 20), 100);
            if (!clicked) return;
            bool confirmed = await ClickAsync(ddo, g, UiDialogAccept, r => (Width(r) / 2 - 46, Height(r) - 36), 50);
            Plugin.Log.Info($"[TrailFollow] {charName}: 리콜 확인창 {(confirmed ? "확인 클릭" : "을 찾지 못함")}");
        }

        // =====================================================================
        //  2·3차 동작 (서브 쪽)
        // =====================================================================

        /// <summary>리더가 입장창에서 Enter를 누름 → 1초 뒤 내 입장창의 Enter 클릭</summary>
        public static void EnterWithLeader(IDdoGameDataProvider ddo, string charName)
        {
            IGameDataProvider g = (IGameDataProvider)ddo;
            Plugin.Log.Info($"[TrailFollow] {charName}: 리더를 따라 입장 시도");
            _ = ClickAsync(ddo, g, UiQuestEntry, r => (80, 560), 1000);
        }

        /// <summary>리더가 Ctrl+사용한 대상을 선택해서 사용 (함정은 설정에 따라 제외)</summary>
        // Ctrl 없이도 따라 사용하는 대상 종류 (Awesome AF 분석값: 문/입구/포탈 계열)
        private static readonly uint[] AutoUseTypes = { 1376385, 1638529, 5243009 };
        private const uint PropAutoUseFlag = 268438318;   // 종류 129일 때: 1이면 사용 가능한 장치
        private const uint PropAutoUseKind = 268441752;   // 종류 129일 때: 8이면 입구 계열

        /// <summary>대상이 "Ctrl 없이도 따라 사용"하는 종류(문·입구·포탈)인지</summary>
        private static bool IsAutoUseObject(IGameDataProvider g, IEntity e)
        {
            if (e?.WeenieId == null) return false;
            var props = g.PropertyMaster?.GetPropertyCollection(e.WeenieId.Value);
            uint? type = props?.GetEnumProperty(PropObjectType)?.UInt32Value;
            if (!type.HasValue) return false;
            if (AutoUseTypes.Contains(type.Value)) return true;
            if (type.Value == 129)
            {
                bool flag = props.GetBytePropertyValue(PropAutoUseFlag) == 1;
                uint? kind = props.GetEnumProperty(PropAutoUseKind)?.UInt32Value;
                return flag && kind == 8;
            }
            return false;
        }

        public static void UseObject(IDdoGameDataProvider ddo, ulong target, bool includeTraps, bool autoOnly, string charName)
        {
            IGameDataProvider g = (IGameDataProvider)ddo;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!(g.GetAllEntityIds()?.Contains(target) ?? false)) { Plugin.Log.Info($"[TrailFollow] {charName}: 리더가 사용한 대상이 보이지 않음"); return; }
                    if (autoOnly)
                    {
                        var ent = g.GetEntity(target);
                        if (!IsAutoUseObject(g, ent)) return;   // 문·입구 종류가 아니면 Ctrl 없이는 따라 하지 않음
                    }
                    SelectSafe(ddo, target);
                    await Task.Delay(150);
                    var obj = g.GetSelectedObject();
                    if (obj == null || obj.InstanceId != target) return;
                    uint? type = obj.WeeniePropertyCollection?.GetEnumProperty(PropObjectType)?.UInt32Value;
                    if (!includeTraps && type.HasValue && (type.Value & 0xFFFF) == 129 && (type.Value >> 16) == 22)
                    {
                        Plugin.Log.Info($"[TrailFollow] {charName}: 함정이라 사용하지 않음");
                        return;
                    }
                    CursorWatch.Note("대상 사용");
                    ddo.UseSelectedObject();
                    Plugin.Log.Info($"[TrailFollow] {charName}: 대상 사용 ({obj.Name}{(autoOnly ? ", 문/입구 자동" : "")})");
                }
                catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] Ctrl+사용 따라 하기 실패", ex); }
            });
        }

        /// <summary>리더가 고른 대화 선택지(0부터)를 내 NPC 대화창에서 고름</summary>
        private static readonly object SelectSync = new object();

        /// <summary>
        /// 대상 선택. v0.7.5: 서브가 대상을 선택할 때 실제 마우스 커서가 움직이는 경우가 있어(메인에서 버튼을 누르려던 커서가 튐),
        /// 선택 직후 커서가 크게 움직였으면 원래 자리로 되돌림
        /// </summary>
        public static void SelectSafe(IDdoGameDataProvider ddo, ulong id, Func<bool> stillWanted = null)
        {
            lock (SelectSync)
            {
                // v0.8.26: 차례를 기다리는 사이에 더 새로운 선택 요청이 생겼으면 이 요청은 버림
                if (stillWanted != null && !stillWanted()) return;
                // v0.8.7: 이미 선택돼 있으면 다시 선택하지 않음 (선택할 때마다 커서가 튈 수 있으므로 횟수를 줄임)
                try { if (((IGameDataProvider)ddo).GetSelectedObjectId() == id) return; } catch { }
                // 사용자가 마우스 버튼을 누르고 있으면(클릭 중) 뗄 때까지 최대 0.6초 기다림
                for (int i = 0; i < 30 && Native.MouseButtonDown; i++) System.Threading.Thread.Sleep(20);

                // v0.8.34: 진단 기록으로 확인 - 예전 방식(선택 뒤 0.15초 동안 80px 넘게 움직이면 되돌림)은 사용자가 마우스를
                //          보통 속도로 움직이기만 해도 "튄 것"으로 오인해 커서를 계속 끌어다 놓았음 (4분에 30번 넘게).
                //          오른쪽 버튼으로 시점을 돌리는 중에도 게임과 서로 커서를 잡아당겼음.
                //          이제는 "마우스가 가만히 있다가, 한 번에 크게 움직이고, 그 자리에 멈춰 있을 때"만 되돌림 (사람 손은 멈추지 않고 계속 움직임)
                Native.GetCursorPos(out var before);
                System.Threading.Thread.Sleep(16);
                Native.GetCursorPos(out var last);
                bool userMoving = Math.Abs(last.X - before.X) + Math.Abs(last.Y - before.Y) > 2;
                CursorWatch.Note("대상 선택");
                ddo.SelectObject(id);
                bool fixedOnce = false;
                if (!userMoving && !Native.MouseButtonDown)
                {
                    for (int i = 0; i < 15; i++)
                    {
                        System.Threading.Thread.Sleep(10);
                        if (Native.MouseButtonDown) break;   // 클릭·시점 돌리기 시작 - 건드리지 않음
                        Native.GetCursorPos(out var now);
                        int dx = now.X - last.X, dy = now.Y - last.Y;
                        if (dx * dx + dy * dy > 80 * 80)
                        {
                            // 한 번 더 확인: 그 자리에 그대로 멈춰 있어야 프로그램이 옮긴 것
                            System.Threading.Thread.Sleep(12);
                            Native.GetCursorPos(out var now2);
                            bool stayed = Math.Abs(now2.X - now.X) + Math.Abs(now2.Y - now.Y) <= 2;
                            if (stayed && !Native.MouseButtonDown)
                            {
                                CursorWatch.Note("대상 선택 뒤 커서 되돌림");
                                Native.SetCursorPos(last.X, last.Y);
                                fixedOnce = true;
                            }
                            Diag.Write("_cursor", $"SELECT 대상 선택 {(i + 1) * 10}ms 뒤 커서가 ({last.X},{last.Y})->({now.X},{now.Y})로 움직임 - {(fixedOnce ? "가만히 있다가 튄 것이라 되돌림" : "사용자가 움직이는 중이라 그대로 둠")}");
                            break;
                        }
                        if (Math.Abs(dx) + Math.Abs(dy) > 2) break;   // 사용자가 마우스를 움직이기 시작 - 그만 지켜봄
                    }
                }
                if (fixedOnce && (DateTime.Now - _lastCursorLog).TotalSeconds > 10)
                {
                    _lastCursorLog = DateTime.Now;
                    Plugin.Log.Info("[TrailFollow] 대상 선택 중 커서가 튀어서 되돌림");
                }
            }
        }
        private static DateTime _lastCursorLog = DateTime.MinValue;

        private static readonly System.Threading.SemaphoreSlim CursorLock = new System.Threading.SemaphoreSlim(1, 1);

        public static void ChooseDialogOption(IDdoGameDataProvider ddo, int option, string charName)
        {
            if (option < 0 || option > 9) return;
            IGameDataProvider g = (IGameDataProvider)ddo;
            _ = Task.Run(async () =>
            {
                try
                {
                    RECT? rect = null;
                    for (int i = 0; i < 16 && !rect.HasValue; i++)   // 서브 대화창이 늦게 열릴 수 있어 최대 4초 기다림
                    {
                        try { rect = g.GetUiElementLocation(UiNpcDialog); } catch { }
                        if (!rect.HasValue) await Task.Delay(250);
                    }
                    if (!rect.HasValue) { Plugin.Log.Info($"[TrailFollow] {charName}: 대화창이 없어 선택 생략"); return; }

                    // 대화창 위로 마우스를 옮긴 뒤 번호 키 입력 (Awesome AF와 같은 방식)
                    if (!Enum.TryParse($"POPUPMENU_{option + 1}_KEY", out InputActions action)) return;
                    var hk = g.Keymap?.GetHotkey(action.ToString());
                    if (hk == null) { Plugin.Log.Info($"[TrailFollow] {charName}: 대화 선택 {option + 1}번 키가 설정되어 있지 않음"); return; }
                    // v0.7.0: 대화창 위로 마우스를 옮기는 동작이 실제 커서를 움직여서, 그 순간 사용자가 클릭하려던 곳이 어긋났음
                    //         → 커서 위치를 기억해 두고, 키를 보낸 직후 원래 자리로 되돌림 (이동 시간도 최소로)
                    await CursorLock.WaitAsync();   // 서브 여러 명이 동시에 하면 커서 복원이 꼬이므로 한 명씩
                    Native.GetCursorPos(out var saved);
                    try
                    {
                        var move = new MouseEvent { EventType = (MouseEventType)11, X = (short)(rect.Value.Left + 100), Y = (short)(rect.Value.Bottom - 50) };
                        CursorWatch.Note("대화 선택지 (대화창으로 마우스 이동)");
                        g.MouseEvent(move);
                        await Task.Delay(5);
                        g.SendKey(hk);
                    }
                    finally
                    {
                        await Task.Delay(5);
                        Native.SetCursorPos(saved.X, saved.Y);
                        CursorLock.Release();
                    }
                    Plugin.Log.Info($"[TrailFollow] {charName}: 리더를 따라 대화 선택지 {option + 1}번");
                }
                catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 대화 선택 따라 하기 실패", ex); }
            });
        }

        /// <summary>서브 쪽: 리더 대화창이 닫혔을 때 내 NPC 대화창도 닫기 (Esc 키)</summary>
        public static void CloseNpcDialog(IDdoGameDataProvider ddo, string charName)
        {
            IGameDataProvider g = (IGameDataProvider)ddo;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300);   // 서브 쪽 선택이 막 처리되는 중일 수 있어 잠시 기다림
                    if (!IsNpcDialogOpen(ddo)) return;   // 이미 닫혀 있으면 아무것도 안 함 (Esc로 게임 메뉴가 열리지 않게)
                    HotKeyEventArgs hk = null;
                    try { hk = g.Keymap?.GetHotkey(AltInputActions.EscapeKey.ToString()); } catch { }
                    if (hk == null || hk.Key <= 0) hk = HotKeyEventArgs.FromKeys(27, false, false, false);   // 기본: Esc
                    g.SendKey(hk);
                    Plugin.Log.Info($"[TrailFollow] {charName}: 리더를 따라 대화창 닫기");
                }
                catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 대화창 닫기 실패", ex); }
            });
        }

        public static bool IsQuestEntryOpen(IDdoGameDataProvider ddo)
        {
            try { return ((IGameDataProvider)ddo).GetUiElementLocation(UiQuestEntry).HasValue; }
            catch { return false; }
        }

        public static bool IsNpcDialogOpen(IDdoGameDataProvider ddo)
        {
            try { return ((IGameDataProvider)ddo).GetUiElementLocation(UiNpcDialog).HasValue; }
            catch { return false; }
        }

        /// <summary>탐험 일지 창이 떠 있으면 닫기</summary>
        public static void CloseExplorerJournal(IDdoGameDataProvider ddo)
        {
            try
            {
                IGameDataProvider g = (IGameDataProvider)ddo;
                RECT? r = g.GetUiElementLocation(UiExplorerJournal);
                if (!r.HasValue) return;
                var me = new MouseEvent { EventType = (MouseEventType)1, X = 190, Y = (short)(Height(r.Value) - 62) };
                ddo.SendUiEvent(UiExplorerJournal, me);
            }
            catch { }
        }

        /// <summary>
        /// 상자를 열면 나에게 배정된 아이템을 모두 리더에게 넘김 (Awesome AF 방식).
        /// v0.8.18: 예전엔 3초를 기다렸는데, 이제 0.3초부터 아이템이 보이는 즉시 넘기고 한 개씩 빠르게 처리
        /// </summary>
        public static void PassLootLater(IDdoGameDataProvider ddo, ulong chestId, ulong leaderId, string charName)
        {
            IGameDataProvider g = (IGameDataProvider)ddo;
            // v0.8.28: 상자 열림 신호가 두 번 오는 경우가 있어(기록으로 확인) 같은 상자를 두 작업이 동시에 처리하며 서로 클릭을 망쳤음 → 하나만 실행
            string busyKey = charName + ":" + chestId;
            if (!LootBusy.TryAdd(busyKey, 0)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300);
                    ulong me = g.GetCurrentCharacterId() ?? 0;
                    // 루팅 창과 내 몫 아이템이 보일 때까지 최대 5초 기다림
                    IEntity Mine() => g.GetEntities()?.FirstOrDefault(e => e != null && e.OwnerId == chestId && AssignedTo(e) == me);
                    for (int i = 0; i < 50 && (!g.GetUiElementLocation(UiLoot).HasValue || Mine() == null); i++) await Task.Delay(100);

                    // v0.8.28: 아이템이 아직 들어오는 중에 누르면 첫 클릭이 헛돌았음(기록으로 확인) → 내 몫 개수가 0.3초 동안 그대로일 때까지 기다림 (최대 2초)
                    {
                        int last = -1, same = 0;
                        for (int i = 0; i < 20 && same < 3; i++)
                        {
                            int c = g.GetEntities()?.Count(e => e != null && e.OwnerId == chestId && AssignedTo(e) == me) ?? 0;
                            if (c == last && c > 0) same++; else { same = 0; last = c; }
                            await Task.Delay(100);
                        }
                    }

                    // v0.8.24: 진단 기록 - 상자 안 아이템 전체와 배정 상태
                    try
                    {
                        RECT? lr = g.GetUiElementLocation(UiLoot);
                        var all = g.GetEntities()?.Where(e => e != null && e.OwnerId == chestId).ToList();
                        Diag.Write(charName, $"LOOT 시작 상자={chestId} 나={me} 리더={leaderId} 창={(lr.HasValue ? $"{lr.Value.Left},{lr.Value.Top},{lr.Value.Right},{lr.Value.Bottom}" : "없음")} 아이템={all?.Count ?? 0}");
                        if (all != null)
                            for (int ai = 0; ai < all.Count; ai++)
                                Diag.Write(charName, $"LOOT   [{ai}] {all[ai].Name ?? "?"} id={all[ai].InstanceId} 배정={AssignedTo(all[ai])} 가능=[{EligibleDump(all[ai])}]");
                    }
                    catch { }

                    // v0.8.26: 진단 기록으로 확인된 문제 수정.
                    //  창의 첫 줄(Y=108)을 눌러 넘기면 "창에서 맨 위에 있는 내 아이템"이 넘어가는데, 예전에는 엉뚱한 아이템 하나만
                    //  지켜보다가 "안 넘어감"으로 잘못 판단해서 2초씩 기다리고, 넘길 수 있는 아이템을 남긴 채 끝냈음.
                    //  이제는 "내 몫 아이템 수가 줄었는지"로 판단하고, 줄어든 즉시 다음 아이템으로 넘어감.
                    int MyCount() => g.GetEntities()?.Count(e => e != null && e.OwnerId == chestId && AssignedTo(e) == me) ?? 0;
                    string MyNames() { try { return string.Join(" / ", g.GetEntities()?.Where(e => e != null && e.OwnerId == chestId && AssignedTo(e) == me).Select(e => e.Name ?? "?") ?? new string[0]); } catch { return "?"; } }
                    int passed = 0, fails = 0;
                    for (int guard = 0; guard < 80 && fails < 2; guard++)
                    {
                        if (!g.GetUiElementLocation(UiLoot).HasValue) break;   // 루팅 창이 닫힘
                        var item = Mine();
                        if (item == null) break;   // 더 넘길 아이템 없음

                        // 배정 가능 목록이 늦게 로드될 수 있으므로 최대 500ms 재시도
                        int? idx = null;
                        for (int ew = 0; ew < 5 && idx == null; ew++)
                        {
                            idx = LeaderIndex(item, me, leaderId);
                            if (idx == null) { await Task.Delay(100); item = Mine() ?? item; }
                        }
                        if (idx == null) break;   // 리더가 배정 가능 목록에 없음 - 넘길 수 없음

                        int before = MyCount();
                        // 창의 첫 줄에서 분배 목록을 열고 리더를 선택
                        CursorWatch.Note("상자 아이템 넘기기 클릭");
                        ddo.SendUiEvent(UiLoot, new MouseEvent { EventType = (MouseEventType)1, X = 138, Y = 108 });
                        await Task.Delay(fails == 0 ? 150 : 400);   // 한 번 실패했으면 분배 목록이 열릴 시간을 더 줌
                        ddo.SendUiEvent(UiLoot, new MouseEvent { EventType = (MouseEventType)1, X = 138, Y = (short)(128 + 14 * idx.Value) });

                        // 내 몫이 하나 줄 때까지 최대 1.5초 대기 (보통 0.4초쯤)
                        var started = DateTime.UtcNow;
                        bool moved = false;
                        int now = before;
                        while ((DateTime.UtcNow - started).TotalSeconds < 1.2)
                        {
                            await Task.Delay(40);
                            now = MyCount();
                            if (now < before) { moved = true; break; }
                        }
                        Diag.Write(charName, $"LOOT 넘기기 목록위치={idx.Value} 내몫 {before}→{now} 결과={(moved ? "넘어감" : "그대로")} ({(DateTime.UtcNow - started).TotalMilliseconds:0}ms)");
                        if (moved) { passed++; fails = 0; await Task.Delay(60); }
                        else
                        {
                            fails++;
                            Diag.Write(charName, $"LOOT 안 넘어간 내 몫: {MyNames()}");
                            // 열린 채 남은 분배 목록을 닫고 다시 시도
                            ddo.SendUiEvent(UiLoot, new MouseEvent { EventType = (MouseEventType)1, X = 10, Y = 10 });
                            await Task.Delay(250);
                        }
                    }
                    Plugin.Log.Info($"[TrailFollow] {charName}: 상자 아이템 {passed}개를 리더에게 넘김");
                    Diag.Write(charName, $"LOOT 넘기기 끝 - {passed}개 넘김, 내몫 남음 {MyCount()}");

                    // v0.8.21: 리더에게 넘길 수 없는 아이템(나에게 남은 것)은 내가 가져감 - 최대 3회 시도
                    await Task.Delay(300);
                    int left = g.GetEntities()?.Count(e => e != null && e.OwnerId == chestId && AssignedTo(e) == me) ?? 0;
                    if (left > 0 && g.GetUiElementLocation(UiLoot).HasValue)
                    {
                        int still = left;
                        for (int lootTry = 0; lootTry < 3 && still > 0; lootTry++)
                        {
                            if (!g.GetUiElementLocation(UiLoot).HasValue) break;
                            // 먼저 LOOTALL 입력 시도
                            ddo.SendInput(InputLootAll);
                            await Task.Delay(500);
                            still = g.GetEntities()?.Count(e => e != null && e.OwnerId == chestId && AssignedTo(e) == me) ?? 0;
                            if (still > 0)
                            {   // 입력이 안 먹으면 창의 [모두 가져오기] 버튼을 직접 누름
                                RECT? r = null;
                                try { r = g.GetUiElementLocation(UiLootAllButton); } catch { }
                                if (r.HasValue)
                                {
                                    ddo.SendUiEvent(UiLootAllButton, new MouseEvent { EventType = (MouseEventType)1, X = (short)(Width(r.Value) / 2), Y = (short)(Height(r.Value) / 2) });
                                    await Task.Delay(500);
                                    still = g.GetEntities()?.Count(e => e != null && e.OwnerId == chestId && AssignedTo(e) == me) ?? 0;
                                }
                            }
                        }
                        Plugin.Log.Info($"[TrailFollow] {charName}: 리더에게 넘길 수 없는 아이템 {left}개 - 내가 가져감{(still > 0 ? $" (아직 {still}개 남음)" : "")}");
                        Diag.Write(charName, $"LOOT 가져오기 대상={left} 남음={still} 창열림={g.GetUiElementLocation(UiLoot).HasValue} 남은것: {MyNames()}");
                    }
                }
                catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 상자 아이템 넘기기 실패", ex); }
                finally { LootBusy.TryRemove(busyKey, out _); }
            });
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LootBusy = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        private static ulong? AssignedTo(IEntity e)
        {
            try { return e.PropertyCollection?.GetArrayProperty(PropLootAssigned)?.GetUInt64PropertyValue(PropCharId); }
            catch { return null; }
        }

        /// <summary>분배 목록에서 리더의 위치 (나를 뺀 1부터 번호, Awesome AF와 같은 계산)</summary>
        private static int? LeaderIndex(IEntity item, ulong me, ulong leader)
        {
            var arr = item.PropertyCollection?.GetArrayProperty(PropLootEligible);
            var list = arr?.Properties?.ToList();
            if (list == null || list.Count == 0)
            {
                // v0.8.23: 진단 로그 - 배정 가능 목록이 없는 이유 추적
                Plugin.Log.Info($"[TrailFollow] LeaderIndex: 배정 가능 목록 없음 (아이템 {item.Name ?? "?"}, prop존재={arr != null})");
                return null;
            }
            int offset = 1;
            for (int i = 0; i < list.Count; i++)
            {
                ulong? id = (list[i] as IArrayProperty)?.GetUInt64PropertyValue(PropCharId);
                if (id == me) offset = 0;
                else if (id == leader) return i + offset;
            }
            // v0.8.23: 진단 로그 - 리더가 목록에 없는 경우
            Plugin.Log.Info($"[TrailFollow] LeaderIndex: 리더({leader})가 목록에 없음 (아이템 {item.Name ?? "?"}, {list.Count}명)");
            return null;
        }

        /// <summary>v0.8.24: 진단용 - 아이템의 배정 가능 목록(캐릭터 ID)을 순서대로</summary>
        private static string EligibleDump(IEntity item)
        {
            try
            {
                var list = item.PropertyCollection?.GetArrayProperty(PropLootEligible)?.Properties?.ToList();
                if (list == null) return "없음";
                return string.Join(",", list.Select(p => ((p as IArrayProperty)?.GetUInt64PropertyValue(PropCharId))?.ToString() ?? "?"));
            }
            catch { return "오류"; }
        }

        private static int Width(RECT r) => r.Right - r.Left;
        private static int Height(RECT r) => r.Bottom - r.Top;

        /// <summary>창이 뜰 때까지 기다렸다가(최대 약 2초) 창 안의 지정 위치를 클릭</summary>
        private static async Task<bool> ClickAsync(IDdoGameDataProvider ddo, IGameDataProvider g, uint uiId, Func<RECT, (int x, int y)> pos, int firstDelayMs)
        {
            try
            {
                await Task.Delay(firstDelayMs);
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    RECT? rect = null;
                    try { rect = g.GetUiElementLocation(uiId); } catch { }
                    if (rect.HasValue)
                    {
                        var (x, y) = pos(rect.Value);
                        var me = new MouseEvent { EventType = (MouseEventType)1, X = (short)x, Y = (short)y };
                        CursorWatch.Note("창 버튼 자동 클릭 (수락 등)");
                        ddo.SendUiEvent(uiId, me);
                        return true;
                    }
                    await Task.Delay(250);
                }
                Plugin.Log.Info($"[TrailFollow] 확인창({uiId})을 찾지 못해 자동 클릭 생략");
            }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 자동 클릭 실패", ex); }
            return false;
        }

        private static void Log(Follower f, string what) => Plugin.Log.Info($"[TrailFollow] {f.CharName}: {what}");
    }
}
