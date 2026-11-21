using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TrailFollow
{
    /// <summary>Dungeon Helper 창 안에 표시되는 설정 화면. v0.8.29: 탭으로 나누고 긴 설명은 마우스를 올리면 뜨는 도움말로 옮김</summary>
    internal sealed class SettingsForm : Form
    {
        private readonly Follower _f;
        private readonly Label _title = new Label { AutoSize = true, Font = new Font("맑은 고딕", 11f, FontStyle.Bold) };
        private readonly ToolTip _tip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300, ReshowDelay = 100 };
        private readonly ComboBox _lang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };

        // 공통 (모든 캐릭터)
        private readonly NumericUpDown _near = new NumericUpDown { Minimum = 0m, Maximum = 50, DecimalPlaces = 1, Increment = 0.5m, Width = 70 };
        private readonly NumericUpDown _far = new NumericUpDown { Minimum = 0.5m, Maximum = 120, DecimalPlaces = 1, Increment = 0.5m, Width = 70 };

        private readonly NumericUpDown _afkMin = new NumericUpDown { Minimum = 1, Maximum = 60, Width = 50 };
        private readonly NumericUpDown _afkBar = new NumericUpDown { Minimum = 1, Maximum = 10, Width = 45 };
        private readonly NumericUpDown _afkSlot = new NumericUpDown { Minimum = 1, Maximum = 10, Width = 45 };
        private readonly NumericUpDown _atkBar = new NumericUpDown { Minimum = 1, Maximum = 10, Width = 45 };
        private readonly NumericUpDown _atkSlot = new NumericUpDown { Minimum = 1, Maximum = 10, Width = 45 };
        private readonly NumericUpDown _buffGap = new NumericUpDown { Minimum = 0.5m, Maximum = 30, Increment = 0.5m, DecimalPlaces = 1, Width = 55 };

        // 캐릭터별
        private readonly NumericUpDown _hotbar = new NumericUpDown { Minimum = 1, Maximum = 10, Width = 50 };
        private readonly NumericUpDown _buffBar = new NumericUpDown { Minimum = 1, Maximum = 10, Width = 50 };
        private readonly CheckBox _showBar = new CheckBox { Text = Lang.T("화면에 이 캐릭터의 창 표시", "Show this character's window on screen"), AutoSize = true };
        private readonly CheckBox _jumps = new CheckBox { Text = Lang.T("리더를 따라 점프", "Jump with the leader"), AutoSize = true };
        private readonly CheckBox _mount = new CheckBox { Text = Lang.T("탈것 같이 타기", "Mount with the leader"), AutoSize = true };
        private readonly CheckBox _recall = new CheckBox { Text = Lang.T("리콜 같이 하기", "Recall with the leader"), AutoSize = true };
        private readonly CheckBox _ctrlUse = new CheckBox { Text = Lang.T("리더가 Ctrl+사용한 대상을 같이 사용", "Use what the leader Ctrl+uses"), AutoSize = true };
        private readonly CheckBox _doorUse = new CheckBox { Text = Lang.T("문·던전 입구·포탈을 같이 사용 (Ctrl 없이도)", "Use doors, dungeon entrances and portals too (no Ctrl needed)"), AutoSize = true };
        private readonly CheckBox _enter = new CheckBox { Text = Lang.T("입장창 Enter 같이 누르기", "Press Enter on the entrance dialog too"), AutoSize = true };
        private readonly CheckBox _tele = new CheckBox { Text = Lang.T("순간이동 아이템 같이 사용", "Use teleport items too"), AutoSize = true };
        private readonly CheckBox _dialog = new CheckBox { Text = Lang.T("퀘스트 대화 선택지 같이 선택", "Pick the same quest dialog option"), AutoSize = true };
        private readonly CheckBox _reward = new CheckBox { Text = Lang.T("리더가 보상창에서 고른 것을 같이 선택 (실험)", "Pick the same reward as the leader (experimental)"), AutoSize = true };
        private readonly CheckBox _journal = new CheckBox { Text = Lang.T("탐험 일지 창 자동 닫기", "Close the Explorer Journal window automatically"), AutoSize = true };
        private readonly CheckBox _party = new CheckBox { Text = Lang.T("파티 초대", "Party invites"), AutoSize = true };
        private readonly CheckBox _quest = new CheckBox { Text = Lang.T("퀘스트 공유", "Quest shares"), AutoSize = true };
        private readonly CheckBox _res = new CheckBox { Text = Lang.T("부활", "Resurrection"), AutoSize = true };
        private readonly CheckBox _airship = new CheckBox { Text = Lang.T("비공정 초대 / 그레이터 텔레포트", "Airship invites / Greater Teleport"), AutoSize = true };
        private readonly Label _timersNote = new Label { Text = Lang.T("※ 타이머는 서브 창의 [자동 힐/버프(+AUTO)] 버튼이 켜져 있을 때만 작동합니다.", "Note: timers only run while the [+AUTO] button on the alt's window is on."), AutoSize = true, ForeColor = Color.DimGray };
        private readonly CheckBox _timersTown = new CheckBox { Text = Lang.T("마을에서는 쉬기", "Pause in town"), AutoSize = true };
        private readonly CheckBox _timersEnemy = new CheckBox { Text = Lang.T("근처에 적이 있을 때만 사용  거리(m):", "Only when enemies are nearby  range (m):"), AutoSize = true };
        private readonly CheckBox _loot = new CheckBox { Text = Lang.T("상자 아이템을 리더에게 넘기기", "Pass chest loot to the leader"), AutoSize = true };
        private readonly NumericUpDown _enemyRadius = new NumericUpDown { Minimum = 5, Maximum = 100, Increment = 5, Width = 50 };
        private readonly NumericUpDown _enemyLinger = new NumericUpDown { Minimum = 0, Maximum = 60, Increment = 1, Width = 45 };
        private readonly CheckBox _faceTarget = new CheckBox { Text = Lang.T("공격 ON일 때 근처의 대상 몬스터를 정면으로 바라보기", "Face the targeted monster while Attack is ON"), AutoSize = true };
        private readonly NumericUpDown _dungeonDelay = new NumericUpDown { Minimum = 0, Maximum = 600, Increment = 5, Width = 55 };
        private readonly CheckBox _farHot = new CheckBox { Text = Lang.T("리더와", "If over"), AutoSize = true };
        private readonly NumericUpDown _farDist = new NumericUpDown { Minimum = 10, Maximum = 200, Increment = 5, Width = 50 };
        private readonly TextBox _farSlot = new TextBox { Width = 45 };
        private readonly CheckBox _timersCatchUp = new CheckBox { Text = Lang.T("뒤처져 따라가는 중에는 미루기", "Hold while catching up"), AutoSize = true };
        private readonly CheckBox _timerSeq = new CheckBox { Text = Lang.T("순서 모드 (표 순서대로 1→2→3… 반복)", "Sequence mode (repeat rows in order 1→2→3…)"), AutoSize = true };
        private readonly NumericUpDown _timerGap = new NumericUpDown { Minimum = 0, Maximum = 10, Increment = 0.5m, DecimalPlaces = 1, Width = 55 };
        private readonly CheckBox _trackRangeOn = new CheckBox { Text = Lang.T("트랙 인식 범위(m):", "Trail width (m):"), AutoSize = true };
        private readonly NumericUpDown _trackRange = new NumericUpDown { Minimum = 1.0m, Maximum = 10.0m, DecimalPlaces = 1, Increment = 0.5m, Width = 55 };
        private readonly CheckBox _timersMounted = new CheckBox { Text = Lang.T("탈것 탑승 중에는 쉬기", "Pause while mounted"), AutoSize = true };
        private readonly DataGridView _grid = new DataGridView();
        private readonly CheckBox _emg = new CheckBox { Text = Lang.T("체력이 기준 아래로 떨어지면 비상 힐 연타", "Spam emergency heals when health drops below the threshold"), AutoSize = true };
        private readonly NumericUpDown _emgPct = new NumericUpDown { Minimum = 5, Maximum = 99, Width = 50 };
        private readonly NumericUpDown _emgMs = new NumericUpDown { Minimum = 200, Maximum = 5000, Increment = 100, Width = 65 };
        private readonly TextBox _emgSlots = new TextBox { Width = 150 };
        private readonly CheckBox _emgParty = new CheckBox { Text = Lang.T("리더가 괜찮으면 다른 파티원도 확인", "Also check other party members when the leader is fine"), AutoSize = true };

        private readonly Button _save = new Button { Text = Lang.T("저장", "Save"), AutoSize = true };
        private readonly Button _test = new Button { Text = Lang.T("선택한 줄 지금 사용해보기", "Test selected row now"), AutoSize = true };
        private readonly Button _delete = new Button { Text = Lang.T("선택한 줄 삭제", "Delete selected row"), AutoSize = true };
        private readonly Button _reload = new Button { Text = Lang.T("저장 전으로 되돌리기", "Revert to saved"), AutoSize = true };
        private readonly Label _msg = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 8, 3, 0) };
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        private ulong? _loadedChar;

        public SettingsForm(Follower follower)
        {
            _f = follower;
            Text = "TrailFollow";
            Font = new Font("맑은 고딕", 9f);
            ClientSize = new Size(500, 560);

            // 타이머 표
            _grid.AllowUserToAddRows = true;
            _grid.AllowUserToDeleteRows = true;
            _grid.RowHeadersWidth = 24;
            _grid.Size = new Size(450, 170);
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.ShowCellToolTips = false;   // v0.8.14: 체크 칸에 마우스가 닿으면 "True" 말풍선이 떠서 끔
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "On", HeaderText = Lang.T("사용", "On"), AutoSizeMode = DataGridViewAutoSizeColumnMode.ColumnHeader });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bar", HeaderText = Lang.T("핫바", "Bar"), FillWeight = 14 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Slot", HeaderText = Lang.T("슬롯", "Slot"), FillWeight = 14 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sec", HeaderText = Lang.T("간격(초)", "Every (s)"), FillWeight = 18 });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Leader", HeaderText = Lang.T("리더 고정", "On leader"), AutoSizeMode = DataGridViewAutoSizeColumnMode.ColumnHeader, ToolTipText = Lang.T("켜면 리더를 선택한 뒤 사용합니다 (리더에게 버프/힐). 1초 뒤 원래 대상으로 돌아갑니다.", "Selects the leader before using the slot (buff/heal the leader), then returns to the previous target after 1 second.") });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Note", HeaderText = Lang.T("메모", "Note"), FillWeight = 34 });
            _grid.DefaultValuesNeeded += (s, e) =>
            {
                e.Row.Cells["On"].Value = true;
                e.Row.Cells["Bar"].Value = 1;
                e.Row.Cells["Slot"].Value = 1;
                e.Row.Cells["Sec"].Value = 60;
                e.Row.Cells["Leader"].Value = false;
                e.Row.Cells["Note"].Value = "";
            };

            var tabs = new TabControl { Dock = DockStyle.Fill, Multiline = true };

            // ---------------- 기본 ----------------
            var basic = AddPage(tabs, Lang.T("공통", "All alts"));
            basic.Controls.Add(CommonBanner());
            Label lNear = L(Lang.T("따라오는 거리  가까이(m):", "Follow distance  Near (m):")), lFar = L(Lang.T("  멀리(m):", "  Far (m):"));
            basic.Controls.Add(Row(lNear, _near, lFar, _far));
            Tip(Lang.T("리더가 걸어간 길을 따라 잰 거리입니다. 메인 창의 −/+ 로도 조정됩니다.\n가까이는 0m(리더와 같은 자리)까지 됩니다.", "Measured along the path the leader walked. Also adjustable with −/+ on the main window.\nNear can go down to 0 m (the leader's own spot)."), lNear, _near, lFar, _far);
            Label lBuffGap = L(Lang.T("[버프] 버튼: 칸 사이 간격(초):", "[Buff] button: delay between slots (s):"));
            basic.Controls.Add(Row(lBuffGap, _buffGap));
            Tip(Lang.T("메인 창 [버프] 버튼을 누르면 버프용 핫바의 칸을 이 간격으로 차례로 누릅니다.", "When you press [Buff] on the main window, the slots of the buff hotbar are pressed one by one with this delay."), lBuffGap, _buffGap);
            Label lAtk = L(Lang.T("[공격] 버튼: 오토어택 칸  핫바", "[Attack] button: auto-attack slot  bar"));
            basic.Controls.Add(Row(lAtk, _atkBar, L(Lang.T("번의", "slot")), _atkSlot, L(Lang.T("번 칸", ""))));
            Tip(Lang.T("기본 10.8. 모든 서브의 이 칸에 게임의 '오토어택(공격)' 버튼을 올려 두세요.\n메인 창 [공격]을 누르면 서브들이 이 칸을 한 번 누릅니다.\n이 핫바가 게임 화면에 켜져 있어야 합니다 (안 보이는 핫바는 게임이 입력을 무시할 수 있음).", "Default 10.8. Put the game's auto-attack button in this slot on every alt.\nPressing [Attack] on the main window makes the alts press this slot once.\nThe hotbar must be visible in the game (the game may ignore input for hidden hotbars)."), lAtk, _atkBar, _atkSlot);
            Label lAfk = L(Lang.T("AFK 방지: 간격(분):", "Anti-AFK: interval (min):"));
            basic.Controls.Add(Row(lAfk, _afkMin, L(Lang.T("  누를 칸: 핫바", "  press: bar")), _afkBar, L(Lang.T("번의", "slot")), _afkSlot, L(Lang.T("번 칸", ""))));
            Tip(Lang.T("메인 창의 AFK 방지 버튼으로 켜고 끕니다.", "Turned on and off with the AFK button on the main window."), lAfk, _afkMin, _afkBar, _afkSlot);

            // v0.9.0: 화면 언어 (다시 시작해야 적용)
            _lang.Items.AddRange(new object[] { "자동 / Auto (Windows)", "한국어", "English" });
            _lang.SelectedIndex = Lang.Choice == "ko" ? 1 : Lang.Choice == "en" ? 2 : 0;
            _lang.SelectedIndexChanged += (s, e) =>
            {
                string c = _lang.SelectedIndex == 1 ? "ko" : _lang.SelectedIndex == 2 ? "en" : "auto";
                _msg.Text = Lang.Save(c)
                    ? "Dungeon Helper를 다시 시작하면 언어가 바뀝니다. / Restart Dungeon Helper to change the language."
                    : "언어 설정을 저장하지 못했습니다. / Could not save the language setting.";
            };
            basic.Controls.Add(Row(L("언어 / Language:"), _lang));

            // ---------------- 내 캐릭터 ----------------
            var mine = AddPage(tabs, Lang.T("내 캐릭터", "This character"));
            mine.Controls.Add(CharBanner());
            mine.Controls.Add(_showBar);
            Label lHb = L(Lang.T("화면에 띄울 핫바 번호:", "Hotbar shown in the window:")), lBuffBar = L(Lang.T("[버프] 버튼용 핫바:", "Hotbar used by [Buff]:"));
            mine.Controls.Add(Row(lHb, _hotbar));
            mine.Controls.Add(Row(lBuffBar, _buffBar));
            Tip(Lang.T("메인 화면의 이 캐릭터 창에 보여 줄 핫바 번호입니다.", "The hotbar number shown in this character's window on the main screen."), lHb, _hotbar);
            Tip(Lang.T("메인 창 [버프] 버튼을 누르면 이 핫바의 칸을 차례로 사용합니다.", "Pressing [Buff] on the main window uses the slots of this hotbar in order."), lBuffBar, _buffBar);

            // ---------------- 따라 하기 ----------------
            var mimic = AddPage(tabs, Lang.T("따라 하기", "Mimic"));
            mimic.Controls.Add(CharBanner());
            mimic.Controls.Add(Note(Lang.T("리더가 하는 행동을 서브가 같이 합니다.", "The alt repeats what the leader does.")));
            mimic.Controls.Add(Header(Lang.T("이동", "Movement")));
            mimic.Controls.Add(_jumps);
            Tip(Lang.T("가까이 모드: 리더와 동시에 점프 / 멀리 모드: 리더가 점프한 자리에서 점프", "Near mode: jump at the same time as the leader / Far mode: jump where the leader jumped"), _jumps);
            mimic.Controls.Add(_mount);
            Tip(Lang.T("리더가 탈것에 타면 같이 탑니다. 핫바에 탈것이 있어야 합니다.", "Mounts when the leader mounts. The mount must be on a hotbar."), _mount);
            mimic.Controls.Add(_recall);
            Tip(Lang.T("리더가 리콜하면 같이 리콜합니다.", "Recalls when the leader recalls."), _recall);
            mimic.Controls.Add(_tele);
            Tip(Lang.T("리더가 순간이동 아이템을 쓰면 같이 씁니다. 같은 아이템이 핫바에 있어야 합니다.", "Uses a teleport item when the leader does. The same item must be on a hotbar."), _tele);
            mimic.Controls.Add(Header(Lang.T("문 · 상자 · NPC", "Doors · Chests · NPCs")));
            mimic.Controls.Add(_doorUse);
            Tip(Lang.T("리더가 문·던전 입구·포탈을 사용하면 Ctrl을 누르지 않아도 같이 사용합니다.", "When the leader uses a door, dungeon entrance or portal, the alt uses it too, even without Ctrl."), _doorUse);
            mimic.Controls.Add(_ctrlUse);
            Tip(Lang.T("리더가 Ctrl을 누른 채 사용한 문·레버·상자·NPC·입구를 같이 사용합니다.", "Uses the door, lever, chest, NPC or entrance the leader used while holding Ctrl."), _ctrlUse);
            mimic.Controls.Add(_enter);
            Tip(Lang.T("던전 입장: 리더가 입구를 사용 → 서브들도 입장창이 열림 → 리더가 Enter → 서브들도 Enter.", "Entering a dungeon: leader uses the entrance → the entrance dialog opens on the alts → leader presses Enter → alts press Enter."), _enter);
            mimic.Controls.Add(_dialog);
            Tip(Lang.T("리더가 NPC를 Ctrl+사용하고, Ctrl을 누른 채 선택지를 고르면 서브들도 같은 번호를 고릅니다.", "Ctrl+use an NPC on the leader, then pick an option while holding Ctrl: the alts pick the same number."), _dialog);
            mimic.Controls.Add(_loot);
            Tip(Lang.T("서브가 상자를 열면 자기 몫 아이템을 리더에게 넘깁니다. 넘길 수 없는 것은 서브가 가져갑니다.", "When an alt opens a chest it assigns its items to the leader. Items that cannot be passed are looted by the alt."), _loot);

            // ---------------- 자동 수락 ----------------
            var accept = AddPage(tabs, Lang.T("자동 수락", "Auto-accept"));
            accept.Controls.Add(CharBanner());
            accept.Controls.Add(Header(Lang.T("자동으로 수락할 것", "Accept automatically")));
            accept.Controls.Add(_party);
            Tip(Lang.T("내 캐릭터가 보낸 초대만 수락합니다.", "Only invites sent by your own characters are accepted."), _party);
            accept.Controls.Add(_quest);
            accept.Controls.Add(_res);
            accept.Controls.Add(_airship);
            Tip(Lang.T("내 캐릭터가 보낸 것만 수락합니다.", "Only those sent by your own characters are accepted."), _airship);
            accept.Controls.Add(Header(Lang.T("자동으로 닫을 것", "Close automatically")));
            accept.Controls.Add(_journal);

            // ---------------- 힐/버프 타이머 ----------------
            var timersPage = AddPage(tabs, Lang.T("힐/버프 타이머", "Heal/Buff timers"));
            timersPage.Controls.Add(CharBanner());
            timersPage.Controls.Add(_timersNote);
            timersPage.Controls.Add(_grid);
            timersPage.Controls.Add(Row(_test, _delete));
            Tip(Lang.T("핫바/슬롯 번호는 게임 화면 기준(1부터)입니다. 줄을 지운 뒤에는 [저장]을 눌러야 반영됩니다.", "Bar and slot numbers are as shown in the game (starting at 1). After deleting a row, press [Save] to apply."), _test, _delete);
            timersPage.Controls.Add(_timerSeq);
            Tip(Lang.T("켜면 표의 순서대로 하나씩 반복합니다.\n이때 간격(초)은 그 줄을 쓴 뒤 다음 줄까지 기다리는 시간입니다.", "Repeats the rows one at a time in table order.\nIn this mode the interval is the wait after that row before the next one."), _timerSeq);
            Label lGap = L(Lang.T("타이머끼리 최소 간격(초):", "Minimum gap between timers (s):"));
            timersPage.Controls.Add(Row(lGap, _timerGap));
            Tip(Lang.T("여러 타이머가 겹쳐도 이만큼 띄워서 차례로 사용합니다 (시전 중 입력이 씹히지 않게).", "When several timers are due at once they are used one after another with this gap (so inputs are not lost while casting)."), lGap, _timerGap);

            timersPage.Controls.Add(Header(Lang.T("타이머를 쉬는 조건", "When timers pause")));
            timersPage.Controls.Add(_timersMounted);
            Tip(Lang.T("탈것에서 내리면 바로 사용합니다.", "Resumes as soon as you dismount."), _timersMounted);
            timersPage.Controls.Add(_timersTown);
            timersPage.Controls.Add(_timersCatchUp);
            Tip(Lang.T("리더에게서 뒤처져 따라가는 중에는 타이머를 미룹니다 (시전하느라 더 뒤처지지 않게).", "Holds timers while the alt is behind and catching up (so casting does not make it fall further behind)."), _timersCatchUp);
            Label lDelay = L(Lang.T("던전에 들어간 뒤 쉬는 시간(초):", "Wait after entering a dungeon (s):"));
            timersPage.Controls.Add(Row(lDelay, _dungeonDelay));
            Tip(Lang.T("0이면 들어가자마자 사용합니다.", "0 = use immediately on entering."), lDelay, _dungeonDelay);
            Label lLinger = L(Lang.T("  적이 사라진 뒤 유지(초):", "  keep after enemies are gone (s):"));
            timersPage.Controls.Add(Row(_timersEnemy, _enemyRadius, lLinger, _enemyLinger));
            Tip(Lang.T("켜면 이 거리 안에 적이 있을 때만 타이머를 사용합니다.", "Timers are used only while an enemy is within this range."), _timersEnemy, _enemyRadius);
            Tip(Lang.T("적이 사라진 뒤에도 이 시간 동안은 계속 사용합니다.", "Timers keep running for this long after the enemies are gone."), lLinger, _enemyLinger);

            timersPage.Controls.Add(Header(Lang.T("비상 힐", "Emergency heal")));
            timersPage.Controls.Add(_emg);
            Tip(Lang.T("[+AUTO]가 켜진 서브만 작동합니다. 리더를 먼저 보고, 그다음 파티원을 봅니다.", "Works only on alts with [+AUTO] on. Checks the leader first, then party members."), _emg);
            Label lPct = L(Lang.T("기준 체력(%):", "Health threshold (%):")), lMs = L(Lang.T("  연타 간격(ms):", "  press every (ms):"));
            timersPage.Controls.Add(Row(lPct, _emgPct, lMs, _emgMs));
            Label lSlots = L(Lang.T("연타할 칸:", "Slots to spam:"));
            timersPage.Controls.Add(Row(lSlots, _emgSlots));
            Tip(Lang.T("핫바.칸 형식으로 쉼표로 구분해 적습니다. 예: 8.1, 8.2", "Write as bar.slot separated by commas. Example: 8.1, 8.2"), lSlots, _emgSlots);
            timersPage.Controls.Add(_emgParty);
            Tip(Lang.T("TrailFollow가 켜진 내 캐릭터들(자기 자신 포함)의 체력도 확인합니다.", "Also checks the health of your characters running TrailFollow (including this one)."), _emgParty);

            // ---------------- 고급 ----------------
            var adv = AddPage(tabs, Lang.T("고급", "Advanced"));
            adv.Controls.Add(CharBanner());
            adv.Controls.Add(Note(Lang.T("잘 모르겠으면 그대로 두세요.", "Leave these as they are if unsure.")));
            _trackRangeOn.CheckedChanged += (_, __) => _trackRange.Enabled = _trackRangeOn.Checked;
            adv.Controls.Add(Row(_trackRangeOn, _trackRange));
            Tip(Lang.T("기본 3.0m. 끄면 3.0m로 고정됩니다.\n넓히면 길에서 멀리 벗어나도 다시 길을 잡지만, 정밀도가 떨어집니다.", "Default 3.0 m. When off, it is fixed at 3.0 m.\nWider lets the alt find the trail again from further away, but follows it less precisely."), _trackRangeOn, _trackRange);
            Label lFar1 = L(Lang.T("m 이상 떨어지면 핫바", "m from the leader, press slot")), lFar2 = L(Lang.T("누르기", ""));
            adv.Controls.Add(Row(_farHot, _farDist, lFar1, _farSlot, lFar2));
            Tip(Lang.T("리더와 이만큼 멀어지면 지정한 칸을 누릅니다 (20초마다 반복). 칸은 핫바.칸 형식, 예: 3.1", "When the alt is this far from the leader it presses the given slot (repeats every 20 seconds). Write the slot as bar.slot, e.g. 3.1"), _farHot, _farDist, lFar1, _farSlot, lFar2);
            adv.Controls.Add(_faceTarget);
            Tip(Lang.T("메인 창 [공격]이 켜져 있을 때, 선택한 대상이 근처의 살아 있는 몬스터면 그쪽을 바라봅니다.\n'근처'의 기준은 힐/버프 타이머 탭의 적 거리(m)와 같습니다.", "While [Attack] on the main window is on, the alt faces its target if it is a living monster nearby.\n'Nearby' uses the enemy range (m) from the Heal/Buff timers tab."), _faceTarget);

            // ---------------- 도움말 ----------------
            var helpPage = AddPage(tabs, Lang.T("도움말", "Help"));
            helpPage.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(450, 0),
                Text = Lang.T("• 각 항목에 마우스를 올리면 자세한 설명이 나옵니다.\n\n", "• Hover over any item for a detailed explanation.\n\n") +
                       Lang.T("• 따라오기는 화면 창의 발자국 버튼으로 캐릭터마다 켜고 끕니다. 켜는 순간 조작 중인 메인이 리더로 고정됩니다.\n\n", "• Following is turned on and off per character with the footprint button on its window. The character you are playing at that moment becomes the leader.\n\n") +
                       Lang.T("• 서브 창을 직접 클릭해 조작하는 동안은 그 서브의 따라오기가 잠시 멈춥니다.\n\n", "• While you click into an alt's game window and play it directly, that alt stops following for a moment.\n\n") +
                       Lang.T("• 거리는 리더가 걸어간 길을 따라 잰 거리입니다. 메인 창의 −/+ 로도 조정됩니다 (모든 서브 공통).\n\n", "• Distance is measured along the path the leader walked. It can also be changed with −/+ on the main window (shared by all alts).\n\n") +
                       Lang.T("• 던전 입장: 리더가 입구를 사용 → 서브들도 입구를 사용해 입장창이 열림 → 리더가 Enter → 서브들도 Enter.\n\n", "• Entering a dungeon: leader uses the entrance → alts use it and their entrance dialog opens → leader presses Enter → alts press Enter.\n\n") +
                       Lang.T("• 퀘스트 대화: 리더가 NPC를 Ctrl+사용하고, Ctrl을 누른 채 선택지를 고르면 서브들도 같은 번호를 고릅니다.\n\n", "• Quest dialog: Ctrl+use the NPC on the leader, then pick an option while holding Ctrl and the alts pick the same number.\n\n") +
                       Lang.T("• 타이머의 핫바/슬롯 번호는 게임 화면 기준(1부터)입니다.", "• Timer bar and slot numbers are as shown in the game (starting at 1).")
            });
            helpPage.Controls.Add(new Label
            {
                Text = $"TrailFollow v{typeof(SettingsForm).Assembly.GetName().Version?.ToString(3)}  ·  made by Madmachine",
                AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(3, 16, 3, 3)
            });

            // ---------------- 전체 배치: 제목 / 탭 / 저장 줄 ----------------
            var bottom = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            bottom.Controls.Add(_save);
            bottom.Controls.Add(_reload);
            bottom.Controls.Add(_msg);
            Tip(Lang.T("모든 탭의 내용을 한 번에 저장합니다.", "Saves all tabs at once."), _save);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(_title, 0, 0);
            root.Controls.Add(tabs, 0, 1);
            root.Controls.Add(bottom, 0, 2);
            Controls.Add(root);

            _save.Click += (s, e) => SaveToSettings();
            _reload.Click += (s, e) => LoadFromSettings();
            _delete.Click += (s, e) => DeleteSelectedRows();
            _test.Click += (s, e) => TestSelected();
            _timer.Tick += (s, e) => { if (_f.CharId != _loadedChar) LoadFromSettings(); };
            VisibleChanged += (s, e) => { if (Visible) LoadFromSettings(); };
            _timer.Start();

            LoadFromSettings();
        }

        private Label Header(string text) => new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) };

        /// <summary>탭 하나를 만들고, 그 안에 위에서 아래로 쌓이는(길면 스크롤되는) 판을 돌려줌</summary>
        private static FlowLayoutPanel AddPage(TabControl tabs, string title)
        {
            var page = new TabPage(title) { UseVisualStyleBackColor = true };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
            page.Controls.Add(flow);
            tabs.TabPages.Add(page);
            return flow;
        }

        // v0.8.31: 탭마다 맨 위에 "모든 서브 공통"(파랑)인지 "이 캐릭터만"(주황)인지 색 띠로 표시
        private readonly List<Label> _charBanners = new List<Label>();

        private static Label Banner(string text, Color back, Color fore) => new Label
        {
            Text = text, AutoSize = false, Size = new Size(450, 24), TextAlign = ContentAlignment.MiddleLeft,
            BackColor = back, ForeColor = fore, Padding = new Padding(6, 0, 0, 0), Margin = new Padding(0, 0, 0, 6)
        };

        private Label CommonBanner()
        {
            var b = Banner(Lang.T("모든 서브 공통  —  여기서 바꾸면 모든 캐릭터에 똑같이 적용됩니다", "All alts  —  changes here apply to every character"), Color.FromArgb(222, 235, 250), Color.FromArgb(20, 60, 120));
            b.Font = new Font(Font, FontStyle.Bold);
            return b;
        }

        private Label CharBanner()
        {
            var b = Banner(Lang.T("이 캐릭터만", "This character only"), Color.FromArgb(253, 235, 210), Color.FromArgb(130, 70, 0));
            b.Font = new Font(Font, FontStyle.Bold);
            _charBanners.Add(b);
            return b;
        }

        private void SetCharBanners(string text)
        {
            foreach (var b in _charBanners) b.Text = text;
        }

        /// <summary>한 줄에 나란히 놓기</summary>
        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            row.Controls.AddRange(controls);
            return row;
        }

        /// <summary>숫자 칸 옆에 붙는 글자</summary>
        private static Label L(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(3, 6, 2, 0) };

        private static Label Note(string text) => new Label { Text = text, AutoSize = true, MaximumSize = new Size(450, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 0, 3, 2) };

        /// <summary>마우스를 올리면 뜨는 설명</summary>
        private void Tip(string text, params Control[] controls)
        {
            foreach (var c in controls) _tip.SetToolTip(c, text);
        }

        private IEnumerable<Control> CharControls() => new Control[]
            { _hotbar, _buffBar, _showBar, _jumps, _mount, _recall, _trackRangeOn, _trackRange, _doorUse, _ctrlUse, _enter, _tele, _dialog, _reward, _loot, _journal, _party, _quest, _res, _airship, _timersMounted, _timersTown, _timersEnemy, _enemyRadius, _enemyLinger, _faceTarget, _timersCatchUp, _dungeonDelay, _farHot, _farDist, _farSlot, _timerSeq, _timerGap, _emg, _emgPct, _emgMs, _emgSlots, _emgParty, _grid, _save, _test, _delete, _reload };

        private void LoadFromSettings()
        {
            _loadedChar = _f.CharId;
            _near.Value = Clamp((decimal)_f.Shared.NearGap, _near);
            _far.Value = Clamp((decimal)_f.Shared.FarGap, _far);
            _afkMin.Value = _f.Shared.AfkMinutes;
            _afkBar.Value = _f.Shared.AfkBar;
            _afkSlot.Value = _f.Shared.AfkSlot;
            _buffGap.Value = (decimal)Math.Round(_f.Shared.BuffInterval * 2) / 2;
            _atkBar.Value = _f.Shared.AtkBar;
            _atkSlot.Value = _f.Shared.AtkSlot;

            var s = _f.Settings;
            bool ok = s != null;
            foreach (var c in CharControls()) c.Enabled = ok;
            if (!ok)
            {
                _title.Text = Lang.T("TrailFollow - 캐릭터 접속 후 설정할 수 있습니다", "TrailFollow - log in a character to change settings");
                SetCharBanners(Lang.T("이 캐릭터만  —  캐릭터 접속 후 설정할 수 있습니다", "This character only  —  log in a character to change settings"));
                _grid.Rows.Clear();
                return;
            }

            _title.Text = "TrailFollow - " + (string.IsNullOrEmpty(_f.CharName) ? Lang.T("현재 캐릭터", "current character") : _f.CharName);
            SetCharBanners(Lang.T("이 캐릭터만 (", "This character only (") + (string.IsNullOrEmpty(_f.CharName) ? Lang.T("현재 캐릭터", "current character") : _f.CharName) + Lang.T(")  —  캐릭터마다 따로 설정합니다", ")  —  set separately for each character"));
            lock (_f.SettingsLock)
            {
                _hotbar.Value = Math.Clamp(s.HotbarNumber, 1, 10);
                _buffBar.Value = Math.Clamp(s.BuffHotbar, 1, 10);
                _showBar.Checked = s.ShowBar;
                _jumps.Checked = s.ReplayJumps;
                _mount.Checked = s.MountWithLeader;
                _recall.Checked = s.RecallWithLeader;
                _doorUse.Checked = s.MimicDoorUse;
                _ctrlUse.Checked = s.MimicCtrlUse;
                _enter.Checked = s.EnterWithLeader;
                _tele.Checked = s.MimicTeleportItems;
                _dialog.Checked = s.MimicQuestDialog;
                _reward.Checked = s.MimicRewardChoice;
                _journal.Checked = s.CloseExplorerJournals;
                _party.Checked = s.AcceptPartyInvites;
                _quest.Checked = s.AcceptQuestShares;
                _res.Checked = s.AcceptResurrections;
                _airship.Checked = s.AcceptAirshipAndTeleports;
                _timersMounted.Checked = s.TimersPauseWhileMounted;
                _timerSeq.Checked = s.TimerSequenceMode;
                _timersTown.Checked = s.TimersPauseInTown;
                _timersEnemy.Checked = s.TimersOnlyNearEnemies;
                _timersCatchUp.Checked = s.TimersHoldWhileCatchingUp;
                _dungeonDelay.Value = Math.Clamp(s.TimersDungeonDelaySec, 0, 600);
                _farHot.Checked = s.FarHotkeyEnabled;
                _farDist.Value = Math.Clamp(s.FarHotkeyDistance, 10, 200);
                _farSlot.Text = s.FarHotkeySlot ?? "";
                _enemyRadius.Value = Math.Clamp(s.EnemyRadiusM, 5, 100);
                _loot.Checked = s.PassLootToLeader;
                _enemyLinger.Value = Math.Clamp(s.EnemyLingerSec, 0, 60);
                _faceTarget.Checked = s.FaceTarget;
                _trackRangeOn.Checked = s.TrackRangeEnabled;
                _trackRange.Value = Clamp((decimal)s.TrackRange, _trackRange);
                _trackRange.Enabled = _trackRangeOn.Checked;
                _timerGap.Value = (decimal)Math.Clamp(Math.Round(s.TimerMinGapSec * 2) / 2, 0, 10);
                _emg.Checked = s.EmergencyHeal;
                _emgPct.Value = Math.Clamp(s.EmergencyHpPercent, 5, 99);
                _emgMs.Value = Math.Clamp(s.EmergencyIntervalMs, 200, 5000);
                _emgSlots.Text = s.EmergencySlots ?? "";
                _emgParty.Checked = s.EmergencyIncludeParty;
                _grid.Rows.Clear();
                foreach (var t in s.Timers)
                    _grid.Rows.Add(t.Enabled, t.Bar, t.Slot, t.IntervalSec, t.TargetLeader, t.Note);
            }
            _msg.Text = "";
        }

        private static decimal Clamp(decimal v, NumericUpDown n) => Math.Max(n.Minimum, Math.Min(n.Maximum, v));

        private void SaveToSettings()
        {
            // 공통 거리
            _f.Shared.NearGap = (float)_near.Value;
            _f.Shared.FarGap = (float)_far.Value;
            _f.Shared.AfkMinutes = (int)_afkMin.Value;
            _f.Shared.AfkBar = (int)_afkBar.Value;
            _f.Shared.AfkSlot = (int)_afkSlot.Value;
            _f.Shared.BuffInterval = (float)_buffGap.Value;
            _f.Shared.AtkBar = (int)_atkBar.Value;
            _f.Shared.AtkSlot = (int)_atkSlot.Value;
            _f.AdjustGlobalGap(0);   // 저장

            var s = _f.Settings;
            if (s == null) { _msg.Text = Lang.T("거리 저장했습니다.", "Distances saved."); return; }
            _grid.EndEdit();

            var timers = new List<BuffTimer>();
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (r.IsNewRow) continue;
                if (!TryInt(r.Cells["Bar"].Value, out int bar) || !TryInt(r.Cells["Slot"].Value, out int slot) || !TryInt(r.Cells["Sec"].Value, out int sec))
                {
                    _msg.Text = Lang.T($"{r.Index + 1}번째 줄의 숫자를 확인하세요.", $"Check the numbers in row {r.Index + 1}.");
                    return;
                }
                if (sec < 1) { _msg.Text = Lang.T("간격은 1초 이상이어야 합니다. (순서 모드가 아니면 3초 미만은 3초로 동작)", "The interval must be at least 1 second. (Outside sequence mode, anything under 3 seconds runs as 3 seconds.)"); return; }
                timers.Add(new BuffTimer
                {
                    Enabled = r.Cells["On"].Value is bool b && b,
                    Bar = bar,
                    Slot = slot,
                    IntervalSec = sec,
                    TargetLeader = r.Cells["Leader"].Value is bool lb && lb,
                    Note = r.Cells["Note"].Value?.ToString() ?? ""
                });
            }

            lock (_f.SettingsLock)
            {
                s.HotbarNumber = (int)_hotbar.Value;
                s.BuffHotbar = (int)_buffBar.Value;
                s.ShowBar = _showBar.Checked;
                s.ReplayJumps = _jumps.Checked;
                s.MountWithLeader = _mount.Checked;
                s.RecallWithLeader = _recall.Checked;
                s.MimicDoorUse = _doorUse.Checked;
                s.MimicCtrlUse = _ctrlUse.Checked;
                s.UseTraps = false;   // v0.8.30: 함정 같이 사용 옵션 삭제
                s.EnterWithLeader = _enter.Checked;
                s.MimicTeleportItems = _tele.Checked;
                s.MimicQuestDialog = _dialog.Checked;
                s.MimicRewardChoice = _reward.Checked;
                s.CloseExplorerJournals = _journal.Checked;
                s.AcceptPartyInvites = _party.Checked;
                s.AcceptQuestShares = _quest.Checked;
                s.AcceptResurrections = _res.Checked;
                s.AcceptAirshipAndTeleports = _airship.Checked;
                s.TimersPauseWhileMounted = _timersMounted.Checked;
                s.TimerSequenceMode = _timerSeq.Checked;
                s.TimersPauseInTown = _timersTown.Checked;
                s.TimersOnlyNearEnemies = _timersEnemy.Checked;
                s.TimersHoldWhileCatchingUp = _timersCatchUp.Checked;
                s.TimersDungeonDelaySec = (int)_dungeonDelay.Value;
                s.FarHotkeyEnabled = _farHot.Checked;
                s.FarHotkeyDistance = (int)_farDist.Value;
                s.FarHotkeySlot = _farSlot.Text.Trim();
                s.EnemyRadiusM = (int)_enemyRadius.Value;
                s.PassLootToLeader = _loot.Checked;
                s.EnemyLingerSec = (int)_enemyLinger.Value;
                s.FaceTarget = _faceTarget.Checked;
                s.TrackRangeEnabled = _trackRangeOn.Checked;
                s.TrackRange = (float)_trackRange.Value;
                s.TimerMinGapSec = (double)_timerGap.Value;
                s.EmergencyHeal = _emg.Checked;
                s.EmergencyHpPercent = (int)_emgPct.Value;
                s.EmergencyIntervalMs = (int)_emgMs.Value;
                s.EmergencySlots = _emgSlots.Text.Trim();
                s.EmergencyIncludeParty = _emgParty.Checked;
                s.Timers = timers;
            }
            _f.SaveSettings();
            _msg.Text = Lang.T("저장했습니다.", "Saved.");
        }

        /// <summary>선택한 줄(또는 지금 커서가 있는 줄)을 표에서 지움. [저장]을 눌러야 반영됨</summary>
        private void DeleteSelectedRows()
        {
            var rows = new List<DataGridViewRow>();
            foreach (DataGridViewRow r in _grid.SelectedRows) if (!r.IsNewRow) rows.Add(r);
            if (rows.Count == 0 && _grid.CurrentRow != null && !_grid.CurrentRow.IsNewRow) rows.Add(_grid.CurrentRow);
            if (rows.Count == 0) { _msg.Text = Lang.T("지울 줄을 선택하세요.", "Select a row to delete."); return; }
            _grid.EndEdit();
            foreach (var r in rows) _grid.Rows.Remove(r);
            _msg.Text = Lang.T($"{rows.Count}줄 지움 - [저장]을 눌러야 반영됩니다.", $"{rows.Count} row(s) deleted - press [Save] to apply.");
        }

        private void TestSelected()
        {
            var r = _grid.CurrentRow;
            if (r == null || r.IsNewRow) { _msg.Text = Lang.T("테스트할 줄을 선택하세요.", "Select a row to test."); return; }
            if (!TryInt(r.Cells["Bar"].Value, out int bar) || !TryInt(r.Cells["Slot"].Value, out int slot))
            {
                _msg.Text = Lang.T("핫바/슬롯 번호를 확인하세요.", "Check the bar and slot numbers.");
                return;
            }
            bool leader = r.Cells["Leader"].Value is bool lb && lb;
            if (leader) { _msg.Text = _f.FireSlotOnLeader(bar, slot); return; }
            _f.FireSlot(bar, slot, out string m);
            _msg.Text = m;
        }

        private static bool TryInt(object v, out int result)
        {
            result = 0;
            return v != null && int.TryParse(v.ToString(), out result);
        }
    }
}
