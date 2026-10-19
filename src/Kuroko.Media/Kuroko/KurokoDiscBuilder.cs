using Kuroko.Media.Dvd;

namespace Kuroko.Media.Krk;

// DVD-Video（DvdDisc/IFO）から Kuroko のモデル（disc.json / title.json 骨格）を抽出する。
// ここで作るのは IFO 由来の純構造（コーデック非依存）。符号化由来の項目
//   ・各音声の最終 codec/channels（AC-3再圧縮なら ac3/2ch）
//   ・各トラックの確定 MkvTrack（実際の av.mkv トラック順）
//   ・Timing（RFF：フィールド数/章・セル↔フレーム）
// は変換器が符号化時に上書きする。MkvTrack は「映像=0、音声=1..、字幕=音声の次」の既定順で仮置きする
// （変換器の -map 順と一致させる）。
internal static class KurokoDiscBuilder
{
    public static KurokoManifest BuildManifest(string? createdBy = null)
        => new() { Controller = KurokoLayout.ControllerDvdVideoVm, CreatedBy = createdBy };

    public static KurokoDisc BuildDisc(DvdDisc disc)
    {
        var d = new KurokoDisc();
        foreach (var t in disc.Titles)
        {
            TimeSpan dur = TimeSpan.Zero;
            try { dur = disc.OpenTitle(t.TitleNumber).Duration; } catch { /* 壊れたタイトルは尺0で続行 */ }
            d.Titles.Add(new KurokoTitleEntry
            {
                Id = t.TitleNumber,
                Vts = t.VtsNumber,
                VtsTitle = t.VtsTitleNumber,
                MenuDomain = KurokoLayout.MenuDomainVts(t.VtsNumber),
                DurationMs = dur.TotalMilliseconds, // version 5：長さは ms の数値（旧の "c" 文字列は書かない）
                Video = KurokoLayout.TitleDir(t.TitleNumber).TrimStart('/'),
            });
        }

        // FirstPlay（ディスク投入時に実行する FPC）。
        d.FirstPlay = new KurokoPgcRef { Domain = "vmg", Pgc = 0 };
        d.FirstPlayCommands = ToVmCommands(disc.Vmg.FirstPlayCommands);

        // メニュードメイン：VMGM ＋ 各 VTS の VTSM。各メニュー PGC の命令（PRE/POST/CELL）を JSON に持つ。
        var vmgm = BuildMenuDomain(KurokoLayout.MenuDomainVmgm, SafeMenuPgcs(() => disc.GetVmgmPgcs()));
        if (vmgm.Pgcs.Count > 0) d.MenuDomains.Add(vmgm);
        foreach (int vts in disc.Titles.Select(t => t.VtsNumber).Distinct().OrderBy(v => v))
        {
            var dom = BuildMenuDomain(KurokoLayout.MenuDomainVts(vts), SafeMenuPgcs(() => disc.GetMenuPgcs(vts)));
            if (dom.Pgcs.Count > 0) d.MenuDomains.Add(dom);
        }

        // タイトルドメイン（VTS_PGCIT）：PGC↔タイトル対応＋タイトル番号を持たない中間 PGC の命令。
        // LinkPGCN 等「現ドメイン内」の PGC リンクをタイトル再生中に解決するために必要
        // （例：ある市販ディスク VTS6 の通過専用 PGC#2/#4。これが無いと再生側は行き先を失い状態機械が脱線する）。
        foreach (int vts in disc.Titles.Select(t => t.VtsNumber).Distinct().OrderBy(v => v))
        {
            var td = new KurokoTitleDomain { Vts = vts };
            foreach (var pgc in disc.GetTitleDomainPgcs(vts))
            {
                int titleNo = disc.TitleNumberForVtsPgc(vts, pgc.Number);
                td.Pgcs.Add(new KurokoTitleDomainPgc
                {
                    Pgc = pgc.Number,
                    Title = titleNo,
                    Commands = titleNo > 0 ? null : ToPgcCommands(pgc), // タイトルPGCの命令は title.json 側
                });
            }
            if (td.Pgcs.Count > 0) d.TitleDomains.Add(td);
        }
        return d;
    }

    private static IReadOnlyList<DvdMenuPgc> SafeMenuPgcs(System.Func<IReadOnlyList<DvdMenuPgc>> f)
    {
        try { return f() ?? System.Array.Empty<DvdMenuPgc>(); } catch { return System.Array.Empty<DvdMenuPgc>(); }
    }

    private static KurokoMenuDomain BuildMenuDomain(string id, IReadOnlyList<DvdMenuPgc> pgcs)
    {
        var dom = new KurokoMenuDomain { Id = id };
        for (int i = 0; i < pgcs.Count; i++)
        {
            int pgcNo = i + 1; // ドメイン内 1-based
            dom.Pgcs.Add(new KurokoMenuPgc
            {
                Pgc = pgcNo,
                Type = System.Enum.IsDefined(typeof(DvdMenuType), pgcs[i].Type) ? pgcs[i].Type.ToString() : null,
                Path = KurokoLayout.MenuPgcDir(id, pgcNo).TrimStart('/'),
                Commands = ToPgcCommands(pgcs[i].Pgc),
                UopControl = pgcs[i].Pgc.UopControl,
            });
        }
        return dom;
    }

    // PGC の命令群を hex＋ニーモニックで束ねる。
    private static KurokoPgcCommands ToPgcCommands(DvdPgc pgc) => new()
    {
        Pre = ToVmCommands(pgc.PreCommands),
        Post = ToVmCommands(pgc.PostCommands),
        Cell = ToVmCommands(pgc.CellCommands),
    };

    private static List<KurokoVmCommand> ToVmCommands(IReadOnlyList<byte[]> cmds)
    {
        var list = new List<KurokoVmCommand>(cmds.Count);
        foreach (var c in cmds)
            list.Add(new KurokoVmCommand { Hex = DvdVmDisassembler.Hex(c), Asm = DvdVmDisassembler.ToMnemonic(c) });
        return list;
    }

    // 8バイトVM命令1つを hex＋ニーモニックに（ボタン命令用）。
    private static KurokoVmCommand ToVmCommand(byte[] c)
        => new() { Hex = DvdVmDisassembler.Hex(c), Asm = DvdVmDisassembler.ToMnemonic(c) };

    // メニューPGCのサブピクチャ用 YUV パレット（16色）を palette.json モデルへ。
    public static KurokoMenuPalette BuildMenuPalette(IReadOnlyList<uint> palette)
        => new() { Palette = KurokoPalette.Format(palette) };

    // 代表PCIのペイロード（private_stream_2、先頭0x00）を buttons.json のPCIモデルへ。
    // ボタンが無い/解析不能なら null（背景のみのメニューセル）。典拠 DvdNavPci。
    public static KurokoPci? BuildPci(byte[]? pciPayload)
    {
        if (pciPayload is null) return null;
        var pci = DvdNavPci.Parse(pciPayload);
        if (pci is null || !pci.HasButtons) return null;
        var m = new KurokoPci { ForcedSelect = pci.ForcedSelect, ForcedActivate = pci.ForcedActivate, VobuUop = pci.VobuUopControl };
        foreach (var b in pci.Buttons)
            m.Buttons.Add(new KurokoMenuButton
            {
                Number = b.Number,
                X0 = b.XStart, Y0 = b.YStart, X1 = b.XEnd, Y1 = b.YEnd,
                ColorGroup = b.ColorGroup, AutoAction = b.AutoAction,
                Up = b.Up, Down = b.Down, Left = b.Left, Right = b.Right,
                Command = ToVmCommand(b.Command),
            });
        foreach (var g in pci.ColorGroups)
            m.ColorGroups.Add(new KurokoButtonColorGroup
            {
                SelectColor = g.SelectColor, SelectContrast = g.SelectContrast,
                ActionColor = g.ActionColor, ActionContrast = g.ActionContrast,
            });
        return m;
    }

    // IFO 由来の構造骨格。codec/channels/mkvTrack/timing は変換器が符号化時に確定させる。
    public static KurokoTitle BuildTitleSkeleton(DvdTitlePlayback tp)
    {
        var k = new KurokoTitle();
        var v = tp.VideoAttr;
        k.Video = new KurokoVideo
        {
            Width = v.DisplayWidth,
            Height = v.DisplayHeight,
            Aspect = v.AspectRatio == "16:9" ? "16:9" : "4:3",
            Fps = v.IsPal ? "25/1" : "30000/1001",
            Interlaced = false,
        };

        int track = 1; // 0=映像
        for (int i = 0; i < tp.AudioStreams.Count; i++)
        {
            var a = tp.AudioStreams[i];
            k.Audio.Add(new KurokoAudio
            {
                DvdStream = i,
                MkvTrack = track++,
                Codec = CodecName(a.CodingMode),
                Channels = a.Channels,
                Language = a.HasLanguage ? a.LanguageCode : null,
                AstCtl = AstNumber(tp.Pgc, i),
            });
        }
        for (int i = 0; i < tp.SubpictureStreams.Count; i++)
        {
            var s = tp.SubpictureStreams[i];
            k.Subtitle.Add(new KurokoSubtitle
            {
                DvdStream = i,
                MkvTrack = track++,
                Language = s.HasLanguage ? s.LanguageCode : null,
                SpstCtl = SpstNumber(tp.Pgc, i),
            });
        }

        k.Palette = KurokoPalette.Format(tp.Palette);

        var pgc = tp.Pgc;
        k.UopControl = pgc.UopControl;
        for (int p = 0; p < pgc.ProgramEntryCells.Count; p++)
            k.Programs.Add(new KurokoProgram { Pgn = p + 1, EntryCell = pgc.ProgramEntryCells[p] });
        for (int c = 0; c < pgc.Cells.Count; c++)
            k.Cells.Add(new KurokoCell
            {
                Index = c,
                Duration = KurokoTime.Format(pgc.Cells[c].Duration),
                StartTime = KurokoTime.Format(tp.CellStartTime(c)),
                Still = pgc.Cells[c].StillTime == 0xFF ? -1 : pgc.Cells[c].StillTime,
                CommandNr = pgc.Cells[c].CommandNr,
            });
        foreach (var ch in tp.Chapters)
            k.Chapters.Add(new KurokoChapter
            {
                Number = ch.Number,
                StartCell = ch.StartCellIndex,
                StartTime = KurokoTime.Format(ch.StartTime),
            });

        k.Timing = new KurokoTiming { FrameRate = k.Video.Fps };
        // タイトル PGC の命令（PRE/POST／セル命令）。VM の到達先で実行する。
        k.PgcCommands = ToPgcCommands(pgc);
        return k;
    }

    private static string CodecName(DvdAudioCodingMode m) => m switch
    {
        DvdAudioCodingMode.Ac3 => "ac3",
        DvdAudioCodingMode.Dts => "dts",
        DvdAudioCodingMode.Lpcm => "pcm",
        DvdAudioCodingMode.Mpeg1 or DvdAudioCodingMode.Mpeg2Ext => "mp2",
        _ => "ac3",
    };

    private static int AstNumber(DvdPgc pgc, int i)
        => i < pgc.AudioControl.Count && pgc.AudioControl[i].Available ? pgc.AudioControl[i].Number : -1;

    private static int SpstNumber(DvdPgc pgc, int i)
        => i < pgc.SubpictureControl.Count && pgc.SubpictureControl[i].Available ? pgc.SubpictureControl[i].N43 : -1;
}
