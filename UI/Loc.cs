using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace BoltToolApp.UI
{
    /// <summary>
    /// English / Japanese UI texts. English strings are the keys; anything without a Japanese entry stays English.
    /// Apply() walks the window and translates static XAML texts, T()/F() are used for texts built in code.
    /// </summary>
    public static class Loc
    {
        public static bool Jp;

        public static string T(string en) => Jp && en != null && J.TryGetValue(en, out var v) ? v : en;

        public static string F(string en, params object[] args) =>
            args == null || args.Length == 0 ? T(en) : string.Format(CultureInfo.InvariantCulture, T(en), args);

        // ------------------------------------------------------------ tree walker

        static readonly DependencyProperty KeyProperty =
            DependencyProperty.RegisterAttached("Key", typeof(string), typeof(Loc));

        static string Resolve(DependencyObject d, string current)
        {
            var key = (string)d.GetValue(KeyProperty);
            if (key == null)
            {
                if (current == null || !J.ContainsKey(current)) return null;
                d.SetValue(KeyProperty, current);
                key = current;
            }
            return T(key);
        }

        public static void Apply(DependencyObject d)
        {
            if (d == null) return;
            switch (d)
            {
                case Window w:
                    { var s = Resolve(w, w.Title); if (s != null) w.Title = s; break; }
                case TextBlock tb:
                    { var s = Resolve(tb, tb.Text); if (s != null) tb.Text = s; break; }
                case HeaderedContentControl h when h.Header is string hs:
                    { var s = Resolve(h, hs); if (s != null) h.Header = s; break; }
                case ContentControl c when c.Content is string cs:
                    { var s = Resolve(c, cs); if (s != null) c.Content = s; break; }
                case DataGrid g:
                    foreach (var col in g.Columns)
                        if (col.Header is string ch) { var s = Resolve(col, ch); if (s != null) col.Header = s; }
                    break;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(d))
                if (child is DependencyObject cd) Apply(cd);
        }

        /// <summary>Sets the pop-up explanations (tooltips) of the named controls.</summary>
        public static void ApplyTips(FrameworkElement root)
        {
            foreach (var kv in Tips)
                if (root.FindName(kv.Key) is FrameworkElement fe)
                {
                    fe.ToolTip = new ToolTip { Content = new TextBlock { Text = T(kv.Value), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 } };
                    ToolTipService.SetShowDuration(fe, 30000);
                    ToolTipService.SetInitialShowDelay(fe, 500);
                }
        }

        // ------------------------------------------------------------ tooltips (control name -> English text)

        static readonly Dictionary<string, string> Tips = new()
        {
            ["LangBox"] = "Language of this window.",
            ["StdIso"] = "ISO dimensions (ISO 4014 / 4017 / 4032 / 7089 …).",
            ["StdJis"] = "JIS dimensions (JIS B1180 / B1181 / B1256 …). Head and nut sizes differ from ISO for M10, M12, M14 and M22.",
            ["KindBox"] = "Which single part to create. For a complete bolted connection use the Assembly tab.",
            ["SizeBox"] = "Nominal thread size. Changing it loads the default length for that size.",
            ["LengthBox"] = "Length under the head (for a threaded rod: total length). Pick a standard length or type any value.",
            ["SetDefaultBtn"] = "Remember the current length as the default for this size.",
            ["BaseBox"] = "Where the block origin 0,0,0 sits on the axis. Parts connect to each other at this point.",
            ["ThNone"] = "No thread lines – fastest and lightest block.",
            ["ThSimple"] = "Standard drawing convention: thin lines at the thread root.",
            ["ThDetail"] = "Real helical thread. Looks best in large scale, but creates many more lines.",
            ["Legend"] = "Shows where each dimension letter is measured on the part.",
            ["AsmCheck"] = "On: create bolt, washers and nut together. Off: create only the single part from the Part tab.",
            ["AsmBoltBox"] = "Bolt type used in the connection.",
            ["ClampBox"] = "Total thickness of the plates / parts the bolt holds together.",
            ["HeadWasherBox"] = "Washer between the bolt head and the clamped part.",
            ["NutWasherBox"] = "Washer between the nut and the clamped part.",
            ["SpringBox"] = "Where the spring lock washer sits: on the nut side or on the bolt head side.",
            ["NutKindBox"] = "Type of the main nut.",
            ["NutCountBox"] = "No nut (bolt goes into a tapped hole), one nut, or nut plus thin lock nut.",
            ["AutoLenCheck"] = "Pick the shortest standard bolt length that fits all parts plus the protrusion below.",
            ["ProtrusionBox"] = "How many thread pitches must stick out beyond the nut (2 is the usual rule).",
            ["SeparateCheck"] = "On: every part is its own block and they are inserted together as a linked set. Off: one block for the whole connection.",
            ["StickCheck"] = "When you stretch the bolt with its length grip, the washers and nut on the nut side move with the bolt end.",
            ["Out2D"] = "Flat drawing of the chosen view with hidden lines removed.",
            ["OutMesh"] = "3D mesh model inside the block.",
            ["OutSolid"] = "3D solid inside the block (falls back to a mesh when it can't be converted, e.g. with detailed thread).",
            ["AzSlider"] = "Turn the view left / right. 0° = side view, +90° = head end, −90° = thread end.",
            ["ElSlider"] = "Tilt the view up / down. 90° = top view, 35.26° = isometric.",
            ["SpinSlider"] = "Turn the hexagon about the bolt axis: 0° shows it across corners, 30° across flats.",
            ["AzBox"] = "Exact azimuth in degrees.",
            ["ElBox"] = "Exact elevation in degrees.",
            ["SpinBox"] = "Exact hex spin in degrees.",
            ["HiddenCheck"] = "Also draw the edges that are behind other geometry, as dashed lines.",
            ["ScaleBox"] = "Size factor of the block. 1 = real size in mm.",
            ["WipeCheck"] = "Put a wipeout behind each part so it covers whatever lies under the block.",
            ["WipeLayerBox"] = "Layer for the wipeouts. Pick one from the drawing or type a new name – it is created automatically.",
            ["SnapCheck"] = "On: dragging the length grip jumps between standard lengths. Off: any whole millimetre.",
            ["TqSizeBox"] = "Thread size for the torque calculation.",
            ["TqClassBox"] = "Strength class stamped on the bolt head (8.8, 10.9 …) or stainless grade.",
            ["TqFinishBox"] = "Surface / coating of the bolt and nut. It sets the friction values.",
            ["TqLubeBox"] = "Lubrication of thread and head when tightening. Less friction = less torque for the same preload.",
            ["TqMatBox"] = "Material under the bolt head. It sets the allowed surface pressure.",
            ["TqMuG"] = "Friction coefficient in the thread. Filled from the lists above; you can type your own value.",
            ["TqMuK"] = "Friction coefficient under the head or nut. Filled from the lists above; you can type your own value.",
            ["TqUtil"] = "How much of the bolt's yield strength is used by the preload. 90 % is the usual design value.",
            ["TqWasher"] = "A washer spreads the load, which lowers the surface pressure on the clamped part.",
            ["TqGrid"] = "Preload and tightening torque for every size with the same class, friction and utilisation.",
            ["ShowWipeToggle"] = "Show or hide the wipeout areas in this preview only.",
            ["InsertBtn"] = "Create the block(s) and place them in the drawing.",
            ["UpdateBtn"] = "Apply these settings to the block you selected with BOLTEDIT.",
            ["MakeBtn"] = "Only create the block definition, without placing it.",
            ["ResetBtn"] = "Put all settings back to the defaults.",
            ["ViewSnapCheck"] = "While you orbit with the mouse or joystick, the view catches on side / end / top views and on 30°, 35.26° (isometric), 45° and 60°. Arrow keys never snap.",
            ["ResetOffsetsBtn"] = "Put every washer and nut back to its normal stacked position.",
            ["DistBox"] = "Distance from the bolt head to the nearest selected part. Enter = apply, Esc = cancel.",
        };

        // ------------------------------------------------------------ Japanese

        static readonly Dictionary<string, string> J = new()
        {
            // window, header, buttons
            ["Bolt Tool"] = "ボルトツール",
            ["Bolts · nuts · washers → blocks"] = "ボルト・ナット・座金 → ブロック",
            ["Editing a placed block – press “Update selected block”"] = "配置済みブロックを編集中 –「選択ブロックを更新」を押してください",
            ["Insert into drawing"] = "図面に挿入",
            ["Update selected block"] = "選択ブロックを更新",
            ["Create block only"] = "ブロック定義のみ作成",
            ["Reset"] = "リセット",
            ["Show wipeouts"] = "ワイプアウト表示",

            // part tab
            ["Part"] = "部品",
            ["Standard"] = "規格",
            ["Size"] = "サイズ",
            ["Length L (mm)"] = "長さ L (mm)",
            ["Set as default"] = "既定値に設定",
            ["Insertion / connection point"] = "挿入点 / 接続点",
            ["Block origin 0,0,0 is placed on the axis at this point – snap parts together with it."] =
                "ブロック原点 0,0,0 を軸上のこの位置に置きます。部品同士の接続に使えます。",
            ["Thread"] = "ねじ表示",
            ["None"] = "なし",
            ["Simplified"] = "簡略",
            ["Detailed (helix)"] = "詳細 (らせん)",
            ["Dimensions"] = "寸法",
            ["What the letters mean"] = "記号の意味",
            ["Default for {0}: {1} mm"] = "{0} の既定長さ: {1} mm",
            ["  ·  assembly uses the bolt selected on the Assembly tab"] = "  ·  組立では「組立」タブのボルトを使用",
            ["pitch P"] = "ピッチ P",
            ["head Ø dk"] = "頭部径 dk",
            ["head k"] = "頭部高さ k",
            ["key s"] = "六角穴 s",
            ["thread b"] = "ねじ長さ b",

            // part names
            ["Hex bolt (partial thread)"] = "六角ボルト (半ねじ)",
            ["Hex bolt (full thread)"] = "六角ボルト (全ねじ)",
            ["Socket head cap screw"] = "六角穴付きボルト",
            ["Threaded rod / stud"] = "寸切りボルト / スタッド",
            ["Hex nut"] = "六角ナット",
            ["Thin (jam) nut"] = "薄ナット (3種)",
            ["Thin nut"] = "薄ナット",
            ["Plain washer"] = "平座金",
            ["Large washer"] = "大形平座金",
            ["Spring lock washer"] = "ばね座金",

            // base points
            ["Head top – middle of the head tip"] = "頭部上面 – 頭の先端中心",
            ["Under head – bearing face"] = "首下 – 座面",
            ["Thread end"] = "ねじ先端",
            ["Middle of the length"] = "長さの中央",
            ["Bearing face (contact side)"] = "座面 (接触側)",
            ["Outer face"] = "外側の面",
            ["Middle of the thickness"] = "厚さの中央",

            // assembly tab
            ["Assembly"] = "組立",
            ["Create a bolted connection (bolt + washers + nut)"] = "ボルト締結を作成 (ボルト + 座金 + ナット)",
            ["Bolt"] = "ボルト",
            ["Clamp length – total thickness of the joined parts (mm)"] = "締付け長さ – 被締結部材の合計厚さ (mm)",
            ["Washer under head"] = "頭側の座金",
            ["Washer under nut"] = "ナット側の座金",
            ["Spring lock washer – stick to"] = "ばね座金 – 取付け側",
            ["No spring washer"] = "ばね座金なし",
            ["Nut side (under the nut)"] = "ナット側 (ナットの下)",
            ["Bolt side (under the head)"] = "ボルト側 (頭の下)",
            ["Nut type"] = "ナットの種類",
            ["Nuts"] = "ナット数",
            ["No nut (tapped hole)"] = "ナットなし (めねじ穴)",
            ["1 nut"] = "ナット 1 個",
            ["Nut + lock nut"] = "ナット + ロックナット",
            ["Bolt length"] = "ボルト長さ",
            ["Choose standard length automatically"] = "標準長さを自動選択",
            ["  thread pitches must stick out of the nut"] = "  ナットから出すねじ山の数 (ピッチ)",
            ["Bolt length: {0} mm  ({1})"] = "ボルト長さ: {0} mm  ({1})",
            ["auto"] = "自動",
            ["manual"] = "手動",
            ["Blocks"] = "ブロック",
            ["Separate block per part (linked set)"] = "部品ごとに別ブロック (連動セット)",
            ["Nut side follows the bolt end when the length grip is dragged"] = "長さグリップ操作時にナット側をボルト先端に追従",
            ["Each part gets its own block with its connection point at the contact face. Drag ▶ on the bolt end to change length, ▶ on the nut to change the clamp length."] =
                "各部品は接触面を接続点とする個別ブロックになります。ボルト先端の ▶ で長さ、ナットの ▶ で締付け長さを変更できます。",

            // view & block tab
            ["View & Block"] = "ビュー / ブロック",
            ["Output"] = "出力",
            ["2D view"] = "2D ビュー",
            ["3D mesh"] = "3D メッシュ",
            ["3D solid"] = "3D ソリッド",
            ["View direction"] = "視点方向",
            ["Side"] = "側面",
            ["Head end"] = "頭側",
            ["Top"] = "上面",
            ["Iso SW"] = "アイソメ 南西",
            ["Iso SE"] = "アイソメ 南東",
            ["Iso NE"] = "アイソメ 北東",
            ["Iso NW"] = "アイソメ 北西",
            ["Azimuth °"] = "方位角 °",
            ["Elevation °"] = "仰角 °",
            ["Hex spin °"] = "六角の回転 °",
            ["Hex spin 0° shows the hex across corners, 30° across flats. You can also orbit in the preview with the mouse or the joystick."] =
                "六角の回転 0° は対角、30° は二面幅が見える向きです。プレビュー上でマウスやジョイスティックでも回転できます。",
            ["Show hidden lines (HIDDEN linetype)"] = "隠れ線を表示 (HIDDEN 線種)",
            ["Block"] = "ブロック",
            ["Scale"] = "尺度",
            ["Add wipeout behind each part"] = "各部品の背面にワイプアウトを追加",
            ["Wipeout layer"] = "ワイプアウトの画層",
            ["All lines are created on layer 0 with colour / linetype / lineweight ByBlock – only wipeouts go on this layer."] =
                "線はすべて画層 0、色・線種・線の太さは ByBlock で作成します。ワイプアウトのみこの画層に入ります。",
            ["Length grip snaps to standard lengths"] = "長さグリップを標準長さにスナップ",
            ["2D block"] = "2D ブロック",
            ["3D mesh block (preview)"] = "3D メッシュブロック (プレビュー)",
            ["3D solid block (preview)"] = "3D ソリッドブロック (プレビュー)",
            ["{0} triangles, {1} polylines"] = "三角形 {0}、ポリライン {1}",
            ["Drag = rotate · Arrow keys = 1° (Shift 15°, Ctrl 0.1°) · Wheel = zoom · Right-drag = pan · Double-click = fit · Ctrl+Z = undo"] =
                "ドラッグ = 回転 · 矢印キー = 1° (Shift 15°, Ctrl 0.1°) · ホイール = ズーム · 右ドラッグ = 移動 · ダブルクリック = 全体表示 · Ctrl+Z = 元に戻す",
            ["orbit"] = "回転",

            // torque tab
            ["Torque"] = "締付けトルク",
            ["Property class"] = "強度区分",
            ["Bolt / nut surface"] = "ボルト / ナットの表面処理",
            ["Lubrication"] = "潤滑",
            ["Clamped part material"] = "被締結部材の材質",
            ["μ thread"] = "μ ねじ面",
            ["μ head"] = "μ 座面",
            ["Utilisation %"] = "降伏利用率 %",
            ["Washer under the head / nut"] = "頭 / ナットの下に座金あり",
            ["Tightening torque"] = "締付けトルク",
            ["Preload F_M"] = "軸力 F_M",
            ["All sizes – same conditions"] = "全サイズ – 同じ条件",
            ["range {0} – {1} N·m"] = "範囲 {0} – {1} N·m",
            ["✓ Surface pressure under head {0} MPa ≤ {1} MPa allowed for {2}."] = "✓ 座面の面圧 {0} MPa ≤ {2} の許容値 {1} MPa。",
            ["⚠ Surface pressure {0} MPa exceeds {1} MPa for {2} – use a (large / hardened) washer or lower class."] =
                "⚠ 面圧 {0} MPa が {2} の許容値 {1} MPa を超えています – (大形 / 焼入れ) 座金を使うか強度区分を下げてください。",
            ["Simplified VDI 2230-1: preload at 90 % yield utilisation (equivalent stress), M_A = F_M·(0.16P + 0.58·d2·μG + μK·D_Km/2). Friction values are typical – use the supplier's data for critical joints. min / max = μ ± 0.02."] =
                "簡易 VDI 2230-1: 降伏点の 90 % (相当応力) での軸力、M_A = F_M·(0.16P + 0.58·d2·μG + μK·D_Km/2)。摩擦係数は代表値です – 重要な締結にはメーカーの値を使用してください。min / max = μ ± 0.02。",
            ["Black oxide / plain steel"] = "黒染め / 生地",
            ["Phosphated"] = "りん酸塩処理",
            ["Zinc plated (electro)"] = "電気亜鉛めっき",
            ["Zinc flake (Geomet / Delta)"] = "亜鉛フレーク (ジオメット / デルタ)",
            ["Hot-dip galvanized"] = "溶融亜鉛めっき",
            ["Stainless steel A2 / A4"] = "ステンレス A2 / A4",
            ["Dry (as delivered, no extra lube)"] = "乾燥 (納入状態、追加潤滑なし)",
            ["Lightly oiled"] = "軽く塗油",
            ["MoS2 / anti-seize paste"] = "MoS2 / 焼付き防止ペースト",
            ["Steel S235"] = "鋼 S235 (SS400 相当)",
            ["Steel S355"] = "鋼 S355 (SM490 相当)",
            ["Steel C45 / 42CrMo4"] = "鋼 S45C / SCM440",
            ["Stainless 1.4301 (304)"] = "ステンレス SUS304",
            ["Cast iron GJL-250"] = "ねずみ鋳鉄 FC250",
            ["Ductile iron GJS-400"] = "ダクタイル鋳鉄 FCD400",
            ["Aluminium wrought AlMgSi1"] = "アルミ展伸材 A6061 相当",
            ["Aluminium cast AlSi9Cu3"] = "アルミ鋳物 ADC12 相当",

            // status messages
            ["Preview error: {0}"] = "プレビューエラー: {0}",
            ["Default length for {0} ({1}) set to {2} mm."] = "{0} ({1}) の既定長さを {2} mm に設定しました。",
            ["Settings reset to defaults."] = "設定を既定値に戻しました。",
            ["Choose a layer for the wipeouts."] = "ワイプアウトの画層を選択してください。",
            ["Open a drawing first."] = "先に図面を開いてください。",
            ["Pick the insertion point in the drawing…"] = "図面上で挿入点を指定してください…",
            ["Block definition created – see the command line for its name."] = "ブロック定義を作成しました – 名前はコマンドラインを確認してください。",
            ["Block updated."] = "ブロックを更新しました。",
            ["Loaded from the selected block. Change anything and press “Update selected block”."] =
                "選択ブロックから読み込みました。変更して「選択ブロックを更新」を押してください。",
            ["Nothing to undo."] = "元に戻す操作はありません。",
            ["Nothing to redo."] = "やり直す操作はありません。",
            ["Undone."] = "元に戻しました。",
            ["Redone."] = "やり直しました。",

            // moving parts, view snap
            ["Snap to standard view angles while orbiting"] = "回転時に標準の視点角度へスナップ",
            ["Snap view angles"] = "視点角度スナップ",
            ["Hex spin 0° shows the hex across corners, 30° across flats. View presets and angle snapping are in the bar under the preview."] =
                "六角の回転 0° は対角、30° は二面幅が見える向きです。視点プリセットと角度スナップはプレビュー下のバーにあります。",
            ["Part positions"] = "部品の位置",
            ["Reset part positions"] = "部品位置をリセット",
            ["In the preview, hold Ctrl and drag a washer or nut to move it along the bolt. Ctrl+click several parts to move them together. The red number is the distance from the bolt head – click it to type an exact value."] =
                "プレビュー上で Ctrl を押しながら座金やナットをドラッグすると、ボルトに沿って移動できます。Ctrl+クリックで複数選択すると一緒に動きます。赤い数値はボルト頭からの距離で、クリックすると正確な値を入力できます。",
            ["Ctrl+click = select a part · Ctrl+drag = move it along the bolt · click the distance to type a value · Esc = unselect"] =
                "Ctrl+クリック = 部品を選択 · Ctrl+ドラッグ = ボルトに沿って移動 · 距離をクリックして数値入力 · Esc = 選択解除",
            ["While you orbit with the mouse or joystick, the view catches on side / end / top views and on 30°, 35.26° (isometric), 45° and 60°. Arrow keys never snap."] =
                "マウスやジョイスティックで回転するとき、側面・端面・上面と 30°・35.26° (アイソメ)・45°・60° に吸着します。矢印キーはスナップしません。",
            ["Put every washer and nut back to its normal stacked position."] = "すべての座金とナットを通常の重ね位置に戻します。",
            ["Distance from the bolt head to the nearest selected part. Enter = apply, Esc = cancel."] =
                "ボルト頭から最も近い選択部品までの距離。Enter = 確定、Esc = キャンセル。",

            // tooltips
            ["Language of this window."] = "このウィンドウの表示言語。",
            ["ISO dimensions (ISO 4014 / 4017 / 4032 / 7089 …)."] = "ISO 寸法 (ISO 4014 / 4017 / 4032 / 7089 など)。",
            ["JIS dimensions (JIS B1180 / B1181 / B1256 …). Head and nut sizes differ from ISO for M10, M12, M14 and M22."] =
                "JIS 寸法 (JIS B1180 / B1181 / B1256 など)。M10・M12・M14・M22 は頭とナットの二面幅が ISO と異なります。",
            ["Which single part to create. For a complete bolted connection use the Assembly tab."] =
                "作成する単品部品。ボルト締結一式は「組立」タブを使用します。",
            ["Nominal thread size. Changing it loads the default length for that size."] = "ねじの呼び径。変更するとそのサイズの既定長さが入ります。",
            ["Length under the head (for a threaded rod: total length). Pick a standard length or type any value."] =
                "首下長さ (寸切りボルトは全長)。標準長さから選ぶか、任意の値を入力します。",
            ["Remember the current length as the default for this size."] = "現在の長さをこのサイズの既定値として保存します。",
            ["Where the block origin 0,0,0 sits on the axis. Parts connect to each other at this point."] =
                "ブロック原点 0,0,0 の軸上の位置。部品同士はこの点で接続します。",
            ["No thread lines – fastest and lightest block."] = "ねじ線なし – 最も軽いブロック。",
            ["Standard drawing convention: thin lines at the thread root."] = "製図の慣例表示: 谷径を細線で表します。",
            ["Real helical thread. Looks best in large scale, but creates many more lines."] =
                "実際のらせんねじ。大きな尺度できれいに見えますが、線の数が大幅に増えます。",
            ["Shows where each dimension letter is measured on the part."] = "各寸法記号が部品のどこを指すかを示します。",
            ["On: create bolt, washers and nut together. Off: create only the single part from the Part tab."] =
                "オン: ボルト・座金・ナットを一括作成。オフ:「部品」タブの単品のみ作成。",
            ["Bolt type used in the connection."] = "締結に使うボルトの種類。",
            ["Total thickness of the plates / parts the bolt holds together."] = "ボルトで締め付ける板・部材の合計厚さ。",
            ["Washer between the bolt head and the clamped part."] = "ボルト頭と部材の間の座金。",
            ["Washer between the nut and the clamped part."] = "ナットと部材の間の座金。",
            ["Where the spring lock washer sits: on the nut side or on the bolt head side."] = "ばね座金の位置: ナット側またはボルト頭側。",
            ["Type of the main nut."] = "主ナットの種類。",
            ["No nut (bolt goes into a tapped hole), one nut, or nut plus thin lock nut."] =
                "ナットなし (めねじ穴に締結)、ナット 1 個、またはナット + 薄形ロックナット。",
            ["Pick the shortest standard bolt length that fits all parts plus the protrusion below."] =
                "全部品と下の突出し量が収まる最短の標準長さを自動で選びます。",
            ["How many thread pitches must stick out beyond the nut (2 is the usual rule)."] =
                "ナットから出すねじ山の数 (通常は 2 山以上)。",
            ["On: every part is its own block and they are inserted together as a linked set. Off: one block for the whole connection."] =
                "オン: 部品ごとに別ブロックで、連動セットとして一緒に挿入。オフ: 締結全体を 1 つのブロックにします。",
            ["When you stretch the bolt with its length grip, the washers and nut on the nut side move with the bolt end."] =
                "長さグリップでボルトを伸縮すると、ナット側の座金とナットがボルト先端と一緒に動きます。",
            ["Flat drawing of the chosen view with hidden lines removed."] = "選択した視点の平面図 (隠線消去済み)。",
            ["3D mesh model inside the block."] = "ブロック内に 3D メッシュモデルを作成。",
            ["3D solid inside the block (falls back to a mesh when it can't be converted, e.g. with detailed thread)."] =
                "ブロック内に 3D ソリッドを作成 (詳細ねじなど変換できない場合はメッシュのまま)。",
            ["Turn the view left / right. 0° = side view, +90° = head end, −90° = thread end."] =
                "視点を左右に回転。0° = 側面、+90° = 頭側、−90° = ねじ先端側。",
            ["Tilt the view up / down. 90° = top view, 35.26° = isometric."] = "視点を上下に傾けます。90° = 上面、35.26° = アイソメ。",
            ["Turn the hexagon about the bolt axis: 0° shows it across corners, 30° across flats."] =
                "六角を軸まわりに回転: 0° は対角、30° は二面幅が見えます。",
            ["Exact azimuth in degrees."] = "方位角を数値で指定 (度)。",
            ["Exact elevation in degrees."] = "仰角を数値で指定 (度)。",
            ["Exact hex spin in degrees."] = "六角の回転を数値で指定 (度)。",
            ["Also draw the edges that are behind other geometry, as dashed lines."] = "他の形状に隠れた稜線も破線で描きます。",
            ["Size factor of the block. 1 = real size in mm."] = "ブロックの尺度。1 = 実寸 (mm)。",
            ["Put a wipeout behind each part so it covers whatever lies under the block."] =
                "各部品の背面にワイプアウトを置き、ブロックの下にある図形を隠します。",
            ["Layer for the wipeouts. Pick one from the drawing or type a new name – it is created automatically."] =
                "ワイプアウト用の画層。図面の画層から選ぶか新しい名前を入力 – 自動で作成されます。",
            ["On: dragging the length grip jumps between standard lengths. Off: any whole millimetre."] =
                "オン: 長さグリップが標準長さにスナップ。オフ: 1 mm 単位で任意。",
            ["Thread size for the torque calculation."] = "トルク計算に使うねじサイズ。",
            ["Strength class stamped on the bolt head (8.8, 10.9 …) or stainless grade."] =
                "ボルト頭の刻印の強度区分 (8.8、10.9 など) またはステンレスの区分。",
            ["Surface / coating of the bolt and nut. It sets the friction values."] = "ボルトとナットの表面処理。摩擦係数が決まります。",
            ["Lubrication of thread and head when tightening. Less friction = less torque for the same preload."] =
                "締付け時のねじ面・座面の潤滑。摩擦が小さいほど同じ軸力に必要なトルクは小さくなります。",
            ["Material under the bolt head. It sets the allowed surface pressure."] = "ボルト頭の下の材質。許容面圧が決まります。",
            ["Friction coefficient in the thread. Filled from the lists above; you can type your own value."] =
                "ねじ面の摩擦係数。上のリストから自動入力されますが、任意の値も入力できます。",
            ["Friction coefficient under the head or nut. Filled from the lists above; you can type your own value."] =
                "座面の摩擦係数。上のリストから自動入力されますが、任意の値も入力できます。",
            ["How much of the bolt's yield strength is used by the preload. 90 % is the usual design value."] =
                "軸力がボルトの降伏強さの何 % を使うか。通常の設計値は 90 % です。",
            ["A washer spreads the load, which lowers the surface pressure on the clamped part."] =
                "座金は荷重を分散し、部材の面圧を下げます。",
            ["Preload and tightening torque for every size with the same class, friction and utilisation."] =
                "同じ強度区分・摩擦・利用率での全サイズの軸力と締付けトルク。",
            ["Show or hide the wipeout areas in this preview only."] = "このプレビュー上だけでワイプアウト範囲の表示を切り替えます。",
            ["Preview of the block. Red cross = block origin 0,0,0, blue triangles = stretch grips."] =
                "ブロックのプレビュー。赤い十字 = ブロック原点 0,0,0、青い三角 = ストレッチグリップ。",
            ["Create the block(s) and place them in the drawing."] = "ブロックを作成して図面に配置します。",
            ["Apply these settings to the block you selected with BOLTEDIT."] = "BOLTEDIT で選択したブロックにこの設定を適用します。",
            ["Only create the block definition, without placing it."] = "配置せずにブロック定義だけを作成します。",
            ["Put all settings back to the defaults."] = "すべての設定を既定値に戻します。",
        };
    }
}
