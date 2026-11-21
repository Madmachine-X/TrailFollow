using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using VoK.Sdk;
using VoK.Sdk.Ddo;

namespace TrailFollow
{
    /// <summary>
    /// 캐릭터마다 하나씩 화면에 떠 있는 창. 클릭해도 게임 창의 포커스를 뺏지 않는다.
    ///  - 서브: [발자국(따라오기 켜기/끄기)] + [이 캐릭터의 핫바 10칸 아이콘] → 클릭하면 서브가 사용
    ///  - 메인(지금 조작 중이고 따라오기 꺼진 캐릭터):
    ///      [따라오기] [가까이/멀리] [− 1.0m +] [타게팅] [AFK] [버프]  (서브 창과 같은 크기)
    /// </summary>
    internal sealed class HotbarWindow : Form
    {
        private const int IconSize = 34;

        private readonly Follower _f;
        private readonly Label _name = new Label();
        private readonly Label _status = new Label();
        private readonly FlowLayoutPanel _subPanel;
        private readonly FlowLayoutPanel _leaderPanel;
        private readonly PictureBox _followIcon;
        private readonly PictureBox _actionIcon;
        private readonly PictureBox _healIcon;
        private readonly Image _healOn, _healOff;
        private readonly PictureBox[] _slotBoxes = new PictureBox[10];
        private readonly IHotbarSlot[] _slots = new IHotbarSlot[10];
        private readonly ToolTip _tip = new ToolTip { InitialDelay = 400 };

        private readonly BarButton _allFollow = MakeButton(Lang.T("따라오기 OFF", "Follow OFF"));
        private readonly BarButton _mode = MakeButton(Lang.T("가까이", "Near"));
        private readonly BarButton _minus = MakeButton("−");
        private readonly BarButton _gap = new BarButton { Cursor = Cursors.Default };
        private readonly BarButton _plus = MakeButton("+");
        private readonly BarButton _mimic = MakeButton(Lang.T("타게팅 OFF", "Target OFF"));
        private readonly BarButton _afk = MakeButton("AFK OFF");
        private readonly BarButton _buff = MakeButton(Lang.T("버프", "Buff"));
        private readonly BarButton _attack = MakeButton(Lang.T("공격 OFF", "Attack OFF"));

        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly Image _footOn, _footOff;
        private ulong? _shownChar;
        private string _hotbarSig = "";
        private DateTime _nextIconCheck = DateTime.MinValue;
        private bool _closing;
        private bool _posDirty;
        private DateTime _posChangedAt;

        private static readonly Color Bg = Color.FromArgb(24, 26, 32);
        private static readonly Color SlotBg = Color.FromArgb(44, 48, 58);
        private static readonly Color OnColor = Color.FromArgb(46, 160, 67);
        private static readonly Color OffColor = Color.FromArgb(80, 84, 94);
        private static readonly Color NearColor = Color.FromArgb(52, 120, 200);
        private static readonly Color FarColor = Color.FromArgb(214, 130, 40);

        public HotbarWindow(Follower follower)
        {
            _f = follower;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            Opacity = 0.94;
            Font = new Font("맑은 고딕", 8.5f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(3);

            _footOn = DrawFootprints(true);
            _footOff = DrawFootprints(false);

            var root = NewFlow(FlowDirection.TopDown);
            var header = NewFlow(FlowDirection.LeftToRight);

            _name.AutoSize = true;
            _name.ForeColor = Color.White;
            _name.Font = new Font(Font, FontStyle.Bold);
            _name.Margin = new Padding(2, 2, 8, 0);
            _name.Cursor = Cursors.SizeAll;
            _name.MouseDown += DragStart;

            _status.AutoSize = true;
            _status.ForeColor = Color.FromArgb(160, 166, 180);
            _status.Margin = new Padding(0, 2, 2, 0);
            _status.MouseDown += DragStart;
            header.Controls.Add(_name);
            header.Controls.Add(_status);
            header.MouseDown += DragStart;

            // ---- 서브용: 발자국 + 핫바 10칸 ----
            _subPanel = NewFlow(FlowDirection.LeftToRight);
            _followIcon = NewIconBox();
            _followIcon.Cursor = Cursors.Hand;
            _followIcon.Margin = new Padding(1);
            OnPress(_followIcon, () => { _f.SetFollow(!_f.FollowOn); Refresh2(); });
            _tip.SetToolTip(_followIcon, Lang.T("따라오기 켜기/끄기\n켜는 순간 조작 중인 메인 캐릭터를 리더로 고정합니다", "Follow on/off\nWhen turned on, the character you are playing becomes the leader"));
            _subPanel.Controls.Add(_followIcon);

            // 액션 버튼: 메인이 선택해 둔 대상을 이 서브가 사용(상호작용)
            _actionIcon = NewIconBox();
            _actionIcon.Cursor = Cursors.Hand;
            _actionIcon.Margin = new Padding(1);
            _actionIcon.Image = DrawActionIcon();
            OnPress(_actionIcon, () => _f.UseMainTarget());
            _tip.SetToolTip(_actionIcon, Lang.T("액션: 메인이 선택해 둔 대상(문·레버·상자·NPC 등)을\n이 서브가 선택해서 사용합니다", "Action: this alt selects and uses whatever the main\nhas selected (door, lever, chest, NPC, ...)"));
            _subPanel.Controls.Add(_actionIcon);

            // 자동 힐/버프 버튼: 켜면 설정의 타이머대로 자동 사용
            _healOn = DrawHealIcon(true);
            _healOff = DrawHealIcon(false);
            _healIcon = NewIconBox();
            _healIcon.Cursor = Cursors.Hand;
            _healIcon.Margin = new Padding(1, 1, 5, 1);
            OnPress(_healIcon, () => { _f.AutoTimersOn = !_f.AutoTimersOn; Refresh2(); });
            _tip.SetToolTip(_healIcon, Lang.T("자동 힐/버프 켜기/끄기\n켜면 Dungeon Helper의 TrailFollow 설정에 등록한 타이머(핫바 칸·간격)대로 자동 사용", "Auto heal/buff on/off\nWhen on, uses the timers (slot and interval) set in the TrailFollow settings in Dungeon Helper"));
            _subPanel.Controls.Add(_healIcon);
            for (int i = 0; i < 10; i++)
            {
                int idx = i;
                var box = NewIconBox();
                box.Cursor = Cursors.Hand;
                box.Name = $"핫바칸{i + 1}";
                box.Paint += (s, e) => PaintSlotNumber(e.Graphics, idx);
                OnPress(box, () => { if (_slots[idx] != null) _f.UseSlot(_slots[idx]); });
                _slotBoxes[i] = box;
                _subPanel.Controls.Add(box);
            }

            // ---- 메인용: 전체 조작 ----
            _leaderPanel = NewFlow(FlowDirection.LeftToRight);

            _minus.BackColor = _plus.BackColor = OffColor;
            _gap.BackColor = SlotBg;
            _gap.Hover = false;

            // 전체 따라오기: 한 명이라도 따라오는 중이면 모두 끄고, 아니면 모두 켬
            OnPress(_allFollow, () => { _f.BroadcastFollow(!_f.Shared.AnyFollowing()); Refresh2(); });
            _tip.SetToolTip(_allFollow, Lang.T("모든 서브 따라오기 켜기/끄기\n(서브 하나라도 따라오는 중이면 ON으로 표시)", "Follow on/off for all alts\n(shows ON if at least one alt is following)"));
            OnPress(_mode, () => { _f.ToggleFarMode(); Refresh2(); });
            OnPress(_minus, () => { _f.AdjustGlobalGap(-0.5f); Refresh2(); });
            OnPress(_plus, () => { _f.AdjustGlobalGap(+0.5f); Refresh2(); });
            OnPress(_mimic, () => { _f.ToggleTargetMimic(); Refresh2(); });
            OnPress(_afk, () => { _f.ToggleAntiAfk(); Refresh2(); });
            OnPress(_buff, () => { _f.PostBuff(); Refresh2(); });
            OnPress(_attack, () => { _f.ToggleAttackAll(); Refresh2(); });
            _tip.SetToolTip(_attack, Lang.T("모든 서브의 오토어택 켜기/끄기\n서브들이 설정한 칸(기본 핫바 10.8)을 한 번 누릅니다 - 그 칸에 '오토어택' 버튼을 올려 두세요\n(타게팅을 켜 두면 메인과 같은 대상을 공격)", "Auto-attack on/off for all alts\nThe alts press the configured slot once (default hotbar 10.8) - put the auto-attack button there\n(with Target on they attack the same target as the main)"));
            _buff.BackColor = Color.FromArgb(120, 80, 170);
            _tip.SetToolTip(_buff, Lang.T("모든 캐릭터가 각자의 버프 핫바(설정, 기본 8번)의 1~10번 칸을 설정한 간격(기본 5초)으로 사용\n누를 때 메인이 선택해 둔 대상을 서브들이 버프가 끝날 때까지 유지\n진행 중에 다시 누르면 중단", "Every character uses slots 1-10 of its buff hotbar (see settings, default 8) with the configured delay (default 5 s)\nThe alts keep the main's current target until the buffs are done\nPress again while running to stop"));
            _tip.SetToolTip(_mode, Lang.T("모든 서브의 거리 모드 전환", "Switch distance mode for all alts"));
            _tip.SetToolTip(_minus, Lang.T("지금 모드의 거리 0.5m 줄이기 (모든 서브)", "Decrease the current mode's distance by 0.5 m (all alts)"));
            _tip.SetToolTip(_plus, Lang.T("지금 모드의 거리 0.5m 늘리기 (모든 서브)", "Increase the current mode's distance by 0.5 m (all alts)"));
            _tip.SetToolTip(_mimic, Lang.T("타게팅: 켜 두면 메인이 대상을 바꿀 때마다 서브들도 같은 대상을 선택", "Target: while on, the alts select the same target whenever the main changes target"));
            _leaderPanel.Controls.AddRange(new Control[] { _allFollow, _mode, _minus, _gap, _plus, _mimic, _afk, _attack, _buff });

            root.Controls.Add(header);
            root.Controls.Add(_subPanel);
            root.Controls.Add(_leaderPanel);
            root.MouseDown += DragStart;
            Controls.Add(root);
            SizeLeaderControls();   // 폰트가 창에서 상속된 뒤에 크기 계산

            BuildMenu();

            LocationChanged += (s, e) => { _posDirty = true; _posChangedAt = DateTime.Now; };
            _timer.Tick += (s, e) => { try { Refresh2(); } catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 창 갱신 오류", ex); } };
            _timer.Start();

            Location = new Point(-2000, -2000);
        }

        // 클릭해도 활성화되지 않는 창 (게임 포커스 유지)
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        // =====================================================================
        //  갱신
        // =====================================================================
        private void Refresh2()
        {
            var s = _f.Settings;
            bool visible = s != null && s.ShowBar && _f.CharId.HasValue;

            if (_f.CharId != _shownChar)
            {
                _shownChar = _f.CharId;
                _hotbarSig = "";
                if (s != null) PlaceInitially(s, _f.CharId ?? 0);
                BuildMenu();
            }

            if (!visible) { if (Visible) Hide(); return; }
            if (!Visible) Show();

            bool leader = _f.ShowLeaderPanel;
            string nm = string.IsNullOrEmpty(_f.CharName) ? Lang.T("캐릭터", "Character") : _f.CharName;
            if (leader) _name.Text = "★ " + nm;
            else if (_f.FollowOn && _f.LockedLeader != 0) _name.Text = $"{nm} → {_f.LockedLeaderName}";
            else _name.Text = nm;
            _status.Text = _f.Status;

            _leaderPanel.Visible = leader;
            _subPanel.Visible = !leader;

            if (leader)
            {
                bool anyFollow = _f.Shared.AnyFollowing();
                _allFollow.Text = anyFollow ? Lang.T("따라오기 ON", "Follow ON") : Lang.T("따라오기 OFF", "Follow OFF");
                _allFollow.BackColor = anyFollow ? OnColor : OffColor;
                bool far = _f.Shared.FarMode;
                _mode.Text = far ? Lang.T("멀리", "Far") : Lang.T("가까이", "Near");
                _mode.BackColor = far ? FarColor : NearColor;
                _gap.Text = $"{_f.CurrentGap:0.0}m";
                bool mimic = _f.Shared.TargetMimic;
                _mimic.Text = mimic ? Lang.T("타게팅 ON", "Target ON") : Lang.T("타게팅 OFF", "Target OFF");
                _mimic.BackColor = mimic ? OnColor : OffColor;
                _buff.Text = _f.BuffProgress ?? Lang.T("버프", "Buff");
                bool atk = _f.Shared.AttackOn;
                _attack.Text = atk ? Lang.T("공격 ON", "Attack ON") : Lang.T("공격 OFF", "Attack OFF");
                _attack.BackColor = atk ? Color.FromArgb(190, 70, 50) : OffColor;
                bool afk = _f.Shared.AntiAfk;
                _afk.Text = afk ? "AFK ON" : "AFK OFF";
                _afk.BackColor = afk ? Color.FromArgb(170, 60, 60) : OffColor;
                string afkTip = Lang.T($"켜 두면 모든 캐릭터가 {_f.Shared.AfkMinutes}분마다 핫바 {_f.Shared.AfkBar}번의 {_f.Shared.AfkSlot}번 칸을 한 번 누릅니다\n(간격과 칸은 Dungeon Helper의 TrailFollow 설정에서 변경)", $"While on, every character presses slot {_f.Shared.AfkSlot} of hotbar {_f.Shared.AfkBar} once every {_f.Shared.AfkMinutes} min\n(change the interval and slot in the TrailFollow settings in Dungeon Helper)");
                if (_tip.GetToolTip(_afk) != afkTip) _tip.SetToolTip(_afk, afkTip);
            }
            else
            {
                _followIcon.Image = _f.FollowOn ? _footOn : _footOff;
                _followIcon.BackColor = _f.FollowOn ? Color.FromArgb(30, 90, 45) : SlotBg;
                _healIcon.Image = _f.AutoTimersOn ? _healOn : _healOff;
                _healIcon.BackColor = _f.AutoTimersOn ? Color.FromArgb(30, 90, 45) : SlotBg;
                if (DateTime.Now >= _nextIconCheck)
                {
                    _nextIconCheck = DateTime.Now.AddSeconds(1.5);
                    RefreshHotbar(s.HotbarNumber);
                }
            }

            // 위치 저장 (움직임이 멈추고 1초 뒤)
            if (_posDirty && (DateTime.Now - _posChangedAt).TotalSeconds > 1 && Location.X > -1000)
            {
                _posDirty = false;
                s.BarX = Location.X;
                s.BarY = Location.Y;
                _f.SaveSettings();
            }
        }

        /// <summary>핫바 구성이 바뀌었을 때만 아이콘을 다시 읽음</summary>
        private void RefreshHotbar(int hotbarNumber)
        {
            var slots = _f.GetHotbar(hotbarNumber);
            string sig = hotbarNumber + "|" + string.Join(",", Array.ConvertAll(slots, x => x == null ? "-" : $"{x.ShortcutType}:{x.ReferenceDid}:{x.ItemIid}:{x.WeaponSet}"));
            if (sig == _hotbarSig) { Array.Copy(slots, _slots, 10); return; }
            _hotbarSig = sig;

            for (int i = 0; i < 10; i++)
            {
                _slots[i] = slots[i];
                var old = _slotBoxes[i].Image;
                _slotBoxes[i].Image = slots[i] == null ? null : Icons.Load(_f.Game, slots[i]);
                old?.Dispose();
                _tip.SetToolTip(_slotBoxes[i], slots[i] == null ? Lang.T($"{i + 1}번 칸 (비어 있음)", $"Slot {i + 1} (empty)") : $"{i + 1}. {Icons.NameOf(_f.Game, slots[i])}");
            }
        }

        private void PaintSlotNumber(Graphics g, int idx)
        {
            // 아이콘이 없는 칸에는 번호만 흐리게 표시
            if (_slotBoxes[idx].Image != null) return;
            using var b = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
            string t = ((idx + 1) % 10).ToString();
            var sz = g.MeasureString(t, Font);
            g.DrawString(t, Font, b, (IconSize - sz.Width) / 2, (IconSize - sz.Height) / 2);
        }

        /// <summary>
        /// 메인 창 버튼: 높이는 서브 창 아이콘과 같게, 가로는 글자가 한 줄에 들어가는 최소 크기.
        /// 켜짐/꺼짐처럼 글자가 바뀌는 버튼은 긴 쪽에 맞춰서 크기가 흔들리지 않게 함.
        /// </summary>
        private void SizeLeaderControls()
        {
            var items = new (Control c, string[] texts)[]
            {
                (_allFollow, new[] { Lang.T("따라오기 ON", "Follow ON"), Lang.T("따라오기 OFF", "Follow OFF") }),
                (_mode,   new[] { Lang.T("가까이", "Near"), Lang.T("멀리", "Far") }),
                (_minus,  new[] { "−" }),
                (_gap,    new[] { "00.0m" }),
                (_plus,   new[] { "+" }),
                (_mimic,  new[] { Lang.T("타게팅 ON", "Target ON"), Lang.T("타게팅 OFF", "Target OFF") }),
                (_afk,    new[] { "AFK ON", "AFK OFF" }),
                (_attack, new[] { Lang.T("공격 ON", "Attack ON"), Lang.T("공격 OFF", "Attack OFF") }),
                (_buff,   new[] { Lang.T("버프", "Buff"), "10/10" }),
            };
            // 서브 창 아이콘 줄의 폭 (여기에 맞춤)
            int subRow = 0;
            foreach (Control c in _subPanel.Controls) subRow += c.Width + c.Margin.Horizontal;

            // 1) 글자가 한 줄에 들어가는 최소 폭. 서브 줄보다 넓으면 여백 → 글자 크기 순으로 줄여서 맞춤
            int[] widths = null;
            Font font = null;
            foreach (float size in new[] { 8.5f, 8f, 7.5f, 7f, 6.5f })
            {
                foreach (int pad in new[] { 10, 6, 4 })
                {
                    font?.Dispose();
                    font = new Font(Font.FontFamily, size);
                    widths = MeasureWidths(items, font, pad);
                    if (RowWidth(widths) <= subRow) goto fits;
                }
            }
        fits:
            // 2) 남는 폭은 글자가 긴 켜기/끄기 버튼들에만 나눠서 서브 줄과 같은 폭으로 (−, +, 거리, 버프는 최소 폭 유지)
            int extra = Math.Max(0, subRow - RowWidth(widths));
            var grow = new Control[] { _allFollow, _mode, _mimic, _afk, _attack };
            int growIdx = 0;
            for (int i = 0; i < items.Length; i++)
            {
                int add = 0;
                if (Array.IndexOf(grow, items[i].c) >= 0)
                {
                    add = extra / grow.Length + (growIdx < extra % grow.Length ? 1 : 0);
                    growIdx++;
                }
                var c = items[i].c;
                c.Font = font;
                c.Margin = new Padding(1);
                c.Size = new Size(widths[i] + add, IconSize);
            }
        }

        private static int[] MeasureWidths((Control c, string[] texts)[] items, Font font, int pad)
        {
            var widths = new int[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                int w = 0;
                foreach (var t in items[i].texts)
                    w = Math.Max(w, TextRenderer.MeasureText(t, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
                widths[i] = Math.Max(w + pad, 16);
            }
            return widths;
        }

        private static int RowWidth(int[] widths)
        {
            int used = 0;
            foreach (var w in widths) used += w + 2;   // 좌우 여백 1씩
            return used;
        }

        // =====================================================================
        //  오른쪽 클릭 메뉴: 핫바 번호 선택, 창 숨기기
        // =====================================================================
        private void BuildMenu()
        {
            var menu = new ContextMenuStrip();
            var pick = new ToolStripMenuItem(Lang.T("메인 화면에 띄울 핫바 번호", "Hotbar shown on the main screen"));
            int cur = _f.Settings?.HotbarNumber ?? 7;
            for (int n = 1; n <= 10; n++)
            {
                int num = n;
                var item = new ToolStripMenuItem(Lang.T($"{n}번 핫바", $"Hotbar {n}")) { Checked = n == cur };
                item.Click += (s, e) =>
                {
                    if (_f.Settings == null) return;
                    _f.Settings.HotbarNumber = num;
                    _f.SaveSettings();
                    _hotbarSig = "";
                    _nextIconCheck = DateTime.MinValue;
                    BuildMenu();
                };
                pick.DropDownItems.Add(item);
            }
            menu.Items.Add(pick);
            menu.Items.Add(Lang.T("이 창 숨기기 (Dungeon Helper 설정에서 다시 켤 수 있음)", "Hide this window (turn it back on in the Dungeon Helper settings)"), null, (s, e) =>
            {
                if (_f.Settings != null) { _f.Settings.ShowBar = false; _f.SaveSettings(); }
            });
            var old = ContextMenuStrip;
            ContextMenuStrip = menu;
            _name.ContextMenuStrip = menu;
            _status.ContextMenuStrip = menu;
            old?.Dispose();
        }

        // =====================================================================
        //  도우미
        // =====================================================================
        private static FlowLayoutPanel NewFlow(FlowDirection dir) => new FlowLayoutPanel
        {
            FlowDirection = dir,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Bg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        private static PictureBox NewIconBox() => new PictureBox
        {
            Size = new Size(IconSize, IconSize),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = SlotBg,
            Margin = new Padding(1),
        };

        private static BarButton MakeButton(string text) => new BarButton { Text = text, BackColor = OffColor };

        /// <summary>자동 힐/버프 아이콘: 십자(+) 모양, 켜지면 밝은 초록</summary>
        private static Image DrawHealIcon(bool on)
        {
            var bmp = new Bitmap(IconSize, IconSize);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Color c = on ? Color.FromArgb(120, 235, 140) : Color.FromArgb(120, 126, 140);
            using var br = new SolidBrush(c);
            int m = IconSize / 2, arm = 5, len = 12;
            g.FillRectangle(br, m - arm, m - len, arm * 2, len * 2);
            g.FillRectangle(br, m - len, m - arm, len * 2, arm * 2);
            using var f = new Font("맑은 고딕", 6.5f, FontStyle.Bold);
            using var tb = new SolidBrush(on ? Color.FromArgb(20, 60, 30) : Color.FromArgb(40, 44, 54));
            var sz = g.MeasureString("AUTO", f);
            g.DrawString("AUTO", f, tb, (IconSize - sz.Width) / 2, (IconSize - sz.Height) / 2);
            return bmp;
        }

        /// <summary>액션 버튼 아이콘: 마우스 커서 + 클릭 표시 (직접 그림)</summary>
        private static Image DrawActionIcon()
        {
            var bmp = new Bitmap(IconSize, IconSize);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var pen = new Pen(Color.FromArgb(255, 210, 90), 2f))
            {
                g.DrawLine(pen, 9, 5, 11, 9);
                g.DrawLine(pen, 4, 11, 8, 12);
                g.DrawLine(pen, 15, 4, 15, 8);
            }
            var arrow = new[]
            {
                new PointF(12, 11), new PointF(12, 29), new PointF(16.5f, 24.5f), new PointF(19.5f, 31),
                new PointF(22.5f, 29.5f), new PointF(19.5f, 23.5f), new PointF(25.5f, 23.5f),
            };
            using (var fill = new SolidBrush(Color.FromArgb(235, 240, 250))) g.FillPolygon(fill, arrow);
            using (var outline = new Pen(Color.FromArgb(40, 44, 54), 1.2f)) g.DrawPolygon(outline, arrow);
            return bmp;
        }

        /// <summary>따라오기 버튼용 발자국 아이콘 (직접 그림)</summary>
        private static Image DrawFootprints(bool on)
        {
            var bmp = new Bitmap(IconSize, IconSize);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Color c = on ? Color.FromArgb(140, 235, 150) : Color.FromArgb(120, 126, 140);
            using var br = new SolidBrush(c);
            // 왼발 (아래), 오른발 (위)
            DrawFoot(g, br, 7, 17, -12);
            DrawFoot(g, br, 18, 5, 12);
            return bmp;
        }

        private static void DrawFoot(Graphics g, Brush br, float x, float y, float angle)
        {
            var st = g.Save();
            g.TranslateTransform(x + 5, y + 7);
            g.RotateTransform(angle);
            g.FillEllipse(br, -4, -3, 8, 10);      // 발바닥
            g.FillEllipse(br, -3.5f, -8, 3, 3);    // 발가락
            g.FillEllipse(br, -0.5f, -8.5f, 3, 3);
            g.FillEllipse(br, 2.2f, -7, 2.5f, 2.5f);
            g.Restore(st);
        }

        /// <summary>
        /// v0.8.4: 버튼을 "누르는 순간" 동작. (뗄 때 동작하면, 누르는 사이 서브의 대상 선택 등으로 커서가 튀었을 때 클릭이 취소됐음)
        /// </summary>
        private static void OnPress(Control c, Action action)
        {
            c.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                Native.GetCursorPos(out var start);
                CursorWatch.Note("화면 창 버튼 누름");
                action();
                GuardCursor(start, string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name);
            };
        }

        /// <summary>
        /// v0.8.15: 버튼을 누른 직후 0.4초 동안, 마우스 버튼을 누르고 있는데 커서가 한 번에 튀면 직전 자리로 되돌림.
        /// 원인을 찾기 위해 처음 몇 번은 로그를 남김 (얼마나, 누른 뒤 몇 ms에 튀었는지)
        /// </summary>
        private static int _cursorLogCount;
        private static void GuardCursor(Native.POINT start, string what)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var last = start;
                    Native.GetCursorPos(out var now0);
                    if (Math.Abs(now0.X - start.X) + Math.Abs(now0.Y - start.Y) > 3)
                    {   // 동작하는 사이에 이미 튐
                        CursorWatch.Note("화면 창 버튼 뒤 커서 되돌림");
                        Native.SetCursorPos(start.X, start.Y);
                        LogJump(what, start, now0, sw.ElapsedMilliseconds, "동작 중");
                    }
                    while (sw.ElapsedMilliseconds < 400)
                    {
                        System.Threading.Thread.Sleep(5);
                        Native.GetCursorPos(out var now);
                        int d = Math.Abs(now.X - last.X) + Math.Abs(now.Y - last.Y);
                        if (d > 3 && Native.MouseButtonDown)
                        {
                            CursorWatch.Note("화면 창 버튼 뒤 커서 되돌림");
                            Native.SetCursorPos(last.X, last.Y);
                            LogJump(what, last, now, sw.ElapsedMilliseconds, "누르고 있는 중");
                        }
                        else last = now;
                        if (!Native.MouseButtonDown && sw.ElapsedMilliseconds > 60) break;
                    }
                }
                catch { }
            });
        }

        private static void LogJump(string what, Native.POINT from, Native.POINT to, long ms, string when)
        {
            if (_cursorLogCount >= 20) return;
            _cursorLogCount++;
            Plugin.Log.Info($"[TrailFollow] 버튼({what}) 누른 뒤 {ms}ms에 커서가 ({to.X - from.X},{to.Y - from.Y}) 튐 ({when}) - 되돌림");
        }

        private void DragStart(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
        }

        private void PlaceInitially(CharSettings s, ulong id)
        {
            Point p;
            if (s.BarX >= 0 && s.BarY >= 0) p = new Point(s.BarX, s.BarY);
            else p = new Point(200, 200 + (int)(id % 8) * 64);

            bool onScreen = false;
            foreach (var scr in Screen.AllScreens)
                if (scr.WorkingArea.Contains(p.X + 20, p.Y + 10)) onScreen = true;
            if (!onScreen) p = new Point(200, 200 + (int)(id % 8) * 64);

            Location = p;
            _posDirty = false;
        }

        public void CloseForGood()
        {
            _closing = true;
            _timer.Stop();
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
        }
    }

    /// <summary>핫바 칸 아이콘 읽기 (Awesome AF 분석값의 속성 번호 사용)</summary>
    internal static class Icons
    {
        private const uint PropIcon = 268442192;         // 스킬/주문 아이콘
        private const uint PropLinkedWeenie = 268440011; // 연결된 대상
        private const uint PropMountIcon = 268485605;    // 탈것 아이콘
        private const uint PropItemIcon = 268450334;     // 아이템 아이콘
        private const uint DefaultItemIcon = 1090537269;

        public static Image Load(IGameDataProvider g, IHotbarSlot s)
        {
            try
            {
                int? icon = null;
                switch (s.ShortcutType)
                {
                    case 2:   // 아이템
                        {
                            var pc = g.GetEntity(s.ItemIid)?.PropertyCollection;
                            icon = pc?.GetInt32PropertyValue(PropItemIcon);
                            if (icon.GetValueOrDefault() == 0) icon = (int)DefaultItemIcon;
                            break;
                        }
                    case 5:
                    case 7:   // 스킬/주문/능력
                        icon = g.GetWeenieProperties(s.ReferenceDid)?.GetInt32PropertyValue(PropIcon);
                        break;
                    case 6:
                    case 20:  // 연결된 대상의 아이콘
                        {
                            int? linked = g.GetWeenieProperties(s.ReferenceDid)?.GetInt32PropertyValue(PropLinkedWeenie);
                            if (linked.HasValue) icon = g.GetWeenieProperties((uint)linked.Value)?.GetInt32PropertyValue(PropIcon);
                            break;
                        }
                    case 50:  // 탈것
                        icon = g.GetWeenieProperties(s.ReferenceDid)?.GetInt32PropertyValue(PropMountIcon);
                        break;
                }
                if (icon.GetValueOrDefault() == 0) return TextIcon(s.ShortcutType == 18 ? $"W{s.WeaponSet}" : "?");
                return g.ImageExporter?.GetPngImage((uint)icon.Value) ?? TextIcon("?");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("[TrailFollow] 아이콘 읽기 실패", ex);
                return TextIcon("?");
            }
        }

        public static string NameOf(IGameDataProvider g, IHotbarSlot s)
        {
            try
            {
                if (s.ShortcutType == 2) return g.GetEntity(s.ItemIid)?.Name ?? Lang.T("아이템", "Item");
                if (s.ShortcutType == 18) return Lang.T($"무기 세트 {s.WeaponSet}", $"Weapon set {s.WeaponSet}");
                return g.GetWeenieProperties(s.ReferenceDid)?.Name ?? "";
            }
            catch { return ""; }
        }

        private static Image TextIcon(string text)
        {
            var bmp = new Bitmap(32, 32);
            using var gr = Graphics.FromImage(bmp);
            gr.Clear(Color.FromArgb(60, 64, 76));
            using var f = new Font("맑은 고딕", 9f, FontStyle.Bold);
            var sz = gr.MeasureString(text, f);
            gr.DrawString(text, f, Brushes.White, (32 - sz.Width) / 2, (32 - sz.Height) / 2);
            return bmp;
        }
    }

    /// <summary>
    /// 메인 창용 납작한 버튼. 글자를 직접 그려서 줄바꿈 없이 한 줄로, 가로·세로 가운데에 표시.
    /// </summary>
    internal sealed class BarButton : Control
    {
        private bool _over;
        public bool Hover { get; set; } = true;

        public BarButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            TabStop = false;
            Margin = new Padding(1);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _over = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _over = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var bg = BackColor;
            if (Hover && _over) bg = ControlPaint.Light(bg, 0.3f);
            using (var br = new SolidBrush(bg)) e.Graphics.FillRectangle(br, ClientRectangle);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
